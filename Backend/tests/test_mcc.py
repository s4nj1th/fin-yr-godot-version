import pytest

import config
from mcc import MCCManager


@pytest.fixture(autouse=True)
def isolated_state(tmp_path, monkeypatch):
    """Every test gets its own state/log files so tests never touch the real
    Backend/data/ directory or leak state between tests."""
    monkeypatch.setattr(config, "STATE_FILE", str(tmp_path / "mcc_state.json"))
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
        passed, reason = MCCManager._evaluate_minimal_criterion(make_telemetry())
        assert passed is True
        assert reason is None

    def test_manual_quit_fails_as_quit_early_even_if_exit_reached_is_ambiguous(self):
        # quit_early must win regardless of exit_reached — a deliberate R-key
        # regenerate is a distinct signal from "played it out and failed".
        passed, reason = MCCManager._evaluate_minimal_criterion(
            make_telemetry(exit_reached=False, quit_early=True)
        )
        assert passed is False
        assert reason == "quit_early"

    def test_timeout_without_reaching_exit_is_too_slow_abandoned_not_quit_early(self):
        # This is the bug the original ordering had: exit_reached=False was
        # checked unconditionally first, so a passive timeout (quit_early=False)
        # was indistinguishable from anything else. It must resolve here to
        # "too_slow_abandoned", not "quit_early" and not a generic catch-all.
        passed, reason = MCCManager._evaluate_minimal_criterion(
            make_telemetry(
                exit_reached=False,
                quit_early=False,
                time_taken_sec=config.MAX_TRIAL_SECONDS + 1,
            )
        )
        assert passed is False
        assert reason == "too_slow_abandoned"

    def test_never_reaching_exit_before_timeout_is_did_not_reach_exit(self):
        passed, reason = MCCManager._evaluate_minimal_criterion(
            make_telemetry(
                exit_reached=False,
                quit_early=False,
                time_taken_sec=config.MAX_TRIAL_SECONDS - 1,
            )
        )
        assert passed is False
        assert reason == "did_not_reach_exit"

    def test_reaching_exit_too_fast_is_trivial(self):
        passed, reason = MCCManager._evaluate_minimal_criterion(
            make_telemetry(time_taken_sec=config.MIN_TRIAL_SECONDS - 1)
        )
        assert passed is False
        assert reason == "too_fast_trivial"

    def test_reaching_exit_with_too_many_deaths_fails(self):
        passed, reason = MCCManager._evaluate_minimal_criterion(
            make_telemetry(deaths_retries=config.MAX_DEATHS_ALLOWED + 1)
        )
        assert passed is False
        assert reason == "too_many_deaths"

    def test_deaths_at_exactly_the_allowed_limit_still_passes(self):
        passed, reason = MCCManager._evaluate_minimal_criterion(
            make_telemetry(deaths_retries=config.MAX_DEATHS_ALLOWED)
        )
        assert passed is True
        assert reason is None


# --- MCCManager end-to-end candidate/report_outcome flow -------------------


class TestMCCManagerFlow:
    def test_first_candidate_is_the_default_genome_when_archive_is_empty(self):
        manager = MCCManager()
        candidate, surrogate_estimate = manager.get_next_candidate()
        assert candidate["generation"] == 0
        assert surrogate_estimate is None
        assert candidate["id"] in manager.pending

    def test_passing_outcome_grows_the_hall_of_fame(self):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()

        passed, reason = manager.report_outcome(candidate["id"], make_telemetry())

        assert passed is True
        assert reason is None
        assert manager.hall_of_fame_size == 1
        assert candidate["id"] not in manager.pending

    def test_failing_outcome_does_not_grow_the_hall_of_fame(self):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()

        passed, reason = manager.report_outcome(
            candidate["id"], make_telemetry(exit_reached=False, quit_early=True)
        )

        assert passed is False
        assert reason == "quit_early"
        assert manager.hall_of_fame_size == 0

    def test_unknown_genome_id_is_reported_without_crashing(self):
        manager = MCCManager()
        passed, reason = manager.report_outcome("not-a-real-id", make_telemetry())
        assert passed is False
        assert reason == "unknown_genome_id"

    def test_reporting_the_same_genome_id_twice_only_counts_once(self):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()

        manager.report_outcome(candidate["id"], make_telemetry())
        passed, reason = manager.report_outcome(candidate["id"], make_telemetry())

        assert passed is False
        assert reason == "unknown_genome_id"
        assert manager.hall_of_fame_size == 1

    def test_state_persists_across_manager_instances(self, tmp_path):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()
        manager.report_outcome(candidate["id"], make_telemetry())

        reloaded = MCCManager()
        assert reloaded.hall_of_fame_size == 1


class TestEvaluatorTypeTagging:
    """See docs/EVOLUTIONARY_DESIGN.md 'Scripted bot evaluator' — bot trials
    go through the exact same pass/fail path as human ones, just labeled."""

    def test_defaults_to_human_and_grows_the_hall_of_fame_the_same_way(self):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()

        passed, _ = manager.report_outcome(candidate["id"], make_telemetry())

        assert passed is True
        assert manager.hall_of_fame[0]["evaluator_type"] == "human"
        assert manager.stats["human_evaluations"] == 1
        assert manager.stats["bot_evaluations"] == 0

    def test_bot_trials_are_tagged_and_counted_separately(self):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()

        passed, _ = manager.report_outcome(candidate["id"], make_telemetry(), evaluator_type="bot")

        assert passed is True
        assert manager.hall_of_fame[0]["evaluator_type"] == "bot"
        assert manager.stats["human_evaluations"] == 0
        assert manager.stats["bot_evaluations"] == 1
        # A bot trial is still a real evaluation, not a surrogate guess.
        assert manager.hall_of_fame[0]["decision_source"] == "human"

    def test_human_and_bot_counts_together_equal_total_real_evaluations(self):
        manager = MCCManager()

        candidate_a, _ = manager.get_next_candidate()
        manager.report_outcome(candidate_a["id"], make_telemetry(), evaluator_type="human")
        candidate_b, _ = manager.get_next_candidate()
        manager.report_outcome(candidate_b["id"], make_telemetry(exit_reached=False, quit_early=True), evaluator_type="bot")

        stats = manager.stats
        assert stats["human_evaluations"] + stats["bot_evaluations"] == stats["real_evaluations"] == 2

    def test_evaluator_type_survives_state_reload(self, tmp_path):
        manager = MCCManager()
        candidate, _ = manager.get_next_candidate()
        manager.report_outcome(candidate["id"], make_telemetry(), evaluator_type="bot")

        reloaded = MCCManager()
        assert reloaded.stats["bot_evaluations"] == 1
        assert reloaded.hall_of_fame[0]["evaluator_type"] == "bot"
