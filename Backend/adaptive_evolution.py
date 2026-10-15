"""
Player-in-the-loop adaptive evolution of enemy encounters. See
docs/EVOLUTIONARY_DESIGN.md for the full design rationale.

Only the enemy encounter evolves (density, difficulty scale, tempo, elite
rate, pack size, behaviour mix);
the dungeon itself is fixed (config.FIXED_ENVIRONMENT), so a change in
outcome can be attributed to the enemies. Selection is a minimal criterion:
a design enters the player's archive if and only if the player clears it,
and designs are never ranked against each other.

Every candidate is, in principle, evaluated by a real playthrough. Before
handing a candidate to the player, the online surrogate is checked: if it is
confident (predicted pass probability outside the uncertainty band and low
epistemic uncertainty), the candidate is auto-decided without spending a
playthrough — accepted into the archive if the surrogate predicts a pass,
discarded otherwise. Only a candidate the surrogate is genuinely unsure
about is handed to the player.

Only real outcomes (from report_outcome) train the surrogate; auto-decided
candidates are never fed back into it, since training a model on its own
predictions would let calibration error compound instead of being checked
against ground truth.

Each player has their own manager — archive, surrogate and counters — so
designs adapt to that player, and the scripted bot (its own player id)
never trains a human player's surrogate.
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
import llm_mutation
from surrogate import Surrogate


def is_valid_player_id(player_id: str) -> bool:
    return bool(re.match(config.PLAYER_ID_PATTERN, player_id or ""))


def player_state_file(player_id: str) -> str:
    return os.path.join(config.PLAYERS_DIR, f"{player_id}.json")


class AdaptiveEvolutionManager:
    def __init__(
        self,
        player_id: str = config.DEFAULT_PLAYER_ID,
        *,
        gate_uncertainty: Optional[str] = None,
        bootstrap: Optional[str] = None,
        surrogate_window: Optional[int] = None,
        filter_parents: Optional[bool] = None,
        uncertainty_band: Optional[tuple[float, float]] = None,
        std_threshold: Optional[float] = None,
        genome_space: Optional[str] = None,
        surrogate_kernel: Optional[str] = None,
        play_threshold: Optional[float] = None,
        min_damage: Optional[float] = None,
        use_surrogate: bool = True,
        persist: bool = True,
    ) -> None:
        if not is_valid_player_id(player_id):
            raise ValueError(f"Invalid player id: {player_id!r}")
        self.player_id = player_id
        # Per-instance settings default to config, so experiments can compare
        # variants side by side without touching global state.
        self.gate_uncertainty = (gate_uncertainty or config.SURROGATE_GATE_UNCERTAINTY).lower()
        self.bootstrap = (bootstrap or config.BOOTSTRAP_STRATEGY).lower()
        self.filter_parents = config.FILTER_PARENTS_BY_SURROGATE if filter_parents is None else filter_parents
        self.uncertainty_band = uncertainty_band or config.SURROGATE_UNCERTAINTY_BAND
        self.std_threshold = config.SURROGATE_STD_THRESHOLD if std_threshold is None else std_threshold
        if self.gate_uncertainty not in ("epistemic", "total"):
            raise ValueError(f"gate_uncertainty must be 'epistemic' or 'total', not {self.gate_uncertainty!r}")
        if self.bootstrap not in ("random", "default"):
            raise ValueError(f"bootstrap must be 'random' or 'default', not {self.bootstrap!r}")
        self.space = genome_lib.get_space(genome_space)
        # Remedies from the research paper, both off unless configured:
        # skip uncertain candidates predicted to be failed (play_threshold)
        # and fail trials that cost less than min_damage (a lower bound on
        # challenge).
        self.play_threshold = config.PLAY_THRESHOLD if play_threshold is None else play_threshold
        self.min_damage = config.MIN_DAMAGE_TAKEN if min_damage is None else min_damage
        self.probes_skipped = 0
        # Without the surrogate every candidate is played (it is still
        # trained, so its predictions can be analysed afterwards).
        self.use_surrogate = use_surrogate
        self.persist = persist

        self.hall_of_fame: list[dict[str, Any]] = []
        self.pending: dict[str, dict[str, Any]] = {}
        self.auto_accepted_count = 0
        self.auto_rejected_count = 0
        # Real (non-surrogate) evaluations, split by who generated the
        # telemetry — see docs/EVOLUTIONARY_DESIGN.md "Scripted bot evaluator".
        # human_evaluation_count + bot_evaluation_count == len(surrogate.y_train).
        self.human_evaluation_count = 0
        self.bot_evaluation_count = 0
        self.recent_outcomes: list[dict[str, Any]] = []
        self.llm_mutations_used = 0
        self.random_mutations_used = 0
        self.llm_mutation_failures = 0
        self.bootstrap_designs_served = 0
        window = config.SURROGATE_WINDOW if surrogate_window is None else surrogate_window
        self.surrogate = Surrogate(min_labels=config.SURROGATE_MIN_LABELS, window=window, space=self.space,
                                   kernel=surrogate_kernel)
        self.surrogate_window = window
        self.surrogate_kernel = self.surrogate.kernel
        self._parent_pool: Optional[list[dict[str, Any]]] = None
        self._load_state()

    # --- Public API -----------------------------------------------------

    def get_next_candidate(self) -> tuple[dict[str, Any], Optional[dict[str, float]]]:
        """Returns (candidate, surrogate_estimate). surrogate_estimate is None
        when the surrogate isn't trained yet or wasn't consulted."""
        for _ in range(config.SURROGATE_MAX_PROPOSAL_ATTEMPTS):
            candidate = self._propose_candidate()

            if not self.use_surrogate or not self.surrogate.is_ready():
                self._hand_out(candidate)
                return candidate, None

            prob, total_std, epistemic_std = self.surrogate.predict_detailed(candidate["params"])
            estimate = {"prob": prob, "std": total_std, "epistemic_std": epistemic_std}
            if self._needs_real_evaluation(prob, total_std, epistemic_std):
                if prob < self.play_threshold:
                    # Feasibility-biased probing: an uncertain design the
                    # player would probably fail is not worth their time.
                    self.probes_skipped += 1
                    continue
                self._hand_out(candidate)
                return candidate, estimate

            # Surrogate is confident — resolve this one for free and try again,
            # so the archive keeps growing without spending the player's time.
            self._auto_decide(candidate, estimate)

        # Exhausted attempts without finding an uncertain candidate (surrogate
        # confident everywhere) — hand one out anyway so the player always
        # gets a design and the loop can't stall.
        candidate = self._propose_candidate()
        self._hand_out(candidate)
        return candidate, None

    def report_outcome(
        self, genome_id: str, telemetry: dict[str, Any], evaluator_type: str = "human"
    ) -> tuple[bool, Optional[str]]:
        candidate = self.pending.pop(genome_id, None)
        if candidate is None:
            self._append_log({"event": "unknown_genome_id", "genome_id": genome_id, "telemetry": telemetry})
            self._save_state()
            return False, "unknown_genome_id"

        passed, reason = self._evaluate_minimal_criterion(telemetry, self.min_damage)

        if evaluator_type == "bot":
            self.bot_evaluation_count += 1
        else:
            self.human_evaluation_count += 1

        if passed:
            candidate["accepted_at"] = time.time()
            candidate["telemetry"] = telemetry
            # decision_source distinguishes "a real playthrough decided this"
            # from "the surrogate auto-decided this" (see _auto_decide) — it
            # stays "human" regardless of evaluator_type, since a bot trial
            # is still a real evaluation, not a surrogate guess. Which real
            # evaluator produced it is evaluator_type's job, not this field's.
            candidate["decision_source"] = "human"
            candidate["evaluator_type"] = evaluator_type
            self.hall_of_fame.append(candidate)

        self.surrogate.add_example(candidate["params"], passed)
        self._parent_pool = None

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
                "evaluator_type": evaluator_type,
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
    def settings(self) -> dict[str, Any]:
        """The per-player settings, saved with the player's state so they
        survive a restart (a study condition, for instance)."""
        return {
            "gate_uncertainty": self.gate_uncertainty,
            "bootstrap": self.bootstrap,
            "surrogate_window": self.surrogate_window,
            "filter_parents": self.filter_parents,
            "uncertainty_band": list(self.uncertainty_band),
            "std_threshold": self.std_threshold,
            "genome_space": self.space.name,
            "surrogate_kernel": self.surrogate_kernel,
            "play_threshold": self.play_threshold,
            "min_damage": self.min_damage,
            "use_surrogate": self.use_surrogate,
        }

    @property
    def stats(self) -> dict[str, Any]:
        return {
            "player_id": self.player_id,
            "hall_of_fame_size": len(self.hall_of_fame),
            "real_evaluations": len(self.surrogate.y_train),
            "human_evaluations": self.human_evaluation_count,
            "bot_evaluations": self.bot_evaluation_count,
            "auto_accepted": self.auto_accepted_count,
            "auto_rejected": self.auto_rejected_count,
            "surrogate_ready": self.surrogate.is_ready(),
            "surrogate_noise_std": self.surrogate.noise_level ** 0.5,
            "gate_uncertainty": self.gate_uncertainty,
            "bootstrap": self.bootstrap,
            "bootstrap_designs_served": self.bootstrap_designs_served,
            "genome_space": self.space.name,
            "surrogate_kernel": self.surrogate_kernel,
            "play_threshold": self.play_threshold,
            "min_damage": self.min_damage,
            "probes_skipped": self.probes_skipped,
            "use_surrogate": self.use_surrogate,
            "llm_mutation_mode": config.LLM_MUTATION_MODE,
            "llm_mutation_available": llm_mutation.is_available(),
            "llm_mutations_used": self.llm_mutations_used,
            "llm_mutation_failures": self.llm_mutation_failures,
            "random_mutations_used": self.random_mutations_used,
        }

    # --- Candidate proposal / surrogate gating ------------------------------

    def _hand_out(self, candidate: dict[str, Any]) -> None:
        self.pending[candidate["id"]] = candidate
        self._save_state()

    def _propose_candidate(self) -> dict[str, Any]:
        if not self.hall_of_fame:
            # Cold start: there is no parent to mutate yet.
            if self.bootstrap == "random":
                candidate = genome_lib.make_random_genome(self.space)
                candidate["mutation_source"] = "bootstrap"
                self.bootstrap_designs_served += 1
                return candidate
            return genome_lib.make_default_genome()

        parent = random.choice(self._parents())

        if llm_mutation.is_available():
            llm_child = llm_mutation.propose_mutation(parent, self.recent_outcomes, self.space)
            if llm_child is not None:
                self.llm_mutations_used += 1
                return llm_child
            self.llm_mutation_failures += 1

        child = genome_lib.mutate(parent, self.space)
        child["mutation_source"] = "random"
        self.random_mutations_used += 1
        return child

    def _parents(self) -> list[dict[str, Any]]:
        """The archive, or — with parent filtering on and a ready surrogate —
        only the archived designs the surrogate still predicts the player
        would pass (all of them if it predicts none)."""
        if not self.filter_parents or not self.surrogate.is_ready():
            return self.hall_of_fame
        if self._parent_pool is None:
            means = self.surrogate.predict_mean([g["params"] for g in self.hall_of_fame])
            self._parent_pool = [g for g, m in zip(self.hall_of_fame, means) if m >= 0.5] or list(self.hall_of_fame)
        return self._parent_pool

    def _needs_real_evaluation(self, prob: float, total_std: float, epistemic_std: float) -> bool:
        lo, hi = self.uncertainty_band
        uncertainty = epistemic_std if self.gate_uncertainty == "epistemic" else total_std
        return (lo < prob < hi) or (uncertainty > self.std_threshold)

    def _auto_decide(self, candidate: dict[str, Any], estimate: dict[str, float]) -> None:
        passed = estimate["prob"] >= 0.5
        candidate["decision_source"] = "surrogate"
        candidate["surrogate_estimate"] = estimate

        if passed:
            candidate["accepted_at"] = time.time()
            self.hall_of_fame.append(candidate)
            self.auto_accepted_count += 1
            self._parent_pool = None
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
                "surrogate_prob": estimate["prob"],
                "surrogate_std": estimate["std"],
                "surrogate_epistemic_std": estimate["epistemic_std"],
                "decision_source": "surrogate",
                "mutation_source": candidate.get("mutation_source", "random"),
                "hall_of_fame_size": len(self.hall_of_fame),
            }
        )

    # --- Minimal criterion ------------------------------------------------

    @staticmethod
    def _evaluate_minimal_criterion(telemetry: dict[str, Any], min_damage: float = 0.0) -> tuple[bool, Optional[str]]:
        """Order matters here. `quit_early` and "timed out without reaching
        the exit" are distinct failure signals from "played it out and simply
        didn't make it" — checking `exit_reached` unconditionally first would
        swallow both into the same generic reason, since every non-exit
        trial ending also has exit_reached=False. Client-side (GameManager.cs)
        sends exactly one of three trial endings: exit reached, manual R-key
        quit (quit_early=True), or MAX_TRIAL_SECONDS timeout (quit_early=False,
        exit_reached=False) — each must resolve to a distinct fail_reason so
        `/api/stats`-level analysis can actually tell them apart.
        """
        if telemetry.get("quit_early", False):
            return False, "quit_early"

        if not telemetry.get("exit_reached", False):
            time_taken = telemetry.get("time_taken_sec", 0.0)
            if time_taken >= config.MAX_TRIAL_SECONDS:
                return False, "too_slow_abandoned"
            return False, "did_not_reach_exit"

        time_taken = telemetry.get("time_taken_sec", 0.0)
        if time_taken < config.MIN_TRIAL_SECONDS:
            return False, "too_fast_trivial"

        if telemetry.get("deaths_retries", 0) > config.MAX_DEATHS_ALLOWED:
            return False, "too_many_deaths"

        if min_damage > 0 and telemetry.get("damage_taken", 0) < min_damage:
            return False, "too_easy"

        return True, None

    # --- Persistence -------------------------------------------------------

    def _load_state(self) -> None:
        path = player_state_file(self.player_id)
        if not self.persist or not os.path.exists(path):
            return

        try:
            with open(path, "r", encoding="utf-8") as f:
                state = json.load(f)
            self.hall_of_fame = state.get("hall_of_fame", [])
            self.pending = state.get("pending", {})
            self.auto_accepted_count = state.get("auto_accepted_count", 0)
            self.auto_rejected_count = state.get("auto_rejected_count", 0)
            self.human_evaluation_count = state.get("human_evaluation_count", 0)
            self.bot_evaluation_count = state.get("bot_evaluation_count", 0)
            self.recent_outcomes = state.get("recent_outcomes", [])
            self.llm_mutations_used = state.get("llm_mutations_used", 0)
            self.random_mutations_used = state.get("random_mutations_used", 0)
            self.llm_mutation_failures = state.get("llm_mutation_failures", 0)
            self.bootstrap_designs_served = state.get("bootstrap_designs_served", 0)
            self.probes_skipped = state.get("probes_skipped", 0)
            # Designs saved by the four-archetype version get the new genes
            # at their neutral values.
            for g in self.hall_of_fame + list(self.pending.values()):
                g["params"] = genome_lib.complete_params(g["params"])
            self.surrogate.load_state(state.get("surrogate", {}))
        except (json.JSONDecodeError, OSError):
            self.hall_of_fame = []
            self.pending = {}

    def _save_state(self) -> None:
        if not self.persist:
            return
        path = player_state_file(self.player_id)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        # Write to a temporary file and rename, so a crash mid-write can never
        # leave a truncated (and therefore unloadable) archive behind.
        tmp_path = path + ".tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(
                {
                    "player_id": self.player_id,
                    "settings": self.settings,
                    "hall_of_fame": self.hall_of_fame,
                    "pending": self.pending,
                    "auto_accepted_count": self.auto_accepted_count,
                    "auto_rejected_count": self.auto_rejected_count,
                    "human_evaluation_count": self.human_evaluation_count,
                    "bot_evaluation_count": self.bot_evaluation_count,
                    "recent_outcomes": self.recent_outcomes,
                    "llm_mutations_used": self.llm_mutations_used,
                    "random_mutations_used": self.random_mutations_used,
                    "llm_mutation_failures": self.llm_mutation_failures,
                    "bootstrap_designs_served": self.bootstrap_designs_served,
                    "probes_skipped": self.probes_skipped,
                    "surrogate": self.surrogate.state_dict(),
                },
                f,
                indent=2,
            )
        os.replace(tmp_path, path)

    def _append_log(self, record: dict[str, Any]) -> None:
        if not self.persist:
            return
        record = {"player_id": self.player_id, **record}
        os.makedirs(os.path.dirname(config.SESSION_LOG_FILE), exist_ok=True)
        with open(config.SESSION_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(record) + "\n")


def saved_settings(player_id: str) -> dict[str, Any]:
    """Settings stored with a player's state, or {} if there are none."""
    path = player_state_file(player_id)
    try:
        with open(path, "r", encoding="utf-8") as f:
            settings = json.load(f).get("settings", {})
    except (OSError, json.JSONDecodeError):
        return {}
    if not isinstance(settings, dict):
        return {}
    allowed = {"gate_uncertainty", "bootstrap", "surrogate_window", "filter_parents", "uncertainty_band",
               "std_threshold", "genome_space", "surrogate_kernel", "play_threshold", "min_damage", "use_surrogate"}
    settings = {k: v for k, v in settings.items() if k in allowed}
    if "uncertainty_band" in settings:
        settings["uncertainty_band"] = tuple(settings["uncertainty_band"])
    return settings


class PlayerRegistry:
    """One AdaptiveEvolutionManager per player id, created on first use with
    the settings saved for that player (if any), else the given settings,
    else the defaults from config."""

    def __init__(self) -> None:
        self._managers: dict[str, AdaptiveEvolutionManager] = {}

    def get(self, player_id: str, settings: Optional[dict[str, Any]] = None) -> AdaptiveEvolutionManager:
        if player_id not in self._managers:
            chosen = saved_settings(player_id) or (settings or {})
            self._managers[player_id] = AdaptiveEvolutionManager(player_id, **chosen)
        return self._managers[player_id]

    @staticmethod
    def known_players() -> list[str]:
        """Players with saved state on disk."""
        if not os.path.isdir(config.PLAYERS_DIR):
            return []
        return sorted(
            name[: -len(".json")]
            for name in os.listdir(config.PLAYERS_DIR)
            if name.endswith(".json") and is_valid_player_id(name[: -len(".json")])
        )
