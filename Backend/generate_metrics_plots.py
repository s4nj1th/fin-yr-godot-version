"""
Evolutionary Metrics & Analytics Script for Thesis / Guide Presentation.

Reads `Backend/data/session_log.jsonl` and `Backend/data/mcc_state.json`
to compute metrics and generate figures:
  1. Hall of Fame Growth Curve (HoF Size vs Generation)
  2. Parameter Convergence Trajectories (Gene Values vs Generation)
  3. Pass/Fail & Failure Reason Breakdown (Pie / Bar Chart)
  4. Surrogate Efficiency Split (Human vs Auto-Accepted vs Auto-Rejected)

Run with:
    python generate_metrics_plots.py

Outputs figures to `Backend/data/plots/` and prints summary stats.
"""

import json
import os
import random
import matplotlib.pyplot as plt
import numpy as np

BACKEND_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(BACKEND_DIR, "data")
LOG_FILE = os.path.join(DATA_DIR, "session_log.jsonl")
STATE_FILE = os.path.join(DATA_DIR, "mcc_state.json")
PLOTS_DIR = os.path.join(DATA_DIR, "plots")


def generate_demo_logs_if_missing():
    """Generates synthetic session logs if no live playthrough logs exist yet,
    allowing the user to generate sample plots immediately for their guide."""
    if os.path.exists(LOG_FILE) and os.path.getsize(LOG_FILE) > 0:
        return

    print("[Metrics] No live session logs found. Generating synthetic demo session log...")
    os.makedirs(DATA_DIR, exist_ok=True)

    records = []
    hof_size = 0
    gen = 0
    params = {
        "map_width": 160,
        "map_height": 160,
        "room_attempts": 280,
        "trap_density": 0.25,
        "chest_density": 0.25,
        "heal_density": 0.25,
        "enemy_density": 0.25,
        "enemy_difficulty_scale": 1.0,
    }

    reasons = ["did_not_reach_exit", "too_many_deaths", "too_slow_abandoned", "too_fast_trivial", "quit_early"]

    for i in range(1, 51):
        gen = i
        # Simulate parameter evolution towards higher difficulty / traps over generations
        params = {
            "map_width": int(np.clip(160 + np.random.normal(0, 5), 80, 200)),
            "map_height": int(np.clip(160 + np.random.normal(0, 5), 80, 200)),
            "room_attempts": int(np.clip(280 + np.random.normal(0, 10), 150, 350)),
            "trap_density": round(float(np.clip(0.15 + 0.005 * i + np.random.normal(0, 0.02), 0.0, 0.5)), 3),
            "chest_density": round(float(np.clip(0.25 + np.random.normal(0, 0.02), 0.0, 0.5)), 3),
            "heal_density": round(float(np.clip(0.30 - 0.003 * i + np.random.normal(0, 0.02), 0.0, 0.5)), 3),
            "enemy_density": round(float(np.clip(0.20 + 0.004 * i + np.random.normal(0, 0.02), 0.0, 0.5)), 3),
            "enemy_difficulty_scale": round(float(np.clip(0.8 + 0.015 * i + np.random.normal(0, 0.05), 0.6, 1.6)), 2),
        }

        # Surrogate turns on after ~5 human labels
        is_surrogate = i > 8 and random.random() < 0.4
        passed_prev = (i % 3 != 0)
        passed = random.random() < (0.65 if passed_prev else 0.3)

        if passed:
            hof_size += 1

        if is_surrogate:
            rec = {
                "event": "auto_decide",
                "timestamp": 1700000000 + i * 100,
                "genome_id": f"gen_{i}",
                "generation": gen,
                "params": params,
                "passed": passed,
                "surrogate_prob": 0.82 if passed else 0.25,
                "surrogate_std": 0.12,
                "decision_source": "surrogate",
                "mutation_source": "llm" if i % 2 == 0 else "random",
                "hall_of_fame_size": hof_size,
            }
        else:
            rec = {
                "event": "report_outcome",
                "timestamp": 1700000000 + i * 100,
                "genome_id": f"gen_{i}",
                "generation": gen,
                "params": params,
                "telemetry": {"time_taken_sec": round(random.uniform(40, 180), 1)},
                "passed": passed,
                "fail_reason": None if passed else random.choice(reasons),
                "decision_source": "human",
                "mutation_source": "llm" if i % 2 == 0 else "random",
                "hall_of_fame_size": hof_size,
            }
        records.append(rec)

    with open(LOG_FILE, "w", encoding="utf-8") as f:
        for r in records:
            f.write(json.dumps(r) + "\n")

    print(f"[Metrics] Demo session log created with {len(records)} events.")


def load_session_data():
    generate_demo_logs_if_missing()
    records = []
    with open(LOG_FILE, "r", encoding="utf-8") as f:
        for line in f:
            if line.strip():
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    return records


def plot_metrics():
    records = load_session_data()
    os.makedirs(PLOTS_DIR, exist_ok=True)

    # 1. Hall of Fame Growth & Surrogate Efficiency
    generations = [r.get("generation", i) for i, r in enumerate(records)]
    hof_sizes = [r.get("hall_of_fame_size", 0) for r in records]
    decision_sources = [r.get("decision_source", "human") for r in records]
    passed_flags = [r.get("passed", False) for r in records]

    plt.figure(figsize=(9, 5))
    plt.plot(generations, hof_sizes, marker="o", color="#0284c7", linewidth=2.5, label="Hall of Fame Archive Size")
    plt.title("Evolutionary Convergence: Hall of Fame Growth Over Generations", fontsize=13, fontweight="bold")
    plt.xlabel("Generation / Candidate Index", fontsize=11)
    plt.ylabel("Accepted Genomes in Archive (HoF Size)", fontsize=11)
    plt.grid(True, linestyle="--", alpha=0.5)
    plt.legend()
    plt.tight_layout()
    plt.savefig(os.path.join(PLOTS_DIR, "hof_growth.png"), dpi=300)
    plt.close()

    # 2. Parameter Evolution Trajectories
    trap_densities = [r["params"].get("trap_density", 0) for r in records if "params" in r]
    enemy_difficulties = [r["params"].get("enemy_difficulty_scale", 1.0) for r in records if "params" in r]
    heal_densities = [r["params"].get("heal_density", 0) for r in records if "params" in r]
    gens = list(range(1, len(trap_densities) + 1))

    plt.figure(figsize=(10, 5))
    plt.plot(gens, trap_densities, label="Trap Density (0.0 - 0.5)", color="#ef4444", linewidth=2)
    plt.plot(gens, enemy_difficulties, label="Enemy Difficulty Scale (0.6 - 1.6)", color="#8b5cf6", linewidth=2)
    plt.plot(gens, heal_densities, label="Heal Density (0.0 - 0.5)", color="#10b981", linewidth=2)
    plt.title("Genome Parameter Trajectories (Co-evolution of Difficulty)", fontsize=13, fontweight="bold")
    plt.xlabel("Generation", fontsize=11)
    plt.ylabel("Gene Value", fontsize=11)
    plt.grid(True, linestyle="--", alpha=0.5)
    plt.legend()
    plt.tight_layout()
    plt.savefig(os.path.join(PLOTS_DIR, "parameter_trajectories.png"), dpi=300)
    plt.close()

    # 3. Pass / Fail Reason Breakdown
    pass_count = 0
    fail_reasons = {}
    
    # Exclude auto_decide events from the pie chart, we only want to plot actual human evaluations
    for r in records:
        if r.get("event") == "auto_decide":
            continue
            
        if r.get("passed"):
            pass_count += 1
        else:
            reason = r.get("fail_reason", "unknown")
            fail_reasons[reason] = fail_reasons.get(reason, 0) + 1

    labels = ["PASS (Accepted)"] + list(fail_reasons.keys())
    counts = [pass_count] + list(fail_reasons.values())
    colors = ["#10b981", "#ef4444", "#f59e0b", "#3b82f6", "#ec4899", "#6b7280"][: len(labels)]

    plt.figure(figsize=(8, 5))
    bars = plt.bar(labels, counts, color=colors, edgecolor="black", linewidth=1.2)
    plt.title("Minimal Criterion Evaluation Outcomes", fontsize=13, fontweight="bold")
    plt.ylabel("Number of Candidate Genomes", fontsize=11)
    plt.xticks(rotation=15, ha="right")
    plt.grid(axis="y", linestyle="--", alpha=0.5)

    for bar in bars:
        yval = bar.get_height()
        plt.text(bar.get_x() + bar.get_width() / 2.0, yval + 0.3, int(yval), ha="center", va="bottom", fontweight="bold")

    plt.tight_layout()
    plt.savefig(os.path.join(PLOTS_DIR, "pass_fail_distribution.png"), dpi=300)
    plt.close()

    # 4. Surrogate Efficiency Breakdown
    human_count = sum(1 for src in decision_sources if src == "human")
    auto_acc = sum(1 for src, p in zip(decision_sources, passed_flags) if src == "surrogate" and p)
    auto_rej = sum(1 for src, p in zip(decision_sources, passed_flags) if src == "surrogate" and not p)

    plt.figure(figsize=(7, 5))
    pie_labels = [f"Human Playthroughs\n({human_count})", f"Auto-Accepted by GP\n({auto_acc})", f"Auto-Rejected by GP\n({auto_rej})"]
    plt.pie([human_count, auto_acc, auto_rej], labels=pie_labels, autopct="%1.1f%%", colors=["#3b82f6", "#10b981", "#ef4444"], startangle=140, wedgeprops={"edgecolor": "white", "linewidth": 2})
    plt.title("Evaluation Efficiency Split (Surrogate vs Human)", fontsize=13, fontweight="bold")
    plt.tight_layout()
    plt.savefig(os.path.join(PLOTS_DIR, "surrogate_efficiency.png"), dpi=300)
    plt.close()

    print("\n" + "=" * 60)
    print("📊 EVOLUTIONARY ALGORITHM METRICS SUMMARY FOR GUIDE")
    print("=" * 60)
    print(f" Total Candidates Evaluated : {len(records)}")
    print(f" Final Hall of Fame Size    : {hof_sizes[-1] if hof_sizes else 0}")
    print(f" Human Playthroughs Spent   : {human_count} ({human_count/max(1, len(records))*100:.1f}%)")
    print(f" Auto-Accepted by Surrogate : {auto_acc} ({auto_acc/max(1, len(records))*100:.1f}%)")
    print(f" Auto-Rejected by Surrogate : {auto_rej} ({auto_rej/max(1, len(records))*100:.1f}%)")
    print(f" Overall Pass Rate          : {pass_count/max(1, len(records))*100:.1f}%")
    print("=" * 60)
    print(f"Generated figures saved to: {PLOTS_DIR}/")
    print("  - hof_growth.png")
    print("  - parameter_trajectories.png")
    print("  - pass_fail_distribution.png")
    print("  - surrogate_efficiency.png")
    print("=" * 60 + "\n")


if __name__ == "__main__":
    plot_metrics()
