# Backend — Adaptive Enemy Evolution

A FastAPI service that evolves each player's enemy encounters: a
player-in-the-loop evolutionary search with a minimal criterion (keep a
design if and only if the player clears it) and an uncertainty-gated Gaussian
process surrogate that decides confident designs without a playthrough. Only
the enemies evolve; the dungeon is fixed. Design rationale:
[`docs/EVOLUTIONARY_DESIGN.md`](../docs/EVOLUTIONARY_DESIGN.md).

## Run it

```bash
cd Backend
python3 -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn main:app --host 0.0.0.0 --port 8001 --reload
```

The game (`Scripts/Network/ApiClient.cs`) connects to `http://localhost:8001`.
The backend is optional: without it the game plays the default encounter on
the same fixed map, just without evolution.

## Endpoints

Every endpoint takes an optional `player_id` query parameter (default
`default`); each player has their own archive and surrogate.

| Method | Path | Returns |
|---|---|---|
| GET | `/api/health` | `{status, player_id, hall_of_fame_size}` |
| GET | `/api/stats` | archive size; real evaluations split into human and bot; surrogate auto-accepts and auto-rejects; fitted noise; gate and bootstrap settings; LLM counters |
| GET | `/api/players` | players with saved state |
| GET | `/api/config` | the evolved genes and their bounds, the fixed environment, gate settings, minimal criterion |
| POST | `/api/next_candidate` | the next design to play, with the surrogate's estimate (`prob`, `std`, `epistemic_std`) when it was consulted |
| POST | `/api/report_outcome` | body `{genome_id, telemetry, evaluator_type?, player_id?}`; returns `{passed, hall_of_fame_size, fail_reason, player_id}` |
| POST | `/api/study/session` | body `{participant_id, session_number}`; assigns the counterbalanced condition and returns the session's `player_id` |
| GET / POST | `/api/study/questionnaire` | the in-game questionnaire items / store one session's answers |
| POST | `/api/study/replay_sample` | `?player_id=&n=`: archived designs for the end-of-session replay block |
| POST | `/api/study/replay_outcome` | outcome of a replayed design; recorded only, never changes the archive or surrogate |

Interactive API docs: `http://localhost:8001/docs` while the server runs.

## Files

| File | Role |
|---|---|
| `main.py` | HTTP API |
| `adaptive_evolution.py` | `AdaptiveEvolutionManager` (one per player): proposal, uncertainty gate, minimal criterion, archive, persistence; `PlayerRegistry` |
| `genome.py` | enemy-only genome and genome spaces (12 genes, or the original 6): defaults, random designs, mutation, sanitization of untrusted input |
| `surrogate.py` | GP surrogate; `predict_detailed` returns probability, total and epistemic uncertainty |
| `llm_mutation.py` | optional LLM-guided mutation (off unless configured) |
| `simulated_player.py` | simulated player for offline runs and the paper's experiments, built from `duel_table.json` |
| `duel_table.json` | engagement damage measured in the game (`tools/run_duels.py`) |
| `study.py`, `study_items.json` | human-study conditions, replay block, questionnaire items |
| `config.py` | every setting, with the reason for each default |
| `models.py` | request/response schemas |
| `behavior_analytics.py`, `analyze_behavior_telemetry.py` | per-archetype combat charts ([docs](../docs/BEHAVIOR_ANALYTICS.md)) |
| `generate_metrics_plots.py` | charts of one player's run: archive growth, enemy-gene trajectories, outcomes, evaluation split |
| `view_generations_data.py` | the same run as a table of milestones |
| `live_dashboard.py` | live charts while someone plays |
| `auto_simulate_playthroughs.py` | offline run against the simulated player |
| `demo_client.py` | terminal demo of the HTTP API with a simulated player |

## Settings

Defaults live in `config.py`; the main ones can be overridden in `.env`
(copy `.env.example`):

| Variable | Default | Meaning |
|---|---|---|
| `SURROGATE_GATE_UNCERTAINTY` | `epistemic` | gate on epistemic or `total` uncertainty |
| `BOOTSTRAP_STRATEGY` | `random` | serve random designs (or the `default` one) until the first pass |
| `SURROGATE_WINDOW` | `0` | fit the surrogate on the last N outcomes only (0 = all) |
| `FILTER_PARENTS_BY_SURROGATE` | `0` | draw parents only from designs still predicted passable |
| `MAP_SEED` | `123` | the fixed dungeon layout |
| `GENOME_SPACE` | `full` | `full` (12 genes) or `legacy` (the original 6) |
| `SURROGATE_KERNEL` | `isotropic` | or `ard`: one length scale per gene |
| `PLAY_THRESHOLD` | `0` | feasibility-biased probing: play an uncertain design only if its predicted pass probability is at least this |
| `MIN_DAMAGE_TAKEN` | `0` | lower bound on challenge: a cleared trial costing less damage fails as `too_easy` |
| `EVOLUTION_DATA_DIR` | `Backend/data` | where player archives and the log are written |
| `LLM_MUTATION_MODE` | `auto` | `auto` / `on` / `off` for LLM-guided mutation |

## State

- `data/players/<player_id>.json` — one player's archive, pending designs,
  counters and surrogate training data.
- `data/session_log.jsonl` — append-only log of every played outcome and
  surrogate decision, each tagged with `player_id`.
- `data/study/*.jsonl` — study sessions, replays and questionnaire answers.

Both are created on first use and gitignored. Delete a player's file to reset
that player. `data/mcc_state.json` from the first version is not used.

## Try it without the game

```bash
python auto_simulate_playthroughs.py --skill intermediate --trials 100   # offline, simulated player
python generate_metrics_plots.py --player sim-intermediate               # charts of that run
python view_generations_data.py --player sim-intermediate
```

or, with the server running, `python demo_client.py --trials 20`.

## Tests

```bash
pip install -r requirements-dev.txt
pytest
```

135 tests: genome (enemy-only evolution, genome spaces, fixed environment,
sanitization, old archives), surrogate (epistemic/noise split, window, ARD),
evolution manager (bootstrap, both gates, parent filtering, remedies,
settings, players, persistence), HTTP API and study endpoints, simulated
player, study export and analysis, LLM-output parsing, behaviour analytics. End-to-end tests with the real game:
[`docs/INTEGRATION_TESTING.md`](../docs/INTEGRATION_TESTING.md).
