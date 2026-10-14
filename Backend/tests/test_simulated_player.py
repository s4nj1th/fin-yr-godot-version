import random

import numpy as np

import genome
from simulated_player import (
    DAMAGE_BUDGET,
    SimulatedPlayer,
    calibrate_kappa,
    effective_enemy_share,
    engagement_damage,
)


def design(**overrides):
    params = genome.make_default_genome()["params"]
    params.update(overrides)
    return params


def test_engagement_damage_matches_the_documented_table():
    # paper/ Table II: per-engagement damage at difficulty scales 0.6 / 1 / 1.6.
    expected = {
        "melee_chaser": (2, 14, 42),
        "melee_hit_run": (2, 6, 16),
        "ranged_kite": (3, 15, 44),
        "ranged_strafe": (3, 15, 44),
    }
    for behavior, values in expected.items():
        assert tuple(engagement_damage(behavior, s) for s in (0.6, 1.0, 1.6)) == values


def test_fixed_environment_never_caps_the_enemy_share():
    for density in (0.0, 0.25, 0.5):
        assert effective_enemy_share(design(enemy_density=density)) == density


def test_harder_designs_never_pass_more_often():
    player = SimulatedPlayer(kappa=1.0)
    densities = [player.p_pass(design(enemy_density=d)) for d in (0.05, 0.2, 0.35, 0.5)]
    scales = [player.p_pass(design(enemy_difficulty_scale=s)) for s in (0.6, 1.0, 1.3, 1.6)]
    assert densities == sorted(densities, reverse=True)
    assert scales == sorted(scales, reverse=True)


def test_played_outcomes_match_the_true_pass_probability():
    player = SimulatedPlayer(kappa=1.2)
    params = design()
    rng = np.random.default_rng(0)
    passes = [player.play(params, rng)["deaths_retries"] * 100 < DAMAGE_BUDGET for _ in range(4000)]
    assert abs(np.mean(passes) - player.p_pass(params)) < 0.03


def test_calibrated_skill_clears_the_target_share_of_random_designs():
    kappa = calibrate_kappa(0.5, n_samples=4000)
    random.seed(1)
    player = SimulatedPlayer(kappa=kappa)
    rates = [player.p_pass(genome.make_random_genome()["params"]) for _ in range(4000)]
    assert abs(np.mean(rates) - 0.5) < 0.03


def test_more_skilled_players_take_less_damage():
    assert calibrate_kappa(0.75, n_samples=2000) < calibrate_kappa(0.25, n_samples=2000)
