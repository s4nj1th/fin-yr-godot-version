"""
Genome representation for the adaptive enemy-encounter backend.

A genome describes one enemy encounter: enemy density, a difficulty scale
on enemy hit points and attack, the enemies' tempo, the share of elite
variants, the pack size, and the mix of seven behaviour archetypes. Only
these genes evolve. Every genome's params also carry the fixed environment
(map size, room attempts, resource densities, map seed) from
config.FIXED_ENVIRONMENT, because that is the wire format the Godot client
(GenomeParams in Scripts/Data/DungeonData.cs) deserializes — keep the two in
sync when adding fields. See docs/EVOLUTIONARY_DESIGN.md.

A *genome space* says which genes evolve. The full space evolves all of
them; the legacy space evolves only the original six (density, difficulty
and a four-archetype mix) and holds the rest at their neutral values. Both
produce params in the same complete wire format.
"""

from __future__ import annotations

import random
import uuid
from dataclasses import dataclass, field
from typing import Any, Optional

import config
from config import BEHAVIOR_TYPES, FIXED_ENVIRONMENT, GENOME_BOUNDS, MUTATION_RATE, MUTATION_SIGMA_FRACTION

# Neutral values of the scalar genes: the encounter the original game
# shipped with.
NEUTRAL_SCALARS: dict[str, float] = {
    "enemy_density": 0.25,
    "enemy_difficulty_scale": 1.0,
    "enemy_tempo": 1.0,
    "elite_rate": 0.0,
    "pack_size": 1.0,
}

# The encounter the original game shipped with, in the fixed environment.
DEFAULT_ENEMY_GENES: dict[str, Any] = {
    **NEUTRAL_SCALARS,
    "enemy_behavior_mix": {
        "melee_chaser": 0.34,
        "melee_hit_run": 0.33,
        "ranged_kite": 0.33,
        "ranged_strafe": 0.0,
        "melee_charger": 0.0,
        "ranged_bomber": 0.0,
        "melee_shield": 0.0,
    },
}
DEFAULT_GENOME_PARAMS: dict[str, Any] = {**FIXED_ENVIRONMENT, **DEFAULT_ENEMY_GENES}


@dataclass(frozen=True)
class GenomeSpace:
    """Which genes evolve. Scalar genes outside `scalars` and archetypes
    outside `behaviors` are held at their neutral value / weight zero."""

    name: str
    scalars: tuple[str, ...]
    behaviors: tuple[str, ...]
    bounds: dict[str, tuple[float, float]] = field(default_factory=dict)

    @property
    def dimension(self) -> int:
        return len(self.scalars) + len(self.behaviors)


FULL_SPACE = GenomeSpace("full", tuple(GENOME_BOUNDS), tuple(BEHAVIOR_TYPES),
                         {k: GENOME_BOUNDS[k] for k in GENOME_BOUNDS})
LEGACY_SPACE = GenomeSpace("legacy", tuple(config.LEGACY_SCALAR_GENES), tuple(config.LEGACY_BEHAVIOR_TYPES),
                           {k: GENOME_BOUNDS[k] for k in config.LEGACY_SCALAR_GENES})
SPACES = {"full": FULL_SPACE, "legacy": LEGACY_SPACE}


def get_space(name: Optional[str] = None) -> GenomeSpace:
    key = (name or config.GENOME_SPACE).lower()
    if key not in SPACES:
        raise ValueError(f"genome space must be one of {sorted(SPACES)}, not {key!r}")
    return SPACES[key]


def with_fixed_environment(params: dict[str, Any]) -> dict[str, Any]:
    """Returns params with every environment field reset to the fixed values,
    so no genome — mutated, LLM-proposed or loaded from disk — can carry a
    different map."""
    return {**params, **FIXED_ENVIRONMENT}


def _neutral_params() -> dict[str, Any]:
    return {**FIXED_ENVIRONMENT, **NEUTRAL_SCALARS,
            "enemy_behavior_mix": {b: 0.0 for b in BEHAVIOR_TYPES}}


def complete_params(params: dict[str, Any]) -> dict[str, Any]:
    """Fills in genes missing from params saved by an older version (the
    four-archetype genome) with their neutral values."""
    out = {**_neutral_params(), **params, **FIXED_ENVIRONMENT}
    out["enemy_behavior_mix"] = {b: float(params.get("enemy_behavior_mix", {}).get(b, 0.0)) for b in BEHAVIOR_TYPES}
    return out


def new_genome_id() -> str:
    return uuid.uuid4().hex


def make_default_genome() -> dict[str, Any]:
    return {
        "id": new_genome_id(),
        "parent_id": None,
        "generation": 0,
        "params": {**FIXED_ENVIRONMENT, **DEFAULT_ENEMY_GENES,
                   "enemy_behavior_mix": dict(DEFAULT_ENEMY_GENES["enemy_behavior_mix"])},
    }


def make_random_genome(space: Optional[GenomeSpace] = None, rng: Any = None) -> dict[str, Any]:
    """Evolved genes drawn uniformly within their bounds; the mix from one
    uniform weight per evolved archetype, normalized. `rng` (a random.Random)
    defaults to the module-level generator."""
    space = space or get_space()
    rng = rng or random
    params = _neutral_params()
    for key in space.scalars:
        lo, hi = space.bounds[key]
        params[key] = rng.uniform(lo, hi)

    params["enemy_behavior_mix"] = _random_behavior_mix(space, rng)

    return {
        "id": new_genome_id(),
        "parent_id": None,
        "generation": 0,
        "params": params,
    }


def _random_behavior_mix(space: GenomeSpace, rng: Any = None) -> dict[str, float]:
    rng = rng or random
    weights = {b: rng.random() for b in space.behaviors}
    total = sum(weights.values()) or 1.0
    return {b: weights.get(b, 0.0) / total for b in BEHAVIOR_TYPES}


def _clamp(value: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, value))


def mutate(parent: dict[str, Any], space: Optional[GenomeSpace] = None) -> dict[str, Any]:
    """Gaussian mutation of the evolved genes with per-field clamping. No
    crossover: as in minimal criterion search (Brant & Stanley 2017), each
    offspring comes from a single parent. The environment is never mutated."""
    space = space or get_space()
    parent_params = complete_params(parent["params"])
    child_params = with_fixed_environment(parent_params)
    child_params["enemy_behavior_mix"] = dict(parent_params["enemy_behavior_mix"])

    for key in space.scalars:
        if random.random() > MUTATION_RATE:
            continue

        lo, hi = space.bounds[key]
        sigma = (hi - lo) * MUTATION_SIGMA_FRACTION
        child_params[key] = _clamp(parent_params[key] + random.gauss(0, sigma), lo, hi)

    if random.random() <= MUTATION_RATE:
        mix = dict(parent_params["enemy_behavior_mix"])
        behavior_to_nudge = random.choice(space.behaviors)
        mix[behavior_to_nudge] = max(0.0, mix.get(behavior_to_nudge, 0.0) + random.gauss(0, 0.2))
        total = sum(mix[b] for b in space.behaviors) or 1.0
        child_params["enemy_behavior_mix"] = {b: (mix[b] / total if b in space.behaviors else 0.0) for b in BEHAVIOR_TYPES}

    return {
        "id": new_genome_id(),
        "parent_id": parent["id"],
        "generation": parent["generation"] + 1,
        "params": child_params,
    }


def sanitize_params(candidate_params: Any, fallback_params: dict[str, Any],
                    space: Optional[GenomeSpace] = None) -> dict[str, Any]:
    """Defensively repairs an untrusted params dict (e.g. parsed from an LLM
    response) into a guaranteed-valid genome: any missing/malformed field is
    replaced from fallback_params, every scalar is clamped to its configured
    bounds, genes that do not evolve in this space keep their neutral value,
    and the behavior mix is renormalized. Used so a bad or adversarial LLM
    response can never produce an out-of-bounds genome or crash the server.
    """
    space = space or get_space()
    fallback_params = complete_params(fallback_params)
    if not isinstance(candidate_params, dict):
        candidate_params = {}

    # Environment fields are never taken from the candidate.
    sanitized: dict[str, Any] = _neutral_params()

    for key in space.scalars:
        lo, hi = space.bounds[key]
        value = candidate_params.get(key, fallback_params[key])
        try:
            value = float(value)
        except (TypeError, ValueError):
            value = fallback_params[key]

        sanitized[key] = _clamp(value, lo, hi)

    mix_in = candidate_params.get("enemy_behavior_mix")
    if not isinstance(mix_in, dict):
        mix_in = {}

    mix: dict[str, float] = {}
    for behavior in space.behaviors:
        try:
            mix[behavior] = max(0.0, float(mix_in.get(behavior, 0.0)))
        except (TypeError, ValueError):
            mix[behavior] = 0.0

    total = sum(mix.values())
    if total > 0:
        sanitized["enemy_behavior_mix"] = {b: mix.get(b, 0.0) / total for b in BEHAVIOR_TYPES}
    else:
        sanitized["enemy_behavior_mix"] = dict(fallback_params["enemy_behavior_mix"])

    return sanitized
