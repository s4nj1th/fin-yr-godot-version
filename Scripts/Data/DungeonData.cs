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
	[JsonPropertyName("class_id")] public string ClassId { get; set; } = "scout";
}

public class EnemyStats
{
	[JsonPropertyName("max_hp")] public int MaxHp { get; set; } = 20;
	[JsonPropertyName("speed")] public float Speed { get; set; } = 1.0f;
	[JsonPropertyName("attack")] public int Attack { get; set; } = 8;
	[JsonPropertyName("defense")] public int Defense { get; set; } = 0;
	[JsonPropertyName("vision_range")] public int VisionRange { get; set; } = 5;
	[JsonPropertyName("attack_range")] public int AttackRange { get; set; } = 1;
	[JsonPropertyName("reward")] public int Reward { get; set; } = 10;
}

public class PlayerStats
{
	[JsonPropertyName("max_hp")] public int MaxHp { get; set; } = 100;
	[JsonPropertyName("attack")] public int Attack { get; set; } = 15;
	[JsonPropertyName("defense")] public int Defense { get; set; } = 5;
	[JsonPropertyName("move_speed")] public float MoveSpeed { get; set; } = 1.0f;
	[JsonPropertyName("attack_range")] public int AttackRange { get; set; } = 1;
}

public class EnemyAiPixelGrid
{
	[JsonPropertyName("width")] public int Width { get; set; } = 5;
	[JsonPropertyName("height")] public int Height { get; set; } = 5;
	[JsonPropertyName("pixels")] public List<List<int>> Pixels { get; set; } = new();

	public static EnemyAiPixelGrid CreateDefault()
	{
		var grid = new EnemyAiPixelGrid();
		for (int y = 0; y < grid.Height; y++)
		{
			var row = new List<int>();
			for (int x = 0; x < grid.Width; x++)
			{
				row.Add(0);
			}
			grid.Pixels.Add(row);
		}

		return grid;
	}
}

public class EnemyClassDefinition
{
	[JsonPropertyName("class_id")] public string ClassId { get; set; } = "scout";
	[JsonPropertyName("display_name")] public string DisplayName { get; set; } = "Scout";
	[JsonPropertyName("behavior")] public string Behavior { get; set; } = "patrol";
	[JsonPropertyName("stats")] public EnemyStats Stats { get; set; } = new();
	[JsonPropertyName("ai_grid")] public EnemyAiPixelGrid AiGrid { get; set; } = EnemyAiPixelGrid.CreateDefault();

	public static EnemyClassDefinition Create(string classId, string displayName, string behaviorType, EnemyStats? stats = null)
	{
		var definition = new EnemyClassDefinition
		{
			ClassId = classId,
			DisplayName = displayName,
			Behavior = behaviorType,
			Stats = stats ?? new EnemyStats()
		};

		definition.AiGrid = EnemyAiPixelGrid.CreateDefault();
		return definition;
	}

	public static EnemyClassDefinition CreateDefault(string behaviorType = "patrol")
	{
		return Create("scout", "Scout", behaviorType, new EnemyStats
		{
			MaxHp = 18,
			Speed = 1.2f,
			Attack = 8,
			VisionRange = 5,
			AttackRange = 1,
			Reward = 12
		});
	}
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
	[JsonPropertyName("exit_reached")] public bool ExitReached { get; set; }
	[JsonPropertyName("quit_early")] public bool QuitEarly { get; set; }
}

// --- Evolutionary-algorithm genome contract (Backend/models.py mirrors this) ---
// A genome is a flat set of dungeon-generation + encounter parameters that the
// Python MCC backend evolves. See docs/EVOLUTIONARY_DESIGN.md for the design
// rationale (genotype, mutation, minimal criterion, surrogate gating).

public class GenomeParams
{
	[JsonPropertyName("map_width")] public int MapWidth { get; set; } = 160;
	[JsonPropertyName("map_height")] public int MapHeight { get; set; } = 160;
	[JsonPropertyName("room_attempts")] public int RoomAttempts { get; set; } = 280;
	[JsonPropertyName("trap_density")] public float TrapDensity { get; set; } = 0.25f;
	[JsonPropertyName("chest_density")] public float ChestDensity { get; set; } = 0.25f;
	[JsonPropertyName("heal_density")] public float HealDensity { get; set; } = 0.25f;
	[JsonPropertyName("enemy_density")] public float EnemyDensity { get; set; } = 0.25f;
	[JsonPropertyName("enemy_behavior_mix")] public Dictionary<string, float> EnemyBehaviorMix { get; set; } = new()
	{
		{ "melee_chaser", 0.34f },
		{ "melee_hit_run", 0.33f },
		{ "ranged_kite", 0.33f },
		{ "ranged_strafe", 0.0f }
	};
	[JsonPropertyName("enemy_difficulty_scale")] public float EnemyDifficultyScale { get; set; } = 1.0f;
}

public class GenomeData
{
	[JsonPropertyName("id")] public string Id { get; set; } = "";
	[JsonPropertyName("parent_id")] public string? ParentId { get; set; }
	[JsonPropertyName("generation")] public int Generation { get; set; }
	[JsonPropertyName("params")] public GenomeParams Params { get; set; } = new();

	public static GenomeData CreateDefault()
	{
		return new GenomeData { Id = "", ParentId = null, Generation = 0, Params = new GenomeParams() };
	}
}

public class NextCandidateResponse
{
	[JsonPropertyName("genome_id")] public string GenomeId { get; set; } = "";
	[JsonPropertyName("genome")] public GenomeData Genome { get; set; } = new();
}

public class ReportOutcomeRequest
{
	[JsonPropertyName("genome_id")] public string GenomeId { get; set; } = "";
	[JsonPropertyName("telemetry")] public TelemetryMetrics Telemetry { get; set; } = new();
}

public class ReportOutcomeResponse
{
	[JsonPropertyName("passed")] public bool Passed { get; set; }
	[JsonPropertyName("hall_of_fame_size")] public int HallOfFameSize { get; set; }
	[JsonPropertyName("fail_reason")] public string? FailReason { get; set; }
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
