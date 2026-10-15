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


def fake_study(data_dir, n_participants=6):
    rng = random.Random(0)
    random.seed(0)
    sessions, log, replays, answers = [], [], [], []
    for i in range(1, n_participants + 1):
        pid = f"P{i:02d}"
        for session in (1, 2):
            condition = study.condition_for(pid, session)
            player = study.player_id_for(pid, condition)
            sessions.append({"participant_id": pid, "session_number": session, "condition": condition, "player_id": player})
            for _ in range(20):
                passed = rng.random() < (0.7 if condition == "MC" else 0.6)
                log.append({"event": "report_outcome", "player_id": player, "passed": passed,
                            "fail_reason": None if passed else "too_many_deaths",
                            "telemetry": {"time_taken_sec": 60, "damage_taken": 100, "deaths_retries": 1},
                            "params": genome.make_random_genome()["params"], "hall_of_fame_size": 0})
            for _ in range(30 if condition == "GP" else 0):
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
    report = analyze_study.analyze(str(tmp_path / "out"))
    assert "Participants with trials: 6" in report
    assert "Wilcoxon signed-rank (GP < MC)" in report
    assert "non-inferior" in report
    assert "odds ratio of passing, GP vs MC" in report
    assert "kappa =" in report
