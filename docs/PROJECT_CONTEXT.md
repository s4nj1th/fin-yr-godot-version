# Project Context (Current Playable State)

Project name: Closed-Loop LLM PCG Dungeon Crawler (Godot C#)
Engine: Godot 4.3 Mono
Framework: .NET 8.0

This file reflects the current runtime setup used for gameplay and AI testing.

## 1. Current Focus

The active prototype is now a 3D top-down dungeon experience with:

1. Very large procedural maps.
2. Shader-based fog-of-war with discovery memory.
3. Capsule player and capsule enemies.
4. Dedicated enemy AI sandbox scenes for behavior iteration.

The main loop no longer depends on the older 2D-only renderer path for primary testing.

## 2. Active Main Scene and Core Runtime

Main scene:

1. [Main.tscn](../Main.tscn)

Main runtime script:

1. [Scripts/Core/GameManager.cs](../Scripts/Core/GameManager.cs)

Current GameManager responsibilities:

1. Generates a large tile map from configurable settings.
2. Builds a 3D top-down arena from primitive meshes.
3. Handles player movement on a grid with normal axes.
4. Spawns enemies and updates their AI each frame.
5. Applies fog shader parameters and discovery updates.
6. Supports full map regeneration with R key.

Important tuneables in DungeonGenerationSettings:

1. MapWidth / MapHeight
2. RoomAttempts
3. Room min and max dimensions
4. FogRevealRadius
5. FogFeather
6. FogOpacity and FogDiscoveredOpacity

## 3. Fog-of-War Model

Fog behavior now has two states for non-visible regions:

1. Undiscovered: hidden under stronger fog.
2. Discovered: dimmed under lighter fog.

Implementation notes:

1. A discovery texture is maintained in memory and updated as player reveals tiles.
2. Fog is rendered through a spatial shader overlay quad.
3. Shader blends between undiscovered and discovered opacity from the discovery map.

Primary code:

1. [Scripts/Core/GameManager.cs](../Scripts/Core/GameManager.cs)

## 4. Enemy System (Current)

Primary enemy script:

1. [Scripts/Entities/EnemyController.cs](../Scripts/Entities/EnemyController.cs)

Detailed enemy documentation:

1. [docs/ENEMY_AI.md](ENEMY_AI.md)

Current visual and debug model:

1. Enemy body is a capsule mesh.
2. Enemy carries an 8x8 NN-style debug grid board behind it.
3. Grid cells visualize a local map window, the player cell, and cells adjacent to the player.
4. The board is a visualization of local state; it is not a neural-network inference layer and does not choose actions.

Grid and movement summary:

1. `GridPos` is the authoritative enemy location as integer `(x, y)` coordinates.
2. The map array is indexed as `grid[y, x]`, while `Vector2I` stores `X` and `Y`.
3. An enemy evaluates only the four cardinal neighboring cells and rejects coordinates outside the map or with tile value `0`.
4. Normal movement uses Manhattan distance to the player and greedily selects the best legal neighbor. It does not currently use A\*, BFS, line-of-sight checks, or a reservation system for other enemies.
5. The 8x8 board covers a local window around the enemy and encodes wall/out-of-bounds, player, adjacent-to-player, and ordinary walkable cells. See [ENEMY_AI.md](ENEMY_AI.md) for the exact coordinate and color mapping.

Current behavior types (AI differs, stats can remain identical in test scenes):

1. melee_chaser
2. melee_hit_run
3. ranged_kite
4. ranged_strafe

Behavior design rule in this branch:

1. AI changes movement and post-attack repositioning only.
2. Stats such as HP, speed, attack, and reward can stay constant for controlled tests.

## 5. Dedicated AI Test Scenes

Two separate scenes exist for isolated behavior testing:

1. [Scenes/MeleeAiTest.tscn](../Scenes/MeleeAiTest.tscn)
2. [Scenes/RangedAiTest.tscn](../Scenes/RangedAiTest.tscn)

Shared scene driver script:

1. [Scripts/Core/EnemyAiTestScene.cs](../Scripts/Core/EnemyAiTestScene.cs)

What each test scene contains:

1. Player capsule with WASD movement.
2. Two enemies in the selected category (melee or ranged).
3. Same base stats for both enemies in that scene.
4. Different AI behavior per enemy.
5. Arena obstacles for pathing and attack-style visibility.

## 6. Tile Semantics in Use

Tile IDs are still aligned with project logic:

1. 0 = Wall
2. 1 = Floor
3. 2 = Corridor
4. 3 = Door
5. 4 = Trap
6. 5 = Enemy spawn
7. 6 = Chest reward
8. 7 = Start
9. 8 = Exit

Current visuals:

1. Reward marker is a cube.
2. Player and enemies are capsules.

## 7. Known Legacy vs Active Paths

Legacy scripts still exist for earlier 2D and backend flow work, but current vibe-coding work should prioritize:

1. [Scripts/Core/GameManager.cs](../Scripts/Core/GameManager.cs)
2. [Scripts/Entities/EnemyController.cs](../Scripts/Entities/EnemyController.cs)
3. [Scripts/Core/EnemyAiTestScene.cs](../Scripts/Core/EnemyAiTestScene.cs)
4. [Scenes/MeleeAiTest.tscn](../Scenes/MeleeAiTest.tscn)
5. [Scenes/RangedAiTest.tscn](../Scenes/RangedAiTest.tscn)

## 8. Quick Run Targets

For AI behavior iteration:

1. Run current scene with [Scenes/MeleeAiTest.tscn](../Scenes/MeleeAiTest.tscn)
2. Run current scene with [Scenes/RangedAiTest.tscn](../Scenes/RangedAiTest.tscn)

For main large-map dungeon loop:

1. Run [Main.tscn](../Main.tscn)

For the full explanation of enemy state, movement heuristics, attack timing, and grid encoding:

1. Read [ENEMY_AI.md](ENEMY_AI.md)

## 9. Next Recommended Extensions

If continuing this branch, most aligned next steps are:

1. Convert large map into streamed chunk generation for true infinite traversal.
2. Persist discovered fog state per chunk.
3. Add in-scene labels for AI type and current state machine phase.
4. Add deterministic seed controls for reproducible AI tests.
