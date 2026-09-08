"""
Pure aggregation logic for per-archetype enemy AI telemetry, read from
Backend/data/session_log.jsonl. See docs/BEHAVIOR_ANALYTICS.md for the full
picture — this module answers "how did each enemy behavior archetype
actually perform, per situation, across trials"; analyze_behavior_telemetry.py
turns its output into charts.

Kept separate from the plotting script so the aggregation math is
unit-testable without importing matplotlib (matches the split already used
between mcc.py and main.py — logic vs. the thing that calls it).

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
from typing import Any, Iterable

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
