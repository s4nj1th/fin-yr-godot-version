import genome
from surrogate import Surrogate, genome_to_features


def make_params(**overrides):
    params = dict(genome.DEFAULT_GENOME_PARAMS)
    params.update(overrides)
    return params


def test_not_ready_before_min_labels_reached():
    surrogate = Surrogate(min_labels=3)
    surrogate.add_example(make_params(), True)
    surrogate.add_example(make_params(enemy_density=0.45), False)
    assert surrogate.is_ready() is False


def test_predict_before_training_returns_maximally_uncertain_fallback():
    surrogate = Surrogate(min_labels=3)
    prob, std = surrogate.predict(make_params())
    assert prob == 0.5
    assert std == 1.0


def test_single_class_labels_never_produce_a_ready_model():
    # A GP fit on only one observed outcome can't say anything meaningful —
    # it should refuse to train rather than report a constant prediction.
    surrogate = Surrogate(min_labels=2)
    surrogate.add_example(make_params(enemy_density=0.05), True)
    surrogate.add_example(make_params(enemy_density=0.45), True)
    assert surrogate.is_ready() is False


def test_becomes_ready_once_both_classes_seen_past_min_labels():
    surrogate = Surrogate(min_labels=2)
    surrogate.add_example(make_params(enemy_density=0.05), True)
    surrogate.add_example(make_params(enemy_density=0.45), False)
    assert surrogate.is_ready() is True

    prob, std = surrogate.predict(make_params(enemy_density=0.25))
    assert 0.0 <= prob <= 1.0
    assert std >= 0.0


def test_state_round_trips_through_state_dict():
    surrogate = Surrogate(min_labels=2)
    surrogate.add_example(make_params(enemy_density=0.05), True)
    surrogate.add_example(make_params(enemy_density=0.45), False)

    restored = Surrogate(min_labels=2)
    restored.load_state(surrogate.state_dict())

    assert restored.is_ready() is True
    assert restored.x_train == surrogate.x_train
    assert restored.y_train == surrogate.y_train


def _noisy_surrogate(window=0):
    """Repeated, contradictory outcomes on the same designs: the GP has to
    explain them as outcome noise."""
    surrogate = Surrogate(min_labels=2, window=window)
    for _ in range(6):
        for density in (0.1, 0.3):
            surrogate.add_example(make_params(enemy_density=density), True)
            surrogate.add_example(make_params(enemy_density=density), False)
    return surrogate


def test_predictive_variance_splits_exactly_into_epistemic_and_noise():
    surrogate = _noisy_surrogate()
    assert surrogate.noise_level > 0.0
    for density in (0.1, 0.3, 0.45):
        prob, total, epistemic = surrogate.predict_detailed(make_params(enemy_density=density))
        assert epistemic <= total
        assert abs(total ** 2 - (epistemic ** 2 + surrogate.noise_level)) < 1e-6


def test_epistemic_uncertainty_is_small_where_the_player_has_played_a_lot():
    # Noisy outcomes keep the total std high everywhere they were observed;
    # the epistemic part is what more data would remove, so it is lower there.
    surrogate = _noisy_surrogate()
    _, total, epistemic = surrogate.predict_detailed(make_params(enemy_density=0.1))
    assert epistemic < total


def test_window_fits_only_the_most_recent_outcomes():
    surrogate = Surrogate(min_labels=2, window=4)
    for _ in range(10):
        surrogate.add_example(make_params(enemy_density=0.1), True)
    # Old data are all passes; the four newest include a fail, so a model exists.
    for passed in (True, False, True, False):
        surrogate.add_example(make_params(enemy_density=0.4), passed)
    assert surrogate.is_ready() is True
    assert len(surrogate.y_train) == 14  # every outcome is kept for counting
    assert surrogate._model.X_train_.shape[0] == 4


def test_window_with_a_single_recent_class_has_no_model():
    surrogate = Surrogate(min_labels=2, window=3)
    surrogate.add_example(make_params(enemy_density=0.1), False)
    for _ in range(3):
        surrogate.add_example(make_params(enemy_density=0.1), True)
    assert surrogate.is_ready() is False


def test_saved_training_data_from_a_different_genome_layout_is_discarded():
    surrogate = Surrogate(min_labels=2)
    # Six features: saved by the original (two genes, four archetypes) layout.
    surrogate.load_state({"x_train": [[0.1] * 6, [0.2] * 6], "y_train": [1.0, 0.0]})
    assert surrogate.x_train == [] and surrogate.y_train == []
    assert surrogate.is_ready() is False


def test_feature_vector_follows_the_genome_space():
    import genome
    params = genome.make_default_genome()["params"]
    assert len(genome_to_features(params, genome.FULL_SPACE)) == 12
    assert len(genome_to_features(params, genome.LEGACY_SPACE)) == 6


def test_ard_kernel_learns_one_length_scale_per_gene():
    import random
    import genome
    random.seed(0)
    surrogate = Surrogate(min_labels=2, kernel="ard")
    for i in range(12):
        g = genome.make_random_genome()
        surrogate.add_example(g["params"], passed=g["params"]["enemy_density"] < 0.25)
    length_scales = surrogate._model.kernel_.k1.k2.length_scale
    assert len(length_scales) == genome.FULL_SPACE.dimension


# --- GP classification -----------------------------------------------------


def trained_classifier(n=30):
    surrogate = Surrogate(min_labels=5, model="classifier")
    for i in range(n):
        density = 0.5 * i / (n - 1)
        surrogate.add_example(make_params(enemy_density=density), density < 0.25)
    return surrogate


def test_classifier_predicts_pass_where_the_player_passed_and_fail_where_they_failed():
    surrogate = trained_classifier()
    assert surrogate.is_ready()
    easy, _, _ = surrogate.predict_detailed(make_params(enemy_density=0.05))
    hard, _, _ = surrogate.predict_detailed(make_params(enemy_density=0.45))
    assert easy > 0.6 and hard < 0.4


def test_classifier_uncertainty_is_a_valid_split():
    # Total std is that of a Bernoulli outcome; the epistemic part (the std
    # of the pass probability itself) can never exceed it.
    surrogate = trained_classifier()
    for density in (0.05, 0.25, 0.45):
        prob, total, epistemic = surrogate.predict_detailed(make_params(enemy_density=density))
        assert abs(total - (prob * (1 - prob)) ** 0.5) < 1e-9
        assert 0.0 <= epistemic <= total + 1e-9
    assert surrogate.noise_level == 0.0


def test_classifier_probability_matches_scikit_learn():
    import numpy as np
    surrogate = trained_classifier()
    params = [make_params(enemy_density=d) for d in (0.1, 0.2, 0.3)]
    ours = surrogate.predict_batch(params)[0]
    theirs = surrogate._model.predict_proba(np.array([genome_to_features(p) for p in params]))[:, 1]
    # Exact quadrature of E[sigmoid(f)] vs scikit-learn's probit approximation of it.
    assert np.max(np.abs(ours - theirs)) < 0.05


def test_unknown_model_is_rejected():
    import pytest
    with pytest.raises(ValueError):
        Surrogate(min_labels=2, model="forest")
