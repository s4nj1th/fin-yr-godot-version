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

import config
from genome import GenomeSpace, complete_params, get_space


def genome_to_features(params: dict[str, Any], space: Optional[GenomeSpace] = None) -> list[float]:
    """Flattens+normalizes a genome's params dict into a GP-friendly feature
    vector: each evolved scalar gene scaled to [0, 1] by its bounds, plus the
    behavior-mix weights of the evolved archetypes (already in [0, 1])."""
    space = space or get_space()
    params = complete_params(params)
    features = []
    for key in space.scalars:
        lo, hi = space.bounds[key]
        span = (hi - lo) or 1.0
        features.append((params[key] - lo) / span)

    mix = params["enemy_behavior_mix"]
    for behavior in space.behaviors:
        features.append(mix.get(behavior, 0.0))

    return features


class Surrogate:
    def __init__(self, min_labels: int, window: int = 0, space: Optional[GenomeSpace] = None,
                 kernel: Optional[str] = None) -> None:
        self.min_labels = min_labels
        # 0 = fit on every real outcome; N > 0 = fit on only the most recent N,
        # so the model can follow a player whose skill changes.
        self.window = window
        self.space = space or get_space()
        self.kernel = (kernel or config.SURROGATE_KERNEL).lower()
        if self.kernel not in ("isotropic", "ard"):
            raise ValueError(f"kernel must be 'isotropic' or 'ard', not {self.kernel!r}")
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
        self.x_train.append(genome_to_features(params, self.space))
        self.y_train.append(1.0 if passed else 0.0)
        self._retrain()

    def predict(self, params: dict[str, Any]) -> tuple[float, float]:
        """(pass probability, total predictive std)."""
        prob, total_std, _ = self.predict_detailed(params)
        return prob, total_std

    def predict_detailed(self, params: dict[str, Any]) -> tuple[float, float, float]:
        """(pass probability, total predictive std, epistemic std).

        scikit-learn evaluates the WhiteKernel term on the test point too, so
        the predictive variance is exactly var_epistemic + noise, where noise
        is the fitted WhiteKernel level (the irreducible randomness of
        pass/fail outcomes). The epistemic part — what more data would
        remove — is recovered by subtracting it."""
        if self._model is None:
            return 0.5, 1.0, 1.0  # maximally uncertain fallback before the model is trained

        features = np.array([genome_to_features(params, self.space)])
        mean, std = self._model.predict(features, return_std=True)
        prob = float(np.clip(mean[0], 0.0, 1.0))
        total_std = float(std[0])
        epistemic = float(np.sqrt(max(0.0, total_std ** 2 - self.noise_level)))
        return prob, total_std, epistemic

    def predict_mean(self, params_list: list[dict[str, Any]]) -> np.ndarray:
        """Unclipped posterior means for several genomes at once."""
        if self._model is None:
            return np.full(len(params_list), 0.5)
        return self._model.predict(np.array([genome_to_features(p, self.space) for p in params_list]))

    @property
    def noise_level(self) -> float:
        """Fitted outcome-noise variance (the WhiteKernel term), 0 before training."""
        if self._model is None:
            return 0.0
        return float(self._model.kernel_.k2.noise_level)

    def _retrain(self) -> None:
        # A GP fit needs both classes represented to say anything meaningful —
        # with only one class seen so far, every prediction would just be a
        # constant, so hold off rather than fit (and report) a useless model.
        x_fit = self.x_train[-self.window:] if self.window > 0 else self.x_train
        y_fit = self.y_train[-self.window:] if self.window > 0 else self.y_train
        if len(y_fit) < 2 or len(set(y_fit)) < 2:
            self._model = None
            return

        length_scale = np.ones(self.space.dimension) if self.kernel == "ard" else 1.0
        kernel = ConstantKernel(1.0, (1e-2, 1e2)) * RBF(length_scale=length_scale, length_scale_bounds=(1e-2, 1e2)) \
            + WhiteKernel(noise_level=0.1, noise_level_bounds=(1e-4, 1.0))

        model = GaussianProcessRegressor(kernel=kernel, normalize_y=False, n_restarts_optimizer=2, alpha=1e-6)
        model.fit(np.array(x_fit), np.array(y_fit))
        self._model = model

    def state_dict(self) -> dict[str, Any]:
        return {"x_train": self.x_train, "y_train": self.y_train}

    def load_state(self, state: dict[str, Any]) -> None:
        x_train = state.get("x_train", [])
        y_train = state.get("y_train", [])
        n_features = self.space.dimension
        # Training data saved with a different genome layout cannot be reused.
        if len(x_train) != len(y_train) or any(len(x) != n_features for x in x_train):
            x_train, y_train = [], []
        self.x_train = x_train
        self.y_train = y_train
        self._retrain()
