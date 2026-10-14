"""
Backend for the dungeon crawler's adaptive enemy-encounter evolution. See
docs/EVOLUTIONARY_DESIGN.md for the design rationale.

Run with:
    uvicorn main:app --host 0.0.0.0 --port 8001 --reload

The Godot client (Scripts/Network/ApiClient.cs) talks to this over HTTP at
BaseUrl (default http://localhost:8001). If this server isn't running, the
client falls back to its own local defaults — so it's always safe to play
without the backend, just without evolution.

Every endpoint takes an optional `player_id` query parameter (default
"default"): each player has their own archive and surrogate.
"""

from typing import Optional

from fastapi import FastAPI, HTTPException, Query

import config
from adaptive_evolution import PlayerRegistry, is_valid_player_id
from models import NextCandidateResponse, ReportOutcomeRequest, ReportOutcomeResponse

app = FastAPI(title="Adaptive Enemy Encounter Backend", version="2.0.0")
players = PlayerRegistry()


def _player(player_id: str):
    if not is_valid_player_id(player_id):
        raise HTTPException(status_code=422, detail="player_id must be 1-40 characters of A-Z, a-z, 0-9, _ or -")
    return players.get(player_id)


@app.get("/api/health")
def health(player_id: str = Query(config.DEFAULT_PLAYER_ID)) -> dict:
    return {"status": "ok", "player_id": player_id, "hall_of_fame_size": _player(player_id).hall_of_fame_size}


@app.get("/api/stats")
def stats(player_id: str = Query(config.DEFAULT_PLAYER_ID)) -> dict:
    """How many real playthroughs were spent vs. how many designs the
    surrogate decided for free, plus the LLM-vs-random mutation split."""
    return _player(player_id).stats


@app.get("/api/players")
def list_players() -> dict:
    """Players that have saved evolution state."""
    return {"players": PlayerRegistry.known_players()}


@app.get("/api/config")
def evolution_config() -> dict:
    """What evolves, what is held fixed, and how the surrogate gate is set."""
    return {
        "evolved_genes": {**{k: list(v) for k, v in config.GENOME_BOUNDS.items()},
                          "enemy_behavior_mix": list(config.BEHAVIOR_TYPES)},
        "fixed_environment": config.FIXED_ENVIRONMENT,
        "surrogate_gate_uncertainty": config.SURROGATE_GATE_UNCERTAINTY,
        "surrogate_uncertainty_band": list(config.SURROGATE_UNCERTAINTY_BAND),
        "surrogate_std_threshold": config.SURROGATE_STD_THRESHOLD,
        "surrogate_window": config.SURROGATE_WINDOW,
        "bootstrap_strategy": config.BOOTSTRAP_STRATEGY,
        "minimal_criterion": {
            "min_trial_seconds": config.MIN_TRIAL_SECONDS,
            "max_trial_seconds": config.MAX_TRIAL_SECONDS,
            "max_deaths_allowed": config.MAX_DEATHS_ALLOWED,
        },
    }


@app.post("/api/next_candidate", response_model=NextCandidateResponse)
def next_candidate(player_id: str = Query(config.DEFAULT_PLAYER_ID)) -> NextCandidateResponse:
    candidate, surrogate_estimate = _player(player_id).get_next_candidate()
    return NextCandidateResponse(genome_id=candidate["id"], genome=candidate, surrogate_estimate=surrogate_estimate)


@app.post("/api/report_outcome", response_model=ReportOutcomeResponse)
def report_outcome(
    request: ReportOutcomeRequest, player_id: Optional[str] = Query(None)
) -> ReportOutcomeResponse:
    resolved = request.player_id or player_id or config.DEFAULT_PLAYER_ID
    manager = _player(resolved)
    passed, fail_reason = manager.report_outcome(
        request.genome_id, request.telemetry.model_dump(), evaluator_type=request.evaluator_type
    )
    if fail_reason == "unknown_genome_id":
        raise HTTPException(status_code=404, detail="Unknown or already-reported genome_id for this player")

    return ReportOutcomeResponse(
        passed=passed, hall_of_fame_size=manager.hall_of_fame_size, fail_reason=fail_reason, player_id=resolved
    )
