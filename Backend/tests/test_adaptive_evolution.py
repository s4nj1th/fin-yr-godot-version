import json

import pytest

import config
import genome as genome_lib
from adaptive_evolution import AdaptiveEvolutionManager, PlayerRegistry


@pytest.fixture(autouse=True)
def isolated_state(tmp_path, monkeypatch):
    """Every test gets its own state/log files so tests never touch the real
    Backend/data/ directory or leak state between tests."""
    monkeypatch.setattr(config, "PLAYERS_DIR", str(tmp_path / "players"))
    monkeypatch.setattr(config, "SESSION_LOG_FILE", str(tmp_path / "session_log.jsonl"))


def make_telemetry(**overrides):
    base = {
        "time_taken_sec": 60.0,
        "damage_taken": 10,
        "health_remaining": 80,
        "deaths_retries": 0,
        "enemies_killed": 2,
        "total_enemies": 5,
        "chests_opened": 1,
        "total_chests": 2,
        "traps_triggered": 0,
        "exploration_percent": 55.0,
        "exit_reached": True,
        "quit_early": False,
    }
    base.update(overrides)
    return base


# --- Minimal criterion: the three real client-side trial endings ----------


class TestMinimalCriterion:
    def test_reaching_exit_with_reasonable_time_and_deaths_passes(self):
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(make_telemetry())
        assert passed is True
        assert reason is None

    def test_manual_quit_fails_as_quit_early_even_if_exit_reached_is_ambiguous(self):
        # quit_early must win regardless of exit_reached — a deliberate R-key
        # regenerate is a distinct signal from "played it out and failed".
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(
            make_telemetry(exit_reached=False, quit_early=True)
        )
        assert passed is False
        assert reason == "quit_early"

    def test_timeout_without_reaching_exit_is_too_slow_abandoned_not_quit_early(self):
        # This is the bug the original ordering had: exit_reached=False was
        # checked unconditionally first, so a passive timeout (quit_early=False)
        # was indistinguishable from anything else. It must resolve here to
        # "too_slow_abandoned", not "quit_early" and not a generic catch-all.
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(
            make_telemetry(
                exit_reached=False,
                quit_early=False,
                time_taken_sec=config.MAX_TRIAL_SECONDS + 1,
            )
        )
        assert passed is False
        assert reason == "too_slow_abandoned"

    def test_never_reaching_exit_before_timeout_is_did_not_reach_exit(self):
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(
            make_telemetry(
                exit_reached=False,
                quit_early=False,
                time_taken_sec=config.MAX_TRIAL_SECONDS - 1,
            )
        )
        assert passed is False
        assert reason == "did_not_reach_exit"

    def test_reaching_exit_too_fast_is_trivial(self):
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(
            make_telemetry(time_taken_sec=config.MIN_TRIAL_SECONDS - 1)
        )
        assert passed is False
        assert reason == "too_fast_trivial"

    def test_reaching_exit_with_too_many_deaths_fails(self):
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(
            make_telemetry(deaths_retries=config.MAX_DEATHS_ALLOWED + 1)
        )
        assert passed is False
        assert reason == "too_many_deaths"

    def test_deaths_at_exactly_the_allowed_limit_still_passes(self):
        passed, reason = AdaptiveEvolutionManager._evaluate_minimal_criterion(
            make_telemetry(deaths_retries=config.MAX_DEATHS_ALLOWED)
        )
        assert passed is True
        assert reason is None


# --- AdaptiveEvolutionManager end-to-end candidate/report_outcome flow -------------------


class TestAdaptiveEvolutionManagerFlow:
    def test_first_candidate_is_the_default_genome_with_the_default_bootstrap(self):
        manager = AdaptiveEvolutionManager(bootstrap="default")
        candidate, surrogate_estimate = manager.get_next_candidate()
        assert candidate["generation"] == 0
        assert surrogate_estimate is None
        assert candidate["id"] in manager.pending

    def test_passing_outcome_grows_the_hall_of_fame(self):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()

        passed, reason = manager.report_outcome(candidate["id"], make_telemetry())

        assert passed is True
        assert reason is None
        assert manager.hall_of_fame_size == 1
        assert candidate["id"] not in manager.pending

    def test_failing_outcome_does_not_grow_the_hall_of_fame(self):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()

        passed, reason = manager.report_outcome(
            candidate["id"], make_telemetry(exit_reached=False, quit_early=True)
        )

        assert passed is False
        assert reason == "quit_early"
        assert manager.hall_of_fame_size == 0

    def test_unknown_genome_id_is_reported_without_crashing(self):
        manager = AdaptiveEvolutionManager()
        passed, reason = manager.report_outcome("not-a-real-id", make_telemetry())
        assert passed is False
        assert reason == "unknown_genome_id"

    def test_reporting_the_same_genome_id_twice_only_counts_once(self):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()

        manager.report_outcome(candidate["id"], make_telemetry())
        passed, reason = manager.report_outcome(candidate["id"], make_telemetry())

        assert passed is False
        assert reason == "unknown_genome_id"
        assert manager.hall_of_fame_size == 1

    def test_state_persists_across_manager_instances(self, tmp_path):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()
        manager.report_outcome(candidate["id"], make_telemetry())

        reloaded = AdaptiveEvolutionManager()
        assert reloaded.hall_of_fame_size == 1


class TestEvaluatorTypeTagging:
    """See docs/EVOLUTIONARY_DESIGN.md 'Scripted bot evaluator' — bot trials
    go through the exact same pass/fail path as human ones, just labeled."""

    def test_defaults_to_human_and_grows_the_hall_of_fame_the_same_way(self):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()

        passed, _ = manager.report_outcome(candidate["id"], make_telemetry())

        assert passed is True
        assert manager.hall_of_fame[0]["evaluator_type"] == "human"
        assert manager.stats["human_evaluations"] == 1
        assert manager.stats["bot_evaluations"] == 0

    def test_bot_trials_are_tagged_and_counted_separately(self):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()

        passed, _ = manager.report_outcome(candidate["id"], make_telemetry(), evaluator_type="bot")

        assert passed is True
        assert manager.hall_of_fame[0]["evaluator_type"] == "bot"
        assert manager.stats["human_evaluations"] == 0
        assert manager.stats["bot_evaluations"] == 1
        # A bot trial is still a real evaluation, not a surrogate guess.
        assert manager.hall_of_fame[0]["decision_source"] == "human"

    def test_human_and_bot_counts_together_equal_total_real_evaluations(self):
        manager = AdaptiveEvolutionManager()

        candidate_a, _ = manager.get_next_candidate()
        manager.report_outcome(candidate_a["id"], make_telemetry(), evaluator_type="human")
        candidate_b, _ = manager.get_next_candidate()
        manager.report_outcome(candidate_b["id"], make_telemetry(exit_reached=False, quit_early=True), evaluator_type="bot")

        stats = manager.stats
        assert stats["human_evaluations"] + stats["bot_evaluations"] == stats["real_evaluations"] == 2

    def test_evaluator_type_survives_state_reload(self, tmp_path):
        manager = AdaptiveEvolutionManager()
        candidate, _ = manager.get_next_candidate()
        manager.report_outcome(candidate["id"], make_telemetry(), evaluator_type="bot")

        reloaded = AdaptiveEvolutionManager()
        assert reloaded.stats["bot_evaluations"] == 1
        assert reloaded.hall_of_fame[0]["evaluator_type"] == "bot"


# --- Cold start -------------------------------------------------------------


class TestBootstrap:
    def test_random_bootstrap_serves_fresh_random_designs_until_one_passes(self):
        manager = AdaptiveEvolutionManager(bootstrap="random")
        first, _ = manager.get_next_candidate()
        manager.report_outcome(first["id"], make_telemetry(exit_reached=False))
        second, _ = manager.get_next_candidate()

        assert first["mutation_source"] == second["mutation_source"] == "bootstrap"
        assert first["params"] != second["params"]
        assert manager.stats["bootstrap_designs_served"] == 2

    def test_after_the_first_pass_designs_are_mutated_from_the_archive(self):
        manager = AdaptiveEvolutionManager(bootstrap="random")
        first, _ = manager.get_next_candidate()
        manager.report_outcome(first["id"], make_telemetry())
        child, _ = manager.get_next_candidate()

        assert child["parent_id"] == first["id"]
        assert child["mutation_source"] == "random"

    def test_default_bootstrap_repeats_the_default_design(self):
        manager = AdaptiveEvolutionManager(bootstrap="default")
        first, _ = manager.get_next_candidate()
        manager.report_outcome(first["id"], make_telemetry(exit_reached=False))
        second, _ = manager.get_next_candidate()
        assert first["params"] == second["params"]

    def test_unknown_settings_are_rejected(self):
        with pytest.raises(ValueError):
            AdaptiveEvolutionManager(bootstrap="sometimes")
        with pytest.raises(ValueError):
            AdaptiveEvolutionManager(gate_uncertainty="vibes")


# --- Uncertainty gate -------------------------------------------------------


class FakeSurrogate:
    """Ready surrogate with fixed predictions, to test the gate in isolation."""

    def __init__(self, prob, total_std, epistemic_std):
        self.estimate = (prob, total_std, epistemic_std)
        self.y_train = []

    def is_ready(self):
        return True

    def predict_detailed(self, params):
        return self.estimate

    def add_example(self, params, passed):
        self.y_train.append(1.0 if passed else 0.0)

    noise_level = 0.0

    def state_dict(self):
        return {}


class TestUncertaintyGate:
    # Confident prediction, but noisy outcomes: total std is above the 0.25
    # threshold only because of outcome noise.
    NOISY_BUT_KNOWN = (0.9, 0.4, 0.1)

    def test_epistemic_gate_auto_decides_when_only_outcome_noise_is_high(self):
        manager = AdaptiveEvolutionManager(gate_uncertainty="epistemic")
        assert manager._needs_real_evaluation(*self.NOISY_BUT_KNOWN) is False

    def test_total_gate_plays_the_same_design(self):
        manager = AdaptiveEvolutionManager(gate_uncertainty="total")
        assert manager._needs_real_evaluation(*self.NOISY_BUT_KNOWN) is True

    def test_both_gates_play_a_design_whose_prediction_is_in_the_band(self):
        for gate in ("epistemic", "total"):
            manager = AdaptiveEvolutionManager(gate_uncertainty=gate)
            assert manager._needs_real_evaluation(0.5, 0.05, 0.05) is True

    def test_confident_designs_are_auto_accepted_without_a_playthrough(self):
        manager = AdaptiveEvolutionManager(gate_uncertainty="epistemic")
        seed, _ = manager.get_next_candidate()
        manager.report_outcome(seed["id"], make_telemetry())
        manager.surrogate = FakeSurrogate(*self.NOISY_BUT_KNOWN)

        candidate, estimate = manager.get_next_candidate()

        # Every attempt was auto-accepted, so the last one is handed out as a fallback.
        assert manager.auto_accepted_count == config.SURROGATE_MAX_PROPOSAL_ATTEMPTS
        assert manager.hall_of_fame_size == 1 + config.SURROGATE_MAX_PROPOSAL_ATTEMPTS
        assert estimate is None and candidate["id"] in manager.pending
        auto = [g for g in manager.hall_of_fame if g["decision_source"] == "surrogate"]
        assert auto[0]["surrogate_estimate"]["epistemic_std"] == 0.1

    def test_auto_decisions_never_train_the_surrogate(self):
        manager = AdaptiveEvolutionManager(gate_uncertainty="epistemic")
        seed, _ = manager.get_next_candidate()
        manager.report_outcome(seed["id"], make_telemetry())
        fake = FakeSurrogate(*self.NOISY_BUT_KNOWN)
        manager.surrogate = fake
        manager.get_next_candidate()
        assert fake.y_train == []


class TestParentFiltering:
    def test_only_designs_predicted_passable_are_used_as_parents(self):
        manager = AdaptiveEvolutionManager(filter_parents=True)
        easy = genome_lib.make_default_genome()
        easy["params"]["enemy_density"] = 0.05
        hard = genome_lib.make_default_genome()
        hard["params"]["enemy_density"] = 0.45
        manager.hall_of_fame = [easy, hard]

        class Predicts:
            def is_ready(self):
                return True

            def predict_mean(self, params_list):
                return [1.0 if p["enemy_density"] < 0.2 else 0.0 for p in params_list]

        manager.surrogate = Predicts()
        assert manager._parents() == [easy]

    def test_without_filtering_the_whole_archive_is_used(self):
        manager = AdaptiveEvolutionManager(filter_parents=False)
        manager.hall_of_fame = [genome_lib.make_default_genome(), genome_lib.make_default_genome()]
        assert manager._parents() == manager.hall_of_fame


# --- Players ---------------------------------------------------------------


class TestPlayers:
    def test_each_player_has_a_separate_archive_and_surrogate(self):
        alice = AdaptiveEvolutionManager("alice")
        candidate, _ = alice.get_next_candidate()
        alice.report_outcome(candidate["id"], make_telemetry())

        bob = AdaptiveEvolutionManager("bob")
        assert alice.hall_of_fame_size == 1
        assert bob.hall_of_fame_size == 0
        assert bob.surrogate.y_train == []

    def test_a_genome_cannot_be_reported_under_another_player(self):
        alice = AdaptiveEvolutionManager("alice")
        candidate, _ = alice.get_next_candidate()
        passed, reason = AdaptiveEvolutionManager("bob").report_outcome(candidate["id"], make_telemetry())
        assert reason == "unknown_genome_id"

    def test_player_state_persists_in_its_own_file(self, tmp_path):
        alice = AdaptiveEvolutionManager("alice")
        candidate, _ = alice.get_next_candidate()
        alice.report_outcome(candidate["id"], make_telemetry())

        assert (tmp_path / "players" / "alice.json").exists()
        assert AdaptiveEvolutionManager("alice").hall_of_fame_size == 1
        assert PlayerRegistry.known_players() == ["alice"]

    def test_log_records_carry_the_player_id(self, tmp_path):
        bot = AdaptiveEvolutionManager("bot")
        candidate, _ = bot.get_next_candidate()
        bot.report_outcome(candidate["id"], make_telemetry(), evaluator_type="bot")
        records = [json.loads(line) for line in (tmp_path / "session_log.jsonl").read_text().splitlines()]
        assert records and all(r["player_id"] == "bot" for r in records)

    def test_invalid_player_ids_are_rejected(self):
        for bad in ("", "../etc", "a b", "x" * 41):
            with pytest.raises(ValueError):
                AdaptiveEvolutionManager(bad)

    def test_in_memory_manager_writes_nothing(self, tmp_path):
        manager = AdaptiveEvolutionManager("ghost", persist=False)
        candidate, _ = manager.get_next_candidate()
        manager.report_outcome(candidate["id"], make_telemetry())
        assert not (tmp_path / "players").exists()
        assert not (tmp_path / "session_log.jsonl").exists()

    def test_registry_reuses_one_manager_per_player(self):
        registry = PlayerRegistry()
        assert registry.get("alice") is registry.get("alice")
        assert registry.get("alice") is not registry.get("bob")


class TestCandidatesUseTheFixedEnvironment:
    def test_every_handed_out_design_has_the_fixed_environment(self):
        manager = AdaptiveEvolutionManager()
        for i in range(10):
            candidate, _ = manager.get_next_candidate()
            for key, value in config.FIXED_ENVIRONMENT.items():
                assert candidate["params"][key] == value
            manager.report_outcome(candidate["id"], make_telemetry(exit_reached=(i % 2 == 0)))
