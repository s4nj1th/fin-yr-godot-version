"""
Pure aggregation logic for per-archetype enemy AI telemetry, read from
Backend/data/session_log.jsonl. See docs/BEHAVIOR_ANALYTICS.md for the full
picture — this module answers "how did each enemy behavior archetype
actually perform, per situation, across trials"; analyze_behavior_telemetry.py
turns its output into charts.

Kept separate from the plotting script so the aggregation math is
unit-testable without importing matplotlib (matches the split already used
between adaptive_evolution.py and main.py — logic vs. the thing that calls it).

Terminology, matching Scripts/AI/EnemyBehaviorTelemetry.cs:
- "archetype" / "behavior type": melee_chaser, melee_hit_run, ranged_kite,
  ranged_strafe (config.BEHAVIOR_TYPES).
- "situation": baseline vs. adapted — whether the specific enemy involved in
  an event carried an active AdaptationDirector tactical order (rear-guard
  and/or last-known-position search) at the time. This is recorded
  per-enemy, per-event, client-side — not approximated here after the fact.
- "evaluator_type": human vs. bot — which evaluator played the trial (see
  docs/EVOLUTIONARY_DESIGN.md "Scripted bot evaluator"). Orthogonal to
  situation; filter_by_evaluator_type keeps the two kinds of trial
  separable rather than silently blended.
"""

from __future__ import annotations

import json
from typing import Any, Iterable, Optional

import config

SITUATIONS = ["baseline", "adapted"]

COUNTER_KEYS = [
    "spawned",
    "attacks_on_player",
    "damage_dealt_to_player",
    "hits_taken_from_player",
    "damage_taken_from_player",
    "rear_hits_taken",
    "killed_by_player",
]


def load_report_events(path: str = config.SESSION_LOG_FILE) -> list[dict[str, Any]]:
    """Reads session_log.jsonl and returns only report_outcome events — the
    only event type backed by a real playthrough, human or bot (auto_decide
    events are surrogate-resolved for free and carry no telemetry, per
    docs/EVOLUTIONARY_DESIGN.md)."""
    events: list[dict[str, Any]] = []
    try:
        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if record.get("event") == "report_outcome":
                    events.append(record)
    except OSError:
        return []
    return events


def events_with_behavior_breakdown(events: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Filters to trials that actually carry the per-archetype breakdown —
    log entries recorded before this instrumentation existed are silently
    skipped rather than treated as zero-valued, since "no data" and "zero
    events" are different claims and conflating them would understate early
    archetypes' activity."""
    return [e for e in events if e.get("telemetry", {}).get("enemy_behavior_breakdown")]


def filter_by_evaluator_type(events: Iterable[dict[str, Any]], evaluator_type: str) -> list[dict[str, Any]]:
    """evaluator_type: "human", "bot", or "all" (no filtering). Records
    logged before the scripted bot evaluator existed have no evaluator_type
    field at all — they default to "human" here, matching the same default
    Backend/models.py's ReportOutcomeRequest and the Godot client both use,
    since every trial before that feature existed really was played by a
    human. This keeps old data visible in the human-only view instead of
    silently vanishing from it."""
    if evaluator_type == "all":
        return list(events)
    return [e for e in events if e.get("evaluator_type", "human") == evaluator_type]


def filter_by_player(events: Iterable[dict[str, Any]], player_id: str) -> list[dict[str, Any]]:
    """player_id, or "all" (no filtering). Records logged before per-player
    archives existed have no player_id and count as the default player."""
    if player_id == "all":
        return list(events)
    return [e for e in events if e.get("player_id", config.DEFAULT_PLAYER_ID) == player_id]


def count_by_evaluator_type(events: Iterable[dict[str, Any]]) -> dict[str, int]:
    """{"human": n, "bot": n} — same "missing means human" default as
    filter_by_evaluator_type, so the two functions never disagree."""
    counts = {"human": 0, "bot": 0}
    for event in events:
        key = event.get("evaluator_type", "human")
        counts[key] = counts.get(key, 0) + 1
    return counts


def _empty_counters() -> dict[str, int]:
    return {key: 0 for key in COUNTER_KEYS}


def aggregate_by_archetype(events: Iterable[dict[str, Any]]) -> dict[str, dict[str, dict[str, int]]]:
    """Sums every counter across all given trials, per archetype and
    situation. Shape: {archetype: {"baseline": {...}, "adapted": {...}}}."""
    totals: dict[str, dict[str, dict[str, int]]] = {}
    for event in events:
        breakdown = event.get("telemetry", {}).get("enemy_behavior_breakdown", {})
        for archetype, situations in breakdown.items():
            bucket = totals.setdefault(archetype, {s: _empty_counters() for s in SITUATIONS})
            for situation in SITUATIONS:
                counters = situations.get(situation, {})
                for key in COUNTER_KEYS:
                    bucket[situation][key] += counters.get(key, 0)
    return totals


def _derived_metrics(counters: dict[str, int]) -> dict[str, float]:
    """Converts raw sums into rate/ratio metrics comparable across
    archetypes and situations regardless of sample size. Every ratio
    guards its own zero denominator (a situation with zero spawns this run
    is a real, reportable zero, not an error)."""
    spawned = max(1, counters["spawned"])
    attacks = max(1, counters["attacks_on_player"])
    hits_taken = max(1, counters["hits_taken_from_player"])

    return {
        "damage_per_spawn": counters["damage_dealt_to_player"] / spawned,
        "damage_per_attack": counters["damage_dealt_to_player"] / attacks,
        "kill_rate": counters["killed_by_player"] / spawned,
        "damage_taken_per_spawn": counters["damage_taken_from_player"] / spawned,
        "rear_hit_rate": counters["rear_hits_taken"] / hits_taken,
    }


def archetype_situation_profiles(events: Iterable[dict[str, Any]]) -> dict[str, dict[str, dict[str, float]]]:
    """Ties aggregate_by_archetype + _derived_metrics together — the shape
    analyze_behavior_telemetry.py plots directly.
    Shape: {archetype: {situation: {metric: value}}}."""
    totals = aggregate_by_archetype(events)
    return {
        archetype: {situation: _derived_metrics(counters) for situation, counters in situations.items()}
        for archetype, situations in totals.items()
    }


def archetype_overall_profiles(events: Iterable[dict[str, Any]]) -> dict[str, dict[str, float]]:
    """Same as archetype_situation_profiles but collapsed across baseline +
    adapted — "how does this archetype behave overall," the coarser
    comparison when the situation split isn't the point."""
    totals = aggregate_by_archetype(events)
    overall: dict[str, dict[str, float]] = {}
    for archetype, situations in totals.items():
        combined = _empty_counters()
        for counters in situations.values():
            for key in COUNTER_KEYS:
                combined[key] += counters[key]
        overall[archetype] = _derived_metrics(combined)
    return overall


def generation_series(events: Iterable[dict[str, Any]]) -> list[tuple[int, dict[str, dict[str, float]]]]:
    """Groups events by genome generation (already logged per
    report_outcome record) and returns (generation, per-archetype overall
    profile) pairs sorted by generation — the x-axis for "how it's
    evolving." Multiple trials can share a generation number (sibling
    genomes mutated from the same parent); they're merged."""
    by_generation: dict[int, list[dict[str, Any]]] = {}
    for event in events:
        generation = event.get("generation", 0)
        by_generation.setdefault(generation, []).append(event)

    return [(generation, archetype_overall_profiles(bucket)) for generation, bucket in sorted(by_generation.items())]


def per_trial_archetype_metric_values(events: Iterable[dict[str, Any]], metric: str) -> dict[str, list[float]]:
    """For each archetype, one value of `metric` per trial that archetype
    appeared in (baseline+adapted combined for that trial) — the
    per-trial granularity outlier detection needs, as opposed to
    archetype_overall_profiles' single number aggregated across *every*
    trial at once."""
    values: dict[str, list[float]] = {}
    for event in events:
        breakdown = event.get("telemetry", {}).get("enemy_behavior_breakdown", {})
        for archetype, situations in breakdown.items():
            combined = _empty_counters()
            for situation in SITUATIONS:
                counters = situations.get(situation, {})
                for key in COUNTER_KEYS:
                    combined[key] += counters.get(key, 0)
            metric_value = _derived_metrics(combined).get(metric)
            if metric_value is not None:
                values.setdefault(archetype, []).append(metric_value)
    return values


def find_iqr_outliers(values: list[float], k: float = 1.5) -> list[int]:
    """Tukey's-fences outlier indices: values below Q1 - k*IQR or above
    Q3 + k*IQR (the same rule matplotlib's boxplot uses to place fliers,
    kept consistent with plot_archetype_outliers in
    analyze_behavior_telemetry.py). Needs at least 4 points to compute a
    meaningful quartile split; returns [] below that rather than flagging
    everything (or crashing) on a handful of samples."""
    if len(values) < 4:
        return []

    sorted_vals = sorted(values)
    n = len(sorted_vals)

    def percentile(p: float) -> float:
        idx = p * (n - 1)
        lo, hi = int(idx), min(int(idx) + 1, n - 1)
        frac = idx - lo
        return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * frac

    q1, q3 = percentile(0.25), percentile(0.75)
    iqr = q3 - q1
    lower, upper = q1 - (k * iqr), q3 + (k * iqr)
    return [i for i, v in enumerate(values) if v < lower or v > upper]


def archetype_statistical_outliers(events: Iterable[dict[str, Any]], metric: str) -> dict[str, list[float]]:
    """Per archetype, the individual trial values of `metric` that are
    statistical outliers relative to that archetype's *own* distribution
    across trials collected so far — data-driven, no fixed domain number
    (compare flag_threshold_exceeding, which uses config.THRESHOLDS
    instead). Archetypes with no outliers are omitted, not listed empty."""
    per_archetype = per_trial_archetype_metric_values(events, metric)
    outliers: dict[str, list[float]] = {}
    for archetype, values in per_archetype.items():
        indices = find_iqr_outliers(values)
        if indices:
            outliers[archetype] = [values[i] for i in indices]
    return outliers


def flag_threshold_exceeding(
    events: Iterable[dict[str, Any]], thresholds: Optional[dict[str, float]] = None
) -> list[dict[str, Any]]:
    """Deterministic, domain-threshold flags (config.THRESHOLDS by
    default) — distinct from the statistical outlier functions above,
    which need no fixed number. Returns one record per trial that crosses
    at least one threshold, naming which ones and by how much, so a
    genome/trial can be inspected without re-deriving why it was flagged."""
    active_thresholds = thresholds if thresholds is not None else config.THRESHOLDS
    flagged: list[dict[str, Any]] = []

    for event in events:
        telemetry = event.get("telemetry", {})
        reasons: list[str] = []

        total_enemies = telemetry.get("total_enemies", 0)
        if total_enemies >= active_thresholds.get("high_enemy_count", float("inf")):
            reasons.append(f"high_enemy_count ({total_enemies})")
        if 0 < total_enemies <= active_thresholds.get("low_enemy_count", -1):
            reasons.append(f"low_enemy_count ({total_enemies})")

        deaths = telemetry.get("deaths_retries", 0)
        if deaths >= active_thresholds.get("near_max_deaths", float("inf")):
            reasons.append(f"near_max_deaths ({deaths})")

        breakdown = telemetry.get("enemy_behavior_breakdown", {})
        for archetype, situations in breakdown.items():
            combined = _empty_counters()
            for situation in SITUATIONS:
                counters = situations.get(situation, {})
                for key in COUNTER_KEYS:
                    combined[key] += counters.get(key, 0)
            if combined["spawned"] <= 0:
                continue
            metrics = _derived_metrics(combined)

            if metrics["damage_per_spawn"] >= active_thresholds.get("high_damage_per_spawn", float("inf")):
                reasons.append(f"{archetype}: high_damage_per_spawn ({metrics['damage_per_spawn']:.1f})")
            if metrics["rear_hit_rate"] >= active_thresholds.get("high_rear_hit_rate", float("inf")):
                reasons.append(f"{archetype}: high_rear_hit_rate ({metrics['rear_hit_rate']:.2f})")
            if metrics["kill_rate"] <= active_thresholds.get("low_kill_rate", -1):
                reasons.append(f"{archetype}: low_kill_rate ({metrics['kill_rate']:.2f})")
            if metrics["kill_rate"] >= active_thresholds.get("high_kill_rate", float("inf")):
                reasons.append(f"{archetype}: high_kill_rate ({metrics['kill_rate']:.2f})")

        if reasons:
            flagged.append({
                "genome_id": event.get("genome_id"),
                "generation": event.get("generation"),
                "timestamp": event.get("timestamp"),
                "reasons": reasons,
            })

    return flagged


# Standard metrics for situational-shift analysis — the four that most directly
# capture archetype performance differences between baseline and adapted situations.
_SITUATION_SHIFT_METRICS = [
    "damage_per_spawn",
    "damage_taken_per_spawn",
    "kill_rate",
    "rear_hit_rate",
]


def archetype_situation_shift(
    events: Iterable[dict[str, Any]], metric: str
) -> dict[str, dict[str, float]]:
    """For each archetype that has data in both baseline and adapted situations,
    computes the shift on `metric` between those two situations and returns one
    record per archetype. Each record has:

        {
            "baseline": <float>,
            "adapted":  <float>,
            "delta":    adapted - baseline,
            "percent_change": delta / baseline (guards baseline==0:
                              float("inf") if adapted > 0 else 0.0).
        }

    Archetypes where the metric can't be computed for at least one situation
    (because the situation simply didn't occur in the data) are still included
    if their aggregated counters allow a valid _derived_metrics call — but
    archetypes entirely absent from the dataset are not.

    Reuses archetype_situation_profiles (which is unit-tested) rather than
    re-implementing the aggregation math — the only new work here is the
    baseline-vs-adapted diff."""
    profiles = archetype_situation_profiles(events)
    result: dict[str, dict[str, float]] = {}
    for archetype, situations in profiles.items():
        baseline = situations.get("baseline", {}).get(metric)
        adapted = situations.get("adapted", {}).get(metric)
        if baseline is None or adapted is None:
            continue
        delta = adapted - baseline
        if baseline == 0:
            percent_change = float("inf") if adapted > 0 else 0.0
        else:
            percent_change = delta / baseline
        result[archetype] = {
            "baseline": baseline,
            "adapted": adapted,
            "delta": delta,
            "percent_change": percent_change,
        }
    return result


def flag_situational_shift(
    events: Iterable[dict[str, Any]],
    metrics: Optional[list[str]] = None,
    threshold_pct: Optional[float] = None,
) -> list[dict[str, Any]]:
    """For each archetype/metric combination, flags cases where the archetype's
    behavior shifts meaningfully between baseline and adapted situations.

    A shift is flagged when |percent_change| >= threshold_pct (default:
    config.THRESHOLDS["high_situational_shift_pct"]), or when baseline == 0 and
    adapted > 0 (a metric that was zero under baseline appears under adaptation
    — always meaningful regardless of the threshold number).

    Returns one record per flagged (archetype, metric) pair:
        {
            "archetype":       str,
            "metric":          str,
            "baseline":        float,
            "adapted":         float,
            "delta":           float,
            "percent_change":  float,  # float("inf") when baseline==0, adapted>0
        }

    This is the function that most directly answers "how does behavior differ
    across situations" — unlike flag_threshold_exceeding (which flags individual
    trials based on absolute domain boundaries) and
    archetype_statistical_outliers (which flags a trial relative to that
    archetype's own distribution), this operates on the aggregated
    situation-split totals and measures whether the aggregate behavior itself
    shifts between baseline and adapted enemy states.

    Use archetype_situation_shift to see all archetypes' shifts (flagged or not);
    this function is the "above threshold only" filter for the console report."""
    active_threshold = (
        threshold_pct
        if threshold_pct is not None
        else config.THRESHOLDS.get("high_situational_shift_pct", 0.5)
    )
    active_metrics = metrics if metrics is not None else _SITUATION_SHIFT_METRICS

    flagged: list[dict[str, Any]] = []
    events_list = list(events)  # consume once — archetype_situation_shift takes Iterable
    for metric in active_metrics:
        shifts = archetype_situation_shift(events_list, metric)
        for archetype, shift in shifts.items():
            pct = shift["percent_change"]
            # A baseline==0, adapted>0 edge case always qualifies regardless of threshold.
            qualifies = (pct == float("inf")) or (abs(pct) >= active_threshold)
            if qualifies:
                flagged.append(
                    {
                        "archetype": archetype,
                        "metric": metric,
                        "baseline": shift["baseline"],
                        "adapted": shift["adapted"],
                        "delta": shift["delta"],
                        "percent_change": pct,
                    }
                )
    return flagged


def genome_mix_series(events: Iterable[dict[str, Any]]) -> list[tuple[int, dict[str, float]]]:
    """Average enemy_behavior_mix weight per generation, across every
    report_outcome trial regardless of whether it carries the newer
    per-archetype combat breakdown. enemy_behavior_mix has been part of the
    genome contract since before this instrumentation existed, so unlike
    the functions above this works on the full session_log.jsonl history
    immediately — a coarser "what mix does the evolutionary loop keep
    proposing" view that complements the per-archetype combat charts
    (which only populate once new trials are played with the updated
    client)."""
    by_generation: dict[int, list[dict[str, float]]] = {}
    for event in events:
        generation = event.get("generation", 0)
        mix = event.get("params", {}).get("enemy_behavior_mix", {})
        if mix:
            by_generation.setdefault(generation, []).append(mix)

    series: list[tuple[int, dict[str, float]]] = []
    for generation, mixes in sorted(by_generation.items()):
        archetypes: set[str] = set()
        for mix in mixes:
            archetypes.update(mix.keys())
        averaged = {archetype: sum(m.get(archetype, 0.0) for m in mixes) / len(mixes) for archetype in archetypes}
        series.append((generation, averaged))
    return series
