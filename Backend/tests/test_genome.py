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
        "map_width": 999999,  # way out of bounds
        "trap_density": "not-a-number",
        "enemy_behavior_mix": {"melee_chaser": 5.0, "totally_unknown_key": 3.0},
        # heal_density, enemy_density, room_attempts, etc. missing entirely
    }

    sanitized = genome.sanitize_params(bad_input, fallback)

    lo, hi = config.GENOME_BOUNDS["map_width"]
    assert lo <= sanitized["map_width"] <= hi
    assert sanitized["trap_density"] == fallback["trap_density"]
    assert sanitized["room_attempts"] == fallback["room_attempts"]

    mix = sanitized["enemy_behavior_mix"]
    assert set(mix.keys()) == set(config.BEHAVIOR_TYPES)
    assert "totally_unknown_key" not in mix
    assert abs(sum(mix.values()) - 1.0) < 1e-9


def test_sanitize_params_falls_back_to_default_mix_when_all_weights_zero():
    fallback = genome.DEFAULT_GENOME_PARAMS
    sanitized = genome.sanitize_params({"enemy_behavior_mix": {}}, fallback)
    assert sanitized["enemy_behavior_mix"] == fallback["enemy_behavior_mix"]


def test_sanitize_params_rejects_non_dict_input_entirely():
    fallback = genome.DEFAULT_GENOME_PARAMS
    sanitized = genome.sanitize_params("not even a dict", fallback)
    assert sanitized["map_width"] == fallback["map_width"]
    assert sanitized["enemy_behavior_mix"] == fallback["enemy_behavior_mix"]
