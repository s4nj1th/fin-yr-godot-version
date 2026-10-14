> **Historical note.** This is a dated hand-off note from Phase 2's first half. It describes the first
> version of the backend — one shared archive, the map evolved together with the enemies, "MCC" naming —
> which was revised after the Phase 2 panel review. For the current design see
> [EVOLUTIONARY_DESIGN.md](../EVOLUTIONARY_DESIGN.md) and the project [README](../../README.md).

# Project State and Novelty — 2026-09-07

This document answers three questions as of `master` @ `8f83742`: what changed
in the last merge, what the project currently does end-to-end, and what is
actually novel about it (for writeup/thesis framing). It supplements, not
replaces, the deeper per-system docs: [EVOLUTIONARY_DESIGN.md](../EVOLUTIONARY_DESIGN.md),
[ENEMY_AI_ADAPTATION_PLAN.md](../ENEMY_AI_ADAPTATION_PLAN.md), [ENEMY_AI.md](../ENEMY_AI.md).

## 1. What just changed — merging two branches of the same project

Two people independently built large, overlapping chunks of this project at
the same time: this branch (`feature/evolutionary-loop`, by YoganandS) and
`feature/evolutionary-pcg-backend` (by s4nj1th / Adithya-Monish-Kumar-K),
which had already been merged into `origin/master` via PR #1 before this
session started.

The two branches had, independently:

- Both built the **same human-coupled MCC backend** (`Backend/*.py`) from the
  same design doc, differing only in comments/citations.
- Both wired the **same evolutionary-loop integration** into `GameManager.cs`
  (fetch genome → play trial → report outcome → fetch next genome), but only
  this branch's copy had the later async-void try/catch safety net
  (`484cee7`) and DTO-drift fixes.
- Both gave enemies the **same distinct melee/ranged behavior split**
  (`melee_hit_run` / `melee_chaser` / `ranged_kite`) and the same attack-FX
  refactor (lunge tween for melee, projectile mesh for ranged) — origin's
  copy (`fdd25d3`, Adithya-Monish-Kumar-K) and this branch's copy turned out
  to be functionally identical.
- Only this branch had the **adaptive enemy AI** system (telemetry → playstyle
  profile → adaptation director → BFS/A\* investigate-and-search behavior).

Reconciling this required an actual line-by-line merge, not a fast-forward:

- Kept this branch's version of the 9 near-duplicate files outright.
- Kept this branch's version of `GameManager.cs`, `EnemyController.cs`,
  `DungeonData.cs`, `ApiClient.cs` — already a superset of origin's changes,
  with the extra fixes.
- **Caught mid-merge**: Git's auto-merge silently duplicated six whole C#
  methods (`RequestNextGenomeAsync`, `EndTrialAndAdvanceAsync`,
  `ComputeExplorationPercent`, `CountTilesOfType`,
  `CreateEnemyDefinitionForRoll`, `ScaleStats`) by inserting origin's older
  copies alongside this branch's newer ones, without raising conflict
  markers (the two copies landed in non-overlapping diff hunks). Removed the
  stale duplicates by hand; this would not have compiled otherwise.
- Verified after resolution: `dotnet build` — 0 warnings, 0 errors; AI test
  suite — 28/28 passing; `Backend/main.py` imports cleanly.
- Pushed straight to `origin/master` (`ef51974..8f83742`) — this branch's
  Godot-side implementation is now what ships.

**Known stale doc, not touched by this merge**: `docs/PROJECT_CONTEXT.md`
still says enemies "do not currently use A\*, BFS, or line-of-sight checks"
and that the AI test scenes should be prioritized over `Main.tscn`. Both are
now wrong — the adaptive AI uses exactly those, and `Main.tscn` is the real
integrated game loop. Worth a follow-up pass; left alone here since it's a
pre-existing documentation gap, not something this merge introduced.

## 2. What's on `master` right now

**Engine**: Godot 4.7 (Mono/C#), .NET 8.0. Entry point configured in
`project.godot` is `Scenes/MeleeAiTest.tscn` (an isolated 2-enemy sandbox);
the actual full game is `Main.tscn`, run separately.

**Core gameplay loop** (`Scripts/Core/GameManager.cs`, `Scripts/Entities/`):
a 3D top-down procedurally generated dungeon (rooms + corridors), shader-based
fog-of-war with discovery memory, a player capsule with grid-based movement
and melee attack, and up to 120 enemies spawned per dungeon from three/four
class archetypes (Scout/Brute/Guardian/Sentinel), each with HP/attack/defense
stats and one of four AI behaviors.

**Enemy AI** (`Scripts/AI/`, `Scripts/Entities/EnemyController.cs`):
- Four movement archetypes: `melee_chaser` (relentless approach),
  `melee_hit_run` (approach, strike, disengage), `ranged_kite` (maintain
  distance band, retreat/strafe), `ranged_strafe` (approach then circle).
- Distinct attack FX per archetype: melee lunge tween, ranged projectile mesh.
- Perception model (`EnemyPerception.cs`): Unaware → Tracking → Investigating
  → Searching, driven by real line-of-sight rather than omniscient player
  position.
- Grid pathfinding (`GridPathfinder.cs`, BFS) and line-of-sight (`GridLineOfSight.cs`)
  for the investigate/search states, replacing pure Manhattan-greedy movement
  when the enemy has lost the player.
- **Adaptive countermeasures** (`AdaptationDirector.cs`,
  `PlayerBehaviorTelemetry.cs`, `PlayerPlaystyleProfile.cs`): the game tracks
  the player's rear-attack rate and line-of-sight-break rate over a rolling
  window, and once there's enough evidence (≥3 occurrences, confidence over
  threshold, evidence not diluted by unrelated events), newly spawned enemies
  start guarding their rear (faster turn-to-threat) or investigating the
  player's last known position instead of losing track passively. Effects
  decay back off if the player changes tactics, with a minimum on-duration so
  a single stray event can't flip a countermeasure straight back off.
  Existing enemies keep whatever order they spawned with — only new spawns
  pick up an updated read of the player. This is the completed "first
  milestone" from `ENEMY_AI_ADAPTATION_PLAN.md` (phases 1–5 of 7; the
  encounter-director phase 6 and formal evaluation phase 7 are not built).

**Evolutionary loop / procedural generation** (`Backend/`, human-coupled MCC):
a Python FastAPI service implementing Minimal Criterion Coevolution (Brant &
Stanley 2017) over dungeon-generation parameters — map size, room count,
trap/chest/heal/enemy density, and the enemy-behavior mix. The live player is
the evaluator: each playthrough either passes or fails a minimal criterion
(reached exit, not manually quit, not trivially fast/slow, deaths under a
cap), and passing genomes join a monotonically growing Hall of Fame that
future genomes mutate from. An online Gaussian-process surrogate
(`surrogate.py`) auto-decides confident candidates without spending a real
playthrough, and an optional, off-by-default LLM-guided mutation arm
(`llm_mutation.py`, Anthropic/OpenAI/Ollama) can replace random Gaussian
mutation with a targeted one — ablatable and never load-bearing, since a
fresh checkout with no `.env` makes zero LLM calls.

**Debug UI** (`Scripts/UI/`): a minimal HUD showing HP, score, current trial
telemetry, and last trial result; a local 8x8 debug grid rendered behind each
enemy visualizing what that enemy currently perceives (not a neural net, per
`ENEMY_AI.md` section 1).

**Automated tests**: `Tests/` — 28 xUnit tests covering the adaptation
director, attack-direction classifier, grid pathfinder, line-of-sight, and
playstyle profile. `Backend/tests/` covers the Python side (genome, mcc,
surrogate, llm_mutation).

## 3. What's actually novel here

Three claims, stated at the strength the code actually supports (see each
doc's own "known limitations" section for the honest boundaries):

1. **Human-coupled Minimal Criterion Coevolution as a live PCG driver.**
   Most player-adaptive PCG work either uses a fixed population GA with
   scripted fitness, or couples to the player only as a fitness signal for an
   offline search. Here the *player themselves* is the second half of the
   MCC pass/fail criterion, evaluated in real time, with the archive growing
   across actual play sessions rather than simulated ones. The honest
   caveat: this evolves a fixed generator's *parameters*, not the level
   *structure/topology* — stated explicitly in `EVOLUTIONARY_DESIGN.md`
   rather than oversold.

2. **Surrogate-gated human evaluation.** Because a human playthrough is
   expensive (minutes, not milliseconds), the system fits an online GP
   surrogate on real outcomes and only spends a real playthrough on
   candidates the surrogate is still uncertain about — a documented,
   explicitly-justified deviation from textbook GP classification (using a
   regressor on binary labels to get the variance a classifier doesn't
   expose), rather than a hidden approximation.

3. **Evidence-gated, decaying, per-spawn adaptive enemy AI**, separate from
   and orthogonal to the evolutionary loop. Enemies don't get player-position
   telepathy or react to single events; they accumulate a rolling behavioral
   profile (rear-attack rate, stealth rate) and only newly-spawned enemies
   act on it once the evidence clears a confidence and dilution threshold,
   with decay if the player's tactics shift and a minimum on-duration so the
   response doesn't flicker. This is deliberately scoped to two
   countermeasures (rear defense, last-known-position search) rather than a
   general learned policy — a small, inspectable, and testable adaptive
   system instead of an opaque one.

Together: the level generation adapts to the player across sessions (slow,
persistent, evolutionary), while the enemy AI adapts to the player within a
session (fast, decaying, rule-based) — two different adaptation loops at two
different timescales, sharing nothing but the same player.

## 4. Honest gaps (don't oversell these in a writeup)

- No structural/topological level evolution — flat-parameter genome only.
- No MAP-Elites-style quality-diversity archive — pass/fail only.
- Single real evaluator (the live player) — no scripted bot agents to scale
  archive growth or validate bot-vs-human telemetry as a proxy.
- Enemy AI adaptation phases 6 (group/encounter director) and 7 (formal
  evaluation scenarios) from the adaptation plan are not built.
- No automated C# tests for `GameManager`/`EnemyController` directly (Godot's
  Node lifecycle makes that hard without a Godot test runner) — the AI logic
  they call into is unit-tested, the Node wiring itself is not.
