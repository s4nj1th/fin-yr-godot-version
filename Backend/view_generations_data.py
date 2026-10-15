"""
Prints how one player's accepted encounter designs changed over the run:
about ten milestones from the event log, with each design's enemy genes and
whether a playthrough or the surrogate accepted it.

    python view_generations_data.py --player default
"""

from __future__ import annotations

import argparse

import config
from generate_metrics_plots import load_events

SHORT = {"melee_chaser": "chase", "melee_hit_run": "hit&run", "ranged_kite": "kite", "ranged_strafe": "strafe",
         "melee_charger": "charge", "ranged_bomber": "bomb", "melee_shield": "shield"}


def view(player_id: str, log_path: str) -> None:
    accepted = [e for e in load_events(log_path, player_id) if e.get("passed")]
    if not accepted:
        print(f"No accepted designs logged for player '{player_id}' yet.")
        return

    step = max(1, len(accepted) // 10)
    milestones = accepted[::step]
    if milestones[-1] is not accepted[-1]:
        milestones.append(accepted[-1])

    header = (f"{'#':>5} | {'GEN':>4} | {'DENSITY':>7} | {'DIFFICULTY':>10} | {'TEMPO':>5} | {'ELITE':>5} | "
              f"{'PACK':>4} | {'BEHAVIOUR MIX':<40} | ACCEPTED BY")
    print("\n" + header + "\n" + "-" * len(header))
    for m in milestones:
        p = m["params"]
        mix = " ".join(f"{SHORT.get(k, k)}={v:.2f}" for k, v in p["enemy_behavior_mix"].items() if v > 0.005)
        source = "surrogate" if m["event"] == "auto_decide" else f"playthrough ({m.get('evaluator_type', 'human')})"
        print(f"{accepted.index(m) + 1:>5} | {m.get('generation', '?'):>4} | {p['enemy_density']:>7.3f} | "
              f"{p['enemy_difficulty_scale']:>10.3f} | {p.get('enemy_tempo', 1.0):>5.2f} | {p.get('elite_rate', 0.0):>5.2f} | "
              f"{p.get('pack_size', 1.0):>4.2f} | {mix:<40} | {source}")
    print(f"\nAccepted designs for player '{player_id}': {len(accepted)}\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--player", default=config.DEFAULT_PLAYER_ID)
    parser.add_argument("--log", default=config.SESSION_LOG_FILE)
    args = parser.parse_args()
    view(args.player, args.log)
