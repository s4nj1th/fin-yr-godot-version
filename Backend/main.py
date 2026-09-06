"""
MCC backend for the dungeon crawler's evolutionary loop. See
docs/EVOLUTIONARY_DESIGN.md for the design rationale.

Run with:
    uvicorn main:app --host 0.0.0.0 --port 8000 --reload

The Godot client (Scripts/Network/ApiClient.cs) talks to this over HTTP at
BaseUrl (default http://localhost:8000). If this server isn't running, the
client falls back to its own local defaults — so it's always safe to play
without the backend, just without evolution.
"""

from fastapi import FastAPI, HTTPException

from mcc import MCCManager
from models import NextCandidateResponse, ReportOutcomeRequest, ReportOutcomeResponse

app = FastAPI(title="Dungeon Crawler MCC Backend", version="0.1.0")
manager = MCCManager()


@app.get("/api/health")
def health() -> dict:
    return {"status": "ok", "hall_of_fame_size": manager.hall_of_fame_size}


@app.get("/api/stats")
def stats() -> dict:
    """How many real playthroughs were spent per accepted genome vs. how many
    the surrogate resolved for free, plus the LLM-vs-random mutation split."""
    return manager.stats


@app.post("/api/next_candidate", response_model=NextCandidateResponse)
def next_candidate() -> NextCandidateResponse:
    candidate, surrogate_estimate = manager.get_next_candidate()
    return NextCandidateResponse(genome_id=candidate["id"], genome=candidate, surrogate_estimate=surrogate_estimate)


@app.post("/api/report_outcome", response_model=ReportOutcomeResponse)
def report_outcome(request: ReportOutcomeRequest) -> ReportOutcomeResponse:
    passed, fail_reason = manager.report_outcome(request.genome_id, request.telemetry.model_dump())
    if fail_reason == "unknown_genome_id":
        raise HTTPException(status_code=404, detail="Unknown or already-reported genome_id")

    return ReportOutcomeResponse(passed=passed, hall_of_fame_size=manager.hall_of_fame_size, fail_reason=fail_reason)
