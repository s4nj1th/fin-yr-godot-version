# Enemy AI and Grid System

This document describes the enemy implementation in the current Godot C# branch. The authoritative behavior lives in [Scripts/Entities/EnemyController.cs](../Scripts/Entities/EnemyController.cs); every combat number comes from the archetype catalog in [Scripts/AI/EnemyArchetypes.cs](../Scripts/AI/EnemyArchetypes.cs), and the telegraph geometry from [Scripts/AI/AttackGeometry.cs](../Scripts/AI/AttackGeometry.cs) (both engine-free and unit-tested). Looks and animation are in [Scripts/Entities/EnemyVisuals.cs](../Scripts/Entities/EnemyVisuals.cs) and [Scripts/Entities/TelegraphFx.cs](../Scripts/Entities/TelegraphFx.cs). The test harness is [Scripts/Core/EnemyAiTestScene.cs](../Scripts/Core/EnemyAiTestScene.cs), and the large-map spawner is [Scripts/Core/GameManager.cs](../Scripts/Core/GameManager.cs).

## 1. Two Grids, Two Jobs

The enemy system has two related but different grids:

1. The **world movement grid** is the complete dungeon or arena array. It controls collision and movement legality.
2. The **8x8 AI pixel grid** is a small debug view centered near one enemy. It shows the local information currently being visualized on the board behind the enemy.

The 8x8 board must not be interpreted as a pathfinding map or a trained neural network. `ChooseNextStep` does not read `AiPixelGrid`; it reads `GridPos`, the player position, and the complete `walkableGrid` directly. The board is useful for inspecting what the enemy sees locally, while the movement heuristics remain explicit C# rules.

## 2. Coordinate Conventions

Enemy positions are integer grid coordinates:

```text
GridPos = (x, y)
```

The map is a rectangular two-dimensional array with row-major indexing:

```text
walkableGrid[y, x]
```

This distinction matters. `Vector2I.X` selects the column and `Vector2I.Y` selects the row, but array access must always put `Y` first. Bounds are checked against `GetLength(1)` for `x` and `GetLength(0)` for `y`.

The 3D scene uses the grid as the horizontal X/Z plane. In the main dungeon, the map is centered around the world origin:

```text
world.x = grid.x - ((mapWidth - 1) / 2)
world.z = grid.y - ((mapHeight - 1) / 2)
```

The enemy keeps grid coordinates as its source of truth and then updates its `Node3D.Position`. This prevents visual floating-point movement from becoming a second, conflicting position system.

## 3. Walkability

The current walkability rule is deliberately simple:

```text
walkable = in bounds AND walkableGrid[y, x] != 0
```

Tile value `0` is a wall. Every other tile value is considered traversable by the enemy controller, including floor, corridor, trap, chest, start, exit, and enemy-spawn markers. The special marker values describe gameplay or presentation state; they do not form separate collision classes in the current AI.

There is no explicit check for another enemy occupying a candidate cell. There is also no path reservation, diagonal movement, corner cutting, or route history. Two enemies can therefore select the same cell, and a greedy enemy can stop when every immediate neighbor is no closer to its target even if a route exists around an obstacle.

## 4. Enemy Tick, Movement Rate and Attack Timing

`EnemyController.Tick` is called once per frame by `GameManager` or `EnemyAiTestScene`. Every frame it updates perception, the visuals and the attack state machine (section 8). Movement happens on a timer:

```text
stepDelay = max(0.25, 1.0 / max(Stats.Speed, 0.5))
```

When the timer fires and the enemy is not winding up or stunned, it calls `ChooseNextStep`, adds the returned cardinal direction to `GridPos`, turns to face it, and glides its 3D position to the new tile (a short tween; the grid position is the source of truth). Elite enemies below half health run every timer 1.3 times faster.

The player uses the same discrete grid idea. In the AI test scene, player input is throttled to a `0.14` second movement delay; in the main scene, it is throttled to `0.18` seconds.

## 5. Local 8x8 Debug Board

`BuildAiPixelGrid` creates a new `int[8, 8]` array. For each display cell `(x, y)`, it samples the world map at:

```text
worldX = GridPos.X + x - 4
worldY = GridPos.Y + y - 4
```

The visible window therefore spans four cells to the left/up of the enemy through three cells to the right/down. The sampling is intentionally local and fixed-size. At map edges, samples outside the map are encoded as walls instead of being omitted.

The integer values are:

| Value | Meaning                              | Debug color    |
| ----- | ------------------------------------ | -------------- |
| `0`   | Ordinary walkable cell               | Dark blue-gray |
| `1`   | Wall or outside the map              | Red            |
| `2`   | Player's exact cell                  | Cyan           |
| `3`   | Cell within one square of the player | Amber          |

The adjacency test uses `abs(worldX - playerX) <= 1` and `abs(worldY - playerY) <= 1`, so it marks a 3x3 square around the player, including diagonal cells. The player's exact cell wins first and is encoded as `2`. Walls win before player or adjacency checks, so a wall tile cannot be displayed as a target marker.

The board is refreshed after an AI tick and once during initialization. It is not updated continuously while the enemy is waiting for its next movement interval. That is useful when debugging movement decisions, but it means the display can lag behind a player move for part of a frame interval.

## 6. Distance and Candidate Selection

The controller uses Manhattan distance:

```text
d = abs(player.x - enemy.x) + abs(player.y - enemy.y)
```

Only these directions are considered, in this order:

```text
(+1, 0), (-1, 0), (0, +1), (0, -1)
```

`FindBestStep` filters out blocked candidates, calculates each candidate's Manhattan distance, and selects the closest or furthest candidate depending on the caller. It returns no movement when the selected distance fails to improve on the current distance. This last guard stops an enemy from wandering while it is trying to approach or retreat.

This is a local greedy heuristic. It works well in open rooms and straight corridors, but it does not understand global routes. Around a wall, the best immediate direction may be a sideways move that temporarily keeps the same distance or increases it. Because such a move is rejected, the enemy can stall instead of navigating around the obstacle.

## 7. Archetypes

Seven archetypes (`EnemyArchetypes.All`, in the backend's `BEHAVIOR_TYPES` order). The first four keep the original game's hit points, attack, defence, speed, reach and 0.85 s cooldown; the wind-ups are new.

| Behaviour key | Name | Attack | Wind-up / cooldown (s) | Movement |
|---|---|---|---|---|
| `melee_chaser` | Brute | melee strike, reach 2 | 0.30 / 0.85 | greedy approach |
| `melee_hit_run` | Skirmisher | melee strike, reach 2 | 0.20 / 0.85 | hovers just outside reach; retreats 2 steps after striking |
| `ranged_kite` | Archer | tracking shot, range 5, needs line of sight | 0.35 / 0.85 | holds distance 3–5; retreats 2 steps after firing |
| `ranged_strafe` | Sentinel | tracking shot, range 5, needs line of sight | 0.35 / 0.85 | holds distance 3–6; strafes 2 steps and switches sides after firing |
| `melee_charger` | Charger | **telegraphed dash** up to 6 tiles along its row/column; hits anything on the line | 0.70 / 2.40, then stunned 1.2 s (takes 1.5× damage) | lines up on the player's row or column, then closes in |
| `ranged_bomber` | Bomber | **telegraphed bomb** on a 3×3 area, range 7, aimed where a moving player is heading | 0.90 / 2.20 | holds distance 4–7; side-steps after throwing |
| `melee_shield` | Shieldbearer | shield bash, reach 1, only facing the player | 0.45 / 1.30 | greedy approach; **turns 90° per 0.5 s**; hits from its front arc do 25% damage |

`patrol` (no target) and `ambush` (same as the chaser) remain for the AI test scenes.

### Encounter modifiers (evolved genes)

`EnemyArchetypes.WithModifiers` applies the design's genes to an archetype:

- `enemy_difficulty_scale` multiplies hit points and attack (rounded half-to-even, as the engine does);
- `enemy_tempo` multiplies speed and divides every wind-up, cooldown, recovery and turn time;
- an **elite** (each enemy independently, with probability `elite_rate`) has 1.5× hit points and 1.25× attack, a gold aura and crown, and enrages below half health;
- `pack_size` is the expected number of enemies per spawn point (`PackCount`: the integer part, plus one with probability equal to the fractional part, 1 to 3). Extra pack members take the nearest free tiles (`AttackGeometry.PackPlacement`).

## 8. The Attack State Machine

Attacks are checked every frame, not only on movement steps:

1. **Ready.** If the cooldown has expired and the archetype's trigger holds (in reach; in range with line of sight; on a clear row/column within 6 tiles for the charger; facing the player for the shieldbearer), the enemy starts a **wind-up**. The cooldown restarts at the start of the wind-up, so it is the archetype's attack period.
2. **Winding up.** The enemy stands still, glows and squashes as the wind-up fills. Charges and bombs also paint their target tiles on the floor (`TelegraphFx`: red for a dash line, orange for a blast); the inner square of each tile grows as time runs out. `TelegraphStarted` is raised with the tiles, which the scripted bots use to dodge.
3. **Resolve.** Melee strikes land if the player is still in reach, shots if still in range and sight, the dash if the player is on the swept line (the charger stops in front of the player or an ally), the bomb if the player is still inside the area. A hit emits `EnemyAttackedPlayer`; every attack raises `AttackResolved(hit, telegraphed)`, and a telegraphed miss counts as a dodge in the per-archetype telemetry.
4. **Recovering** (charger only): stunned for the recovery time, with stars over its head, taking extra damage.

Damage to an enemy goes through `TakeDamage(raw, direction, attackerPos)`: mitigation by defence (floor 1), the shieldbearer's frontal block, the stunned charger's vulnerability, a hit flash and knock-back, a floating damage number, and the health bar. It returns the damage dealt and whether it was blocked, for telemetry.

## 9. Test Scenes, Gallery, Duel Arena and Main Dungeon

The game starts in [Main.tscn](../Main.tscn). Two sandbox scenes compare archetypes in isolation: [Scenes/MeleeAiTest.tscn](../Scenes/MeleeAiTest.tscn) and [Scenes/RangedAiTest.tscn](../Scenes/RangedAiTest.tscn). [Scenes/EnemyGallery.tscn](../Scenes/EnemyGallery.tscn) shows every archetype, regular and elite, with the two floor telegraphs (`-- --gallery-out=FILE.png` saves a frame).

**Duel mode** (`-- --duel-plan=PLAN.json --duel-out=OUT.jsonl`, run by `tools/run_duels.py`) puts a scripted fighter (`FighterBotPolicy`: closes in and attacks; with probability `dodge` per telegraph it steps out of the marked tiles or flanks a shieldbearer) against a pack of identical enemies in an open arena and records the damage it takes. These measurements are the simulated player's damage model (`Backend/duel_table.json`).

The main dungeon is built from the fixed map seed, finds the tiles marked `5` (enemy spawn), and spawns a pack at each, up to 160 enemies. Each enemy's archetype is drawn from the design's `enemy_behavior_mix` and its elite status from `elite_rate` (see [EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md)). The 8x8 debug board is disabled there for frame rate.

## 10. Looks and Animation

Each archetype is built from primitive meshes (no external art assets) in its own colour: the Brute a horned block (red), the Skirmisher a slim figure with two blades (orange), the Archer a hooded robe with a bow (violet), the Sentinel a floating orb in a spinning ring (teal), the Charger a low ram with forward horns (magenta), the Bomber a round body with a lit fuse (lime) and the Shieldbearer a helmeted figure behind a tower shield (blue). Animations: spawn rise, idle bob, smooth steps and turning, wind-up glow and squash, lunge, projectile, lobbed bomb with explosion, hit flash and knock-back, stun stars, enrage pulse for elites, and a topple-and-shrink death with a particle burst. A health bar appears once an enemy is hurt.

## 11. Current Limitations and Extension Points

The baseline movement described above is intentionally lightweight and easy to inspect. Enemies that receive a stealth countermeasure order already plan routes on the grid: with **intercept** (the default) they head for a cell ahead of the player on the player's shortest route to the exit (`InterceptPlanner`, breadth-first distances) and wait there; with **search** they use line of sight within their vision range (`GridLineOfSight`) and breadth-first search (`GridPathfinder`) to search the player's last known position ([ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md)). Remaining upgrades:

1. Use BFS or A\* for ordinary chasing too, when an obstacle blocks direct progress.
2. Add occupancy checks so enemies do not select the same cell during ordinary movement (dashes and pack placement already respect occupancy).
3. Enemies now have a facing (for animation and the shieldbearer's block), but the director's rear-hit signal still uses the player's heading; switching it to the enemy's facing would make that signal exact.
4. Make the debug board dimensions and encoding data-driven instead of hard-coded to 8x8 and values `0` through `3`.
