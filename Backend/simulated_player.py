"""
A simulated player for offline runs of the evolution loop.

It is a controlled oracle, not a model of how people play: its true pass
probability p*(g) is known for every encounter design g, which makes it
possible to measure things that cannot be observed with human players (how
many surrogate auto-decisions were wrong, how well the surrogate is
calibrated). It is used by auto_simulate_playthroughs.py and by the paper's
experiments.

The damage model is measured, not assumed. tools/run_duels.py plays the
game's own combat code — the scripted fighter against packs of each enemy
archetype in an open arena — over a grid of difficulty scales, tempos,
elite/regular enemies, pack sizes and two levels of the fighter's reaction
skill, and stores the mean damage per engagement in duel_table.json. This
module interpolates that table.

A player has two skill parameters:
  dodge  (q)     reaction skill: the probability of stepping out of a
                 telegraphed charge or blast, or of flanking a shieldbearer
                 (the duel fighter's own parameter; damage is interpolated
                 linearly between the q = 0 and q = 1 measurements);
  kappa  (κ)     how much of the engagement damage the player actually takes
                 over a whole dungeon, including fights avoided altogether.

Expected damage E(g) = κ · (enemy packs) · (damage per pack); realized
damage is lognormal around E(g), and the trial fails the minimal criterion's
death cap when it reaches the budget of (MAX_DEATHS_ALLOWED + 1) * 100 HP.
"""

from __future__ import annotations

import json
import math
import os
import random
from dataclasses import dataclass, field
from typing import Any, Optional

import numpy as np

import config
import genome as genome_lib

PLAYER_MAX_HP = 100            # GameManager._playerStats.MaxHp
PLAYER_ATTACK = 15             # GameManager._playerStats.Attack
PLAYER_DEFENSE = 5             # GameManager._playerStats.Defense
PLAYER_ATTACK_COOLDOWN = 0.35  # GameManager.AttackCooldown (s)
DAMAGE_BUDGET = (config.MAX_DEATHS_ALLOWED + 1) * PLAYER_MAX_HP  # fail at the 4th death
MAX_ENEMIES = 160              # GameManager.MaxEnemies

# Rooms that can hold an enemy pack on the fixed map (map_seed 123: 116
# rooms, minus the start and exit rooms). Logged by the game as "[Dungeon] rooms=".
ELIGIBLE_ROOMS = 114

# The paper's three players: (share of random designs cleared, reaction skill q).
SKILL_PROFILES: dict[str, tuple[float, float]] = {
    "novice": (0.25, 0.2),
    "intermediate": (0.50, 0.5),
    "expert": (0.75, 0.8),
}

DUEL_TABLE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "duel_table.json")


class DuelTable:
    """Mean engagement damage measured in the game (tools/run_duels.py),
    interpolated over difficulty scale and tempo (piecewise linear in the
    square root of damage, which fitted held-out duels better than linear or
    logarithmic interpolation), mixed over elite/regular by the elite rate,
    and linearly over reaction skill."""

    def __init__(self, path: str = DUEL_TABLE_PATH) -> None:
        with open(path) as f:
            data = json.load(f)
        self.behaviors: list[str] = data["behaviors"]
        self.scales = np.array(data["scales"], float)
        self.tempos = np.array(data["tempos"], float)
        self.grid: dict[tuple[str, bool, float], np.ndarray] = {}
        packs: dict[tuple[str, float, int], float] = {}
        for row in data["rows"]:
            if row["kind"] == "grid":
                key = (row["behavior"], bool(row["elite"]), float(row["dodge"]))
                arr = self.grid.setdefault(key, np.full((len(self.scales), len(self.tempos)), np.nan))
                i = int(np.argmin(abs(self.scales - row["scale"])))
                j = int(np.argmin(abs(self.tempos - row["tempo"])))
                arr[i, j] = row["damage_mean"]
            elif row["kind"] == "pack":
                packs[(row["behavior"], float(row["dodge"]), int(row["count"]))] = row["damage_mean"]
        self.holdout = [row for row in data["rows"] if row["kind"] == "holdout"]

        # Pack multiplier m_k = damage of a pack of k / damage of one, at
        # scale 1, tempo 1; packs of k enemies cost m_k times one engagement.
        i1, j1 = int(np.argmin(abs(self.scales - 1.0))), int(np.argmin(abs(self.tempos - 1.0)))
        self.pack_multiplier: dict[tuple[str, float, int], float] = {}
        for behavior in self.behaviors:
            for dodge in (0.0, 1.0):
                single = self.grid[(behavior, False, dodge)][i1, j1]
                self.pack_multiplier[(behavior, dodge, 1)] = 1.0
                for k in (2, 3):
                    measured = packs.get((behavior, dodge, k))
                    self.pack_multiplier[(behavior, dodge, k)] = (
                        measured / single if measured is not None and single > 0.5 else float(k))

    def _interp(self, arr: np.ndarray, scale: float, tempo: float) -> float:
        roots = np.sqrt(arr)
        along_scale = np.array([np.interp(scale, self.scales, roots[:, j]) for j in range(len(self.tempos))])
        return float(np.interp(tempo, self.tempos, along_scale) ** 2)

    def single(self, behavior: str, scale: float, tempo: float, elite_rate: float, dodge: float) -> float:
        """Expected damage of one engagement with one enemy of this archetype."""
        total = 0.0
        for q, wq in ((0.0, 1.0 - dodge), (1.0, dodge)):
            if wq <= 0:
                continue
            regular = self._interp(self.grid[(behavior, False, q)], scale, tempo)
            elite = self._interp(self.grid[(behavior, True, q)], scale, tempo)
            total += wq * ((1.0 - elite_rate) * regular + elite_rate * elite)
        return total

    def multiplier(self, behavior: str, k: int, dodge: float) -> float:
        k = max(1, min(3, k))
        return (1.0 - dodge) * self.pack_multiplier[(behavior, 0.0, k)] + dodge * self.pack_multiplier[(behavior, 1.0, k)]


_DEFAULT_TABLE: Optional[DuelTable] = None


def default_table() -> DuelTable:
    global _DEFAULT_TABLE
    if _DEFAULT_TABLE is None:
        _DEFAULT_TABLE = DuelTable()
    return _DEFAULT_TABLE


def effective_enemy_share(params: dict[str, Any]) -> float:
    """Share of rooms holding a pack: the client rolls trap, chest, heal and
    enemy against cumulative thresholds, so the share is capped."""
    resources = params["trap_density"] + params["chest_density"] + params["heal_density"]
    return max(0.0, min(params["enemy_density"], 1.0 - resources))


def pack_distribution(pack_size: float) -> list[tuple[int, float]]:
    """(count, probability) of the pack sizes the client draws
    (EnemyArchetypes.PackCount): floor(p), plus one with probability frac(p)."""
    p = min(max(pack_size, 1.0), 3.0)
    whole = int(math.floor(p))
    frac = p - whole
    if whole >= 3 or frac <= 0:
        return [(min(whole, 3), 1.0)]
    return [(whole, 1.0 - frac), (whole + 1, frac)]


def raw_threat(params: dict[str, Any], dodge: float = 0.5, table: Optional[DuelTable] = None) -> float:
    """Nominal threat: the engagement damage of every enemy in the design for
    a player of reaction skill `dodge`, before the factor kappa."""
    table = table or default_table()
    params = genome_lib.complete_params(params)
    mix = params["enemy_behavior_mix"]
    total_w = sum(mix.get(b, 0.0) for b in table.behaviors) or 1.0
    scale, tempo, elite = params["enemy_difficulty_scale"], params["enemy_tempo"], params["elite_rate"]
    packs = ELIGIBLE_ROOMS * effective_enemy_share(params)

    per_pack = 0.0
    mean_size = 0.0
    for k, pk in pack_distribution(params["pack_size"]):
        mean_size += pk * k
        for behavior in table.behaviors:
            w = mix.get(behavior, 0.0) / total_w
            if w > 0:
                per_pack += pk * w * table.single(behavior, scale, tempo, elite, dodge) * table.multiplier(behavior, k, dodge)

    threat = packs * per_pack
    enemies = packs * mean_size
    if enemies > MAX_ENEMIES:  # the client stops spawning at the cap
        threat *= MAX_ENEMIES / enemies
    return threat


def _phi(x: float) -> float:
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


@dataclass
class SimulatedPlayer:
    """Expected damage E(g) = kappa * threat(g; dodge); realized damage is
    lognormal around E(g). The trial passes iff realized damage < budget, so
    the true pass probability is p*(g) = Phi((ln B - ln E(g)) / sigma)."""

    kappa: float
    dodge: float = 0.5
    sigma: float = 0.35
    damage_scale: float = 1.0  # sensitivity analyses: a uniform error in the measured table
    table: Optional[DuelTable] = field(default=None, repr=False)

    def expected_damage(self, params: dict[str, Any]) -> float:
        return self.kappa * self.damage_scale * raw_threat(params, self.dodge, self.table)

    def p_pass(self, params: dict[str, Any]) -> float:
        expected = max(self.expected_damage(params), 1e-9)
        return _phi((math.log(DAMAGE_BUDGET) - math.log(expected)) / self.sigma)

    def p_pass_from_threat(self, threat: float) -> float:
        """p*(g) from a precomputed nominal threat (raw_threat at this player's dodge)."""
        expected = max(self.kappa * self.damage_scale * threat, 1e-9)
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


def calibrate_kappa(target_pass_rate: float, dodge: float = 0.5, n_samples: int = 20000, seed: int = 12345,
                    sigma: float = 0.35, space: Optional[genome_lib.GenomeSpace] = None,
                    damage_scale: float = 1.0) -> float:
    """Skill is defined operationally as the fraction of the encounter space a
    player clears: find kappa such that the mean true pass probability over
    random designs equals the target (0.25 novice, 0.5 intermediate, 0.75
    expert in the paper)."""
    space = space or genome_lib.get_space()
    state = random.getstate()
    random.seed(seed)
    threats = [damage_scale * raw_threat(genome_lib.make_random_genome(space)["params"], dodge) for _ in range(n_samples)]
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


# --- The analytic damage race used before the duel measurements -------------
# Kept so the paper can compare it with what the game actually does. It
# assumed every enemy attacks every 0.85 s and that ranged/hit-and-run
# enemies get a fixed head start ("exposure").
ANALYTIC_ARCHETYPES: dict[str, dict[str, float]] = {
    "melee_chaser": {"hp": 28, "atk": 12, "df": 4, "exposure": 0.0},
    "melee_hit_run": {"hp": 18, "atk": 8, "df": 1, "exposure": 0.5},
    "ranged_kite": {"hp": 24, "atk": 10, "df": 6, "exposure": 1.0},
    "ranged_strafe": {"hp": 24, "atk": 10, "df": 6, "exposure": 1.0},
}


def analytic_engagement_damage(behavior: str, scale: float) -> int:
    arch = ANALYTIC_ARCHETYPES[behavior]
    hp = max(1, round(arch["hp"] * scale))
    atk = max(1, round(arch["atk"] * scale))
    hits_to_kill = math.ceil(hp / max(1, PLAYER_ATTACK - arch["df"]))
    duration = hits_to_kill * PLAYER_ATTACK_COOLDOWN + arch["exposure"]
    enemy_hits = math.floor(duration / 0.85) + 1
    return enemy_hits * max(1, atk - PLAYER_DEFENSE)


def make_skilled_player(skill: str, sigma: float = 0.35) -> SimulatedPlayer:
    """One of the paper's three players, with kappa calibrated to its profile."""
    target, dodge = SKILL_PROFILES[skill]
    return SimulatedPlayer(kappa=calibrate_kappa(target, dodge=dodge, sigma=sigma), dodge=dodge, sigma=sigma)
