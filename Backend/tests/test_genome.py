import config
import genome


def test_make_default_genome_matches_documented_defaults():
    g = genome.make_default_genome()
    assert g["generation"] == 0
    assert g["parent_id"] is None
    assert g["params"] == genome.DEFAULT_GENOME_PARAMS


def test_make_random_genome_respects_bounds():
    g = genome.make_random_genome()
    for key, (lo, hi) in config.GENOME_BOUNDS.items():
        assert lo <= g["params"][key] <= hi

    mix = g["params"]["enemy_behavior_mix"]
    assert set(mix.keys()) == set(config.BEHAVIOR_TYPES)
    assert abs(sum(mix.values()) - 1.0) < 1e-9


def test_mutate_stays_within_bounds_and_advances_generation():
    parent = genome.make_default_genome()

    for _ in range(200):
        child = genome.mutate(parent)
        for key, (lo, hi) in config.GENOME_BOUNDS.items():
            assert lo <= child["params"][key] <= hi
        assert child["generation"] == parent["generation"] + 1
        assert child["parent_id"] == parent["id"]
        assert child["id"] != parent["id"]

        mix = child["params"]["enemy_behavior_mix"]
        assert abs(sum(mix.values()) - 1.0) < 1e-9
        for weight in mix.values():
            assert weight >= 0.0

        parent = child  # chain mutations to also exercise non-default parents


def test_sanitize_params_repairs_missing_and_out_of_bounds_fields():
    fallback = genome.DEFAULT_GENOME_PARAMS
    bad_input = {
        "enemy_density": 999999,  # way out of bounds
        "enemy_difficulty_scale": "not-a-number",
        "enemy_behavior_mix": {"melee_chaser": 5.0, "totally_unknown_key": 3.0},
    }

    sanitized = genome.sanitize_params(bad_input, fallback)

    lo, hi = config.GENOME_BOUNDS["enemy_density"]
    assert sanitized["enemy_density"] == hi
    assert sanitized["enemy_difficulty_scale"] == fallback["enemy_difficulty_scale"]

    mix = sanitized["enemy_behavior_mix"]
    assert set(mix.keys()) == set(config.BEHAVIOR_TYPES)
    assert "totally_unknown_key" not in mix
    assert abs(sum(mix.values()) - 1.0) < 1e-9


def test_sanitize_params_never_lets_a_candidate_change_the_environment():
    sanitized = genome.sanitize_params(
        {"map_width": 80, "trap_density": 0.5, "map_seed": 7, "enemy_density": 0.3},
        genome.DEFAULT_GENOME_PARAMS,
    )
    for key, value in config.FIXED_ENVIRONMENT.items():
        assert sanitized[key] == value
    assert sanitized["enemy_density"] == 0.3


def test_sanitize_params_falls_back_to_default_mix_when_all_weights_zero():
    fallback = genome.DEFAULT_GENOME_PARAMS
    sanitized = genome.sanitize_params({"enemy_behavior_mix": {}}, fallback)
    assert sanitized["enemy_behavior_mix"] == fallback["enemy_behavior_mix"]


def test_sanitize_params_rejects_non_dict_input_entirely():
    fallback = genome.DEFAULT_GENOME_PARAMS
    sanitized = genome.sanitize_params("not even a dict", fallback)
    assert sanitized["enemy_density"] == fallback["enemy_density"]
    assert sanitized["enemy_behavior_mix"] == fallback["enemy_behavior_mix"]


def test_only_enemy_genes_evolve():
    assert set(config.GENOME_BOUNDS) == {"enemy_density", "enemy_difficulty_scale"}


def test_every_genome_carries_the_fixed_environment():
    parent = genome.make_default_genome()
    for g in [parent, genome.make_random_genome()] + [genome.mutate(parent) for _ in range(50)]:
        for key, value in config.FIXED_ENVIRONMENT.items():
            assert g["params"][key] == value


def test_mutation_restores_the_environment_of_a_legacy_parent():
    # A design archived before enemy-only evolution may carry a different map.
    legacy = genome.make_default_genome()
    legacy["params"].update({"map_width": 90, "trap_density": 0.4})
    child = genome.mutate(legacy)
    assert child["params"]["map_width"] == config.FIXED_ENVIRONMENT["map_width"]
    assert child["params"]["trap_density"] == config.FIXED_ENVIRONMENT["trap_density"]


def test_fixed_resource_densities_leave_every_enemy_density_expressed():
    # The client rolls trap, chest, heal, then enemy against cumulative
    # thresholds, so the enemy share is capped at 1 - (trap + chest + heal).
    env = config.FIXED_ENVIRONMENT
    cap = 1.0 - (env["trap_density"] + env["chest_density"] + env["heal_density"])
    assert cap >= config.GENOME_BOUNDS["enemy_density"][1]
