"""The human-study export and analysis tools, on a small fabricated data set
(a test fixture only — no real participant data exists)."""

import json
import os
import random
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import analyze_study  # noqa: E402
import export_study_data  # noqa: E402
import genome  # noqa: E402
import study  # noqa: E402


def write_jsonl(path, records):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        for r in records:
            f.write(json.dumps(r) + "\n")


def fake_study(data_dir, n_participants=6, kind="search"):
    rng = random.Random(0)
    random.seed(0)
    sessions, log, replays, answers = [], [], [], []
    for i in range(1, n_participants + 1):
        pid = f"P{i:02d}"
        for session in (1, 2):
            condition = study.condition_for(pid, session, kind)
            player = study.player_id_for(pid, condition)
            sessions.append({"participant_id": pid, "session_number": session, "study": kind, "condition": condition,
                             "player_id": player, "director": study.DIRECTOR_ON[condition]})
            for _ in range(20):
                passed = rng.random() < (0.7 if condition in ("MC", "DOFF") else 0.6)
                breakdown = {"melee_chaser": {"baseline": {"spawned": 3, "damage_dealt_to_player": 60},
                                              "adapted": {"spawned": 1, "damage_dealt_to_player": 40 if condition == "DON" else 0}}}
                log.append({"event": "report_outcome", "player_id": player, "passed": passed,
                            "fail_reason": None if passed else "too_many_deaths",
                            "telemetry": {"time_taken_sec": 60, "damage_taken": 100 + (20 if condition == "DON" else 0),
                                          "deaths_retries": 1, "enemy_behavior_breakdown": breakdown},
                            "params": genome.make_random_genome()["params"], "hall_of_fame_size": 0})
            for _ in range(30 if condition != "MC" else 0):
                log.append({"event": "auto_decide", "player_id": player, "passed": rng.random() < 0.8,
                            "surrogate_prob": 0.8, "surrogate_epistemic_std": 0.1,
                            "params": genome.make_random_genome()["params"]})
            for _ in range(4):
                replays.append({"player_id": player, "passed": rng.random() < 0.85, "decision_source": "human",
                                "params": genome.make_random_genome()["params"]})
            answers.append({"participant_id": pid, "session_number": session, "player_id": player,
                            "responses": {"enjoyed": rng.randint(3, 7), "fair": rng.randint(2, 7)}, "comment": ""})
    write_jsonl(os.path.join(data_dir, "study", "sessions.jsonl"), sessions)
    write_jsonl(os.path.join(data_dir, "study", "replays.jsonl"), replays)
    write_jsonl(os.path.join(data_dir, "study", "questionnaires.jsonl"), answers)
    log.append({"event": "report_outcome", "player_id": "bot", "passed": True, "params": {}, "telemetry": {}})
    write_jsonl(os.path.join(data_dir, "session_log.jsonl"), log)


def test_export_keeps_only_study_sessions(tmp_path):
    fake_study(str(tmp_path / "data"))
    counts = export_study_data.export(str(tmp_path / "data"), str(tmp_path / "out"))
    assert counts["sessions"] == 12
    assert counts["trials"] == 240  # the bot's trial is not a study trial
    assert counts["auto_decisions"] == 180
    assert counts["replays"] == 48
    assert counts["questionnaires"] == 12


def test_analysis_reports_every_planned_test(tmp_path):
    fake_study(str(tmp_path / "data"))
    export_study_data.export(str(tmp_path / "data"), str(tmp_path / "out"))
    report, results = analyze_study.analyze(str(tmp_path / "out"))
    assert "Participants with trials: 6" in report
    assert results["search"]["pairs"] == 6
    assert "Wilcoxon signed-rank (GP < MC)" in report
    assert "non-inferior" in report
    assert "odds ratio of passing, GP vs MC" in report
    assert "kappa =" in report


def test_director_study_is_analysed_separately(tmp_path):
    fake_study(str(tmp_path / "data"), kind="director")
    export_study_data.export(str(tmp_path / "data"), str(tmp_path / "out"))
    report, results = analyze_study.analyze(str(tmp_path / "out"))
    assert "search" not in results
    director = results["director"]
    assert director["pairs"] == 6
    assert director["d2_on"] > director["d2_off"]
    assert abs(director["adapted_damage_share"] - 40 / 120) < 1e-9
    assert "D1  played pass rate" in report


def test_director_analysis_uses_matched_trials_and_guards_separation(tmp_path):
    import analyze_study
    trials = [{"participant_id": f"P0{p}", "condition": c, "trial": str(k), "passed": "1", "damage": "10",
               "deaths": "0", "time_sec": "30", "adapted_damage": "0"}
              for p in range(1, 5) for c, n in (("DOFF", 3), ("DON", 5)) for k in range(1, n + 1)]
    out: list[str] = []
    res = analyze_study.analyze_director(trials, [], [], out)
    assert res["trials_all"] == 32 and res["trials_matched"] == 24
    assert res["gee_estimable"] is False
