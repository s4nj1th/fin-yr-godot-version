"""
Genome representation for the adaptive enemy-encounter backend.

A genome describes one enemy encounter: enemy density, a difficulty scale
applied to enemy hit points and attack, and the mix of four behaviour
archetypes. Only these genes evolve. Every genome's params also carry the
fixed environment (map size, room attempts, resource densities, map seed)
from config.FIXED_ENVIRONMENT, because that is the wire format the Godot
client (GenomeParams in Scripts/Data/DungeonData.cs) deserializes — keep the
two in sync when adding fields. See docs/EVOLUTIONARY_DESIGN.md.
"""

from __future__ import annotations

import random
import uuid
from typing import Any

from config import BEHAVIOR_TYPES, FIXED_ENVIRONMENT, GENOME_BOUNDS, MUTATION_RATE, MUTATION_SIGMA_FRACTION

# The encounter the original game shipped with, in the fixed environment.
DEFAULT_ENEMY_GENES: dict[str, Any] = {
    "enemy_density": 0.25,
    "enemy_behavior_mix": {
        "melee_chaser": 0.34,
        "melee_hit_run": 0.33,
        "ranged_kite": 0.33,
        "ranged_strafe": 0.0,
    },
    "enemy_difficulty_scale": 1.0,
}
DEFAULT_GENOME_PARAMS: dict[str, Any] = {**FIXED_ENVIRONMENT, **DEFAULT_ENEMY_GENES}


def with_fixed_environment(params: dict[str, Any]) -> dict[str, Any]:
    """Returns params with every environment field reset to the fixed values,
    so no genome — mutated, LLM-proposed or loaded from disk — can carry a
    different map."""
    return {**params, **FIXED_ENVIRONMENT}


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


def make_random_genome() -> dict[str, Any]:
    """Enemy genes drawn uniformly within their bounds; the mix from four
    uniform weights, normalized."""
    params: dict[str, Any] = dict(FIXED_ENVIRONMENT)
    for key, (lo, hi) in GENOME_BOUNDS.items():
        params[key] = random.uniform(lo, hi)

    params["enemy_behavior_mix"] = _random_behavior_mix()

    return {
        "id": new_genome_id(),
        "parent_id": None,
        "generation": 0,
        "params": params,
    }


def _random_behavior_mix() -> dict[str, float]:
    weights = [random.random() for _ in BEHAVIOR_TYPES]
    total = sum(weights) or 1.0
    return {behavior: w / total for behavior, w in zip(BEHAVIOR_TYPES, weights)}


def _clamp(value: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, value))


def mutate(parent: dict[str, Any]) -> dict[str, Any]:
    """Gaussian mutation of the enemy genes with per-field clamping. No
    crossover: as in minimal criterion search (Brant & Stanley 2017), each
    offspring comes from a single parent. The environment is never mutated."""
    parent_params = parent["params"]
    child_params = with_fixed_environment(parent_params)

    for key, (lo, hi) in GENOME_BOUNDS.items():
        if random.random() > MUTATION_RATE:
            continue

        span = hi - lo
        sigma = span * MUTATION_SIGMA_FRACTION
        mutated = parent_params[key] + random.gauss(0, sigma)
        child_params[key] = _clamp(mutated, lo, hi)

    if random.random() <= MUTATION_RATE:
        mix = dict(parent_params["enemy_behavior_mix"])
        behavior_to_nudge = random.choice(BEHAVIOR_TYPES)
        mix[behavior_to_nudge] = max(0.0, mix.get(behavior_to_nudge, 0.0) + random.gauss(0, 0.2))
        total = sum(mix.values()) or 1.0
        child_params["enemy_behavior_mix"] = {k: v / total for k, v in mix.items()}

    return {
        "id": new_genome_id(),
        "parent_id": parent["id"],
        "generation": parent["generation"] + 1,
        "params": child_params,
    }


def sanitize_params(candidate_params: Any, fallback_params: dict[str, Any]) -> dict[str, Any]:
    """Defensively repairs an untrusted params dict (e.g. parsed from an LLM
    response) into a guaranteed-valid genome: any missing/malformed field is
    replaced from fallback_params, every scalar is clamped to its configured
    bounds, and the behavior mix is renormalized. Used so a bad or adversarial
    LLM response can never produce an out-of-bounds genome or crash the server.
    """
    if not isinstance(candidate_params, dict):
        candidate_params = {}

    # Environment fields are never taken from the candidate.
    sanitized: dict[str, Any] = dict(FIXED_ENVIRONMENT)

    for key, (lo, hi) in GENOME_BOUNDS.items():
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
    for behavior in BEHAVIOR_TYPES:
        try:
            mix[behavior] = max(0.0, float(mix_in.get(behavior, 0.0)))
        except (TypeError, ValueError):
            mix[behavior] = 0.0

    total = sum(mix.values())
    sanitized["enemy_behavior_mix"] = (
        {k: v / total for k, v in mix.items()} if total > 0 else dict(fallback_params["enemy_behavior_mix"])
    )

    return sanitized
