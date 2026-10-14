"""
Charts of one player's evolution run, from the real event log
(Backend/data/session_log.jsonl):

  1. archive_growth.png       archive size against playthroughs, split into
                              designs accepted by play and by the surrogate
  2. enemy_gene_trajectories.png  the enemy genes of each accepted design, in order
  3. outcome_breakdown.png    passes and each failure reason, played trials only
  4. evaluation_split.png     playthroughs vs. surrogate decisions

Only logged events are plotted: if the log has no events for the player, the
script says so and stops instead of inventing data.

    python generate_metrics_plots.py --player default
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

import config  # noqa: E402

PLOTS_DIR = os.path.join(config.DATA_DIR, "plots")
BEHAVIOR_COLORS = {"melee_chaser": "#ef4444", "melee_hit_run": "#f59e0b",
                   "ranged_kite": "#10b981", "ranged_strafe": "#3b82f6"}


def load_events(log_path: str, player_id: str) -> list[dict]:
    if not os.path.exists(log_path):
        return []
    events = []
    with open(log_path, "r", encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            # Records written before per-player state have no player_id.
            if record.get("player_id", config.DEFAULT_PLAYER_ID) == player_id and \
                    record.get("event") in ("report_outcome", "auto_decide"):
                events.append(record)
    return events


def plot(events: list[dict], out_dir: str, player_id: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    played = [e for e in events if e["event"] == "report_outcome"]
    auto = [e for e in events if e["event"] == "auto_decide"]

    # 1. Archive growth against playthroughs
    plays, by_play, by_surrogate = [], [], []
    n_played = n_play_acc = n_auto_acc = 0
    for e in events:
        if e["event"] == "report_outcome":
            n_played += 1
            n_play_acc += bool(e.get("passed"))
        elif e.get("passed"):
            n_auto_acc += 1
        plays.append(n_played)
        by_play.append(n_play_acc)
        by_surrogate.append(n_auto_acc)
    fig, ax = plt.subplots(figsize=(8, 4.5))
    ax.stackplot(plays, by_play, by_surrogate, labels=["accepted after a playthrough", "accepted by the surrogate"],
                 colors=["#3b82f6", "#10b981"], alpha=0.85)
    ax.set_xlabel("Playthroughs")
    ax.set_ylabel("Designs in archive")
    ax.set_title(f"Archive growth — player '{player_id}'")
    ax.legend(loc="upper left", frameon=False)
    ax.grid(alpha=0.3)
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "archive_growth.png"), dpi=200)
    plt.close(fig)

    # 2. Enemy genes of accepted designs, in acceptance order
    accepted = [e for e in events if e.get("passed")]
    if accepted:
        idx = range(1, len(accepted) + 1)
        fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(9, 6), sharex=True)
        ax1.plot(idx, [e["params"]["enemy_density"] for e in accepted], color="#8b5cf6", label="enemy density")
        ax1.plot(idx, [e["params"]["enemy_difficulty_scale"] for e in accepted], color="#0f172a",
                 label="difficulty scale")
        ax1.set_ylabel("Gene value")
        ax1.legend(frameon=False)
        ax1.grid(alpha=0.3)
        for behavior, color in BEHAVIOR_COLORS.items():
            ax2.plot(idx, [e["params"]["enemy_behavior_mix"].get(behavior, 0.0) for e in accepted],
                     color=color, label=behavior)
        ax2.set_ylabel("Mix weight")
        ax2.set_xlabel("Accepted design (in order)")
        ax2.legend(frameon=False, ncol=2, fontsize=8)
        ax2.grid(alpha=0.3)
        fig.suptitle(f"Enemy genes of accepted designs — player '{player_id}'")
        fig.tight_layout()
        fig.savefig(os.path.join(out_dir, "enemy_gene_trajectories.png"), dpi=200)
        plt.close(fig)

    # 3. Outcomes of played trials
    counts: dict[str, int] = {"passed": 0}
    for e in played:
        key = "passed" if e.get("passed") else e.get("fail_reason") or "unknown"
        counts[key] = counts.get(key, 0) + 1
    fig, ax = plt.subplots(figsize=(8, 4.5))
    bars = ax.bar(list(counts), list(counts.values()),
                  color=["#10b981"] + ["#ef4444"] * (len(counts) - 1), edgecolor="black")
    ax.bar_label(bars)
    ax.set_ylabel("Played trials")
    ax.set_title(f"Minimal criterion outcomes of played trials — player '{player_id}'")
    plt.setp(ax.get_xticklabels(), rotation=15, ha="right")
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "outcome_breakdown.png"), dpi=200)
    plt.close(fig)

    # 4. Playthroughs vs. surrogate decisions
    auto_acc = sum(bool(e.get("passed")) for e in auto)
    sizes = [len(played), auto_acc, len(auto) - auto_acc]
    labels = [f"played ({sizes[0]})", f"auto-accepted ({sizes[1]})", f"auto-rejected ({sizes[2]})"]
    fig, ax = plt.subplots(figsize=(6, 4.5))
    nonzero = [(s, lab, c) for s, lab, c in zip(sizes, labels, ["#3b82f6", "#10b981", "#ef4444"]) if s > 0]
    ax.pie([s for s, _, _ in nonzero], labels=[lab for _, lab, _ in nonzero], colors=[c for _, _, c in nonzero],
           autopct="%1.0f%%", startangle=90, wedgeprops={"edgecolor": "white"})
    ax.set_title(f"How designs were decided — player '{player_id}'")
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "evaluation_split.png"), dpi=200)
    plt.close(fig)

    played_pass = counts["passed"]
    print(f"Player '{player_id}': {len(played)} playthroughs ({played_pass} passed, "
          f"{len(played) - played_pass} failed); surrogate decided {len(auto)} designs "
          f"({auto_acc} accepted); archive {n_play_acc + n_auto_acc}.")
    print(f"Figures written to {out_dir}/")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--player", default=config.DEFAULT_PLAYER_ID)
    parser.add_argument("--log", default=config.SESSION_LOG_FILE)
    parser.add_argument("--out", default=None, help="output folder (default: data/plots/<player>)")
    args = parser.parse_args()

    events = load_events(args.log, args.player)
    if not events:
        print(f"No logged events for player '{args.player}' in {args.log}. Play some trials first "
              f"(or run auto_simulate_playthroughs.py --player {args.player}).")
        sys.exit(1)
    plot(events, args.out or os.path.join(PLOTS_DIR, args.player), args.player)


if __name__ == "__main__":
    main()
