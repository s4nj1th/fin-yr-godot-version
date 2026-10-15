# Project Context — Game Client Runtime

Project: **Adaptive Dungeon Crawler** — adaptive learning of enemy encounters
in a procedural dungeon crawler.
Engine: Godot 4.7 (.NET / C#), .NET 8. Backend: Python 3, FastAPI.

This file describes the Godot client as it runs today. The evolution backend
is described in [EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md), the enemy AI
in [ENEMY_AI.md](ENEMY_AI.md) and [ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md).

## 1. Scenes

| Scene | Purpose |
|---|---|
| [Main.tscn](../Main.tscn) | **The game** (startup scene): the dungeon, the evolution loop, the adaptive enemy director |
| [Scenes/MeleeAiTest.tscn](../Scenes/MeleeAiTest.tscn) | Sandbox: two melee archetypes in a small arena |
| [Scenes/RangedAiTest.tscn](../Scenes/RangedAiTest.tscn) | Sandbox: two ranged archetypes in a small arena |
| [Scenes/EnemyGallery.tscn](../Scenes/EnemyGallery.tscn) | All seven archetypes, regular and elite, with the floor telegraphs |

## 2. One trial, end to end

[`GameManager.cs`](../Scripts/Core/GameManager.cs) drives the loop:

1. **Fetch a design.** `ApiClient.RequestNextGenomeAsync(PlayerId)` →
   `POST /api/next_candidate?player_id=…`. If the backend is unreachable, the
   client uses its built-in default design and the same fixed map, so play
   is never blocked.
2. **Build the dungeon.** `RegenerateDungeon` seeds the generator with the
   design's `map_seed` (fixed at 123, so every trial has the same layout),
   places up to 280 non-overlapping rooms (4–12 × 4–10 tiles) on a 160×160
   grid, joins them with corridors, and rolls each room except the start
   room for a trap, chest, heal tile or enemy spawn against cumulative
   thresholds. It logs `[Dungeon] seed=… rooms=… enemies=… elites=…` and the
   design's genes.
3. **Spawn enemies.** Each spawn point gets a pack whose size is drawn from
   the design's `pack_size` (up to 160 enemies in all); each member's
   archetype is drawn from the behaviour mix and it is an elite with
   probability `elite_rate`. `EnemyArchetypes.WithModifiers` applies the
   difficulty scale (HP, attack) and tempo (speed, wind-ups, cooldowns).
   Each new enemy also gets a tactical order from the `AdaptationDirector`
   (Section 4), except in study mode.
4. **Play.** The player moves on the grid (WASD), attacks with Space (every
   0.35 s), and must reach the exit under fog of war. On death the player
   respawns at the start with full health and the trial continues.
5. **End the trial** on reaching the exit, pressing R (quit), or after 240 s.
   The telemetry (time, deaths, damage, kills, chests, traps, exploration,
   per-archetype combat counters) goes to `POST /api/report_outcome`, which
   applies the minimal criterion and returns pass or fail. The next design is
   then fetched.

## 3. Players and the bot

- `GameManager.PlayerId` (Inspector) or `--player=NAME` selects whose archive
  the backend evolves; the HUD shows it.
- `--autoplay` lets `BotPlayerPolicy` drive the player (BFS to the exit,
  backing off below 30% health). Bot trials are tagged `evaluator_type=bot`
  and, unless `--player` is given, belong to player `bot`.
  `--autoplay-max-trials=N` and `--autoplay-timescale=X` control a batch run;
  `--bot-dodge=Q` makes the bot step out of telegraphed attacks with
  probability Q; `--backend-url=URL` points the client at another backend.

## 3a. Other modes

- **Duel mode** (`--duel-plan=PLAN.json --duel-out=OUT.jsonl`,
  `Scripts/Core/GameManager.Duel.cs`): the scripted fighter against packs of
  one archetype in an open arena; measures engagement damage for the
  simulated player (`tools/run_duels.py`).
- **Study mode** (`--study`, `Scripts/Core/GameManager.Study.cs`,
  `Scripts/UI/StudyFlow.cs`): one session of the human study — participant
  code, consent, briefing, timed play, replay block, questionnaire
  ([HUMAN_STUDY_PROTOCOL.md](HUMAN_STUDY_PROTOCOL.md)).

## 4. Within-session tactical adaptation

Separate from the evolution loop, the client adapts enemy *tactics* during a
session (see [ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md)):

- `PlayerBehaviorTelemetry` counts hits by direction, line-of-sight breaks,
  retreats and kiting; `PlayerPlaystyleProfile` blends them with decay 0.8
  and a confidence that reaches 1 after three hits or line-of-sight breaks.
- `AdaptationDirector` activates *rear defence* after three rear hits (rear
  preference > 0.35, confidence > 0.5) and a *stealth countermeasure* after
  three line-of-sight breaks (confidence > 0.5), each with an 8 s cooldown
  and minimum duration. Newly spawned enemies receive the active order with
  probability equal to the confidence clamped to [0.3, 0.9]; with live
  re-tasking, up to 6 enemies already within 10 tiles of the player take it
  when it switches on, and all drop it when it switches off.
- Enemies with a stealth order use `EnemyPerception` (unaware → tracking →
  investigating 3 s → searching 5 s → returning 3 s), `GridLineOfSight` and
  BFS (`GridPathfinder`) to search the player's last known position.

## 5. Fog of war

A spatial-shader quad over the map darkens everything outside the reveal
radius (6.5 tiles), with a dimmer state for tiles already discovered; a
discovery texture records what the player has seen.

## 6. Tile codes

| Code | Tile | Code | Tile |
|---|---|---|---|
| 0 | wall | 5 | enemy spawn |
| 1 | floor | 6 | chest |
| 2 | corridor | 7 | start |
| 3 | door (reserved; not placed by the current generator) | 8 | exit |
| 4 | trap | 9 | heal |

## 7. Automated tests

- `Tests/` (xUnit, `dotnet test`, 76 tests): the engine-independent AI code —
  archetype catalog and modifiers, telegraph geometry, attack-direction
  classification, line of sight, pathfinding, playstyle profile, adaptation
  director and live re-tasking, behaviour telemetry, exit bot and fighter bot.
- `Backend/tests/` (pytest, 141 tests): genome spaces, surrogate, evolution
  manager, remedies and the per-archetype criterion, HTTP API and study endpoints, simulated player,
  LLM-output parsing, behaviour analytics, study export and analysis.
- [INTEGRATION_TESTING.md](INTEGRATION_TESTING.md): end-to-end runs of the
  real game against the real backend.
