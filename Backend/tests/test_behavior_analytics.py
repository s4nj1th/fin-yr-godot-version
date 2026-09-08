import json

import behavior_analytics as ba


def make_event(generation, breakdown, event="report_outcome", params=None, evaluator_type=None):
    record = {"event": event, "generation": generation, "telemetry": {"enemy_behavior_breakdown": breakdown}}
    if params is not None:
        record["params"] = params
    if evaluator_type is not None:
        record["evaluator_type"] = evaluator_type
    return record


def counters(spawned=0, attacks_on_player=0, damage_dealt_to_player=0, hits_taken_from_player=0,
             damage_taken_from_player=0, rear_hits_taken=0, killed_by_player=0):
    return {
        "spawned": spawned,
        "attacks_on_player": attacks_on_player,
        "damage_dealt_to_player": damage_dealt_to_player,
        "hits_taken_from_player": hits_taken_from_player,
        "damage_taken_from_player": damage_taken_from_player,
        "rear_hits_taken": rear_hits_taken,
        "killed_by_player": killed_by_player,
    }


class TestLoadReportEvents:
    def test_reads_only_report_outcome_events(self, tmp_path):
        log_path = tmp_path / "session_log.jsonl"
        lines = [
            json.dumps({"event": "report_outcome", "generation": 1, "telemetry": {}}),
            json.dumps({"event": "auto_decide", "generation": 1}),
            json.dumps({"event": "unknown_genome_id"}),
        ]
        log_path.write_text("\n".join(lines) + "\n", encoding="utf-8")

        events = ba.load_report_events(str(log_path))

        assert len(events) == 1
        assert events[0]["event"] == "report_outcome"

    def test_missing_file_returns_empty_list(self, tmp_path):
        assert ba.load_report_events(str(tmp_path / "does_not_exist.jsonl")) == []

    def test_skips_malformed_lines(self, tmp_path):
        log_path = tmp_path / "session_log.jsonl"
        log_path.write_text("not json\n" + json.dumps({"event": "report_outcome", "telemetry": {}}) + "\n",
                             encoding="utf-8")

        events = ba.load_report_events(str(log_path))

        assert len(events) == 1


class TestEventsWithBehaviorBreakdown:
    def test_skips_trials_without_the_new_field_rather_than_zeroing_them(self):
        old_event = make_event(0, {})
        new_event = make_event(1, {"melee_chaser": {"baseline": counters(spawned=1), "adapted": counters()}})

        result = ba.events_with_behavior_breakdown([old_event, new_event])

        assert result == [new_event]


class TestAggregateByArchetype:
    def test_sums_counters_across_trials_per_archetype_and_situation(self):
        events = [
            make_event(1, {
                "melee_chaser": {
                    "baseline": counters(spawned=3, damage_dealt_to_player=10),
                    "adapted": counters(spawned=1, damage_dealt_to_player=4),
                }
            }),
            make_event(2, {
                "melee_chaser": {
                    "baseline": counters(spawned=2, damage_dealt_to_player=6),
                    "adapted": counters(),
                }
            }),
        ]

        totals = ba.aggregate_by_archetype(events)

        assert totals["melee_chaser"]["baseline"]["spawned"] == 5
        assert totals["melee_chaser"]["baseline"]["damage_dealt_to_player"] == 16
        assert totals["melee_chaser"]["adapted"]["spawned"] == 1
        assert totals["melee_chaser"]["adapted"]["damage_dealt_to_player"] == 4

    def test_archetypes_absent_from_a_trial_are_simply_not_double_counted(self):
        events = [
            make_event(1, {"melee_chaser": {"baseline": counters(spawned=1), "adapted": counters()}}),
            make_event(2, {"ranged_kite": {"baseline": counters(spawned=1), "adapted": counters()}}),
        ]

        totals = ba.aggregate_by_archetype(events)

        assert set(totals.keys()) == {"melee_chaser", "ranged_kite"}
        assert totals["melee_chaser"]["baseline"]["spawned"] == 1
        assert totals["ranged_kite"]["baseline"]["spawned"] == 1


class TestDerivedMetrics:
    def test_ratios_are_guarded_against_zero_spawns(self):
        profiles = ba.archetype_situation_profiles([make_event(0, {
            "ranged_strafe": {"baseline": counters(), "adapted": counters()}
        })])

        metrics = profiles["ranged_strafe"]["baseline"]
        assert metrics["damage_per_spawn"] == 0.0
        assert metrics["kill_rate"] == 0.0
        assert metrics["rear_hit_rate"] == 0.0

    def test_damage_per_spawn_and_kill_rate(self):
        profiles = ba.archetype_situation_profiles([make_event(0, {
            "melee_hit_run": {
                "baseline": counters(spawned=4, damage_dealt_to_player=40, killed_by_player=1),
                "adapted": counters(),
            }
        })])

        metrics = profiles["melee_hit_run"]["baseline"]
        assert metrics["damage_per_spawn"] == 10.0
        assert metrics["kill_rate"] == 0.25


class TestArchetypeOverallProfiles:
    def test_collapses_baseline_and_adapted_into_one_profile(self):
        events = [make_event(0, {
            "ranged_kite": {
                "baseline": counters(spawned=2, damage_dealt_to_player=10),
                "adapted": counters(spawned=2, damage_dealt_to_player=30),
            }
        })]

        overall = ba.archetype_overall_profiles(events)

        # (10 + 30) damage over (2 + 2) spawns = 10 per spawn
        assert overall["ranged_kite"]["damage_per_spawn"] == 10.0


class TestGenerationSeries:
    def test_groups_and_sorts_by_generation(self):
        events = [
            make_event(3, {"melee_chaser": {"baseline": counters(spawned=1), "adapted": counters()}}),
            make_event(1, {"melee_chaser": {"baseline": counters(spawned=2), "adapted": counters()}}),
        ]

        series = ba.generation_series(events)

        assert [generation for generation, _ in series] == [1, 3]

    def test_merges_multiple_trials_sharing_a_generation(self):
        events = [
            make_event(1, {"melee_chaser": {"baseline": counters(spawned=1), "adapted": counters()}}),
            make_event(1, {"melee_chaser": {"baseline": counters(spawned=1), "adapted": counters()}}),
        ]

        series = ba.generation_series(events)

        assert len(series) == 1
        generation, profiles = series[0]
        assert generation == 1
        assert profiles["melee_chaser"]["damage_per_spawn"] == 0.0


class TestGenomeMixSeries:
    def test_works_without_any_behavior_breakdown_data(self):
        events = [
            make_event(0, {}, params={"enemy_behavior_mix": {"melee_chaser": 0.5, "ranged_kite": 0.5}}),
            make_event(1, {}, params={"enemy_behavior_mix": {"melee_chaser": 0.3, "ranged_kite": 0.7}}),
        ]

        series = ba.genome_mix_series(events)

        assert [g for g, _ in series] == [0, 1]
        assert series[0][1]["melee_chaser"] == 0.5
        assert series[1][1]["melee_chaser"] == 0.3

    def test_averages_multiple_trials_sharing_a_generation(self):
        events = [
            make_event(2, {}, params={"enemy_behavior_mix": {"melee_chaser": 0.2}}),
            make_event(2, {}, params={"enemy_behavior_mix": {"melee_chaser": 0.6}}),
        ]

        series = ba.genome_mix_series(events)

        assert series == [(2, {"melee_chaser": 0.4})]

    def test_events_without_params_are_skipped(self):
        events = [make_event(0, {})]

        assert ba.genome_mix_series(events) == []


class TestFilterByEvaluatorType:
    def test_all_returns_every_event_unfiltered(self):
        events = [
            make_event(0, {}, evaluator_type="human"),
            make_event(0, {}, evaluator_type="bot"),
        ]

        assert ba.filter_by_evaluator_type(events, "all") == events

    def test_splits_human_and_bot(self):
        human_event = make_event(0, {}, evaluator_type="human")
        bot_event = make_event(0, {}, evaluator_type="bot")

        assert ba.filter_by_evaluator_type([human_event, bot_event], "human") == [human_event]
        assert ba.filter_by_evaluator_type([human_event, bot_event], "bot") == [bot_event]

    def test_events_without_the_field_default_to_human(self):
        # Every trial logged before the scripted bot evaluator existed has
        # no evaluator_type at all — it must still show up as human, not
        # silently vanish from the human-only view.
        legacy_event = make_event(0, {})

        assert ba.filter_by_evaluator_type([legacy_event], "human") == [legacy_event]
        assert ba.filter_by_evaluator_type([legacy_event], "bot") == []


class TestCountByEvaluatorType:
    def test_counts_each_type_and_defaults_missing_to_human(self):
        events = [
            make_event(0, {}, evaluator_type="human"),
            make_event(0, {}, evaluator_type="bot"),
            make_event(0, {}, evaluator_type="bot"),
            make_event(0, {}),  # legacy, no field
        ]

        assert ba.count_by_evaluator_type(events) == {"human": 2, "bot": 2}
