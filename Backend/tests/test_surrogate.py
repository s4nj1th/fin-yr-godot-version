import genome
from surrogate import Surrogate


def make_params(**overrides):
    params = dict(genome.DEFAULT_GENOME_PARAMS)
    params.update(overrides)
    return params


def test_not_ready_before_min_labels_reached():
    surrogate = Surrogate(min_labels=3)
    surrogate.add_example(make_params(), True)
    surrogate.add_example(make_params(map_width=100), False)
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
    surrogate.add_example(make_params(map_width=80), True)
    surrogate.add_example(make_params(map_width=200), True)
    assert surrogate.is_ready() is False


def test_becomes_ready_once_both_classes_seen_past_min_labels():
    surrogate = Surrogate(min_labels=2)
    surrogate.add_example(make_params(map_width=80), True)
    surrogate.add_example(make_params(map_width=200), False)
    assert surrogate.is_ready() is True

    prob, std = surrogate.predict(make_params(map_width=140))
    assert 0.0 <= prob <= 1.0
    assert std >= 0.0


def test_state_round_trips_through_state_dict():
    surrogate = Surrogate(min_labels=2)
    surrogate.add_example(make_params(map_width=80), True)
    surrogate.add_example(make_params(map_width=200), False)

    restored = Surrogate(min_labels=2)
    restored.load_state(surrogate.state_dict())

    assert restored.is_ready() is True
    assert restored.x_train == surrogate.x_train
    assert restored.y_train == surrogate.y_train
