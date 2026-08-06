# Closed-Loop LLM PCG Dungeon Crawler (Godot 4 C# Port)

A Godot 4.3 C# procedural content generation (PCG) dungeon crawler featuring a closed-loop LLM integration pipeline.

## 🛠 Prerequisites

- **Godot Engine**: 4.3 (Mono / C# Edition)
- **.NET SDK**: 8.0 or later

## 📁 Project Structure

```text
.
├── .editorconfig       # Code formatting preferences
├── .env.example        # Environment variable template
├── .gitattributes      # Git text encoding & line ending settings
├── .gitignore          # Git ignore rules for Godot & .NET
├── .vscode/            # VS Code launch & task configurations
│   ├── extensions.json
│   ├── launch.json
│   └── tasks.json
├── DungeonCrawler.csproj # C# .NET 8 Project file
├── Main.tscn           # Main game scene
├── project.godot       # Godot project configuration
└── Scripts/            # C# scripts organized by system
    ├── Core/           # Game loops, level loading, dungeon rendering
    ├── Data/           # Dungeon schema definitions & data models
    ├── Entities/       # Player & enemy controller logic
    ├── Network/        # API client for LLM communication
    └── UI/             # HUD & menu interfaces
```

## 🚀 Building & Running

### Using Godot Engine
1. Open Godot Engine (Mono/C# version 4.3+).
2. Click **Import** and select the [`project.godot`](file:///Users/s4n/Documents/clg/sem7/project%20phase%202/fin-yr-godot-version/project.godot) file.
3. Build the project using the C# build button in the top right.
4. Press **F5** to run the project.

### Using CLI / .NET
```bash
dotnet build DungeonCrawler.csproj
```

## ⚙️ Environment Setup

Copy `.env.example` to `.env` and fill in your backend LLM server URL and optional credentials:

```bash
cp .env.example .env
```
