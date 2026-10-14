"""
A simulated player for offline runs of the evolution loop.

It is a controlled oracle, not a model of how people play: its true pass
probability p*(g) is known for every encounter design g, which makes it
possible to measure things that cannot be observed with human players (how
many surrogate auto-decisions were wrong, how well the surrogate is
calibrated). It is used by auto_simulate_playthroughs.py and by the paper's
experiments.

The damage model uses the game's own combat constants
(Scripts/Core/GameManager.cs, Scripts/Entities/EnemyController.cs): for each
archetype, a damage race between the player killing one enemy and the enemy
hitting back. A player's skill is a single factor kappa on the damage they
take; realized damage is lognormal around the expectation, and the trial
fails the minimal criterion's death cap when it reaches the budget of
(MAX_DEATHS_ALLOWED + 1) * 100 HP.
"""

from __future__ import annotations

import math
import random
from dataclasses import dataclass
from typing import Any

import numpy as np

import config
import genome as genome_lib

PLAYER_MAX_HP = 100            # GameManager._playerStats.MaxHp
PLAYER_ATTACK = 15             # GameManager._playerStats.Attack
PLAYER_DEFENSE = 5             # GameManager._playerStats.Defense
PLAYER_ATTACK_COOLDOWN = 0.35  # GameManager.AttackCooldown (s)
ENEMY_ATTACK_COOLDOWN = 0.85   # EnemyController.AttackCooldownSeconds (s)
DAMAGE_BUDGET = (config.MAX_DEATHS_ALLOWED + 1) * PLAYER_MAX_HP  # fail at the 4th death

# Rooms that can hold an enemy on the fixed map (map_seed 123: 116 rooms,
# minus the start and exit rooms). Logged by the game as "[Dungeon] rooms=".
ELIGIBLE_ROOMS = 114

# Base stats from GameManager.CreateEnemyDefinitionForRoll. `exposure` is a
# modelling assumption: seconds per engagement during which the enemy can
# still hit the player but the player cannot yet hit back (closing distance
# on a ranged enemy, re-engaging a hit-and-run enemy).
ARCHETYPES: dict[str, dict[str, float]] = {
    "melee_chaser": {"hp": 28, "atk": 12, "df": 4, "exposure": 0.0},
    "melee_hit_run": {"hp": 18, "atk": 8, "df": 1, "exposure": 0.5},
    "ranged_kite": {"hp": 24, "atk": 10, "df": 6, "exposure": 1.0},
    "ranged_strafe": {"hp": 24, "atk": 10, "df": 6, "exposure": 1.0},
}


def engagement_damage(behavior: str, scale: float, exposure_scale: float = 1.0) -> int:
    """Damage the player takes while killing one enemy of this archetype at
    the given difficulty scale, using the game's stats, mitigation and
    cooldowns."""
    arch = ARCHETYPES[behavior]
    hp = max(1, round(arch["hp"] * scale))  # GameManager.ScaleStats (banker's rounding, as in .NET)
    atk = max(1, round(arch["atk"] * scale))
    hits_to_kill = math.ceil(hp / max(1, PLAYER_ATTACK - arch["df"]))
    duration = hits_to_kill * PLAYER_ATTACK_COOLDOWN + arch["exposure"] * exposure_scale
    enemy_hits = math.floor(duration / ENEMY_ATTACK_COOLDOWN) + 1
    return enemy_hits * max(1, atk - PLAYER_DEFENSE)  # GameManager.OnEnemyAttackedPlayer mitigation


def effective_enemy_share(params: dict[str, Any]) -> float:
    """Share of rooms holding an enemy: the client rolls trap, chest, heal
    and enemy against cumulative thresholds, so the share is capped."""
    resources = params["trap_density"] + params["chest_density"] + params["heal_density"]
    return max(0.0, min(params["enemy_density"], 1.0 - resources))


def raw_threat(params: dict[str, Any], exposure_scale: float = 1.0) -> float:
    """Nominal threat: expected damage from a design before the skill factor."""
    n_enemies = ELIGIBLE_ROOMS * effective_enemy_share(params)
    mix = params["enemy_behavior_mix"]
    total = sum(mix.values()) or 1.0
    per_enemy = sum(
        (mix.get(b, 0.0) / total) * engagement_damage(b, params["enemy_difficulty_scale"], exposure_scale)
        for b in config.BEHAVIOR_TYPES
    )
    return n_enemies * per_enemy


def _phi(x: float) -> float:
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


@dataclass
class SimulatedPlayer:
    """Expected damage E(g) = kappa * threat(g); realized damage is lognormal
    around E(g). The trial passes iff realized damage < DAMAGE_BUDGET, so the
    true pass probability is p*(g) = Phi((ln B - ln E(g)) / sigma)."""

    kappa: float
    sigma: float = 0.35
    exposure_scale: float = 1.0

    def expected_damage(self, params: dict[str, Any]) -> float:
        return self.kappa * raw_threat(params, self.exposure_scale)

    def p_pass(self, params: dict[str, Any]) -> float:
        expected = max(self.expected_damage(params), 1e-9)
        return _phi((math.log(DAMAGE_BUDGET) - math.log(expected)) / self.sigma)

    def play(self, params: dict[str, Any], rng: np.random.Generator) -> dict[str, Any]:
        """Telemetry for one trial, in the shape the game reports."""
        expected = self.expected_damage(params)
        realized = expected * math.exp(self.sigma * rng.standard_normal())
        deaths = int(realized // PLAYER_MAX_HP)
        # Only the death-cap clause is modelled: crossing the fixed map is
        # assumed to take longer than MIN_TRIAL_SECONDS, and timeouts are
        # subsumed by repeated deaths.
        return {
            "time_taken_sec": 90.0,
            "damage_taken": int(realized),
            "health_remaining": max(0, PLAYER_MAX_HP - int(realized) % PLAYER_MAX_HP),
            "deaths_retries": deaths,
            "exit_reached": True,
            "quit_early": False,
        }


_erf_vec = np.vectorize(math.erf)


def calibrate_kappa(target_pass_rate: float, n_samples: int = 20000, seed: int = 12345,
                    sigma: float = 0.35, exposure_scale: float = 1.0) -> float:
    """Skill is defined operationally as the fraction of the encounter space a
    player clears: find kappa such that the mean true pass probability over
    random designs equals the target (0.25 novice, 0.5 intermediate, 0.75
    expert in the paper)."""
    state = random.getstate()
    random.seed(seed)
    threats = [raw_threat(genome_lib.make_random_genome()["params"], exposure_scale) for _ in range(n_samples)]
    random.setstate(state)
    threats_arr = np.maximum(np.array(threats), 1e-9)
    log_budget = math.log(DAMAGE_BUDGET)

    def mean_pass(log_kappa: float) -> float:
        z = (log_budget - (log_kappa + np.log(threats_arr))) / sigma
        return float(np.mean(0.5 * (1.0 + _erf_vec(z / math.sqrt(2.0)))))

    lo, hi = -10.0, 10.0
    for _ in range(100):
        mid = 0.5 * (lo + hi)
        if mean_pass(mid) > target_pass_rate:
            lo = mid  # passes too often -> the player must take more damage
        else:
            hi = mid
    return math.exp(0.5 * (lo + hi))
