"""
Live Evolutionary Metrics Dashboard

This script continuously monitors the `session_log.jsonl` file.
As you play the game in Godot, the backend writes to this log.
This script reads the new data and updates the matplotlib charts in real-time.

Run with:
    python live_dashboard.py
"""

import json
import os
import time
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation

BACKEND_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(BACKEND_DIR, "data")
LOG_FILE = os.path.join(DATA_DIR, "session_log.jsonl")

def load_data():
    records = []
    if os.path.exists(LOG_FILE):
        with open(LOG_FILE, "r", encoding="utf-8") as f:
            for line in f:
                if line.strip():
                    try:
                        records.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
    return records

def update(frame):
    records = load_data()
        
    plt.clf()  # Clear current figure
    
    # We will plot 2 subplots for the live demo
    ax1 = plt.subplot(1, 2, 1)
    ax2 = plt.subplot(1, 2, 2)
    
    # Set titles and labels even if there's no data yet
    ax1.set_title("Live Archive Growth (Hall of Fame)", fontweight='bold')
    ax1.set_xlabel("Generation")
    ax1.set_ylabel("Archive Size")
    ax1.grid(True, linestyle="--", alpha=0.5)
    
    ax2.set_title("Live Map Trajectories", fontweight='bold')
    ax2.set_xlabel("Generation")
    ax2.set_ylabel("Gene Value")
    ax2.grid(True, linestyle="--", alpha=0.5)
    
    if not records:
        ax1.text(0.5, 0.5, "Waiting for first dungeon completion...", 
                 horizontalalignment='center', verticalalignment='center', 
                 transform=ax1.transAxes, color='gray')
        ax2.text(0.5, 0.5, "Waiting for first dungeon completion...", 
                 horizontalalignment='center', verticalalignment='center', 
                 transform=ax2.transAxes, color='gray')
        plt.tight_layout()
        return

    # Data extraction
    generations = [r.get("generation", i) for i, r in enumerate(records)]
    hof_sizes = [r.get("hall_of_fame_size", 0) for r in records]
    
    trap_densities = [r["params"].get("trap_density", 0) for r in records if "params" in r]
    enemy_difficulties = [r["params"].get("enemy_difficulty_scale", 1.0) for r in records if "params" in r]
    gens = list(range(1, len(trap_densities) + 1))
    
    # 1. Hall of Fame Growth
    ax1.plot(generations[-50:], hof_sizes[-50:], marker='o', color='#0284c7', linewidth=2)
    
    # 2. Map Parameters Convergence
    ax2.plot(gens[-50:], trap_densities[-50:], label="Trap Density", color='#ef4444', linewidth=2)
    ax2.plot(gens[-50:], enemy_difficulties[-50:], label="Enemy Scale", color='#8b5cf6', linewidth=2)
    ax2.legend()
    
    plt.tight_layout()

if __name__ == "__main__":
    print("[Live Dashboard] Starting... Play the game to see charts update in real-time.")
    print("Press Ctrl+C to stop.")
    
    fig = plt.figure(figsize=(12, 5))
    fig.canvas.manager.set_window_title("Live Evolutionary Map Metrics")
    
    # Update every 2 seconds
    ani = FuncAnimation(fig, update, interval=2000, cache_frame_data=False)
    
    try:
        plt.show()
    except KeyboardInterrupt:
        print("\n[Live Dashboard] Stopped.")
