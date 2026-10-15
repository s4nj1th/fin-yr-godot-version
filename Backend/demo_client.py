"""
Terminal demo of the HTTP API: plays the role of the Godot client against a
running backend, with a simulated player (simulated_player.py) standing in
for a person, so the archive can be watched growing without opening the game.

    uvicorn main:app --port 8001          # in another terminal
    python demo_client.py --trials 20 --skill intermediate
"""

from __future__ import annotations

import argparse

import numpy as np
import requests

from simulated_player import SKILL_PROFILES, make_skilled_player

SKILLS = SKILL_PROFILES


def summarize(genome: dict) -> str:
    p = genome["params"]
    mix = " ".join(f"{k.split('_', 1)[1]}={v:.2f}" for k, v in p["enemy_behavior_mix"].items())
    return (f"gen={genome['generation']:<3} density={p['enemy_density']:.2f} "
            f"difficulty={p['enemy_difficulty_scale']:.2f} mix=[{mix}]")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="http://127.0.0.1:8001")
    parser.add_argument("--player", default="demo")
    parser.add_argument("--skill", choices=list(SKILLS), default="intermediate")
    parser.add_argument("--trials", type=int, default=20)
    args = parser.parse_args()

    player = make_skilled_player(args.skill)
    rng = np.random.default_rng()
    query = {"player_id": args.player}
    print(f"Starting from: {requests.get(f'{args.base}/api/health', params=query, timeout=5).json()}\n")

    for trial in range(1, args.trials + 1):
        candidate = requests.post(f"{args.base}/api/next_candidate", params=query, timeout=30).json()
        estimate = candidate.get("surrogate_estimate")
        note = (f" [surrogate p={estimate['prob']:.2f}, epistemic std={estimate['epistemic_std']:.2f}]"
                if estimate else "")
        print(f"[trial {trial:02d}] {summarize(candidate['genome'])}{note}")

        telemetry = player.play(candidate["genome"]["params"], rng)
        result = requests.post(f"{args.base}/api/report_outcome", params=query, timeout=30,
                               json={"genome_id": candidate["genome_id"], "telemetry": telemetry,
                                     "evaluator_type": "bot"}).json()
        outcome = "PASS" if result["passed"] else f"FAIL ({result['fail_reason']})"
        print(f"           deaths={telemetry['deaths_retries']} -> {outcome} | archive={result['hall_of_fame_size']}")

    print(f"\nFinal stats: {requests.get(f'{args.base}/api/stats', params=query, timeout=5).json()}")


if __name__ == "__main__":
    main()
