import random

import numpy as np

import genome
from simulated_player import (
    DAMAGE_BUDGET,
    SimulatedPlayer,
    calibrate_kappa,
    default_table,
    effective_enemy_share,
    pack_distribution,
    raw_threat,
)


def design(**overrides):
    params = genome.make_default_genome()["params"]
    params.update(overrides)
    return params


def only(behavior, **overrides):
    mix = {b: 0.0 for b in genome.BEHAVIOR_TYPES}
    mix[behavior] = 1.0
    return design(enemy_behavior_mix=mix, **overrides)


def test_table_covers_every_archetype():
    table = default_table()
    assert table.behaviors == list(genome.BEHAVIOR_TYPES)
    for behavior in table.behaviors:
        for elite in (False, True):
            for dodge in (0.0, 1.0):
                assert not np.isnan(table.grid[(behavior, elite, dodge)]).any()


def test_interpolation_reproduces_the_measured_grid_points():
    table = default_table()
    for behavior in table.behaviors:
        measured = table.grid[(behavior, False, 0.0)]
        for i, s in enumerate(table.scales):
            for j, t in enumerate(table.tempos):
                assert abs(table.single(behavior, s, t, 0.0, 0.0) - measured[i, j]) < 1e-9


def test_fixed_environment_never_caps_the_enemy_share():
    for density in (0.0, 0.25, 0.5):
        assert effective_enemy_share(design(enemy_density=density)) == density


def test_pack_distribution_has_the_gene_as_its_mean():
    for p in (1.0, 1.3, 2.0, 2.5):
        dist = pack_distribution(p)
        assert abs(sum(prob for _, prob in dist) - 1.0) < 1e-12
        assert abs(sum(k * prob for k, prob in dist) - p) < 1e-12


def test_harder_designs_never_pass_more_often():
    player = SimulatedPlayer(kappa=1.0)
    for gene, values in (("enemy_density", (0.05, 0.2, 0.35, 0.5)),
                         ("enemy_difficulty_scale", (0.6, 1.0, 1.3, 1.6)),
                         ("elite_rate", (0.0, 0.1, 0.25, 0.4)),
                         ("pack_size", (1.0, 1.5, 2.0, 2.5))):
        rates = [player.p_pass(design(**{gene: v})) for v in values]
        assert rates == sorted(rates, reverse=True), gene


def test_reaction_skill_matters_for_telegraphed_enemies_only():
    for behavior in ("melee_charger", "ranged_bomber", "melee_shield"):
        assert raw_threat(only(behavior), dodge=0.9) < 0.6 * raw_threat(only(behavior), dodge=0.1)
    for behavior in ("melee_chaser", "ranged_kite"):
        low, high = raw_threat(only(behavior), dodge=0.1), raw_threat(only(behavior), dodge=0.9)
        assert abs(low - high) / low < 0.1


def test_played_outcomes_match_the_true_pass_probability():
    player = SimulatedPlayer(kappa=1.2)
    params = design()
    rng = np.random.default_rng(0)
    passes = [player.play(params, rng)["deaths_retries"] * 100 < DAMAGE_BUDGET for _ in range(4000)]
    assert abs(np.mean(passes) - player.p_pass(params)) < 0.03


def test_calibrated_skill_clears_the_target_share_of_random_designs():
    kappa = calibrate_kappa(0.5, dodge=0.5, n_samples=3000)
    random.seed(1)
    player = SimulatedPlayer(kappa=kappa, dodge=0.5)
    rates = [player.p_pass(genome.make_random_genome()["params"]) for _ in range(3000)]
    assert abs(np.mean(rates) - 0.5) < 0.03


def test_more_skilled_players_take_less_damage():
    assert calibrate_kappa(0.75, n_samples=2000) < calibrate_kappa(0.25, n_samples=2000)
