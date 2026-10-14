"""
Live dashboard: re-reads the event log every two seconds while someone plays
and redraws one player's archive growth and the enemy genes of the designs
being tried.

    python live_dashboard.py --player default
"""

from __future__ import annotations

import argparse

import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation

import config
from generate_metrics_plots import load_events


def make_update(player_id: str, log_path: str):
    def update(_frame):
        events = load_events(log_path, player_id)
        plt.clf()
        ax1 = plt.subplot(1, 2, 1)
        ax2 = plt.subplot(1, 2, 2)
        ax1.set_title(f"Archive size — player '{player_id}'", fontweight="bold")
        ax1.set_xlabel("Playthroughs")
        ax1.set_ylabel("Designs in archive")
        ax2.set_title("Enemy genes of designs tried (last 50)", fontweight="bold")
        ax2.set_xlabel("Design")
        ax2.set_ylabel("Gene value")
        for ax in (ax1, ax2):
            ax.grid(True, linestyle="--", alpha=0.5)

        if not events:
            for ax in (ax1, ax2):
                ax.text(0.5, 0.5, "Waiting for the first trial...", ha="center", va="center",
                        transform=ax.transAxes, color="gray")
            plt.tight_layout()
            return

        plays, sizes, played = [], [], 0
        for e in events:
            played += e["event"] == "report_outcome"
            plays.append(played)
            sizes.append(e.get("hall_of_fame_size", 0))
        ax1.plot(plays, sizes, color="#0284c7", linewidth=2)

        recent = events[-50:]
        x = range(len(events) - len(recent) + 1, len(events) + 1)
        ax2.plot(x, [e["params"]["enemy_density"] for e in recent], label="enemy density", color="#8b5cf6")
        ax2.plot(x, [e["params"]["enemy_difficulty_scale"] for e in recent], label="difficulty scale", color="#0f172a")
        ax2.legend()
        plt.tight_layout()

    return update


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--player", default=config.DEFAULT_PLAYER_ID)
    parser.add_argument("--log", default=config.SESSION_LOG_FILE)
    args = parser.parse_args()

    print(f"[Live Dashboard] Watching player '{args.player}'. Play the game to see the charts update. Ctrl+C stops.")
    fig = plt.figure(figsize=(12, 5))
    fig.canvas.manager.set_window_title("Adaptive enemy evolution — live")
    animation = FuncAnimation(fig, make_update(args.player, args.log), interval=2000, cache_frame_data=False)
    try:
        plt.show()
    except KeyboardInterrupt:
        print("\n[Live Dashboard] Stopped.")
