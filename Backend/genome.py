"""
Genome representation for the Phase-1 naive human-coupled MCC.

A genome is a flat dict of dungeon-generation + encounter parameters, matching
the wire format the Godot client (GenomeData in Scripts/Data/DungeonData.cs)
deserializes. Keep the two in sync when adding fields.
"""

from __future__ import annotations

import random
import uuid
from typing import Any

from config import BEHAVIOR_TYPES, GENOME_BOUNDS, MUTATION_RATE, MUTATION_SIGMA_FRACTION

# Matches today's hardcoded GameManager.cs defaults exactly, so a fresh
# session (empty Hall of Fame) starts from "the game as it already behaved"
# rather than an arbitrary random point.
DEFAULT_GENOME_PARAMS: dict[str, Any] = {
    "map_width": 160,
    "map_height": 160,
    "room_attempts": 280,
    "trap_density": 0.25,
    "chest_density": 0.25,
    "heal_density": 0.25,
    "enemy_density": 0.25,
    "enemy_behavior_mix": {
        "melee_chaser": 0.34,
        "melee_hit_run": 0.33,
        "ranged_kite": 0.33,
        "ranged_strafe": 0.0,
    },
    "enemy_difficulty_scale": 1.0,
}


def new_genome_id() -> str:
    return uuid.uuid4().hex


def make_default_genome() -> dict[str, Any]:
    return {
        "id": new_genome_id(),
        "parent_id": None,
        "generation": 0,
        "params": dict(DEFAULT_GENOME_PARAMS),
    }


def make_random_genome() -> dict[str, Any]:
    params = {}
    for key, (lo, hi) in GENOME_BOUNDS.items():
        if isinstance(lo, int) and isinstance(hi, int):
            params[key] = random.randint(lo, hi)
        else:
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
    """Gaussian mutation with per-field clamping. No crossover in Phase 1 —
    MCC (Brant & Stanley 2017) mutates a single parent per offspring."""
    parent_params = parent["params"]
    child_params = dict(parent_params)

    for key, (lo, hi) in GENOME_BOUNDS.items():
        if random.random() > MUTATION_RATE:
            continue

        span = hi - lo
        sigma = span * MUTATION_SIGMA_FRACTION
        mutated = parent_params[key] + random.gauss(0, sigma)
        mutated = _clamp(mutated, lo, hi)

        if isinstance(lo, int) and isinstance(hi, int):
            mutated = int(round(mutated))

        child_params[key] = mutated

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
