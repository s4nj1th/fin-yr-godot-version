# Project Context & Architectural Documentation

**Project Name:** Closed-Loop LLM PCG Dungeon Crawler (Godot 4 C# Port)  
**Engine:** Godot Engine 4.3 (Mono / C# Edition)  
**Framework:** .NET 8.0 (`net8.0`)  
**Architecture Pattern:** Component-based event-driven Node hierarchy with closed-loop backend REST integration.

---

## 1. Overview

This project is a 2D tile-based dungeon crawler built in Godot 4.3 using C#. It implements a **closed-loop Procedural Content Generation (PCG)** pipeline driven by an external Large Language Model (LLM) server.

As the player navigates through levels, game telemetry (damage taken, time spent, exploration coverage, chests opened, traps triggered) is captured and sent to the backend. The LLM backend assesses player playstyle, updates narrative lore (narrative isomorphism), adapts level parameters (difficulty, enemy density, trap probability, visibility), and generates the next dungeon floor dynamically.

---

## 2. System Architecture

```text
                               +-----------------------------+
                               |   LLM Backend REST Server   |
                               | (http://localhost:8080 or   |
                               |    configured BaseUrl)      |
                               +--------------+--------------+
                                              ^
                      /api/generate           |           /api/adapt
                      (Map Request)           |      (Telemetry & Adapt)
                                              v
                               +--------------+--------------+
                               |         ApiClient           |
                               +--------------+--------------+
                                              |
                                              v
+-----------------------------------------------------------------------------------+
|                                 GameManager                                       |
|                                                                                   |
|  +-------------------+   +---------------------+   +---------------------------+  |
|  |  DungeonRenderer  |   |  PlayerController   |   |     EnemyController(s)    |  |
|  |                   |   |                     |   |                           |  |
|  | - Tile grid rendering | - Grid position      | - Patrol / Ambush AI      |  |
|  | - Fog-of-war LoS   |   | - Stats (HP, Score) | - Combat vs Player        |  |
|  | - Exploration %   |   | - Movement & Input  | - Positional updates      |  |
|  +-------------------+   +---------------------+   +---------------------------+  |
|                                                                                   |
|                                  +------------------+                             |
|                                  |    HudManager    |                             |
|                                  | - Realtime HUD   |                             |
|                                  | - Adaptation UI  |                             |
|                                  +------------------+                             |
+-----------------------------------------------------------------------------------+
```

---

## 3. Directory & Component Breakdown

### Core Subsystem (`Scripts/Core/`)
* **[`GameManager.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/Core/GameManager.cs)**
  - Central orchestrator node attached to [`Main.tscn`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Main.tscn).
  - Instantiates `ApiClient`, `DungeonRenderer`, `PlayerController`, and `HudManager`.
  - Manages level state transitions, timer accumulation, entity spawning, and level restart on player death.
  - Intercepts player exit events, computes metrics, packages `TelemetryRequest`, and triggers backend adaptation requests.

* **[`DungeonRenderer.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/Core/DungeonRenderer.cs)**
  - Custom `Node2D` renderer performing 2D canvas drawing via `_Draw()`.
  - Renders map grid based on `PaletteData` hex color codes.
  - Implements line-of-sight Fog-of-War using circular distance checks (`UpdateVisibility()`).
  - Tracks explored vs. unexplored walkable tiles to compute exact exploration percentages.

---

### Data Models & Schemas (`Scripts/Data/`)
* **[`DungeonData.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/Data/DungeonData.cs)**
  - Defines data contracts mapped with `System.Text.Json` attributes (`[JsonPropertyName(...)]`):
    - `DungeonConfig`: Controls generation parameters (theme, layout style, corridor width, room/enemy density, trap probability, visibility).
    - `PaletteData`: Hex strings for rendering tiles (background, wall, floor, corridor, door, trap, enemy, chest, start, exit, fog).
    - `DungeonMapData`: Contains dimensions, seed, rooms, grid 2D array, start/exit points, and enemy spawn locations.
    - `TelemetryMetrics` & `TelemetryRequest`: Payloads sent to `/api/adapt`.
    - `LLMAdaptationResponse` & `NarrativeData`: Response payloads returned from backend adaptation.

---

### Entity Controllers (`Scripts/Entities/`)
* **[`PlayerController.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/Entities/PlayerController.cs)**
  - Tile-aligned 2D movement controller driven by directional input (WASD / Arrow keys).
  - Tracks health, score, chests opened, traps triggered, and total damage taken.
  - Triggers tile interactions (traps deal 15 damage; chests grant 25 score; exit finishes floor).
  - Emits custom C# Godot signals for UI and game state management.

* **[`EnemyController.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/Entities/EnemyController.cs)**
  - Autonomous AI entity executing step-based movement every `0.5s`.
  - Supports two behaviors:
    - `"patrol"`: Moves in random valid tile directions.
    - `"ambush"`: Detects player within 6.0 tile distance and aggressively pursues them.
  - Attacks player for 10 damage when adjacent (distance $\le 1.5$ tiles).

---

### Network Subsystem (`Scripts/Network/`)
* **[`ApiClient.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/Network/ApiClient.cs)**
  - HTTP REST client built on `System.Net.Http.HttpClient`.
  - Exposes asynchronous API methods:
    - `GenerateMapAsync(prompt, config)` $\rightarrow$ `POST /api/generate`
    - `SendTelemetryAndAdaptAsync(request)` $\rightarrow$ `POST /api/adapt`

---

### UI Subsystem (`Scripts/UI/`)
* **[`HudManager.cs`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/Scripts/UI/HudManager.cs)**
  - `CanvasLayer` based UI rendering:
    - **Header HUD**: Current level, theme, HP bar, timer, and score.
    - **Adaptation Modal**: Modal dialog displayed upon reaching exit showing LLM playstyle assessment, narrative lore title/text, optional player prompt input field, and continuation button.

---

## 4. Tile Encoding & Interaction Rules

| Tile ID | Name | Description | Interaction Effect |
| :---: | :--- | :--- | :--- |
| **0** | Wall | Impassable terrain | Blocks movement & line-of-sight |
| **1** | Floor | Standard walkable floor | Normal movement |
| **2** | Corridor | Connecting hallway tile | Normal movement |
| **3** | Door | Room entry threshold | Normal movement |
| **4** | Trap | Hidden hazard tile | Deals 15 damage, disappears upon trigger |
| **5** | Enemy | Enemy spawn point | Spawns `EnemyController` |
| **6** | Chest | Treasure container | Awards +25 score, cleared on pickup |
| **7** | Start | Level entrance | Player spawn location |
| **8** | Exit | Level exit portal | Triggers level end & telemetry submission |

---

## 5. Event & Signal Architecture

```text
PlayerController                       GameManager                        HudManager
       |                                    |                                 |
       |--- ExitReached ------------------->|                                 |
       |--- PlayerDied -------------------->|                                 |
       |--- StatsChanged ------------------>|--- UpdateStats() -------------->|
       |                                    |                                 |
       |                                    |--- ShowAdaptationModal() ------>|
       |                                    |                                 |
       |                                    |<-- AdaptivePromptSubmitted -----|
```

---

## 6. Closed-Loop Telemetry Workflow

1. **Map Loading:** `GameManager` sends initial prompt to `ApiClient.GenerateMapAsync()`.
2. **Rendering & Spawning:** `DungeonRenderer` draws the tile grid and palette colors. Player spawns at `Start` point (`tile 7`); enemies spawn at `Enemy` positions (`tile 5`).
3. **Gameplay & Fog-of-War:** Player moves turn-by-turn. `DungeonRenderer.UpdateVisibility()` reveals tiles within `VisionRadius` and marks them as explored.
4. **Exit Trigger:** Stepping on `Exit` (`tile 8`) calculates telemetry:
   $$\text{Exploration \%} = \frac{\text{Explored Walkable Tiles}}{\text{Total Walkable Tiles}} \times 100$$
5. **Backend Adaptation:** Telemetry payload sent to `/api/adapt`. Response returns playstyle evaluation, adapted `DungeonConfig`, new `DungeonMapData`, and narrative text.
6. **Modal Display & Progression:** `HudManager` displays narrative modal. Player can optionally type custom prompt feedback and click **Proceed**, starting the next level.
