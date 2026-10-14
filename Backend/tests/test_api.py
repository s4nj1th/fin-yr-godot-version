"""HTTP-level tests of Backend/main.py, through FastAPI's test client."""

import pytest
from fastapi.testclient import TestClient

import config
import main
from adaptive_evolution import PlayerRegistry


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(config, "PLAYERS_DIR", str(tmp_path / "players"))
    monkeypatch.setattr(config, "SESSION_LOG_FILE", str(tmp_path / "session_log.jsonl"))
    monkeypatch.setattr(main, "players", PlayerRegistry())
    return TestClient(main.app)


def passing_telemetry():
    return {"time_taken_sec": 60.0, "deaths_retries": 0, "exit_reached": True, "quit_early": False}


def test_health_reports_an_empty_archive(client):
    response = client.get("/api/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "player_id": "default", "hall_of_fame_size": 0}


def test_next_candidate_returns_an_enemy_design_in_the_fixed_environment(client):
    body = client.post("/api/next_candidate").json()
    params = body["genome"]["params"]
    assert body["genome_id"] == body["genome"]["id"]
    for key, value in config.FIXED_ENVIRONMENT.items():
        assert params[key] == value
    assert set(params["enemy_behavior_mix"]) == set(config.BEHAVIOR_TYPES)


def test_full_loop_for_one_player(client):
    genome_id = client.post("/api/next_candidate?player_id=alice").json()["genome_id"]
    report = client.post(
        "/api/report_outcome?player_id=alice",
        json={"genome_id": genome_id, "telemetry": passing_telemetry()},
    ).json()
    assert report == {"passed": True, "hall_of_fame_size": 1, "fail_reason": None, "player_id": "alice"}
    assert client.get("/api/stats?player_id=alice").json()["hall_of_fame_size"] == 1
    assert client.get("/api/stats?player_id=bob").json()["hall_of_fame_size"] == 0
    # Only players with saved state are listed; bob has never played.
    assert client.get("/api/players").json() == {"players": ["alice"]}


def test_player_id_in_the_body_takes_precedence(client):
    genome_id = client.post("/api/next_candidate?player_id=alice").json()["genome_id"]
    report = client.post(
        "/api/report_outcome",
        json={"genome_id": genome_id, "telemetry": passing_telemetry(), "player_id": "alice"},
    )
    assert report.status_code == 200
    assert report.json()["player_id"] == "alice"


def test_reporting_under_the_wrong_player_is_a_404(client):
    genome_id = client.post("/api/next_candidate?player_id=alice").json()["genome_id"]
    response = client.post(
        "/api/report_outcome?player_id=bob", json={"genome_id": genome_id, "telemetry": passing_telemetry()}
    )
    assert response.status_code == 404


def test_bot_trials_are_counted_separately(client):
    genome_id = client.post("/api/next_candidate?player_id=bot").json()["genome_id"]
    client.post(
        "/api/report_outcome?player_id=bot",
        json={"genome_id": genome_id, "telemetry": passing_telemetry(), "evaluator_type": "bot"},
    )
    stats = client.get("/api/stats?player_id=bot").json()
    assert stats["bot_evaluations"] == 1 and stats["human_evaluations"] == 0


def test_invalid_player_ids_are_rejected(client):
    assert client.post("/api/next_candidate?player_id=../../etc").status_code == 422
    assert client.get("/api/stats?player_id=has space").status_code == 422


def test_malformed_reports_are_rejected(client):
    assert client.post("/api/report_outcome", json={"telemetry": passing_telemetry()}).status_code == 422
    response = client.post(
        "/api/report_outcome", json={"genome_id": "x", "telemetry": {}, "evaluator_type": "robot"}
    )
    assert response.status_code == 422


def test_config_endpoint_describes_what_evolves(client):
    body = client.get("/api/config").json()
    assert set(body["evolved_genes"]) == {"enemy_density", "enemy_difficulty_scale", "enemy_behavior_mix"}
    assert body["fixed_environment"] == config.FIXED_ENVIRONMENT
    assert body["surrogate_gate_uncertainty"] == config.SURROGATE_GATE_UNCERTAINTY
