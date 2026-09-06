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
- `GET /api/stats` — archive size, real vs. surrogate-decided counts, LLM vs.
  random mutation counts
- `POST /api/next_candidate` — returns the next genome to play
- `POST /api/report_outcome` — reports one trial's telemetry, returns
  pass/fail + updated archive size

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
