"""HTTP-level tests of Backend/main.py, through FastAPI's test client."""

import pytest
from fastapi.testclient import TestClient

import config
import main
import study
from adaptive_evolution import PlayerRegistry


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(config, "PLAYERS_DIR", str(tmp_path / "players"))
    monkeypatch.setattr(config, "SESSION_LOG_FILE", str(tmp_path / "session_log.jsonl"))
    monkeypatch.setattr(config, "DATA_DIR", str(tmp_path))
    monkeypatch.setattr(main, "players", PlayerRegistry())
    monkeypatch.setattr(main, "replays", study.ReplayBlock())
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
    assert set(body["evolved_genes"]) == set(config.GENOME_BOUNDS) | {"enemy_behavior_mix"}
    assert body["genome_space"] == "full"
    assert body["fixed_environment"] == config.FIXED_ENVIRONMENT
    assert body["surrogate_gate_uncertainty"] == config.SURROGATE_GATE_UNCERTAINTY


def test_study_sessions_are_counterbalanced(client):
    def condition(pid, n):
        return client.post("/api/study/session", json={"participant_id": pid, "session_number": n}).json()["condition"]

    assert [condition("P01", 1), condition("P01", 2)] == ["MC", "GP"]
    assert [condition("P02", 1), condition("P02", 2)] == ["GP", "MC"]


def test_study_session_configures_the_condition(client):
    body = client.post("/api/study/session", json={"participant_id": "P03", "session_number": 1}).json()
    assert body["player_id"] == "P03-MC"
    assert client.get("/api/stats?player_id=P03-MC").json()["use_surrogate"] is False
    body = client.post("/api/study/session", json={"participant_id": "P03", "session_number": 2}).json()
    assert client.get(f"/api/stats?player_id={body['player_id']}").json()["use_surrogate"] is True


def test_study_rejects_identifying_participant_ids(client):
    response = client.post("/api/study/session", json={"participant_id": "Alice Smith", "session_number": 1})
    assert response.status_code == 422


def test_questionnaire_keeps_only_valid_answers(client):
    items = client.get("/api/study/questionnaire").json()
    first = items["items"][0]["id"]
    body = client.post("/api/study/questionnaire", json={
        "participant_id": "P04", "session_number": 1, "player_id": "P04-GP",
        "responses": {first: 5, "unknown": 3, items["items"][1]["id"]: 99}}).json()
    assert body == {"stored_items": 1}
    stored = study.read_records("questionnaires")
    assert stored[0]["responses"] == {first: 5}


def test_replays_are_recorded_without_touching_the_archive(client):
    for _ in range(3):
        gid = client.post("/api/next_candidate?player_id=P05-MC").json()["genome_id"]
        client.post("/api/report_outcome?player_id=P05-MC", json={"genome_id": gid, "telemetry": passing_telemetry()})
    stats_before = client.get("/api/stats?player_id=P05-MC").json()

    designs = client.post("/api/study/replay_sample?player_id=P05-MC&n=2").json()["designs"]
    assert len(designs) == 2
    failing = {**passing_telemetry(), "deaths_retries": 9}
    assert client.post("/api/study/replay_outcome", json={"genome_id": designs[0]["id"], "player_id": "P05-MC",
                                                         "telemetry": failing}).json() == {"passed": False}
    # A replay id is single-use and tied to its player.
    again = client.post("/api/study/replay_outcome", json={"genome_id": designs[0]["id"], "player_id": "P05-MC",
                                                          "telemetry": failing})
    assert again.status_code == 404
    other = client.post("/api/study/replay_outcome", json={"genome_id": designs[1]["id"], "player_id": "P06-MC",
                                                          "telemetry": failing})
    assert other.status_code == 404

    stats_after = client.get("/api/stats?player_id=P05-MC").json()
    assert stats_after["hall_of_fame_size"] == stats_before["hall_of_fame_size"]
    assert stats_after["real_evaluations"] == stats_before["real_evaluations"]
    assert study.read_records("replays")[0]["passed"] is False


def test_director_study_assigns_director_conditions(client):
    first = client.post("/api/study/session", json={"participant_id": "P07", "session_number": 1, "study": "director"}).json()
    second = client.post("/api/study/session", json={"participant_id": "P07", "session_number": 2, "study": "director"}).json()
    assert (first["condition"], first["director"]) == ("DOFF", False)
    assert (second["condition"], second["director"]) == ("DON", True)
    assert client.get("/api/stats?player_id=P07-DON").json()["use_surrogate"] is False  # a fixed design sequence


def test_director_study_serves_the_same_designs_in_both_sessions(client):
    for n in (1, 2):
        client.post("/api/study/session", json={"participant_id": "P08", "session_number": n, "study": "director"})
    designs = {}
    for player in ("P08-DOFF", "P08-DON"):
        designs[player] = [client.post(f"/api/next_candidate?player_id={player}").json()["genome"]["params"]
                           for _ in range(3)]
    assert designs["P08-DOFF"] == designs["P08-DON"]
    assert designs["P08-DOFF"][0] != designs["P08-DOFF"][1]
