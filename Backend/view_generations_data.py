"""
Generation History Viewer

Reads the timeline of evolution and prints a human-readable table of 
how the parameters mutated and adapted over the generations.

Run with:
    python view_generations_data.py
"""

import os
import json

BACKEND_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(BACKEND_DIR, "data", "session_log.jsonl")

def view_generations():
    if not os.path.exists(LOG_FILE):
        print("No log file found!")
        return

    records = []
    with open(LOG_FILE, "r", encoding="utf-8") as f:
        for line in f:
            if line.strip():
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError:
                    continue

    # Filter to only the passing ones (Hall of Fame additions)
    hof_records = [r for r in records if r.get("passed")]

    if not hof_records:
        print("No passing generations found yet.")
        return

    print("\n" + "="*80)
    print(f"{'GEN':<5} | {'TRAP DENSITY':<14} | {'ENEMY SCALE':<13} | {'HEAL DENSITY':<14} | {'SURROGATE / HUMAN':<20}")
    print("-" * 80)

    # We want to show checkpoints (e.g., Gen 1, Gen 10, Gen 25, Gen 50, etc)
    # or just spread equally across the max size
    total = len(hof_records)
    
    # Pick ~10 representative milestones
    milestones = []
    if total > 10:
        step = total // 10
        milestones = [hof_records[i] for i in range(0, total, step)]
        if milestones[-1] != hof_records[-1]:
            milestones.append(hof_records[-1])
    else:
        milestones = hof_records

    for m in milestones:
        gen = m.get("generation", "?")
        params = m.get("params", {})
        
        traps = params.get("trap_density", 0)
        enemies = params.get("enemy_difficulty_scale", 1.0)
        heals = params.get("heal_density", 0)
        
        event = "Surrogate (Auto)" if m.get("event") == "auto_decide" else "Human Trial"
        
        print(f"#{gen:<4} | {traps:>10.2f}     | {enemies:>10.2f}    | {heals:>10.2f}       | {event}")

    print("=" * 80)
    print(f"Total Viable Generations Available in Archive: {total}")
    print("=" * 80 + "\n")

if __name__ == "__main__":
    view_generations()
