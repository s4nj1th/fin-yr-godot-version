# Behavior Analytics — Per-Archetype Telemetry and Visualization

This document describes the instrumentation and analysis pipeline that
answers, from data rather than from reading the movement code: how does
each enemy AI archetype (`melee_chaser`, `melee_hit_run`, `ranged_kite`,
`ranged_strafe`) actually behave in combat, how does that differ from the
other archetypes, and how does it change across the evolutionary loop's
generations. It is separate from, and downstream of, the two systems it
draws on — [ENEMY_AI.md](ENEMY_AI.md) (what each archetype's movement rule
does), [ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md) (the
player-adaptive countermeasures), and
[EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md) (the MCC loop that evolves
`enemy_behavior_mix`).

## Why this exists

Before this, the only telemetry a trial reported was whole-dungeon
aggregates — total damage taken, deaths, kills, chests, exploration percent
(`Backend/models.py`'s `TelemetryMetrics`). That answers "was this genome
good or bad" for the MCC minimal criterion, but not "which archetype did
the damage," "which archetype gets exploited from the rear," or "does the
adaptive rear-guard/search countermeasure actually change an archetype's
outcomes." Answering those needs telemetry attributed to the specific
enemy and its behavior type at the moment of each combat event, not just a
trial-level sum.

## What's recorded, and where

### Client side: `Scripts/AI/EnemyBehaviorTelemetry.cs`

A per-trial (reset every `RegenerateDungeon`) counter set, keyed by
archetype and then by **situation** — `baseline` vs. `adapted`, meaning
whether the specific enemy involved carried an active
`AdaptationDirector` tactical order (rear-guard and/or last-known-position
search) at the time of the event. The situation split is recorded from
that enemy's own `TacticalOrder` at the moment of each event, not
approximated afterwards from trial-level totals — an "adapted
`ranged_kite`" data point really is one, not a proportional estimate.

Per archetype/situation counters:

```text
spawned
attacks_on_player
damage_dealt_to_player
hits_taken_from_player
damage_taken_from_player
rear_hits_taken
killed_by_player
```

Wired into `Scripts/Core/GameManager.cs` at three points: enemy spawn
(`RecordSpawn`, alongside the existing `AdaptationDirector` order
assignment), `OnEnemyAttackedPlayer` (`RecordAttackOnPlayer`, now
subscribed per-enemy via a closure instead of a bare method group, so the
attacking enemy's archetype and situation are known), and
`TryAttackNearestEnemy` (`RecordPlayerHitOnEnemy` / `RecordKilledByPlayer`,
using the same `AttackDirection.Rear` classification the adaptation
director already computes).

### Wire contract: `enemy_behavior_breakdown`

`EnemyBehaviorTelemetry.Snapshot()` is attached to the existing trial
report as one more field on `TelemetryMetrics`
(`Scripts/Data/DungeonData.cs` / `Backend/models.py`), sent through the
existing `POST /api/report_outcome` call — no new endpoint. Shape:

```json
{
  "melee_chaser": {
    "baseline": { "spawned": 12, "attacks_on_player": 20, "damage_dealt_to_player": 96, "...": "..." },
    "adapted":  { "spawned": 3,  "attacks_on_player": 6,  "damage_dealt_to_player": 33, "...": "..." }
  },
  "ranged_kite": { "...": "..." }
}
```

This is additive: `enemy_behavior_breakdown` defaults to `{}` on both
sides, so an older client talking to a newer backend (or vice versa) still
round-trips the rest of `TelemetryMetrics` exactly as before. It rides
inside the same `telemetry` object `MCCManager` already logs verbatim to
`Backend/data/session_log.jsonl` — no changes to `mcc.py`'s logging or
Hall-of-Fame logic were needed.

**Every `session_log.jsonl` entry recorded before this instrumentation
existed has no `enemy_behavior_breakdown`.** The analysis tooling below
treats that as "no data yet," not as zero — see
`behavior_analytics.events_with_behavior_breakdown`.

## Analysis: `Backend/behavior_analytics.py` + `Backend/analyze_behavior_telemetry.py`

Split the same way `mcc.py`/`main.py` are: `behavior_analytics.py` is pure,
unit-tested aggregation math (`Backend/tests/test_behavior_analytics.py`,
no plotting import); `analyze_behavior_telemetry.py` is a thin CLI that
turns that math into charts. This was a deliberate choice over a live
dashboard — a rerun-on-demand script that emits PNGs is enough for
comparing archetypes and generations for the thesis writeup, and needs no
server to keep running.

Run it:

```bash
cd Backend
pip install -r requirements-analysis.txt   # adds matplotlib on top of requirements.txt
python analyze_behavior_telemetry.py
```

Reads `data/session_log.jsonl` by default (`--log` to point elsewhere),
writes PNGs to `Backend/reports/` by default (`--output-dir` to change).
Pass `--evaluator human` or `--evaluator bot` to restrict every chart to
one evaluator (see [EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md)
"Scripted bot evaluator") — the default `all` mixes both, which is fine
once you trust the bot's data is a reasonable proxy, but comparing
`--evaluator human` against `--evaluator bot` output is how you'd actually
check that before trusting it. Filtered runs get a `_human`/`_bot`
filename suffix so they don't overwrite the combined charts.

Four charts, generated independently — later ones are skipped (with a
printed reason, not a silent gap) if their prerequisite data isn't there
yet:

1. **`genome_mix_evolution.png`** — average `enemy_behavior_mix` weight per
   archetype, per generation. Works on *every* `report_outcome` trial ever
   logged, including pre-instrumentation ones, since `enemy_behavior_mix`
   has been part of the genome contract since the evolutionary loop's
   first version. Answers "what mix does MCC keep proposing," the coarse
   half of "how it's evolving."
2. **`archetype_profile.png`** — one bar-chart-per-metric small multiple
   (`damage_per_spawn`, `damage_taken_per_spawn`, `kill_rate`,
   `rear_hit_rate`), bars = archetypes, baseline+adapted combined. Answers
   "how is archetype A different from archetype B."
3. **`archetype_evolution.png`** — the same headline metrics as a line
   chart across genome generations, one line per archetype. The finer half
   of "how it's evolving": not just the mix weight MCC proposes, but what
   each archetype actually *does* in combat as generations pass.
4. **`situational_comparison.png`** — grouped bars (baseline vs. adapted)
   per archetype per metric. Answers "does the adaptation director's
   countermeasure actually change this archetype's outcomes" — the
   concrete "different situations" comparison, using the real per-enemy
   attribution described above, not an estimate.

Color usage follows this project's dataviz convention: a fixed hue is
assigned per archetype identity (`ARCHETYPE_COLORS` in
`analyze_behavior_telemetry.py`) and never re-cycled depending on which
archetypes happen to have data that run; a legend is shown whenever color
encodes a series shared across categories (the line charts, the
baseline/adapted grouped bars) and omitted when the x-axis already
direct-labels the same identity (the single-series bar charts); no chart
uses two y-axes — each metric gets its own subplot instead of sharing a
scale with a differently-scaled one.

## Honest limitations (don't oversell these in a writeup)

- **Sparse real data, same as the rest of the human-coupled loop.** As of
  this writing `session_log.jsonl` has 24 real trials, none carrying
  `enemy_behavior_breakdown` yet (it predates this instrumentation) — the
  three archetype charts need newly-played trials with the updated client
  to populate. `genome_mix_evolution.png` is the only chart with existing
  data to plot immediately.
- **The scripted bot evaluator (`EVOLUTIONARY_DESIGN.md` "Scripted bot
  evaluator") is a fast way to grow volume, not a validated stand-in for a
  human.** It's a deliberately simple exit-rushing heuristic. Every bot
  trial is tagged `evaluator_type=bot` specifically so it stays comparable
  against human data rather than assumed equivalent to it — run
  `--evaluator human` vs. `--evaluator bot` and look for archetype profiles
  that diverge before treating bot volume as representative of how a human
  actually experiences each archetype.
- **`adapted` is per-enemy-true but still coarse.** It's a binary "carried
  an active tactical order at spawn," not a measure of *how strongly* that
  order shaped the specific event (`GuardRear`'s `TurnSpeedMultiplier` and
  `SearchLastKnownPosition`'s `SearchRadius` both vary continuously with
  profile confidence — see `AdaptationDirector.CreateOrderForNewEnemy`).
  Two "adapted" data points can carry meaningfully different order
  strengths and this pipeline can't currently tell them apart.
- **Static PNGs, not a live view.** Chosen deliberately (see above) — if
  interactive, continuously-updating exploration during development
  becomes worth the added complexity, this is the seam to build a small
  dashboard behind, reusing `behavior_analytics.py` unchanged.
