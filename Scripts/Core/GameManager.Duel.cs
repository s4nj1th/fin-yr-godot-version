using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using DungeonCrawler.AI;

namespace DungeonCrawler.Core;

/// <summary>One measured duel: the fighter against a pack of identical enemies in an open arena.</summary>
public sealed class DuelSpec
{
	[JsonPropertyName("id")] public string Id { get; set; } = "";
	[JsonPropertyName("behavior")] public string Behavior { get; set; } = EnemyArchetypes.MeleeChaser;
	[JsonPropertyName("scale")] public float Scale { get; set; } = 1f;
	[JsonPropertyName("tempo")] public float Tempo { get; set; } = 1f;
	[JsonPropertyName("elite")] public bool Elite { get; set; }
	[JsonPropertyName("count")] public int Count { get; set; } = 1;
	[JsonPropertyName("dodge")] public float Dodge { get; set; }
	[JsonPropertyName("seed")] public ulong Seed { get; set; } = 1;
}

public sealed class DuelPlan
{
	[JsonPropertyName("timeout_sec")] public double TimeoutSeconds { get; set; } = 60;
	[JsonPropertyName("duels")] public List<DuelSpec> Duels { get; set; } = new();
}

/// <summary>
/// Duel mode (`-- --duel-plan=PLAN.json --duel-out=OUT.jsonl`): measures how
/// much damage the scripted fighter (<see cref="FighterBotPolicy"/>) takes
/// to defeat each kind of enemy, using the game's own combat code — the
/// same EnemyController, attack resolution and damage mitigation as the
/// dungeon. The simulated player (Backend/simulated_player.py) is built from
/// these measurements. Run by tools/run_duels.py; no backend is needed.
/// </summary>
public partial class GameManager
{
	private const int ArenaSize = 15;
	private bool _duelMode;
	private string? _duelPlanPath;
	private string? _duelOutPath;
	private DuelPlan _duelPlan = new();
	private int _duelIndex = -1;
	private DuelSpec? _currentDuel;
	private Vector2I _duelAnchor;

	private void StartDuelMode()
	{
		_duelMode = true;
		AutoplayEnabled = true;
		Engine.TimeScale = 1.0;
		try
		{
			_duelPlan = JsonSerializer.Deserialize<DuelPlan>(File.ReadAllText(_duelPlanPath!)) ?? new DuelPlan();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[Duel] Could not read plan {_duelPlanPath}: {ex.Message}");
			GetTree().Quit(1);
			return;
		}

		if (!string.IsNullOrEmpty(_duelOutPath))
		{
			File.WriteAllText(_duelOutPath, "");
		}

		GD.Print($"[Duel] {_duelPlan.Duels.Count} duels, timeout {_duelPlan.TimeoutSeconds}s.");
		NextDuel();
	}

	private void NextDuel()
	{
		_duelIndex++;
		if (_duelIndex >= _duelPlan.Duels.Count)
		{
			GD.Print("[Duel] Plan complete.");
			GetTree().Quit();
			return;
		}

		_currentDuel = _duelPlan.Duels[_duelIndex];
		BotDodgeSkill = Mathf.Clamp(_currentDuel.Dodge, 0f, 1f);
		_botRng.Seed = (_currentDuel.Seed * 7919UL) + 17UL;
		_currentGenome = Data.GenomeData.CreateDefault();
		_currentGenome.Params.MapSeed = (long)_currentDuel.Seed;
		RegenerateDungeon();
	}

	/// <summary>An open square arena in the middle of an otherwise solid map.</summary>
	private void GenerateArena()
	{
		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				_tiles[y, x] = 0;
			}
		}

		int x0 = (_mapWidth - ArenaSize) / 2;
		int y0 = (_mapHeight - ArenaSize) / 2;
		_rooms.Clear();
		_rooms.Add((x0, y0, ArenaSize, ArenaSize));
		CarveRoom(_rooms[0]);

		// Start positions vary with the duel's seed, so repeated duels sample
		// different approach angles and distances.
		var random = new Random((int)(_currentDuel!.Seed % int.MaxValue));
		_startPos = new Vector2I(x0 + random.Next(1, 5), y0 + random.Next(2, ArenaSize - 2));
		_duelAnchor = new Vector2I(x0 + random.Next(8, ArenaSize - 1), y0 + random.Next(2, ArenaSize - 2));
		_exitPos = new Vector2I(-1, -1);
		_playerPos = _startPos;
	}

	private void SpawnDuelEnemies()
	{
		DuelSpec duel = _currentDuel!;
		Vector2I anchor = _duelAnchor;
		var tiles = new List<Vector2I> { anchor };
		foreach (GridPoint extra in AttackGeometry.PackPlacement(ToGridPoint(anchor), Math.Clamp(duel.Count, 1, 6) - 1, _tiles,
			cell => tiles.Contains(new Vector2I(cell.X, cell.Y))))
		{
			tiles.Add(new Vector2I(extra.X, extra.Y));
		}

		foreach (Vector2I tile in tiles)
		{
			SpawnEnemy(tile, duel.Behavior, duel.Scale, duel.Tempo, duel.Elite);
		}
	}

	private void ProcessDuel(double delta)
	{
		if (_currentDuel == null)
		{
			return;
		}

		_trialElapsedSeconds += delta;
		_attackRequested = true;
		HandlePlayerInput(delta);
		HandlePlayerAttack(delta);

		for (int i = _enemies.Count - 1; i >= 0; i--)
		{
			if (_enemies[i].CurrentHp <= 0)
			{
				_enemies.RemoveAt(i);
				continue;
			}

			_enemies[i].Tick(delta, _playerPos, _tiles);
		}

		UpdateCameraPosition();

		bool cleared = _enemies.Count == 0;
		if (cleared || _trialElapsedSeconds >= _duelPlan.TimeoutSeconds)
		{
			FinishDuel(timedOut: !cleared);
		}
	}

	private void FinishDuel(bool timedOut)
	{
		DuelSpec duel = _currentDuel!;
		int attacks = 0, telegraphed = 0, dodged = 0, blocked = 0;
		foreach (KeyValuePair<string, Dictionary<string, Dictionary<string, int>>> archetype in _behaviorTelemetry.Snapshot())
		{
			foreach (Dictionary<string, int> counters in archetype.Value.Values)
			{
				attacks += counters["attacks_on_player"];
				telegraphed += counters["telegraphed_attacks"];
				dodged += counters["attacks_dodged"];
				blocked += counters["hits_blocked"];
			}
		}

		var record = new Dictionary<string, object>
		{
			["id"] = duel.Id,
			["behavior"] = duel.Behavior,
			["scale"] = duel.Scale,
			["tempo"] = duel.Tempo,
			["elite"] = duel.Elite,
			["count"] = duel.Count,
			["dodge"] = duel.Dodge,
			["seed"] = duel.Seed,
			["damage_taken"] = _trialDamageTaken,
			["deaths"] = _trialDeaths,
			["time_sec"] = Math.Round(_trialElapsedSeconds, 3),
			["kills"] = _trialEnemiesKilled,
			["hits_on_player"] = attacks,
			["telegraphed_attacks"] = telegraphed,
			["attacks_dodged"] = dodged,
			["hits_blocked"] = blocked,
			["timed_out"] = timedOut
		};
		string line = JsonSerializer.Serialize(record);
		if (!string.IsNullOrEmpty(_duelOutPath))
		{
			File.AppendAllText(_duelOutPath, line + System.Environment.NewLine);
		}
		else
		{
			GD.Print($"[Duel] {line}");
		}

		NextDuel();
	}
}
