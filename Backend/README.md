# Dungeon Crawler MCC Backend

The evolutionary side of the game: a small FastAPI service implementing
human-coupled Minimal Criterion Coevolution over dungeon-generation genomes.
See [`docs/EVOLUTIONARY_DESIGN.md`](../docs/EVOLUTIONARY_DESIGN.md) at the
repo root for the full design rationale — this file is just setup/usage.

## Run it

```bash
cd Backend
python3 -m venv .venv
source .venv/bin/activate       # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn main:app --host 0.0.0.0 --port 8000 --reload
```

The Godot client (`Scripts/Network/ApiClient.cs`) talks to
`http://localhost:8000` by default. **The backend is optional** — if it isn't
running, the game falls back to a hardcoded default genome and plays exactly
as it did before the evolutionary loop existed.

## Endpoints

- `GET /api/health` — `{"status": "ok", "hall_of_fame_size": N}`
- `GET /api/stats` — archive size, real vs. surrogate-decided counts
  (`real_evaluations`, split into `human_evaluations`/`bot_evaluations` —
  see "Scripted bot evaluator" below), LLM vs. random mutation counts
- `POST /api/next_candidate` — returns the next genome to play
- `POST /api/report_outcome` — reports one trial's telemetry (optionally
  tagged `evaluator_type: "human" | "bot"`, default `"human"`), returns
  pass/fail + updated archive size

## Enabling the optional scripted bot evaluator

Off by default. To try it in the Godot editor: select the `GameManager`
node in `Main.tscn` and check `Autoplay Enabled` in the Inspector (or pass
`--autoplay` on an exported build's command line). It plays trials through
the real game loop via `Scripts/AI/BotPlayerPolicy.cs` instead of keyboard
input, reports them with `evaluator_type: "bot"`, and — because
`Engine.TimeScale` is raised while it runs — grinds through trials much
faster than a human would. `Autoplay Max Trials` (default `0` = unbounded)
makes it self-quit after N trials, for an unattended batch run. See
`docs/EVOLUTIONARY_DESIGN.md` "Scripted bot evaluator" for what it does and
does not simulate, and [`docs/BEHAVIOR_ANALYTICS.md`](../docs/BEHAVIOR_ANALYTICS.md)
for comparing its output against human trials before trusting its volume.

## Enabling optional LLM-guided mutation

Off by default. To try it: `cp .env.example .env`, fill in `LLM_API_KEY`
(or set `LLM_PROVIDER=local` for a local Ollama model), restart the server.
See `docs/EVOLUTIONARY_DESIGN.md` for why this is deliberately optional and
never load-bearing for the core evolutionary claim.

## Tests

```bash
pip install -r requirements-dev.txt
pytest
```

Covers genome mutation/sanitization, the minimal-criterion evaluator
(including the quit/timeout/exit-reached ordering — see `mcc.py`'s
docstring on `_evaluate_minimal_criterion`), and surrogate gating logic.
The Godot/C# side has no automated tests — `GameManager`/`EnemyController`
are tied to Godot's Node lifecycle, which needs a Godot-specific test runner
this project doesn't yet have.

## State

`data/mcc_state.json` (Hall of Fame, surrogate training data) and
`data/session_log.jsonl` (an append-only event log of every proposal/outcome,
useful for the coverage/QD-style analysis in the thesis) are created on first
run and gitignored — delete `data/` to reset the archive to empty.

## Behavior analytics (per-archetype telemetry charts)

```bash
pip install -r requirements-analysis.txt   # adds matplotlib
python analyze_behavior_telemetry.py       # writes PNGs to reports/
python analyze_behavior_telemetry.py --evaluator bot    # bot trials only
python analyze_behavior_telemetry.py --evaluator human  # human trials only
```

Turns `data/session_log.jsonl` into charts comparing enemy AI archetypes
(`melee_chaser`/`melee_hit_run`/`ranged_kite`/`ranged_strafe`) against each
other and across evolutionary generations, plus baseline-vs-adapted
comparisons per archetype. See
[`docs/BEHAVIOR_ANALYTICS.md`](../docs/BEHAVIOR_ANALYTICS.md) for what each
chart shows and the honest limitations (sparse real data, bot data being a
volume source rather than a validated human proxy). Aggregation logic
lives in `behavior_analytics.py`, covered by `tests/test_behavior_analytics.py`.
