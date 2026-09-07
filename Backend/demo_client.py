"""
Standalone demo driver — plays the exact role Godot's ApiClient.cs does,
without needing Godot installed. Simulates a sequence of trials against a
running backend (uvicorn main:app --port 8000) so you can watch the archive
grow and genome params drift live, in the terminal.

Not part of the shipped system — a throwaway script for this demo only.
"""

import random

import requests

BASE = "http://127.0.0.1:8000"


def summarize_genome(genome: dict) -> str:
    p = genome["params"]
    mix = ", ".join(f"{k}={v:.2f}" for k, v in p["enemy_behavior_mix"].items())
    return (
        f"gen={genome['generation']:<3} map={p['map_width']}x{p['map_height']} "
        f"rooms~{p['room_attempts']:<3} trap={p['trap_density']:.2f} chest={p['chest_density']:.2f} "
        f"heal={p['heal_density']:.2f} enemy={p['enemy_density']:.2f} "
        f"diff_scale={p['enemy_difficulty_scale']:.2f} mix=[{mix}]"
    )


def simulate_trial(genome_params: dict, skill: float) -> dict:
    """Fakes a plausible human playthrough outcome as a function of the
    genome's difficulty knobs and a fixed 'player skill' — NOT part of the
    real system, just enough to make the demo's pass/fail rate believable."""
    difficulty = (
        genome_params["enemy_difficulty_scale"]
        + genome_params["enemy_density"] * 2
        + genome_params["trap_density"]
    )
    reached_exit = random.random() < max(0.05, min(0.95, skill / difficulty))
    deaths = max(0, int(random.gauss(difficulty * skill * 0.5, 1.0)))
    time_taken = random.uniform(20, 180) if reached_exit else random.uniform(200, 260)
    quit_early = False

    return {
        "time_taken_sec": time_taken,
        "damage_taken": random.randint(0, 60),
        "health_remaining": random.randint(0, 100),
        "deaths_retries": deaths,
        "enemies_killed": random.randint(0, 5),
        "total_enemies": random.randint(3, 8),
        "chests_opened": random.randint(0, 2),
        "total_chests": 2,
        "traps_triggered": random.randint(0, 3),
        "exploration_percent": random.uniform(30, 90),
        "exit_reached": reached_exit,
        "quit_early": quit_early,
    }


def main(n_trials: int = 20, skill: float = 1.4) -> None:
    print(f"Starting from: {requests.get(f'{BASE}/api/health').json()}\n")

    for trial in range(1, n_trials + 1):
        candidate = requests.post(f"{BASE}/api/next_candidate").json()
        genome = candidate["genome"]
        genome_id = candidate["genome_id"]
        surrogate = candidate.get("surrogate_estimate")

        surrogate_note = f" [surrogate: prob={surrogate['prob']:.2f} std={surrogate['std']:.2f}]" if surrogate else ""
        print(f"[trial {trial:02d}] proposed: {summarize_genome(genome)}{surrogate_note}")

        telemetry = simulate_trial(genome["params"], skill)
        result = requests.post(
            f"{BASE}/api/report_outcome",
            json={"genome_id": genome_id, "telemetry": telemetry},
        ).json()

        outcome = "PASS" if result["passed"] else f"FAIL ({result['fail_reason']})"
        print(
            f"           played: exit={telemetry['exit_reached']} "
            f"deaths={telemetry['deaths_retries']} time={telemetry['time_taken_sec']:.0f}s "
            f"-> {outcome} | hall_of_fame_size={result['hall_of_fame_size']}"
        )

    stats = requests.get(f"{BASE}/api/stats").json()
    print(f"\nFinal stats: {stats}")


if __name__ == "__main__":
    main()
