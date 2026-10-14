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


class TestFindIqrOutliers:
    def test_returns_empty_below_four_samples(self):
        assert ba.find_iqr_outliers([1.0, 2.0, 100.0]) == []

    def test_flags_a_clear_high_outlier(self):
        values = [10.0, 11.0, 9.0, 10.5, 9.5, 100.0]
        assert ba.find_iqr_outliers(values) == [5]

    def test_no_outliers_in_a_tight_cluster(self):
        values = [10.0, 10.5, 11.0, 9.5, 10.2, 9.8]
        assert ba.find_iqr_outliers(values) == []


class TestArchetypeStatisticalOutliers:
    def test_flags_the_archetype_with_a_wild_trial_and_omits_the_rest(self):
        events = [
            make_event(0, {
                "melee_chaser": {"baseline": counters(spawned=5, damage_dealt_to_player=25), "adapted": counters()},
                "ranged_kite": {"baseline": counters(spawned=5, damage_dealt_to_player=25), "adapted": counters()},
            }),
            make_event(1, {
                "melee_chaser": {"baseline": counters(spawned=5, damage_dealt_to_player=27), "adapted": counters()},
                "ranged_kite": {"baseline": counters(spawned=5, damage_dealt_to_player=26), "adapted": counters()},
            }),
            make_event(2, {
                "melee_chaser": {"baseline": counters(spawned=5, damage_dealt_to_player=24), "adapted": counters()},
                "ranged_kite": {"baseline": counters(spawned=5, damage_dealt_to_player=25), "adapted": counters()},
            }),
            make_event(3, {
                # melee_chaser stays consistent; ranged_kite spikes wildly.
                "melee_chaser": {"baseline": counters(spawned=5, damage_dealt_to_player=26), "adapted": counters()},
                "ranged_kite": {"baseline": counters(spawned=5, damage_dealt_to_player=500), "adapted": counters()},
            }),
        ]

        outliers = ba.archetype_statistical_outliers(events, "damage_per_spawn")

        assert "melee_chaser" not in outliers
        assert outliers["ranged_kite"] == [100.0]


class TestFlagThresholdExceeding:
    def test_flags_high_enemy_count(self):
        events = [make_event(0, {}, params=None)]
        events[0]["telemetry"]["total_enemies"] = 90

        flagged = ba.flag_threshold_exceeding(events)

        assert len(flagged) == 1
        assert any("high_enemy_count" in r for r in flagged[0]["reasons"])

    def test_flags_near_max_deaths(self):
        events = [make_event(0, {})]
        events[0]["telemetry"]["deaths_retries"] = 2  # config.MAX_DEATHS_ALLOWED - 1

        flagged = ba.flag_threshold_exceeding(events)

        assert any("near_max_deaths" in r for r in flagged[0]["reasons"])

    def test_flags_archetype_high_damage_per_spawn(self):
        events = [make_event(0, {
            "ranged_strafe": {"baseline": counters(spawned=2, damage_dealt_to_player=100), "adapted": counters()},
        })]

        flagged = ba.flag_threshold_exceeding(events)

        assert len(flagged) == 1
        assert any("ranged_strafe: high_damage_per_spawn" in r for r in flagged[0]["reasons"])

    def test_clean_trial_is_not_flagged(self):
        events = [make_event(0, {
            "melee_chaser": {"baseline": counters(spawned=5, damage_dealt_to_player=20, killed_by_player=2,
                                                    hits_taken_from_player=4, rear_hits_taken=1),
                              "adapted": counters()},
        })]
        events[0]["telemetry"]["total_enemies"] = 10
        events[0]["telemetry"]["deaths_retries"] = 0

        assert ba.flag_threshold_exceeding(events) == []

    def test_custom_thresholds_override_config_defaults(self):
        events = [make_event(0, {})]
        events[0]["telemetry"]["total_enemies"] = 5

        assert ba.flag_threshold_exceeding(events, thresholds={"high_enemy_count": 3}) != []
        assert ba.flag_threshold_exceeding(events, thresholds={"high_enemy_count": 10}) == []


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


class TestArchetypeSituationShift:
    def test_computes_delta_and_percent_change(self):
        events = [make_event(0, {
            "melee_chaser": {
                "baseline": counters(spawned=4, damage_dealt_to_player=8),   # 2.0 dps
                "adapted": counters(spawned=4, damage_dealt_to_player=16),  # 4.0 dps
            }
        })]

        shifts = ba.archetype_situation_shift(events, "damage_per_spawn")

        assert "melee_chaser" in shifts
        rec = shifts["melee_chaser"]
        assert rec["baseline"] == 2.0
        assert rec["adapted"] == 4.0
        assert rec["delta"] == 2.0
        assert rec["percent_change"] == 1.0  # 100% increase

    def test_baseline_zero_returns_inf_if_adapted_nonzero(self):
        events = [make_event(0, {
            "ranged_kite": {
                "baseline": counters(spawned=0, damage_dealt_to_player=0),
                "adapted": counters(spawned=2, damage_dealt_to_player=10),  # 5.0 dps
            }
        })]

        shifts = ba.archetype_situation_shift(events, "damage_per_spawn")

        rec = shifts["ranged_kite"]
        assert rec["baseline"] == 0.0
        assert rec["adapted"] == 5.0
        assert rec["delta"] == 5.0
        assert rec["percent_change"] == float("inf")

    def test_no_change_returns_zero_delta_and_pct(self):
        events = [make_event(0, {
            "ranged_strafe": {
                "baseline": counters(spawned=5, damage_dealt_to_player=10),
                "adapted": counters(spawned=5, damage_dealt_to_player=10),
            }
        })]

        shifts = ba.archetype_situation_shift(events, "damage_per_spawn")

        rec = shifts["ranged_strafe"]
        assert rec["delta"] == 0.0
        assert rec["percent_change"] == 0.0


class TestFlagSituationalShift:
    def test_flags_shifts_exceeding_threshold(self):
        events = [make_event(0, {
            "melee_chaser": {
                "baseline": counters(spawned=4, damage_dealt_to_player=8),   # 2.0 dps
                "adapted": counters(spawned=4, damage_dealt_to_player=16),  # 4.0 dps (+100%)
            },
            "ranged_kite": {
                "baseline": counters(spawned=10, damage_dealt_to_player=10),  # 1.0 dps
                "adapted": counters(spawned=10, damage_dealt_to_player=11),  # 1.1 dps (+10%)
            }
        })]

        flagged = ba.flag_situational_shift(events, metrics=["damage_per_spawn"], threshold_pct=0.5)

        assert len(flagged) == 1
        assert flagged[0]["archetype"] == "melee_chaser"
        assert flagged[0]["metric"] == "damage_per_spawn"
        assert flagged[0]["baseline"] == 2.0
        assert flagged[0]["adapted"] == 4.0
        assert flagged[0]["delta"] == 2.0
        assert flagged[0]["percent_change"] == 1.0

    def test_baseline_zero_adapted_nonzero_always_flagged(self):
        events = [make_event(0, {
            "ranged_kite": {
                "baseline": counters(spawned=0),
                "adapted": counters(spawned=2, damage_dealt_to_player=10),
            }
        })]

        flagged = ba.flag_situational_shift(events, metrics=["damage_per_spawn"], threshold_pct=0.5)

        assert len(flagged) == 1
        assert flagged[0]["archetype"] == "ranged_kite"
        assert flagged[0]["percent_change"] == float("inf")



class TestFilterByPlayer:
    def test_keeps_only_the_requested_player(self):
        events = [{"player_id": "alice"}, {"player_id": "bot"}, {"player_id": "alice"}]
        assert ba.filter_by_player(events, "alice") == [{"player_id": "alice"}, {"player_id": "alice"}]

    def test_records_without_a_player_belong_to_the_default_player(self):
        events = [{"evaluator_type": "human"}, {"player_id": "bot"}]
        assert ba.filter_by_player(events, "default") == [{"evaluator_type": "human"}]

    def test_all_keeps_everything(self):
        events = [{"player_id": "alice"}, {}]
        assert ba.filter_by_player(events, "all") == events
