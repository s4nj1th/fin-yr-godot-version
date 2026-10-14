# Metrics Reference

What the system records and measures. Field names match the code.

## 1. Encounter genome (evolved by the backend)

| Gene | Range | Meaning |
|---|---|---|
| `enemy_density` | 0 – 0.5 | share of rooms that receive an enemy |
| `enemy_difficulty_scale` | 0.6 – 1.6 | multiplier on every enemy's HP and attack |
| `enemy_behavior_mix` | 4 weights summing to 1 | spawn share of `melee_chaser`, `melee_hit_run`, `ranged_kite`, `ranged_strafe` |

Fixed environment (not evolved, sent with every genome): 160×160 map, 280
room attempts, trap/chest/heal densities 0.15 each, `map_seed` 123. See
[EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md).

## 2. Trial telemetry (sent by the client after each trial)

| Field | Meaning |
|---|---|
| `time_taken_sec` | seconds from start to the end of the trial |
| `damage_taken` | HP lost to enemies over the trial |
| `health_remaining` | HP at the end of the trial |
| `deaths_retries` | deaths during the trial (the player respawns at the start) |
| `enemies_killed` / `total_enemies` | kills and enemies spawned |
| `chests_opened` / `total_chests` | chests collected and placed |
| `traps_triggered` | traps stepped on |
| `exploration_percent` | share of walkable tiles revealed |
| `exit_reached` | the player reached the exit |
| `quit_early` | the player pressed R |
| `enemy_behavior_breakdown` | per archetype, split into baseline and adapted enemies: `spawned`, `attacks_on_player`, `damage_dealt_to_player`, `hits_taken_from_player`, `damage_taken_from_player`, `rear_hits_taken`, `killed_by_player` |

The minimal criterion uses `quit_early`, `exit_reached`, `time_taken_sec`
(≥ 15 s, < 240 s) and `deaths_retries` (≤ 3).

## 3. Playstyle profile (client, within a session)

`BackAttackPreference`, `DirectAttackPreference`, `StealthPreference`,
`Aggression`, `RetreatPreference`, `KitingPreference` — each an
exponentially decayed ratio (decay 0.8) — and `Confidence`, which reaches 1
after three hits or line-of-sight breaks. They drive the `AdaptationDirector`
(see [ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md)).

## 4. Backend statistics (`GET /api/stats?player_id=…`)

`hall_of_fame_size` (archive size), `real_evaluations`, `human_evaluations`,
`bot_evaluations`, `auto_accepted`, `auto_rejected`, `surrogate_ready`,
`surrogate_noise_std`, `gate_uncertainty`, `bootstrap`,
`bootstrap_designs_served`, and the LLM-mutation counters.

## 5. Evaluation metrics (simulation study in the research paper)

| Metric | Definition |
|---|---|
| Archive size | designs accepted after a fixed number of playthroughs |
| Archive E[fail] | mean of 1 − p*(g) over the archive: the chance a design drawn from it would be failed |
| Played failure rate | share of played designs the player failed |
| Challenge | median of expected damage / damage budget over the archive |
| Diversity | mean pairwise distance between designs in the normalized 6-gene space |
| Surrogate quality | MSE and calibration error against p*, decision accuracy on held-out designs |

Charts of a real run: `Backend/generate_metrics_plots.py --player NAME`
(archive growth, enemy-gene trajectories, outcome breakdown, evaluation
split). Charts compare only logged events; nothing is synthesized.
