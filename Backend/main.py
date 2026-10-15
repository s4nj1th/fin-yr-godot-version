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
import genome as genome_lib
import study
from adaptive_evolution import PlayerRegistry, is_valid_player_id
from models import (
    NextCandidateResponse,
    QuestionnaireRequest,
    ReplayOutcomeRequest,
    ReportOutcomeRequest,
    ReportOutcomeResponse,
    StudySessionRequest,
    StudySessionResponse,
)

app = FastAPI(title="Adaptive Enemy Encounter Backend", version="3.0.0")
players = PlayerRegistry()
replays = study.ReplayBlock()


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
    space = genome_lib.get_space()
    return {
        "genome_space": space.name,
        "evolved_genes": {**{k: list(v) for k, v in space.bounds.items()},
                          "enemy_behavior_mix": list(space.behaviors)},
        "fixed_environment": config.FIXED_ENVIRONMENT,
        "surrogate_kernel": config.SURROGATE_KERNEL,
        "play_threshold": config.PLAY_THRESHOLD,
        "surrogate_gate_uncertainty": config.SURROGATE_GATE_UNCERTAINTY,
        "surrogate_uncertainty_band": list(config.SURROGATE_UNCERTAINTY_BAND),
        "surrogate_std_threshold": config.SURROGATE_STD_THRESHOLD,
        "surrogate_window": config.SURROGATE_WINDOW,
        "bootstrap_strategy": config.BOOTSTRAP_STRATEGY,
        "minimal_criterion": {
            "min_trial_seconds": config.MIN_TRIAL_SECONDS,
            "max_trial_seconds": config.MAX_TRIAL_SECONDS,
            "max_deaths_allowed": config.MAX_DEATHS_ALLOWED,
            "min_damage_taken": config.MIN_DAMAGE_TAKEN,
            "archetype_cost_ratio": config.ARCHETYPE_COST_RATIO,
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


# --- Human study ------------------------------------------------------------


def _participant(participant_id: str) -> str:
    if not study.is_valid_participant_id(participant_id):
        raise HTTPException(status_code=422, detail="participant_id must look like P01 (P and 2-3 digits)")
    return participant_id


@app.post("/api/study/session", response_model=StudySessionResponse)
def study_session(request: StudySessionRequest) -> StudySessionResponse:
    """Starts a study session: assigns the counterbalanced condition and the
    player id whose archive this session trains."""
    participant_id = _participant(request.participant_id)
    condition = study.condition_for(participant_id, request.session_number)
    player_id = study.player_id_for(participant_id, condition)
    players.get(player_id, settings=study.CONDITIONS[condition])
    study.append_record("sessions", {"participant_id": participant_id, "session_number": request.session_number,
                                     "condition": condition, "player_id": player_id})
    return StudySessionResponse(participant_id=participant_id, session_number=request.session_number,
                                condition=condition, player_id=player_id)


@app.get("/api/study/questionnaire")
def study_questionnaire() -> dict:
    return study.load_items()


@app.post("/api/study/questionnaire")
def submit_questionnaire(request: QuestionnaireRequest) -> dict:
    participant_id = _participant(request.participant_id)
    clean = study.validate_responses(request.responses, study.load_items())
    study.append_record("questionnaires", {"participant_id": participant_id, "session_number": request.session_number,
                                           "player_id": request.player_id, "responses": clean,
                                           "comment": request.comment})
    return {"stored_items": len(clean)}


@app.post("/api/study/replay_sample")
def replay_sample(player_id: str = Query(...), n: int = Query(study.REPLAY_SAMPLE_SIZE, ge=1, le=10)) -> dict:
    """Archived designs for the end-of-session replay block."""
    return {"player_id": player_id, "designs": replays.sample(_player(player_id), n)}


@app.post("/api/study/replay_outcome")
def replay_outcome(request: ReplayOutcomeRequest) -> dict:
    """Outcome of a replayed design: recorded for the analysis only; the
    archive and the surrogate are not changed."""
    passed = replays.report(request.genome_id, request.player_id, request.telemetry.model_dump(),
                            _player(request.player_id))
    if passed is None:
        raise HTTPException(status_code=404, detail="Unknown or already-reported replay id for this player")
    return {"passed": passed}
