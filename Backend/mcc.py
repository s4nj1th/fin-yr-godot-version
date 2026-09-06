"""
Phase-1 naive human-coupled Minimal Criterion Coevolution manager.

Every candidate is evaluated by a real playthrough (no surrogate yet — that's
Phase 2). This module owns: issuing candidates, evaluating the minimal
criterion against reported telemetry, and persisting the Hall of Fame.
"""

from __future__ import annotations

import json
import os
import random
import time
from typing import Any, Optional

import genome as genome_lib
from config import MAX_DEATHS_ALLOWED, MAX_TRIAL_SECONDS, MIN_TRIAL_SECONDS, SESSION_LOG_FILE, STATE_FILE


class MCCManager:
    def __init__(self) -> None:
        self.hall_of_fame: list[dict[str, Any]] = []
        self.pending: dict[str, dict[str, Any]] = {}
        self._load_state()

    # --- Public API -----------------------------------------------------

    def get_next_candidate(self) -> dict[str, Any]:
        if not self.hall_of_fame:
            candidate = genome_lib.make_default_genome()
        else:
            parent = random.choice(self.hall_of_fame)
            candidate = genome_lib.mutate(parent)

        self.pending[candidate["id"]] = candidate
        self._save_state()
        return candidate

    def report_outcome(self, genome_id: str, telemetry: dict[str, Any]) -> tuple[bool, Optional[str]]:
        candidate = self.pending.pop(genome_id, None)
        if candidate is None:
            # Unknown/expired id (e.g. server restarted mid-session) — log and reject gracefully.
            self._append_log({"event": "unknown_genome_id", "genome_id": genome_id, "telemetry": telemetry})
            self._save_state()
            return False, "unknown_genome_id"

        passed, reason = self._evaluate_minimal_criterion(telemetry)

        if passed:
            candidate["accepted_at"] = time.time()
            candidate["telemetry"] = telemetry
            self.hall_of_fame.append(candidate)

        self._append_log(
            {
                "event": "report_outcome",
                "timestamp": time.time(),
                "genome_id": genome_id,
                "generation": candidate["generation"],
                "params": candidate["params"],
                "telemetry": telemetry,
                "passed": passed,
                "fail_reason": reason,
                "hall_of_fame_size": len(self.hall_of_fame),
            }
        )
        self._save_state()
        return passed, reason

    @property
    def hall_of_fame_size(self) -> int:
        return len(self.hall_of_fame)

    # --- Minimal criterion ------------------------------------------------

    @staticmethod
    def _evaluate_minimal_criterion(telemetry: dict[str, Any]) -> tuple[bool, Optional[str]]:
        if not telemetry.get("exit_reached", False):
            return False, "did_not_reach_exit"

        if telemetry.get("quit_early", False):
            return False, "quit_early"

        time_taken = telemetry.get("time_taken_sec", 0.0)
        if time_taken < MIN_TRIAL_SECONDS:
            return False, "too_fast_trivial"
        if time_taken > MAX_TRIAL_SECONDS:
            return False, "too_slow_abandoned"

        if telemetry.get("deaths_retries", 0) > MAX_DEATHS_ALLOWED:
            return False, "too_many_deaths"

        return True, None

    # --- Persistence -------------------------------------------------------

    def _load_state(self) -> None:
        if not os.path.exists(STATE_FILE):
            return

        try:
            with open(STATE_FILE, "r", encoding="utf-8") as f:
                state = json.load(f)
            self.hall_of_fame = state.get("hall_of_fame", [])
            self.pending = state.get("pending", {})
        except (json.JSONDecodeError, OSError):
            # Corrupt/partial state file — start fresh rather than crash the server.
            self.hall_of_fame = []
            self.pending = {}

    def _save_state(self) -> None:
        os.makedirs(os.path.dirname(STATE_FILE), exist_ok=True)
        with open(STATE_FILE, "w", encoding="utf-8") as f:
            json.dump({"hall_of_fame": self.hall_of_fame, "pending": self.pending}, f, indent=2)

    @staticmethod
    def _append_log(record: dict[str, Any]) -> None:
        os.makedirs(os.path.dirname(SESSION_LOG_FILE), exist_ok=True)
        with open(SESSION_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(record) + "\n")
