using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DungeonCrawler.Data;

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

	// Per-archetype, per-situation combat counters for this trial (see
	// DungeonCrawler.AI.EnemyBehaviorTelemetry.Snapshot and
	// docs/BEHAVIOR_ANALYTICS.md). Shape: {behaviorType: {"baseline": {...},
	// "adapted": {...}}}, each innermost dict a flat set of int counters.
	// Optional/additive: an older backend that doesn't know this field just
	// ignores it (Backend/models.py defaults it to {}), so this never
	// breaks the core report_outcome contract.
	[JsonPropertyName("enemy_behavior_breakdown")] public Dictionary<string, Dictionary<string, Dictionary<string, int>>> EnemyBehaviorBreakdown { get; set; } = new();
}

// --- Evolutionary-algorithm genome contract (Backend/models.py mirrors this) ---
// A genome describes one enemy encounter (density, difficulty scale,
// tempo, elite rate, pack size, behaviour mix), which the Python backend
// evolves. The map and resource fields are not evolved: the backend sends the same fixed values with every
// genome (Backend/config.py FIXED_ENVIRONMENT), and the defaults below mirror
// them so offline play builds the same dungeon. See
// docs/EVOLUTIONARY_DESIGN.md for the design rationale.

public class GenomeParams
{
	[JsonPropertyName("map_width")] public int MapWidth { get; set; } = 160;
	[JsonPropertyName("map_height")] public int MapHeight { get; set; } = 160;
	[JsonPropertyName("room_attempts")] public int RoomAttempts { get; set; } = 280;
	[JsonPropertyName("trap_density")] public float TrapDensity { get; set; } = 0.15f;
	[JsonPropertyName("chest_density")] public float ChestDensity { get; set; } = 0.15f;
	[JsonPropertyName("heal_density")] public float HealDensity { get; set; } = 0.15f;
	// Seeds the layout generator so every trial is played on the same dungeon;
	// 0 means a new random layout every trial.
	[JsonPropertyName("map_seed")] public long MapSeed { get; set; } = 123;
	[JsonPropertyName("enemy_density")] public float EnemyDensity { get; set; } = 0.25f;
	// Spawn share of each archetype (DungeonCrawler.AI.EnemyArchetypes); the
	// default is the original game's encounter, which used only three.
	[JsonPropertyName("enemy_behavior_mix")] public Dictionary<string, float> EnemyBehaviorMix { get; set; } = new()
	{
		{ "melee_chaser", 0.34f },
		{ "melee_hit_run", 0.33f },
		{ "ranged_kite", 0.33f },
		{ "ranged_strafe", 0.0f },
		{ "melee_charger", 0.0f },
		{ "ranged_bomber", 0.0f },
		{ "melee_shield", 0.0f }
	};
	[JsonPropertyName("enemy_difficulty_scale")] public float EnemyDifficultyScale { get; set; } = 1.0f;
	// Speeds up enemy movement and shortens every wind-up and cooldown.
	[JsonPropertyName("enemy_tempo")] public float EnemyTempo { get; set; } = 1.0f;
	// Probability that a spawned enemy is an elite (tougher, harder-hitting, enrages at half health).
	[JsonPropertyName("elite_rate")] public float EliteRate { get; set; } = 0.0f;
	// Expected enemies per spawn point (1 to 3; the fractional part is a probability).
	[JsonPropertyName("pack_size")] public float PackSize { get; set; } = 1.0f;
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

public class SurrogateEstimate
{
	[JsonPropertyName("prob")] public float Prob { get; set; }
	[JsonPropertyName("std")] public float Std { get; set; }
	[JsonPropertyName("epistemic_std")] public float? EpistemicStd { get; set; }
}

public class NextCandidateResponse
{
	[JsonPropertyName("genome_id")] public string GenomeId { get; set; } = "";
	[JsonPropertyName("genome")] public GenomeData Genome { get; set; } = new();
	[JsonPropertyName("surrogate_estimate")] public SurrogateEstimate? SurrogateEstimate { get; set; }
}

public class ReportOutcomeRequest
{
	[JsonPropertyName("genome_id")] public string GenomeId { get; set; } = "";
	[JsonPropertyName("telemetry")] public TelemetryMetrics Telemetry { get; set; } = new();
	// "human" (default) or "bot" — which evaluator generated this trial. Set
	// by GameManager.EndTrialAndAdvanceAsync from AutoplayEnabled. See
	// docs/EVOLUTIONARY_DESIGN.md "Scripted bot evaluator" and
	// Backend/models.py's ReportOutcomeRequest.evaluator_type.
	[JsonPropertyName("evaluator_type")] public string EvaluatorType { get; set; } = "human";
	// Whose archive this trial belongs to (Backend/models.py player_id).
	[JsonPropertyName("player_id")] public string PlayerId { get; set; } = "default";
}

public class ReportOutcomeResponse
{
	[JsonPropertyName("passed")] public bool Passed { get; set; }
	[JsonPropertyName("hall_of_fame_size")] public int HallOfFameSize { get; set; }
	[JsonPropertyName("fail_reason")] public string? FailReason { get; set; }
	[JsonPropertyName("player_id")] public string? PlayerId { get; set; }
}
