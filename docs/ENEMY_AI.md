# Enemy AI and Grid System

This document describes the enemy implementation in the current Godot C# branch. The authoritative behavior lives in [Scripts/Entities/EnemyController.cs](../Scripts/Entities/EnemyController.cs). The test harness is [Scripts/Core/EnemyAiTestScene.cs](../Scripts/Core/EnemyAiTestScene.cs), and the large-map spawner is [Scripts/Core/GameManager.cs](../Scripts/Core/GameManager.cs).

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

## 4. Enemy Tick and Movement Rate

`EnemyController.Tick` is called once per frame by `GameManager` or `EnemyAiTestScene`. The controller does not move every frame. It accumulates elapsed time and acts when this delay has passed:

```text
stepDelay = max(0.25, 1.0 / max(Stats.Speed, 0.5))
```

When the timer fires, the controller:

1. Decreases the attack cooldown.
2. Calls `ChooseNextStep`.
3. Adds the returned cardinal direction to `GridPos`, if non-zero.
4. Updates the 3D position.
5. Rebuilds and redraws the 8x8 debug board.

The player uses the same discrete grid idea. In the AI test scene, player input is throttled to a `0.14` second movement delay; in the main scene, it is throttled to `0.18` seconds. The player and enemy can therefore be compared as turn-like grid actors even though both are driven from the frame loop.

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

## 7. Behavior Types

Behavior changes movement and post-attack positioning. Stats are supplied separately through `EnemyClassDefinition` and `EnemyStats`.

### `melee_chaser` and `ambush`

These behaviors call `FindBestStep` with `preferCloser: true`. They continually select the walkable cardinal step that reduces Manhattan distance. In the main dungeon, `ambush` is assigned to the brute definition; it currently shares the same movement rule as `melee_chaser`.

### `melee_hit_run`

This behavior approaches while it is farther than `attackDistance + 1`. When it is close, it retreats by choosing the legal step that increases distance. After an attack it also requests two retreat steps. If no legal step improves the desired distance, it remains in place.

### `ranged_kite`

This behavior tries to maintain a mid-range band. It retreats below distance `3`, approaches above distance `5`, and strafes between those thresholds. After an attack it requests two retreat steps.

### `ranged_strafe`

This behavior approaches when farther than distance `6`, retreats below distance `3`, and strafes in the middle. After an attack it takes two strafe steps and flips its strafe sign, which alternates the preferred side between attacks.

### `patrol`

This is the fallback/default behavior. It does not target the player for movement. It returns the first walkable direction in the fixed direction order, so it is deterministic and currently behaves more like a simple wall-following probe than a patrol route.

## 8. Attacking and Post-Attack State

Before normal movement selection, the controller checks whether the Manhattan distance is within attack range and whether the `0.85` second cooldown has expired.

Attack distance is calculated as follows:

1. Any behavior whose name starts with `ranged` uses distance `4`.
2. Other behaviors use `Stats.AttackRange + 1`.

On attack, the controller emits `EnemyAttackedPlayer` with `Stats.Attack` and resets the cooldown. `melee_hit_run` and `ranged_kite` set a two-step retreat request. `ranged_strafe` sets a two-step strafe request and reverses its strafe direction. The actual response to the signal is owned by the scene that connected it: the isolated test scene logs the hit, while the main scene currently handles collision with the player by applying a score penalty and resetting the player position.

## 9. Test Scenes and Main Dungeon

The game starts in [Main.tscn](../Main.tscn). Two sandbox scenes compare archetypes in isolation: [Scenes/MeleeAiTest.tscn](../Scenes/MeleeAiTest.tscn) (`melee_chaser` vs `melee_hit_run`, same stats) and [Scenes/RangedAiTest.tscn](../Scenes/RangedAiTest.tscn) (`ranged_kite` vs `ranged_strafe`), each in a 20x16 arena with fixed pillars.

The main dungeon is built from the fixed map seed, finds the tiles marked `5` (enemy spawn), and spawns one enemy per spawn point, up to 120. Each enemy's archetype is drawn from the current design's `enemy_behavior_mix`, and its HP and attack are multiplied by the design's `enemy_difficulty_scale` (see [EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md)). The 8x8 debug board is disabled there for frame rate.

## 10. Current Limitations and Extension Points

The baseline movement described above is intentionally lightweight and easy to inspect. Enemies that receive a stealth countermeasure order already use line of sight within their vision range (`GridLineOfSight`) and breadth-first search (`GridPathfinder`) to search the player's last known position ([ENEMY_AI_ADAPTATION_PLAN.md](ENEMY_AI_ADAPTATION_PLAN.md)). Remaining upgrades:

1. Use BFS or A\* for ordinary chasing too, when an obstacle blocks direct progress.
2. Add occupancy checks so enemies do not select the same cell.
3. Give enemies a facing direction, so a rear hit can be judged from the enemy's side rather than the player's heading.
4. Make the debug board dimensions and encoding data-driven instead of hard-coded to 8x8 and values `0` through `3`.
5. Expose the current behavior phase and selected direction in the test UI.
