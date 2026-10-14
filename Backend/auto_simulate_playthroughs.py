"""
Offline demo of the evolution loop: runs the real AdaptiveEvolutionManager
against a simulated player (simulated_player.py) instead of the Godot game.

The simulated player is a controlled oracle whose true pass probability is
known for every design, so the run can also report what is invisible with a
human player: how many archived designs this player would actually fail.

Each run plays as its own player id (default "sim-<skill>"), so it never
touches a human player's archive.

    python auto_simulate_playthroughs.py                         # intermediate player, 100 trials
    python auto_simulate_playthroughs.py --skill novice --trials 60 --reset
"""

from __future__ import annotations

import argparse
import os

import numpy as np

import config

config.LLM_MUTATION_MODE = "off"  # a fast offline run should never call an LLM

from adaptive_evolution import AdaptiveEvolutionManager, player_state_file  # noqa: E402
from simulated_player import SimulatedPlayer, calibrate_kappa  # noqa: E402

# Share of random designs each simulated player clears (see calibrate_kappa).
SKILLS = {"novice": 0.25, "intermediate": 0.50, "expert": 0.75}


def run(skill: str, trials: int, player_id: str, seed: int) -> None:
    player = SimulatedPlayer(kappa=calibrate_kappa(SKILLS[skill]))
    manager = AdaptiveEvolutionManager(player_id)
    rng = np.random.default_rng(seed)

    print(f"Simulated {skill} player as '{player_id}' (kappa={player.kappa:.2f}); "
          f"gate={manager.gate_uncertainty}, bootstrap={manager.bootstrap}\n")
    failures = 0
    for trial in range(1, trials + 1):
        candidate, _ = manager.get_next_candidate()
        # A simulated player is a scripted evaluator, so its trials are tagged "bot".
        passed, reason = manager.report_outcome(candidate["id"], player.play(candidate["params"], rng),
                                                evaluator_type="bot")
        failures += not passed
        if trial % 10 == 0 or trial == trials:
            stats = manager.stats
            print(f"trial {trial:4d}: archive {stats['hall_of_fame_size']:5d} "
                  f"(auto-accepted {stats['auto_accepted']}, auto-rejected {stats['auto_rejected']})")

    archive = manager.hall_of_fame
    expected_fail = float(np.mean([1 - player.p_pass(g["params"]) for g in archive])) if archive else float("nan")
    print(f"\nPlayed {trials} trials; the player failed {failures} ({100 * failures / trials:.0f}%).")
    print(f"Archive: {len(archive)} designs, of which the player would fail {100 * expected_fail:.1f}% on replay.")
    print(f"State: {player_state_file(player_id)}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skill", choices=list(SKILLS), default="intermediate")
    parser.add_argument("--trials", type=int, default=100)
    parser.add_argument("--player", default=None, help="player id (default: sim-<skill>)")
    parser.add_argument("--seed", type=int, default=0, help="seed for the simulated outcomes")
    parser.add_argument("--reset", action="store_true", help="delete this player's saved state first")
    args = parser.parse_args()

    player_id = args.player or f"sim-{args.skill}"
    if args.reset and os.path.exists(player_state_file(player_id)):
        os.remove(player_state_file(player_id))
    run(args.skill, args.trials, player_id, args.seed)


if __name__ == "__main__":
    main()
