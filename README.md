# Closed-Loop LLM PCG Dungeon Crawler

A Godot 4 C# procedural dungeon crawler driven by real-time player telemetry and LLM closed-loop level generation. The current playable branch includes a 3D top-down dungeon loop and isolated enemy-AI test scenes.

## 🚀 Quick Start

### Prerequisites

- **Godot Engine**: 4.3 (Mono / C# Edition)
- **.NET SDK**: 8.0+

### Running the Project

1. Open Godot 4 with C# support and import [`project.godot`](project.godot).
2. Build via C# solution in Godot or via terminal:
   ```bash
   dotnet build DungeonCrawler.csproj
   ```
3. Press **F5** in Godot to play.

---

## 📖 Documentation

All technical documentation, system architecture, data models, and tile encodings are in the **[`docs/`](docs/)** folder:

- **[`docs/PROJECT_CONTEXT.md`](docs/PROJECT_CONTEXT.md)** — Architectural Overview and current runtime state.
- **[`docs/ENEMY_AI.md`](docs/ENEMY_AI.md)** — Enemy classes, grid coordinates, decision flow, debug board encoding, and known AI limitations.

The project currently starts in [`Scenes/MeleeAiTest.tscn`](Scenes/MeleeAiTest.tscn), as configured in `project.godot`. Use [`Main.tscn`](Main.tscn) to exercise the generated large-map dungeon loop.
