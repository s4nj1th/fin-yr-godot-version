# Behavior Analytics — Per-Archetype Telemetry and Visualization

This document describes the instrumentation and analysis pipeline that
answers, from data rather than from reading the movement code: how does
each of the seven enemy archetypes (`melee_chaser`, `melee_hit_run`,
`ranged_kite`, `ranged_strafe`, `melee_charger`, `ranged_bomber`,
`melee_shield`) actually behave in combat, how does that differ from the
other archetypes, and how does it change across the evolutionary loop's
generations. It is separate from, and downstream of, the two systems it
draws on — [ENEMY_AI.md](ENEMY_AI.md) (what each archetype's movement rule
does), [ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md) (the
player-adaptive countermeasures), and
[EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md) (the evolution loop that evolves
`enemy_behavior_mix`).

## Why this exists

Before this, the only telemetry a trial reported was whole-dungeon
aggregates — total damage taken, deaths, kills, chests, exploration percent
(`Backend/models.py`'s `TelemetryMetrics`). That answers "was this genome
good or bad" for the minimal criterion, but not "which archetype did
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
`AdaptationDirector` tactical order (rear defence and/or the stealth
countermeasure, intercept or search) at the time of the event. The situation split is recorded from
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
elites_spawned          (how many of this archetype were elites)
telegraphed_attacks     (charges and bomb throws started)
attacks_dodged          (telegraphed attacks that missed)
hits_blocked            (player hits absorbed by a shield)
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
inside the same `telemetry` object `AdaptiveEvolutionManager` already logs verbatim to
`Backend/data/session_log.jsonl` — no changes to `adaptive_evolution.py`'s logging or
Hall-of-Fame logic were needed.

**Every `session_log.jsonl` entry recorded before this instrumentation
existed has no `enemy_behavior_breakdown`.** The analysis tooling below
treats that as "no data yet," not as zero — see
`behavior_analytics.events_with_behavior_breakdown`.

## Analysis: `Backend/behavior_analytics.py` + `Backend/analyze_behavior_telemetry.py`

Split the same way `adaptive_evolution.py`/`main.py` are: `behavior_analytics.py` is pure,
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

Five charts (plus a console-only threshold report), generated
independently — later ones are skipped (with a printed reason, not a
silent gap) if their prerequisite data isn't there yet:

1. **`genome_mix_evolution.png`** — average `enemy_behavior_mix` weight per
   archetype, per generation. Works on *every* `report_outcome` trial ever
   logged, including pre-instrumentation ones, since `enemy_behavior_mix`
   has been part of the genome contract since the evolutionary loop's
   first version. Answers "what mix does the search keep proposing," the coarse
   half of "how it's evolving."
2. **`archetype_profile.png`** — one bar-chart-per-metric small multiple
   (`damage_per_spawn`, `damage_taken_per_spawn`, `kill_rate`,
   `rear_hit_rate`), bars = archetypes, baseline+adapted combined. Answers
   "how is archetype A different from archetype B."
3. **`archetype_evolution.png`** — the same headline metrics as a line
   chart across genome generations, one line per archetype. The finer half
   of "how it's evolving": not just the mix weight the search proposes, but what
   each archetype actually *does* in combat as generations pass.
4. **`situational_comparison.png`** — grouped bars (baseline vs. adapted)
   per archetype per metric. Answers "does the adaptation director's
   countermeasure actually change this archetype's outcomes" — the
   concrete "different situations" comparison, using the real per-enemy
   attribution described above, not an estimate.
5. **`archetype_outliers.png`** — box plot of the per-trial *spread* for
   each archetype/metric, with statistical outlier trials marked. See
   "Outliers and thresholds" below — this is the chart (plus the console
   threshold report) that turns "here's an average" into "here's what's
   actually unusual and worth a second look."

## Outliers and thresholds

The first four charts above show *aggregates* — one number (or one bar per
situation) per archetype. That hides two things worth surfacing
separately, because they answer different questions than "what's the
average":

1. **Statistical outliers** — a single trial where an archetype behaved
   very differently from its *own* other trials. `damage_per_spawn`
   averaging to 8 across 20 trials could mean "consistently 7-9," or it
   could mean "consistently ~5, except one trial where it spiked to 80" —
   the average can't tell those apart, and they mean very different
   things (the first is a stable archetype; the second is either a real
   edge-case genome or a telemetry bug). `behavior_analytics.find_iqr_outliers`
   (Tukey's fences: below Q1-1.5×IQR or above Q3+1.5×IQR, needs ≥4 samples)
   flags these per archetype per metric — data-driven, no fixed number.
   `plot_archetype_outliers` renders it as a box plot per metric with
   outlier trials marked as a red `x`.
2. **Threshold-exceeding trials** — a trial that crosses a fixed,
   game-design-meaningful boundary regardless of what the rest of the data
   looks like. `config.THRESHOLDS` (e.g. `high_enemy_count: 60` — the main
   dungeon caps spawns at 160, so a trial spawning more than 60 is worth a
   look; `near_max_deaths: 2` — one death short of failing the
   minimal criterion outright) — see `config.py`'s own comments for the
   full list and reasoning per threshold. `flag_threshold_exceeding`
   returns one record per crossed trial naming every threshold it hit;
   `analyze_behavior_telemetry.py` prints this as a console report (no PNG
   — the payload is "which genome_id and why," not a shape).
3. **Situational shift** — how aggregate archetype behavior changes across
   *situations* (baseline vs. adapted enemy states), answering "how does
   behavior differ across situation A and situation B" at the aggregate level.
   `archetype_situation_shift` computes the `baseline`, `adapted`, `delta`, and
   `percent_change` per archetype for a given metric. `flag_situational_shift`
   filters to combinations where `|percent_change| >= config.THRESHOLDS["high_situational_shift_pct"]`
   (0.5 = 50% shift) or where baseline == 0 and adapted > 0. Printed in the
   console report alongside threshold-exceeding trials.

**Why this matters here specifically, not just as a generic statistics
feature:**

- **The minimal criterion is coarse** (`_evaluate_minimal_criterion` only
  checks exit-reached/quit/time/deaths). A genome can pass it while
  producing something degenerate — e.g. 118 enemies crammed into a small
  room, or one archetype dealing 10x its usual damage because a difficulty
  roll interacted badly with a density roll. The minimal criterion has no
  way to catch that; threshold/outlier scanning over the Hall of Fame does,
  cheaply, without playing every genome again.
- **It's the sharper version of "how is archetype A different from
  archetype B."** A bar chart says "ranged_kite deals more damage on
  average." An outlier scan says "ranged_kite is usually comparable to the
  others, except in this one genome where it's wildly disproportionate" —
  a stronger, more specific claim, and the kind of finding worth actually
  writing up.
- **It's a cheap first step toward the "no MAP-Elites-style
  quality-diversity archive yet" gap** already named in
  [EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md)'s known scope
  boundaries. Bucketing the Hall of Fame by threshold-crossed vs. not
  (or by which threshold) is a crude behavioral-descriptor split — not a
  real QD grid, but the same idea in miniature, and a natural place to
  grow from if a proper MAP-Elites archive gets built later.
- **It's a bug-detection tripwire.** The mislabeled-evaluator bug found
  while building the bot evaluator is
  exactly the kind of thing an outlier scan would have caught faster than
  eyeballing an averaged chart — an archetype's `damage_per_spawn` jumping
  10x in one trial is either a great thesis finding or a bug, and the
  outlier flag forces that question to get asked instead of silently
  averaging it away.

**Honest limitation**: with few trials per archetype (single digits),
count-based ratios like `rear_hit_rate` produce noisy-looking "outliers"
that are really just small-sample variance, not a real pattern — Tukey's
fences don't know the difference between "genuinely unusual" and "not
enough data yet." Treat outlier flags as *leads to investigate*, not
conclusions, until archetype sample sizes are large enough that the
spread itself is trustworthy.

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

- **Sparse real data, same as the rest of the human-coupled loop.** When
  this pipeline was built (September 2026) `session_log.jsonl` had 35 total entries (25 tagged human, 10 bot;
  29 with complete `enemy_behavior_breakdown` telemetry).`genome_mix_evolution.png`
  is the only chart with historical data across all trials.
- **The scripted bot evaluator (`EVOLUTIONARY_DESIGN.md` "Scripted bot
  evaluator") is a fast way to grow volume, not a validated stand-in for a
  human.** It's a deliberately simple exit-rushing heuristic. Every bot
  trial is tagged `evaluator_type=bot` specifically so it stays comparable
  against human data rather than assumed equivalent to it — run
  `--evaluator human` vs. `--evaluator bot` and look for archetype profiles
  that diverge before treating bot volume as representative of how a human
  actually experiences each archetype.
- **`adapted` is per-enemy-true but still coarse.** It's a binary "carried
  an active tactical order when the event happened" (orders can change after
  spawn through live re-tasking), not a measure of *how strongly* that
  order shaped the specific event (`GuardRear`'s `TurnSpeedMultiplier` and
  `SearchLastKnownPosition`'s `SearchRadius` both vary continuously with
  profile confidence — see `AdaptationDirector.CreateOrderForNewEnemy`).
  Two "adapted" data points can carry meaningfully different order
  strengths and this pipeline can't currently tell them apart.
- **Static PNGs, not a live view.** Chosen deliberately (see above) — if
  interactive, continuously-updating exploration during development
  becomes worth the added complexity, this is the seam to build a small
  dashboard behind, reusing `behavior_analytics.py` unchanged.
