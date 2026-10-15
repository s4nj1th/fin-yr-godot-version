"""
Support for the within-subjects human study (docs/HUMAN_STUDY_PROTOCOL.md).

Each participant plays two sessions, one per condition, in counterbalanced
order:
  MC   minimal criterion search without the surrogate (every design played)
  GP   minimal criterion search with the epistemic surrogate gate
Each session gets its own player id ("<participant>-<condition>"), so each
archive and surrogate is trained only on that participant's trials in that
condition. The adaptive tactical director is switched off in the client for
study sessions, so it cannot confound the comparison.

At the end of a session the participant replays a random sample of their
archive (the replay block): those outcomes estimate how many archived
designs the participant would actually clear, and never touch the archive
or the surrogate. Then they answer the in-game questionnaire.

Everything is stored under DATA_DIR/study/ as JSON lines, with pseudonymous
participant ids only. tools/export_study_data.py turns it into CSV files and
tools/analyze_study.py analyses them.
"""

from __future__ import annotations

import json
import os
import random
import re
import time
from typing import Any, Optional

import config
import genome as genome_lib

PARTICIPANT_ID_PATTERN = r"^P[0-9]{2,3}$"
CONDITIONS = {
    "MC": {"use_surrogate": False, "gate_uncertainty": "epistemic", "bootstrap": "random"},
    "GP": {"use_surrogate": True, "gate_uncertainty": "epistemic", "bootstrap": "random"},
}
REPLAY_SAMPLE_SIZE = 4
ITEMS_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "study_items.json")


def study_dir() -> str:
    return os.path.join(config.DATA_DIR, "study")


def is_valid_participant_id(participant_id: str) -> bool:
    return bool(re.match(PARTICIPANT_ID_PATTERN, participant_id or ""))


def condition_for(participant_id: str, session_number: int) -> str:
    """Counterbalanced order: odd-numbered participants play MC first, even
    ones GP first."""
    number = int(participant_id[1:])
    first, second = ("MC", "GP") if number % 2 == 1 else ("GP", "MC")
    return first if session_number == 1 else second


def player_id_for(participant_id: str, condition: str) -> str:
    return f"{participant_id}-{condition}"


def load_items() -> dict[str, Any]:
    with open(ITEMS_FILE, "r", encoding="utf-8") as f:
        return json.load(f)


def append_record(name: str, record: dict[str, Any]) -> None:
    os.makedirs(study_dir(), exist_ok=True)
    record = {"timestamp": time.time(), **record}
    with open(os.path.join(study_dir(), f"{name}.jsonl"), "a", encoding="utf-8") as f:
        f.write(json.dumps(record) + "\n")


def read_records(name: str) -> list[dict[str, Any]]:
    path = os.path.join(study_dir(), f"{name}.jsonl")
    if not os.path.exists(path):
        return []
    with open(path, "r", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def validate_responses(responses: dict[str, Any], items: dict[str, Any]) -> dict[str, int]:
    """Keeps only known Likert items with an integer answer on the scale."""
    lo, hi = items["scale"]["min"], items["scale"]["max"]
    known = {item["id"] for item in items["items"]}
    clean: dict[str, int] = {}
    for key, value in responses.items():
        if key in known and isinstance(value, int) and not isinstance(value, bool) and lo <= value <= hi:
            clean[key] = value
    return clean


class ReplayBlock:
    """Hands out archived designs for replay and evaluates their outcomes
    without changing the archive or the surrogate."""

    def __init__(self) -> None:
        self._pending: dict[str, dict[str, Any]] = {}

    def sample(self, manager: Any, n: int = REPLAY_SAMPLE_SIZE, seed: Optional[int] = None) -> list[dict[str, Any]]:
        archive = manager.hall_of_fame
        rng = random.Random(seed)
        chosen = rng.sample(archive, min(n, len(archive))) if archive else []
        out = []
        for design in chosen:
            replay_id = f"replay-{genome_lib.new_genome_id()}"
            self._pending[replay_id] = {"player_id": manager.player_id, "design": design}
            out.append({
                "id": replay_id,
                "parent_id": design["id"],
                "generation": design.get("generation", 0),
                "params": genome_lib.complete_params(design["params"]),
            })
        return out

    def report(self, replay_id: str, player_id: str, telemetry: dict[str, Any], manager: Any) -> Optional[bool]:
        entry = self._pending.get(replay_id)
        if entry is None or entry["player_id"] != player_id:
            return None
        del self._pending[replay_id]
        passed, reason = manager._evaluate_minimal_criterion(telemetry, manager.min_damage)
        design = entry["design"]
        append_record("replays", {
            "player_id": player_id,
            "design_id": design["id"],
            "decision_source": design.get("decision_source", "human"),
            "params": design["params"],
            "passed": passed,
            "fail_reason": reason,
            "telemetry": telemetry,
        })
        return passed
