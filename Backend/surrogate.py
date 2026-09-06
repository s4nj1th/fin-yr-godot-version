"""
Online surrogate model that lets the archive grow without spending a real
playthrough on every candidate. See docs/EVOLUTIONARY_DESIGN.md.

Implementation note / honest simplification: a calibrated approach would use
Gaussian Process *Classification* on the binary pass/fail labels.
scikit-learn's GaussianProcessClassifier exposes a calibrated predict_proba
but, because it fits the latent function via a Laplace approximation, does
not expose a simple per-point predictive variance the way
GaussianProcessRegressor does. Since the uncertainty-gated infill criterion
needs both a probability-like mean AND a variance from one model, this fits a
GaussianProcessRegressor directly on the binary {0, 1} pass/fail labels
instead. This is a common pragmatic proxy (not "textbook" GP classification)
— call this out explicitly in the report as a documented simplification.
"""

from __future__ import annotations

from typing import Any, Optional

import numpy as np
from sklearn.gaussian_process import GaussianProcessRegressor
from sklearn.gaussian_process.kernels import ConstantKernel, RBF, WhiteKernel

from config import BEHAVIOR_TYPES, GENOME_BOUNDS

FEATURE_KEYS = list(GENOME_BOUNDS.keys())


def genome_to_features(params: dict[str, Any]) -> list[float]:
    """Flattens+normalizes a genome's params dict into a GP-friendly feature
    vector (each scalar gene scaled to [0, 1] by its configured bounds, plus
    the raw behavior-mix weights, which are already normalized to sum to 1)."""
    features = []
    for key in FEATURE_KEYS:
        lo, hi = GENOME_BOUNDS[key]
        span = (hi - lo) or 1.0
        features.append((params[key] - lo) / span)

    mix = params.get("enemy_behavior_mix", {})
    for behavior in BEHAVIOR_TYPES:
        features.append(mix.get(behavior, 0.0))

    return features


class Surrogate:
    def __init__(self, min_labels: int) -> None:
        self.min_labels = min_labels
        self.x_train: list[list[float]] = []
        self.y_train: list[float] = []
        self._model: Optional[GaussianProcessRegressor] = None

    def is_ready(self) -> bool:
        return len(self.y_train) >= self.min_labels and self._model is not None

    def add_example(self, params: dict[str, Any], passed: bool) -> None:
        """Only real, human-verified outcomes should be added here — training
        the surrogate on its own auto-decided predictions would let
        calibration error compound instead of being checked against ground
        truth (real playtelemetry is sparse for a single-player game, so this
        matters more than it would with a large training set)."""
        self.x_train.append(genome_to_features(params))
        self.y_train.append(1.0 if passed else 0.0)
        self._retrain()

    def predict(self, params: dict[str, Any]) -> tuple[float, float]:
        if self._model is None:
            return 0.5, 1.0  # maximally uncertain fallback before the model is trained

        features = np.array([genome_to_features(params)])
        mean, std = self._model.predict(features, return_std=True)
        prob = float(np.clip(mean[0], 0.0, 1.0))
        return prob, float(std[0])

    def _retrain(self) -> None:
        # A GP fit needs both classes represented to say anything meaningful —
        # with only one class seen so far, every prediction would just be a
        # constant, so hold off rather than fit (and report) a useless model.
        if len(self.y_train) < 2 or len(set(self.y_train)) < 2:
            self._model = None
            return

        kernel = ConstantKernel(1.0, (1e-2, 1e2)) * RBF(length_scale=1.0, length_scale_bounds=(1e-2, 1e2)) \
            + WhiteKernel(noise_level=0.1, noise_level_bounds=(1e-4, 1.0))

        model = GaussianProcessRegressor(kernel=kernel, normalize_y=False, n_restarts_optimizer=2, alpha=1e-6)
        model.fit(np.array(self.x_train), np.array(self.y_train))
        self._model = model

    def state_dict(self) -> dict[str, Any]:
        return {"x_train": self.x_train, "y_train": self.y_train}

    def load_state(self, state: dict[str, Any]) -> None:
        self.x_train = state.get("x_train", [])
        self.y_train = state.get("y_train", [])
        self._retrain()
