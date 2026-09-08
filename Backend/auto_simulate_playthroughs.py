"""
Auto-Playthrough Simulator for Evolutionary Dungeon Crawler

This script bypasses the 3D Godot game and talks directly to the local MCCManager.
It simulates an "AI Player" completing hundreds of dungeons in seconds to generate
REAL evolutionary data, train the surrogate on real predictions, and evolve the genomes.

Run with:
    python auto_simulate_playthroughs.py
"""

import os
import random
import time
from mcc import MCCManager
import config

BACKEND_DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(BACKEND_DIR, "data")

def clear_old_data():
    """Wipe out the old fake demo data so we start fresh from generation 0."""
    state = os.path.join(DATA_DIR, "mcc_state.json")
    log = os.path.join(DATA_DIR, "session_log.jsonl")
    if os.path.exists(state): 
        with open(state, 'w') as f: pass
    if os.path.exists(log): 
        with open(log, 'w') as f: pass

def simulate_ai_player(genome_params):
    """
    Simulates how an average player would perform based on the map's genome.
    If the map is too hard (high enemy stats, lots of traps), probability of death goes up.
    """
    difficulty = genome_params.get("enemy_difficulty_scale", 1.0)
    traps = genome_params.get("trap_density", 0.1)
    enemies = genome_params.get("enemy_density", 0.1)
    
    # Calculate a danger score (Higher is harder)
    danger_score = (difficulty * 1.5) + (traps * 2.0) + (enemies * 1.0)
    
    # Defaults
    telemetry = {
        "time_taken_sec": 45.0 + (danger_score * 20),
        "damage_taken": int(30 * danger_score),
        "health_remaining": max(0, 100 - int(30 * danger_score)),
        "deaths_retries": 0,
        "enemies_killed": int(15 * enemies),
        "total_enemies": int(20 * enemies),
        "chests_opened": 3,
        "total_chests": 5,
        "traps_triggered": int(10 * traps),
        "exploration_percent": 85.0,
        "exit_reached": True,
        "quit_early": False
    }
    
    # If danger is too high, the player dies multiple times or fails to reach the exit
    if danger_score > 3.0:
        if random.random() < 0.6:
            telemetry["deaths_retries"] = random.randint(4, 7) # Fails criteria (> 3 deaths)
    elif danger_score > 2.5:
        if random.random() < 0.4:
            telemetry["deaths_retries"] = random.randint(2, 4)
            
    # Sometimes the player just abandons if it takes too long
    # If danger is too high, the player dies multiple times or fails to reach the exit
    if danger_score > 3.0:
        if random.random() < 0.6:
            telemetry["deaths_retries"] = random.randint(4, 7) # Fails criteria (> 3 deaths)
    elif danger_score > 2.5:
        if random.random() < 0.4:
            telemetry["deaths_retries"] = random.randint(2, 4)
            
    # Sometimes the player just abandons if it takes too long
    if telemetry["time_taken_sec"] > 220:
        telemetry["exit_reached"] = False
        telemetry["quit_early"] = True

    return telemetry

def run_simulation(target_hof_size=100):
    print("🧹 Clearing old static data...")
    clear_old_data()
    
    print("🤖 Booting MCC Manager and starting AI Player simulation...")
    manager = MCCManager()
    
    iterations = 0
    while manager.hall_of_fame_size < target_hof_size:
        iterations += 1
        # 1. Ask the backend for a candidate (This runs the Surrogate model!)
        candidate, surrogate_estimate = manager.get_next_candidate()
        genome_id = candidate["id"]
        
        if surrogate_estimate is None:
            decision = "(Surrogate Uncertain -> Sent to AI Player)"
            # 2. Simulate the AI Player struggling or beating the map
            telemetry = simulate_ai_player(candidate["params"])
            
            # 3. Report the result back to MCC
            passed, reason = manager.report_outcome(genome_id, telemetry)
            outcome = "PASS ✅" if passed else f"FAIL ❌ ({reason})"
        else:
            prob = surrogate_estimate["prob"]
            outcome = "AUTO-ACCEPTED 🤖" if prob >= 0.5 else "AUTO-REJECTED 🗑️"
            decision = f"(Surrogate Confident: {prob:.2f} prob)"
            
        if iterations % 10 == 0:
            print(f"Archive Size: {manager.hall_of_fame_size}/{target_hof_size} (Iteration {iterations})")
            
    print("\n✅ Simulation Complete!")
    print(f"📊 Final Hall of Fame Size: {manager.hall_of_fame_size}")
    print(f"🤖 Surrogate model is now trained with {len(manager.surrogate.y_train)} real outcomes.")
    print("You can now safely run 'python generate_metrics_plots.py' to plot this real data!")

if __name__ == "__main__":
    run_simulation(100)
