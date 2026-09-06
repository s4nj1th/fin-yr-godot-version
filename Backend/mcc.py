"""
Human-coupled Minimal Criterion Coevolution manager.

Phase 1: every candidate is evaluated by a real playthrough.
Phase 2 (this version): before handing a candidate to the player, check the
online surrogate. If it's confident (predicted pass probability far from the
decision boundary, low variance), auto-decide the candidate without spending
a real playthrough — accept it straight into the Hall of Fame if the
surrogate predicts pass, discard it if it predicts fail. Only a candidate the
surrogate is genuinely unsure about gets handed to the player. This is the
mechanism (Idea.md section 5.5, the "infill criterion") that makes
human-coupled MCC computationally tractable at all.

Only real, human-verified outcomes (from report_outcome) are used to train
the surrogate — auto-decided candidates are never fed back into it, since
training a model on its own predictions would let calibration error
compound instead of being checked against ground truth.
"""

from __future__ import annotations

import json
import os
import random
import time
from typing import Any, Optional

import config
import genome as genome_lib
import llm_mutation
from surrogate import Surrogate


class MCCManager:
    def __init__(self) -> None:
        self.hall_of_fame: list[dict[str, Any]] = []
        self.pending: dict[str, dict[str, Any]] = {}
        self.auto_accepted_count = 0
        self.auto_rejected_count = 0
        self.recent_outcomes: list[dict[str, Any]] = []
        self.llm_mutations_used = 0
        self.random_mutations_used = 0
        self.llm_mutation_failures = 0
        self.surrogate = Surrogate(min_labels=config.SURROGATE_MIN_LABELS)
        self._load_state()

    # --- Public API -----------------------------------------------------

    def get_next_candidate(self) -> tuple[dict[str, Any], Optional[dict[str, float]]]:
        """Returns (candidate, surrogate_estimate). surrogate_estimate is None
        when the surrogate isn't trained yet or wasn't consulted."""
        for _ in range(config.SURROGATE_MAX_PROPOSAL_ATTEMPTS):
            candidate = self._propose_candidate()

            if not self.surrogate.is_ready():
                self.pending[candidate["id"]] = candidate
                self._save_state()
                return candidate, None

            prob, std = self.surrogate.predict(candidate["params"])
            if self._needs_real_evaluation(prob, std):
                self.pending[candidate["id"]] = candidate
                self._save_state()
                return candidate, {"prob": prob, "std": std}

            # Surrogate is confident — resolve this one for free and try again,
            # so the archive keeps growing without spending the player's time.
            self._auto_decide(candidate, prob, std)

        # Exhausted attempts without finding an uncertain candidate (surrogate
        # saturated/very confident everywhere) — force a real evaluation anyway
        # so the player always gets a genome and the loop can't stall forever.
        candidate = self._propose_candidate()
        self.pending[candidate["id"]] = candidate
        self._save_state()
        return candidate, None

    def report_outcome(self, genome_id: str, telemetry: dict[str, Any]) -> tuple[bool, Optional[str]]:
        candidate = self.pending.pop(genome_id, None)
        if candidate is None:
            self._append_log({"event": "unknown_genome_id", "genome_id": genome_id, "telemetry": telemetry})
            self._save_state()
            return False, "unknown_genome_id"

        passed, reason = self._evaluate_minimal_criterion(telemetry)

        if passed:
            candidate["accepted_at"] = time.time()
            candidate["telemetry"] = telemetry
            candidate["decision_source"] = "human"
            self.hall_of_fame.append(candidate)

        self.surrogate.add_example(candidate["params"], passed)

        self.recent_outcomes.append({"params": candidate["params"], "passed": passed, "fail_reason": reason})
        self.recent_outcomes = self.recent_outcomes[-config.LLM_MUTATION_HISTORY_SIZE :]

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
                "decision_source": "human",
                "mutation_source": candidate.get("mutation_source", "random"),
                "hall_of_fame_size": len(self.hall_of_fame),
            }
        )
        self._save_state()
        return passed, reason

    @property
    def hall_of_fame_size(self) -> int:
        return len(self.hall_of_fame)

    @property
    def stats(self) -> dict[str, Any]:
        return {
            "hall_of_fame_size": len(self.hall_of_fame),
            "human_evaluations": len(self.surrogate.y_train),
            "auto_accepted": self.auto_accepted_count,
            "auto_rejected": self.auto_rejected_count,
            "surrogate_ready": self.surrogate.is_ready(),
            "llm_mutation_mode": config.LLM_MUTATION_MODE,
            "llm_mutation_available": llm_mutation.is_available(),
            "llm_mutations_used": self.llm_mutations_used,
            "llm_mutation_failures": self.llm_mutation_failures,
            "random_mutations_used": self.random_mutations_used,
        }

    # --- Candidate proposal / surrogate gating ------------------------------

    def _propose_candidate(self) -> dict[str, Any]:
        if not self.hall_of_fame:
            return genome_lib.make_default_genome()

        parent = random.choice(self.hall_of_fame)

        if llm_mutation.is_available():
            llm_child = llm_mutation.propose_mutation(parent, self.recent_outcomes)
            if llm_child is not None:
                self.llm_mutations_used += 1
                return llm_child
            self.llm_mutation_failures += 1

        child = genome_lib.mutate(parent)
        child["mutation_source"] = "random"
        self.random_mutations_used += 1
        return child

    @staticmethod
    def _needs_real_evaluation(prob: float, std: float) -> bool:
        lo, hi = config.SURROGATE_UNCERTAINTY_BAND
        return (lo < prob < hi) or (std > config.SURROGATE_STD_THRESHOLD)

    def _auto_decide(self, candidate: dict[str, Any], prob: float, std: float) -> None:
        passed = prob >= 0.5
        candidate["decision_source"] = "surrogate"
        candidate["surrogate_estimate"] = {"prob": prob, "std": std}

        if passed:
            candidate["accepted_at"] = time.time()
            self.hall_of_fame.append(candidate)
            self.auto_accepted_count += 1
        else:
            self.auto_rejected_count += 1

        self._append_log(
            {
                "event": "auto_decide",
                "timestamp": time.time(),
                "genome_id": candidate["id"],
                "generation": candidate["generation"],
                "params": candidate["params"],
                "passed": passed,
                "surrogate_prob": prob,
                "surrogate_std": std,
                "decision_source": "surrogate",
                "mutation_source": candidate.get("mutation_source", "random"),
                "hall_of_fame_size": len(self.hall_of_fame),
            }
        )

    # --- Minimal criterion ------------------------------------------------

    @staticmethod
    def _evaluate_minimal_criterion(telemetry: dict[str, Any]) -> tuple[bool, Optional[str]]:
        if not telemetry.get("exit_reached", False):
            return False, "did_not_reach_exit"

        if telemetry.get("quit_early", False):
            return False, "quit_early"

        time_taken = telemetry.get("time_taken_sec", 0.0)
        if time_taken < config.MIN_TRIAL_SECONDS:
            return False, "too_fast_trivial"
        if time_taken > config.MAX_TRIAL_SECONDS:
            return False, "too_slow_abandoned"

        if telemetry.get("deaths_retries", 0) > config.MAX_DEATHS_ALLOWED:
            return False, "too_many_deaths"

        return True, None

    # --- Persistence -------------------------------------------------------

    def _load_state(self) -> None:
        if not os.path.exists(config.STATE_FILE):
            return

        try:
            with open(config.STATE_FILE, "r", encoding="utf-8") as f:
                state = json.load(f)
            self.hall_of_fame = state.get("hall_of_fame", [])
            self.pending = state.get("pending", {})
            self.auto_accepted_count = state.get("auto_accepted_count", 0)
            self.auto_rejected_count = state.get("auto_rejected_count", 0)
            self.recent_outcomes = state.get("recent_outcomes", [])
            self.llm_mutations_used = state.get("llm_mutations_used", 0)
            self.random_mutations_used = state.get("random_mutations_used", 0)
            self.llm_mutation_failures = state.get("llm_mutation_failures", 0)
            self.surrogate.load_state(state.get("surrogate", {}))
        except (json.JSONDecodeError, OSError):
            self.hall_of_fame = []
            self.pending = {}

    def _save_state(self) -> None:
        os.makedirs(os.path.dirname(config.STATE_FILE), exist_ok=True)
        with open(config.STATE_FILE, "w", encoding="utf-8") as f:
            json.dump(
                {
                    "hall_of_fame": self.hall_of_fame,
                    "pending": self.pending,
                    "auto_accepted_count": self.auto_accepted_count,
                    "auto_rejected_count": self.auto_rejected_count,
                    "recent_outcomes": self.recent_outcomes,
                    "llm_mutations_used": self.llm_mutations_used,
                    "random_mutations_used": self.random_mutations_used,
                    "llm_mutation_failures": self.llm_mutation_failures,
                    "surrogate": self.surrogate.state_dict(),
                },
                f,
                indent=2,
            )

    @staticmethod
    def _append_log(record: dict[str, Any]) -> None:
        os.makedirs(os.path.dirname(config.SESSION_LOG_FILE), exist_ok=True)
        with open(config.SESSION_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(record) + "\n")
