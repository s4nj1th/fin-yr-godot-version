using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DungeonCrawler.Data;

public class DungeonConfig
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("layout_style")]
    public string LayoutStyle { get; set; } = "balanced";

    [JsonPropertyName("corridor_width")]
    public int CorridorWidth { get; set; } = 2;

    [JsonPropertyName("room_density")]
    public float RoomDensity { get; set; } = 0.5f;

    [JsonPropertyName("enemy_density")]
    public float EnemyDensity { get; set; } = 0.2f;

    [JsonPropertyName("enemy_type")]
    public string EnemyType { get; set; } = "patrol";

    [JsonPropertyName("difficulty")]
    public float Difficulty { get; set; } = 0.5f;

    [JsonPropertyName("trap_probability")]
    public float TrapProbability { get; set; } = 0.15f;

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "normal";

    [JsonPropertyName("reward_density")]
    public float RewardDensity { get; set; } = 0.15f;
}

public class PaletteData
{
    [JsonPropertyName("background")] public string Background { get; set; } = "#030712";
    [JsonPropertyName("wall")] public string Wall { get; set; } = "#0f172a";
    [JsonPropertyName("floor")] public string Floor { get; set; } = "#0f2b46";
    [JsonPropertyName("corridor")] public string Corridor { get; set; } = "#091e36";
    [JsonPropertyName("door")] public string Door { get; set; } = "#38bdf8";
    [JsonPropertyName("trap")] public string Trap { get; set; } = "#f43f5e";
    [JsonPropertyName("enemy")] public string Enemy { get; set; } = "#e11d48";
    [JsonPropertyName("chest")] public string Chest { get; set; } = "#f59e0b";
    [JsonPropertyName("start")] public string Start { get; set; } = "#00ffcc";
    [JsonPropertyName("exit")] public string Exit { get; set; } = "#00ff66";
    [JsonPropertyName("fog")] public string Fog { get; set; } = "#020617";
}

public class PointData
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}

public class RoomData
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
}

public class EnemyData
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "patrol";
}

public class DungeonMapData
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("seed")] public long Seed { get; set; }
    [JsonPropertyName("config")] public DungeonConfig Config { get; set; } = new();
    [JsonPropertyName("palette")] public PaletteData Palette { get; set; } = new();
    [JsonPropertyName("theme_name")] public string ThemeName { get; set; } = "Dark Obsidian";
    [JsonPropertyName("start")] public PointData Start { get; set; } = new();
    [JsonPropertyName("exit")] public PointData Exit { get; set; } = new();
    [JsonPropertyName("rooms")] public List<RoomData> Rooms { get; set; } = new();
    [JsonPropertyName("enemies")] public List<EnemyData> Enemies { get; set; } = new();
    [JsonPropertyName("grid")] public List<List<int>> Grid { get; set; } = new();
}

public class TelemetryMetrics
{
    [JsonPropertyName("time_taken_sec")] public float TimeTakenSec { get; set; }
    [JsonPropertyName("damage_taken")] public int DamageTaken { get; set; }
    [JsonPropertyName("health_remaining")] public int HealthRemaining { get; set; }
    [JsonPropertyName("deaths_retries")] public int DeathsRetries { get; set; }
    [JsonPropertyName("enemies_killed")] public int EnemiesKilled { get; set; }
    [JsonPropertyName("total_enemies")] public int TotalEnemies { get; set; }
    [JsonPropertyName("chests_opened")] public int ChestsOpened { get; set; }
    [JsonPropertyName("total_chests")] public int TotalChests { get; set; }
    [JsonPropertyName("traps_triggered")] public int TrapsTriggered { get; set; }
    [JsonPropertyName("exploration_percent")] public float ExplorationPercent { get; set; }
}

public class TelemetryRequest
{
    [JsonPropertyName("level_number")] public int LevelNumber { get; set; }
    [JsonPropertyName("telemetry")] public TelemetryMetrics Telemetry { get; set; } = new();
    [JsonPropertyName("user_preference")] public string UserPreference { get; set; } = "";
    [JsonPropertyName("previous_config")] public DungeonConfig PreviousConfig { get; set; } = new();
}

public class NarrativeData
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("lore")] public string Lore { get; set; } = "";
    [JsonPropertyName("degrade_factor")] public float DegradeFactor { get; set; }
    [JsonPropertyName("theme")] public string Theme { get; set; } = "dark";
}

public class LLMAdaptationResponse
{
    [JsonPropertyName("playstyle_assessment")] public string PlaystyleAssessment { get; set; } = "";
    [JsonPropertyName("adaptation_rationale")] public string AdaptationRationale { get; set; } = "";
    [JsonPropertyName("narrative_isomorphism")] public NarrativeData NarrativeIsomorphism { get; set; } = new();
    [JsonPropertyName("next_config")] public DungeonConfig NextConfig { get; set; } = new();
    [JsonPropertyName("map_data")] public DungeonMapData MapData { get; set; } = new();
}
