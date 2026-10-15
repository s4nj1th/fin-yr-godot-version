"""
Exports the human-study data (Backend/data/study/ and the session log) to
CSV files for analysis (tools/analyze_study.py) and archiving.

    python tools/export_study_data.py --data Backend/data --out study_export/

Writes
  sessions.csv        participant, session number, condition, player id
  trials.csv          every evaluated trial: participant, condition, trial index, pass/fail,
                      failure reason, time, damage, deaths, and the design's genes
  auto_decisions.csv  designs the surrogate accepted or rejected without a playthrough
  replays.csv         end-of-session replays of archived designs and their outcomes
  questionnaires.csv  one row per session, one column per item

Only pseudonymous participant ids (P01, P02, ...) are stored or exported.
"""

from __future__ import annotations

import argparse
import csv
import json
import os

GENES = ["enemy_density", "enemy_difficulty_scale", "enemy_tempo", "elite_rate", "pack_size"]
BEHAVIORS = ["melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe", "melee_charger", "ranged_bomber", "melee_shield"]


def read_jsonl(path: str) -> list[dict]:
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def gene_columns(params: dict) -> dict:
    mix = params.get("enemy_behavior_mix", {})
    row = {g: params.get(g, "") for g in GENES}
    row.update({f"mix_{b}": mix.get(b, 0.0) for b in BEHAVIORS})
    return row


def write_csv(path: str, rows: list[dict]) -> None:
    if not rows:
        open(path, "w").close()
        return
    fields = list(dict.fromkeys(k for row in rows for k in row))
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def export(data_dir: str, out_dir: str) -> dict[str, int]:
    os.makedirs(out_dir, exist_ok=True)
    study_dir = os.path.join(data_dir, "study")
    sessions = read_jsonl(os.path.join(study_dir, "sessions.jsonl"))
    by_player = {s["player_id"]: s for s in sessions}

    session_rows = [{k: s[k] for k in ("participant_id", "session_number", "condition", "player_id")} for s in sessions]

    trial_rows, auto_rows = [], []
    counters: dict[str, int] = {}
    for record in read_jsonl(os.path.join(data_dir, "session_log.jsonl")):
        session = by_player.get(record.get("player_id"))
        if session is None:
            continue  # not a study session (e.g. the bot or a demo player)
        base = {"participant_id": session["participant_id"], "condition": session["condition"],
                "session_number": session["session_number"]}
        if record.get("event") == "report_outcome":
            counters[record["player_id"]] = counters.get(record["player_id"], 0) + 1
            tel = record.get("telemetry", {})
            trial_rows.append({**base, "trial": counters[record["player_id"]], "passed": int(record["passed"]),
                               "fail_reason": record.get("fail_reason") or "", "time_sec": tel.get("time_taken_sec"),
                               "damage": tel.get("damage_taken"), "deaths": tel.get("deaths_retries"),
                               "archive_size": record.get("hall_of_fame_size"), **gene_columns(record["params"])})
        elif record.get("event") == "auto_decide":
            auto_rows.append({**base, "accepted": int(record["passed"]), "surrogate_prob": record.get("surrogate_prob"),
                              "epistemic_std": record.get("surrogate_epistemic_std"), **gene_columns(record["params"])})

    replay_rows = []
    for record in read_jsonl(os.path.join(study_dir, "replays.jsonl")):
        session = by_player.get(record["player_id"])
        if session is None:
            continue
        replay_rows.append({"participant_id": session["participant_id"], "condition": session["condition"],
                            "decision_source": record.get("decision_source", "human"), "passed": int(record["passed"]),
                            "fail_reason": record.get("fail_reason") or "", **gene_columns(record["params"])})

    questionnaire_rows = []
    for record in read_jsonl(os.path.join(study_dir, "questionnaires.jsonl")):
        session = by_player.get(record["player_id"], {})
        questionnaire_rows.append({"participant_id": record["participant_id"], "session_number": record["session_number"],
                                   "condition": session.get("condition", ""), **record["responses"],
                                   "comment": record.get("comment", "")})

    tables = {"sessions": session_rows, "trials": trial_rows, "auto_decisions": auto_rows,
              "replays": replay_rows, "questionnaires": questionnaire_rows}
    for name, rows in tables.items():
        write_csv(os.path.join(out_dir, f"{name}.csv"), rows)
    return {name: len(rows) for name, rows in tables.items()}


def main() -> None:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", default=os.path.join(root, "Backend", "data"))
    parser.add_argument("--out", default="study_export")
    args = parser.parse_args()
    counts = export(args.data, args.out)
    print(", ".join(f"{name}: {n}" for name, n in counts.items()), f"-> {args.out}")


if __name__ == "__main__":
    main()
