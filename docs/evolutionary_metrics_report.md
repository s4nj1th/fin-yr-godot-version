# 📊 Evolutionary Dungeon Crawler: Complete Metrics Report

This report documents the exhaustive list of data points, parameters, and telemetry metrics utilized across the Godot client and Python backend to drive the **Human-Coupled Minimal Criterion Coevolution (MCC)** loop and the **Adaptive Enemy AI**.

---

## 1. 🧬 The Evolvable Genome (Map Metrics)
These are the architectural parameters the Python backend evolves and sends to Godot to procedurally generate the actual dungeons.

| Metric Name | Type | Description |
| :--- | :--- | :--- |
| `map_width` | Integer (40-160) | The physical X-axis footprint limit of the map. |
| `map_height` | Integer (40-160) | The physical Y-axis footprint limit of the map. |
| `room_attempts` | Integer | Max attempts the Cellular Automata uses to carve structural rooms. |
| `trap_density` | Float (0.0-1.0) | The percentage chance a room node spawns a spike trap. |
| `enemy_density` | Float (0.0-1.0) | Total capacity ratio of monsters spawned in the dungeon. |
| `enemy_difficulty_scale` | Float (1.0+) | Mutator that exponentially scales enemy HP and Damage. |
| `enemy_behavior_mix` | Float (0.0-1.0) | The ratio deciding what percentage of enemies use aggressive vs defensive AI. |
| `chest_density` | Float (0.0-1.0) | The ratio determining loot placements. |
| `heal_density` | Float (0.0-1.0) | The ratio determining health regeneration placements. |

---

## 2. 🏃 Player Telemetry (Gameplay Metrics)
This is the raw data captured by the Godot `GameManager.cs` every time a player attempts a room. This payload is transmitted to the Python backend to judge if the genome passes the **Minimal Criterion**.

| Metric Name | Type | Description |
| :--- | :--- | :--- |
| `time_taken_sec` | Float | Total seconds spent in the map before exiting or dying. |
| `damage_taken` | Integer | Total numerical HP lost to enemies/traps. |
| `health_remaining` | Integer | Player's HP upon hitting the exit. |
| `deaths_retries` | Integer | How many times the player died or pressed 'R' to retry during this single genome. |
| `enemies_killed` | Integer | Total enemies slain by the player. |
| `total_enemies` | Integer | Total enemies present on the spawned map. |
| `chests_opened` | Integer | Total amount of loot collected. |
| `total_chests` | Integer | Total amount of loot placed on the map. |
| `traps_triggered` | Integer | Total accidental (or intentional) trap step-ons. |
| `exploration_percent` | Float (0-100) | The percentage of the Fog-of-War cleared by the player. |
| `exit_reached` | Boolean | True if the player found the exit tile. |
| `quit_early` | Boolean | True if the player actively abandoned (pressed R) or idled out. |

---

## 3. 🧠 Adaptive AI (Dynamic Playstyle Metrics)
Tracked by `AdaptationDirector.cs` internally in the Godot client. These metrics use exponential decay to score real-time combat behavior and issue instantaneous tactical orders to enemy clusters.

*   **`BackAttackPreference`**: Increases when the player strikes enemies from behind. Triggers the `GuardRear` tactical response, causing enemies to pivot faster.
*   **`StealthPreference`**: Increases when the player intentionally breaks Line-of-Sight or avoids detection perimeters. Triggers the `SearchLastKnownPosition` tactical response.
*   **`RetreatPreference`**: Increases when the player continuously backs away while fighting (Kiting).
*   **`Aggression`**: Scaled by the player's willingness to charge into enemy line-of-sight and trade damage rather than employing stealth.

---

## 4. 📈 Evaluation & Presentation Graphics
These are the high-level aggregate metrics plotted out via `matplotlib` into presentation-ready visual charts (`Backend/data/plots/`).

1. **Hall of Fame Growth**: The aggregate number of viable map geometries successfully stored in the archive over time. Proves the algorithm is finding solutions without stalling.
2. **Gene Convergence Trajectories**: A plot tracing exactly how `trap_density` and `enemy_difficulty_scale` naturally stabilize to match the specific player's skill limit across generations. 
3. **Surrogate Efficiency Ratio**: The percentage of evaluated genomes that were resolved by the Gaussian Process Active Learning model (Auto-Accepted) versus forced into manual play (Human Trials).
4. **Pass/Fail Distribution**: A pie chart breaking down exactly *why* human runs failed (e.g. `too_slow_abandoned`, `too_many_deaths`, `did_not_reach_exit`) compared to passes.
