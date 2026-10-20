using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using DungeonCrawler.AI;
using DungeonCrawler.Audio;
using DungeonCrawler.Data;
using DungeonCrawler.Entities;
using DungeonCrawler.Network;

namespace DungeonCrawler.Core;

internal sealed class DungeonGenerationSettings
{
	public int MapWidth { get; set; } = 160;
	public int MapHeight { get; set; } = 160;
	public int RoomAttempts { get; set; } = 280;
	public int MinRoomWidth { get; set; } = 4;
	public int MaxRoomWidth { get; set; } = 12;
	public int MinRoomHeight { get; set; } = 4;
	public int MaxRoomHeight { get; set; } = 10;
	public float FogRevealRadius { get; set; } = 6.5f;
	public float FogFeather { get; set; } = 2.5f;
	public float FogOpacity { get; set; } = 0.88f;
	public float FogDiscoveredOpacity { get; set; } = 0.45f;
}

public partial class GameManager : Node3D
{
	private const float TileHeight = 0.35f;

	private readonly RandomNumberGenerator _rng = new();
	private readonly DungeonGenerationSettings _generationSettings = new();
	private readonly List<EnemyController> _enemies = new();
	private readonly List<(int X, int Y, int W, int H)> _rooms = new();
	// Tiles are GPU-instanced via MultiMesh, not one MeshInstance3D per tile —
	// a 160x160 map is 25,600 cells; that many individual nodes/meshes/
	// materials was the single biggest frame-rate cost in the game (worse
	// than a full enemy spawn). Grouped into three MultiMeshInstance3D
	// buckets by geometry (wall / floor+corridor / taller special tiles);
	// per-tile color still varies via MultiMesh instance color, so this
	// keeps the existing tile palette and LightenTile's single-tile
	// recolor-on-chest-open exactly as before, just GPU-instanced.
	private readonly Dictionary<Vector2I, (MultiMesh Mesh, int Index, Color BaseColor)> _tileInstanceByPos = new();
	// Tile IDs: 0 wall, 1 floor, 2 corridor, 3 door (unused by generation yet),
	// 4 trap (drains HP), 5 enemy spawn marker (no effect), 6 chest (one-time score),
	// 7 start, 8 exit, 9 heal (restores HP). Colors are deliberately spread across
	// distinct hues — 4/5 and 7/9 used to share near-identical reds/greens, which
	// made trap-vs-spawn and heal-vs-start indistinguishable at a glance.
	private readonly Dictionary<int, Color> _tileColors = new()
	{
		{ 0, Color.FromHtml("#170f2e") },
		{ 1, Color.FromHtml("#241a44") },
		{ 2, Color.FromHtml("#1c1438") },
		{ 3, Color.FromHtml("#a78bfa") },
		{ 4, Color.FromHtml("#fb2c46") },
		{ 5, Color.FromHtml("#d946ef") },
		{ 6, Color.FromHtml("#fbbf24") },
		{ 7, Color.FromHtml("#22d3ee") },
		{ 8, Color.FromHtml("#34d399") },
		{ 9, Color.FromHtml("#f472b6") }
	};

	private const double TrapDamagePerSecond = 12.0;
	private const double HealPerSecond = 10.0;

	private int[,] _tiles = null!;
	private bool[,] _discoveredTiles = null!;
	private bool[,] _chestClaimed = null!;
	private Image _discoveryImage = null!;
	private ImageTexture _discoveryTexture = null!;
	private int _mapWidth;
	private int _mapHeight;
	private ulong _generationCounter;

	private Node3D _levelRoot = null!;
	private Node3D _enemyRoot = null!;
	private Node3D _markerRoot = null!;
	private readonly Dictionary<Vector2I, MeshInstance3D> _markersByPos = new();
	private MeshInstance3D _playerMesh = null!;
	private MeshInstance3D _fogOverlay = null!;
	private ShaderMaterial _fogMaterial = null!;
	private Camera3D _camera = null!;
	private DirectionalLight3D? _sunLight;
	private WorldEnvironment? _worldEnvironment;
	private Vector2I _playerPos;
	private Vector2I _startPos;
	private Vector2I _exitPos;
	private double _moveTimer;
	private int _score;
	private bool _rewardCollected;
	private double _tileEffectAccumulator;
	private int _lastStandingTileType = -1;

	private readonly PlayerStats _playerStats = new()
	{
		MaxHp = 100,
		Attack = 15,
		Defense = 5,
		MoveSpeed = 1.0f,
		AttackRange = 1
	};
	private int _playerCurrentHp;
	private double _attackCooldownTimer;
	// Set from the actual key-down InputEvent in _Input, not polled from
	// Input.IsKeyPressed in _Process — a poll only sees the key's state at
	// the instant _Process runs, so a quick tap that starts and ends between
	// two frames is invisible to it (worse the lower the frame rate, which
	// is exactly when "attack didn't register" complaints showed up). Godot
	// queues the actual press event regardless of frame rate, so this can't
	// miss a tap the way the poll could.
	private bool _attackRequested;
	private const double AttackCooldown = 0.35;
	// This is a 4-directional grid (matches enemy movement, pathfinding, and
	// attack-range logic throughout) — there's no diagonal step. Tracks which
	// axis's key was pressed most recently, so holding two keys from
	// different axes at once (e.g. W+A) resolves to the direction the player
	// most recently reached for, instead of both axes' "only this key"
	// guards individually failing and producing no movement at all.
	private bool _lastMovementAxisWasHorizontal;

	// --- Evolutionary loop integration (docs/EVOLUTIONARY_DESIGN.md) ---
	// Each "trial" is one dungeon generated from a genome; when it ends
	// (exit reached, manual regenerate, or timeout) we report pass/fail
	// telemetry to the Python backend under Backend/ and fetch the next
	// genome. If the backend isn't running, everything falls back to the
	// default local genome — the backend is optional.
	private const double MaxTrialSeconds = 240.0; // mirrors Backend/config.py MAX_TRIAL_SECONDS
	private ApiClient _apiClient = null!;
	private GenomeData _currentGenome = GenomeData.CreateDefault();
	private string? _currentGenomeId;
	private bool _backendAvailable;
	private bool _isFetchingGenome;
	private bool _rKeyWasDown;

	private double _trialElapsedSeconds;
	private int _trialDeaths;
	private int _trialDamageTaken;
	private int _trialEnemiesKilled;
	private int _trialTrapsTriggered;
	private int _trialChestsOpened;
	private int _trialTotalChests;
	private bool _trialReported;

	// --- Scripted bot evaluator (optional, off by default) ---
	// See docs/EVOLUTIONARY_DESIGN.md "Scripted bot evaluator" section.
	// Reuses the exact same trial loop, GameManager/EnemyController combat
	// code, and /api/report_outcome path a human would — only the input
	// source changes (BotPlayerPolicy instead of keyboard). Trials it plays
	// are tagged evaluator_type="bot" so they stay a labeled, filterable
	// ablation rather than silently blending into human-sourced data.
	[Export] public bool AutoplayEnabled { get; set; }
	// Godot's Engine.TimeScale speeds up every delta-driven system uniformly
	// (movement step timers, attack cooldowns, MAX_TRIAL_SECONDS) — a bot
	// doesn't need real-time pacing the way a human reading the screen does.
	[Export] public float AutoplayTimeScale { get; set; } = 4.0f;
	// 0 = run until the process is stopped externally; >0 = self-quit after
	// that many reported trials, for an unattended overnight batch run.
	[Export] public int AutoplayMaxTrials { get; set; }
	private int _autoplayTrialsCompleted;
	// Probability that the scripted player reacts to a telegraphed attack
	// (steps out of a charge line or blast area, flanks a shieldbearer).
	// 0 = never, as the original bot; set with --bot-dodge=Q.
	[Export] public float BotDodgeSkill { get; set; }
	private const double BotReactionSeconds = 0.2;
	private readonly RandomNumberGenerator _botRng = new();
	private readonly List<(EnemyController Source, HashSet<GridPoint> Cells, double ActiveAt)> _botDangers = new();
	private readonly Dictionary<EnemyController, bool> _botFlankDecisions = new();
	// Scripted playstyle of the exit bot: "exit" walks to the exit; "hide"
	// also steps out of the line of sight of enemies that spot it (used to
	// evaluate the director's stealth countermeasure, tools/evaluate_director.py).
	private string _botStyle = "exit";
	private const int HideTriggerDistance = 6;

	// The tactical director: "live" (default) hands orders to new enemies and
	// re-tasks nearby ones; "spawn" hands orders to new enemies only; "off"
	// gives no orders. Set with --director=MODE; study sessions turn it off
	// unless the session belongs to the director study.
	private string _directorMode = "live";
	private bool DirectorGivesOrders => _directorMode != "off" && !(_studyMode && !_studyDirectorOn);
	private bool DirectorRetasks => _directorMode == "live" && DirectorGivesOrders;

	// Whose archive the backend evolves designs for: each player gets their
	// own archive and surrogate (Backend/adaptive_evolution.py). Set here, or
	// with `--player=NAME` on the command line. Left empty, human play uses
	// "default" and the bot uses "bot", so bot trials never train a human
	// player's surrogate.
	[Export] public string PlayerId { get; set; } = "";

	// --- Adaptive enemy AI (docs/ENEMY_AI_ADAPTATION_PLAN.md) ---
	// Unlike the _trial* counters above (which reset every regeneration and
	// feed the evolutionary-loop backend), these persist for the whole play
	// session — the player's demonstrated habits should accumulate across
	// dungeon visits, not reset every time a trial ends (section 1: "learn
	// tendencies from evidence," not from one run in isolation).
	private readonly PlayerBehaviorTelemetry _playerTelemetry = new();
	private readonly PlayerPlaystyleProfile _playerProfile = new();
	// The stealth countermeasure intercepts the player on their route to the
	// exit by default (--stealth=search: search where they were last seen).
	private readonly AdaptationDirector _adaptationDirector = new() { InterceptRoute = true };
	// Per-archetype combat counters, reset every trial (unlike the
	// lifetime-accumulating fields above) — see docs/BEHAVIOR_ANALYTICS.md.
	private readonly EnemyBehaviorTelemetry _behaviorTelemetry = new();
	private Vector2I _playerFacing = Vector2I.Down;
	private bool _anyEnemyTrackingLastFrame;
	private bool _debugRearDefenseWasActive;
	private bool _debugStealthWasActive;
	private double _sinceLastAttackSeconds = double.MaxValue;
	private int _nearestEnemyDistanceBeforeMove = int.MaxValue;
	private const double KitingWindowSeconds = 2.0;

	public int PlayerCurrentHp => _playerCurrentHp;
	public int PlayerMaxHp => _playerStats.MaxHp;
	public int Score => _score;
	public string? LastTrialResultMessage { get; private set; }
	public Vector2I PlayerGridPosition => _playerPos;
	public Vector2I ExitGridPosition => _exitPos;
	public Vector2I PlayerFacingDirection => _playerFacing;
	public string PlaystyleDebugSummary => _playerProfile.DebugSummary();

	public override void _Ready()
	{
		_apiClient = new ApiClient();
		AddChild(_apiClient);

		// CLI flags let a headless/exported build be launched unattended
		// (e.g. `godot --headless -- --autoplay --autoplay-max-trials=20`)
		// without needing the editor GUI to set the scene's Inspector
		// exports each time — either source (Inspector or CLI) turns it on.
		// GetCmdlineUserArgs(), not GetCmdlineArgs() — the latter returns the
		// engine's own parsed args (--headless, the scene path, etc.), not
		// what follows `--` on the command line. Confirmed the hard way: an
		// earlier version of this used GetCmdlineArgs() and --autoplay was
		// silently never seen, so the "bot" run just sat idle and timed out.
		string[] cmdlineArgs = OS.GetCmdlineUserArgs();
		if (!AutoplayEnabled && Array.Exists(cmdlineArgs, arg => arg == "--autoplay"))
		{
			AutoplayEnabled = true;
		}

		foreach (string arg in cmdlineArgs)
		{
			if (arg.StartsWith("--autoplay-max-trials=", StringComparison.Ordinal)
				&& int.TryParse(arg.AsSpan("--autoplay-max-trials=".Length), out int maxTrials))
			{
				AutoplayMaxTrials = maxTrials;
			}
			else if (arg.StartsWith("--autoplay-timescale=", StringComparison.Ordinal)
				&& float.TryParse(arg.AsSpan("--autoplay-timescale=".Length), out float timeScale))
			{
				AutoplayTimeScale = timeScale;
			}
			else if (arg.StartsWith("--player=", StringComparison.Ordinal))
			{
				PlayerId = arg.Substring("--player=".Length);
			}
			else if (arg.StartsWith("--bot-dodge=", StringComparison.Ordinal)
				&& float.TryParse(arg.AsSpan("--bot-dodge=".Length), System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out float dodge))
			{
				BotDodgeSkill = Mathf.Clamp(dodge, 0f, 1f);
			}
			else if (arg.StartsWith("--bot-style=", StringComparison.Ordinal))
			{
				_botStyle = arg.Substring("--bot-style=".Length);
			}
			else if (arg.StartsWith("--stealth=", StringComparison.Ordinal))
			{
				string response = arg.Substring("--stealth=".Length);
				_adaptationDirector.InterceptRoute = response == "intercept";
				GD.Print($"[AdaptiveAI] Stealth countermeasure: {(_adaptationDirector.InterceptRoute ? "intercept" : "search")}.");
			}
			else if (arg.StartsWith("--director=", StringComparison.Ordinal))
			{
				string mode = arg.Substring("--director=".Length);
				_directorMode = mode is "off" or "spawn" or "live" ? mode : "live";
				GD.Print($"[AdaptiveAI] Director mode: {_directorMode}.");
			}
			else if (arg.StartsWith("--backend-url=", StringComparison.Ordinal))
			{
				_apiClient.BaseUrl = arg.Substring("--backend-url=".Length).TrimEnd('/');
			}
			else if (arg.StartsWith("--duel-plan=", StringComparison.Ordinal))
			{
				_duelPlanPath = arg.Substring("--duel-plan=".Length);
			}
			else if (arg.StartsWith("--duel-out=", StringComparison.Ordinal))
			{
				_duelOutPath = arg.Substring("--duel-out=".Length);
			}
		}

		_botRng.Seed = 20261015;
		if (!string.IsNullOrEmpty(_duelPlanPath))
		{
			StartDuelMode();
			return;
		}

		if (SetUpStudyMode(cmdlineArgs))
		{
			return; // the study screens start the session
		}

		if (string.IsNullOrWhiteSpace(PlayerId))
		{
			PlayerId = AutoplayEnabled ? "bot" : "default";
		}
		GD.Print($"[Evolution] Playing as player '{PlayerId}'.");

		if (AutoplayEnabled)
		{
			Engine.TimeScale = AutoplayTimeScale;
			string stopCondition = AutoplayMaxTrials > 0 ? $"stopping after {AutoplayMaxTrials} trial(s)" : "running until stopped externally";
			GD.Print($"[Autoplay] Enabled — bot-driven trials, Engine.TimeScale={AutoplayTimeScale}, {stopCondition}.");
		}

		RequestNextGenomeAndRegenerateAsync();
	}

	// Event-driven, not polled: guarantees a tap of Space is seen even if it
	// starts and ends within a single (possibly slow) _Process frame. Echo
	// is excluded so holding the key down doesn't spam requests — the actual
	// attack rate is still governed by _attackCooldownTimer in
	// HandlePlayerAttack, this only records that a press happened at all.
	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey { Pressed: true, Echo: false } keyEvent)
		{
			return;
		}

		switch (keyEvent.Keycode)
		{
			case Key.Space:
				_attackRequested = true;
				break;
			case Key.A or Key.D or Key.Left or Key.Right:
				_lastMovementAxisWasHorizontal = true;
				break;
			case Key.W or Key.S or Key.Up or Key.Down:
				_lastMovementAxisWasHorizontal = false;
				break;
		}
	}

	public override void _Process(double delta)
	{
		bool rKeyDown = Input.IsKeyPressed(Key.R);
		if (rKeyDown && !_rKeyWasDown && !_isFetchingGenome)
		{
			_rKeyWasDown = rKeyDown;
			EndTrialAndAdvanceAsync(exitReached: false, quitEarly: true);
			return;
		}
		_rKeyWasDown = rKeyDown;

		if (_duelMode)
		{
			ProcessDuel(delta);
			return;
		}

		if (StudyBlocksGameplay)
		{
			return;
		}

		if (_isFetchingGenome)
		{
			return;
		}

		_trialElapsedSeconds += delta;
		if (_studyMode && !_studySessionOver)
		{
			_studyElapsed += delta;
		}
		if (_trialElapsedSeconds > MaxTrialSeconds && !_trialReported)
		{
			// A passive timeout is a distinct signal from a manual quit — see
			// docs/EVOLUTIONARY_DESIGN.md and adaptive_evolution.py's _evaluate_minimal_criterion.
			EndTrialAndAdvanceAsync(exitReached: false, quitEarly: false);
			return;
		}

		_adaptationDirector.Tick(delta);
		_sinceLastAttackSeconds += delta;

		if (AutoplayEnabled)
		{
			// Equivalent to a human holding the attack key down — the actual
			// rate is still governed by _attackCooldownTimer in
			// HandlePlayerAttack, and TryAttackNearestEnemy no-ops when
			// nothing is in range, so this is safe to set unconditionally.
			_attackRequested = true;
		}

		HandlePlayerInput(delta);
		HandlePlayerAttack(delta);
		HandleStandingTileEffects(delta);
		UpdateTileVisibility();
		UpdateCameraPosition();

		bool anyEnemyTrackingNow = false;
		bool anyEnemyHiddenFromAware = false;
		bool anyLineOfSightJustLost = false;

		for (int i = _enemies.Count - 1; i >= 0; i--)
		{
			EnemyController enemy = _enemies[i];

			if (enemy.CurrentHp <= 0)
			{
				// Already dying — EnemyController plays its own death animation and frees itself.
				_enemies.RemoveAt(i);
				continue;
			}

			enemy.Tick(delta, _playerPos, _tiles);

			// Consumed per-enemy regardless (each enemy's own flag must be
			// cleared), but only counted once per frame below — one player
			// action (e.g. ducking around a corner) can make several
			// enemies lose sight simultaneously, and that's still one
			// demonstrated stealth action, not several.
			if (enemy.ConsumeLineOfSightLostFlag())
			{
				anyLineOfSightJustLost = true;
			}

			switch (enemy.Perception.State)
			{
				case PerceptionState.Tracking:
					anyEnemyTrackingNow = true;
					break;
				case PerceptionState.Investigating or PerceptionState.Searching:
					anyEnemyHiddenFromAware = true;
					break;
			}
		}

		if (anyLineOfSightJustLost)
		{
			_playerTelemetry.RecordLineOfSightBreak();
			_playerProfile.Update(_playerTelemetry);
			ReevaluateDirector();
			GD.Print($"[AdaptiveAI] Line-of-sight break #{_playerTelemetry.LineOfSightBreaks} recorded — " +
				$"StealthPreference={_playerProfile.StealthPreference:F2} Confidence={_playerProfile.Confidence:F2}");
			LogAdaptationTransitions();
		}

		_playerTelemetry.RecordProximityTick(delta, hiddenFromAwareEnemy: anyEnemyHiddenFromAware, observedByAnyEnemy: anyEnemyTrackingNow);

		if (anyEnemyTrackingNow && !_anyEnemyTrackingLastFrame)
		{
			_playerTelemetry.RecordEncounterStarted();
		}
		else if (!anyEnemyTrackingNow && _anyEnemyTrackingLastFrame)
		{
			_playerTelemetry.RecordEncounterEnded();
		}
		_anyEnemyTrackingLastFrame = anyEnemyTrackingNow;
	}

	// Debug visibility only — docs/ENEMY_AI_ADAPTATION_PLAN.md section 6
	// recommends exposing the profile in a debug panel during development;
	// this is the console-log equivalent, printed only on the actual
	// activate/deactivate transition rather than every frame.
	private void LogAdaptationTransitions()
	{
		if (_adaptationDirector.RearDefenseActive != _debugRearDefenseWasActive)
		{
			_debugRearDefenseWasActive = _adaptationDirector.RearDefenseActive;
			GD.Print(_debugRearDefenseWasActive
				? "[AdaptiveAI] Rear-defense countermeasure ACTIVATED — newly spawned enemies will react faster to rear hits."
				: "[AdaptiveAI] Rear-defense countermeasure deactivated (player's tactics shifted away from rear attacks).");
		}

		if (_adaptationDirector.StealthCountermeasureActive != _debugStealthWasActive)
		{
			_debugStealthWasActive = _adaptationDirector.StealthCountermeasureActive;
			GD.Print(_debugStealthWasActive
				? "[AdaptiveAI] Stealth countermeasure ACTIVATED — newly spawned enemies will investigate your last known position."
				: "[AdaptiveAI] Stealth countermeasure deactivated (player's tactics shifted away from breaking line of sight).");
		}
	}

	private async Task RequestNextGenomeAsync()
	{
		NextCandidateResponse? response = await _apiClient.RequestNextGenomeAsync(PlayerId);
		if (response != null)
		{
			_currentGenome = response.Genome;
			_currentGenomeId = response.GenomeId;
			_backendAvailable = true;
		}
		else
		{
			_currentGenome = GenomeData.CreateDefault();
			_currentGenomeId = null;
			_backendAvailable = false;
		}
	}

	// async void is required here, not a style slip: these are entry points
	// called from Godot's synchronous _Ready/_Process lifecycle callbacks
	// (which can't be awaited), so there is no caller able to observe a
	// returned Task. The try/catch is the safety net that pattern needs —
	// an unhandled exception in an async void method would otherwise crash
	// the process instead of surfacing through the log like every other
	// backend-call failure in this file.
	private async void RequestNextGenomeAndRegenerateAsync()
	{
		_isFetchingGenome = true;
		try
		{
			await RequestNextGenomeAsync();
			RegenerateDungeon();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[GameManager] Unexpected error fetching initial genome: {ex.Message}");
		}
		finally
		{
			_isFetchingGenome = false;
		}
	}

	private async void EndTrialAndAdvanceAsync(bool exitReached, bool quitEarly)
	{
		if (_trialReported || _isFetchingGenome)
		{
			return;
		}

		_trialReported = true;
		_isFetchingGenome = true;
		if (EnemyController.InterceptsPlanned > 0)
		{
			GD.Print($"[AdaptiveAI] Intercepts planned this trial: {EnemyController.InterceptsPlanned}.");
		}
		EnemyController.InterceptsPlanned = 0;

		try
		{
			float explorationPercent = ComputeExplorationPercent();
			_playerProfile.UpdateExploration(explorationPercent);

			var telemetry = new TelemetryMetrics
			{
				TimeTakenSec = (float)_trialElapsedSeconds,
				DamageTaken = _trialDamageTaken,
				HealthRemaining = _playerCurrentHp,
				DeathsRetries = _trialDeaths,
				EnemiesKilled = _trialEnemiesKilled,
				TotalEnemies = _enemies.Count + _trialEnemiesKilled,
				ChestsOpened = _trialChestsOpened,
				TotalChests = _trialTotalChests,
				TrapsTriggered = _trialTrapsTriggered,
				ExplorationPercent = explorationPercent,
				ExitReached = exitReached,
				QuitEarly = quitEarly,
				EnemyBehaviorBreakdown = _behaviorTelemetry.Snapshot()
			};

			if (_currentIsReplay)
			{
				await ReportReplayAsync(telemetry);
			}
			else if (_backendAvailable && !string.IsNullOrEmpty(_currentGenomeId))
			{
				ReportOutcomeResponse? result = await _apiClient.ReportOutcomeAsync(new ReportOutcomeRequest
				{
					GenomeId = _currentGenomeId,
					Telemetry = telemetry,
					EvaluatorType = AutoplayEnabled ? "bot" : "human",
					PlayerId = PlayerId
				});

				LastTrialResultMessage = result != null
					? (result.Passed ? $"GENOME ACCEPTED — HALL OF FAME: {result.HallOfFameSize}" : $"GENOME REJECTED ({result.FailReason})")
					: "BACKEND UNREACHABLE — RESULT NOT RECORDED";
			}
			else
			{
				LastTrialResultMessage = "LOCAL MODE — BACKEND OFFLINE";
			}

			if (AutoplayEnabled)
			{
				_autoplayTrialsCompleted++;
				if (AutoplayMaxTrials > 0 && _autoplayTrialsCompleted >= AutoplayMaxTrials)
				{
					GD.Print($"[Autoplay] Reached AutoplayMaxTrials={AutoplayMaxTrials} — quitting.");
					GetTree().Quit();
					return;
				}
			}

			if (_studyMode && !await PrepareNextStudyTrialAsync())
			{
				return;
			}

			if (!TryTakeReplayDesign())
			{
				await RequestNextGenomeAsync();
			}
			RegenerateDungeon();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[GameManager] Unexpected error ending trial: {ex.Message}");
		}
		finally
		{
			_isFetchingGenome = false;
		}
	}

	private float ComputeExplorationPercent()
	{
		int discovered = 0;
		int walkable = 0;

		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				if (_tiles[y, x] == 0)
				{
					continue;
				}

				walkable++;
				if (_discoveredTiles[y, x])
				{
					discovered++;
				}
			}
		}

		return walkable > 0 ? (discovered / (float)walkable) * 100f : 0f;
	}

	private int CountTilesOfType(int type)
	{
		int count = 0;
		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				if (_tiles[y, x] == type)
				{
					count++;
				}
			}
		}

		return count;
	}

	private void RegenerateDungeon()
	{
		_generationSettings.MapWidth = _currentGenome.Params.MapWidth;
		_generationSettings.MapHeight = _currentGenome.Params.MapHeight;
		_generationSettings.RoomAttempts = _currentGenome.Params.RoomAttempts;

		_mapWidth = Mathf.Max(40, _generationSettings.MapWidth);
		_mapHeight = Mathf.Max(40, _generationSettings.MapHeight);
		_tiles = new int[_mapHeight, _mapWidth];
		_discoveredTiles = new bool[_mapHeight, _mapWidth];
		_chestClaimed = new bool[_mapHeight, _mapWidth];
		_markersByPos.Clear();
		_tileEffectAccumulator = 0.0;
		_lastStandingTileType = -1;
		_discoveryImage = Image.CreateEmpty(_mapWidth, _mapHeight, false, Image.Format.Rgba8);
		_discoveryImage.Fill(new Color(0, 0, 0, 1));
		_discoveryTexture = ImageTexture.CreateFromImage(_discoveryImage);

		// Only the enemies evolve; the dungeon is fixed. A non-zero map_seed
		// rebuilds the same layout every trial, so a change in outcome can be
		// attributed to the encounter rather than to a different map.
		long mapSeed = _currentGenome.Params.MapSeed;
		ulong seed = mapSeed != 0 ? (ulong)mapSeed : (ulong)Time.GetTicksUsec() + _generationCounter;
		_generationCounter++;
		_rng.Seed = seed;

		if (_levelRoot != null)
		{
			_levelRoot.QueueFree();
		}

		if (_enemyRoot != null)
		{
			_enemyRoot.QueueFree();
		}

		if (_markerRoot != null)
		{
			_markerRoot.QueueFree();
		}

		if (_fogOverlay != null)
		{
			_fogOverlay.QueueFree();
		}

		if (_camera != null)
		{
			_camera.QueueFree();
		}

		// SetupCamera() below (re-)creates the sun light every regenerate;
		// without freeing the previous one first, each regenerate added a
		// brand new DirectionalLight3D on top of every prior one (nothing
		// ever referenced or freed the old ones), and total scene light
		// energy compounded — the game visibly got brighter every time a
		// level was regenerated or completed.
		if (_sunLight != null)
		{
			_sunLight.QueueFree();
		}

		if (_playerMesh != null)
		{
			_playerMesh.QueueFree();
		}

		_tileInstanceByPos.Clear();
		_enemies.Clear();
		_rooms.Clear();

		_playerCurrentHp = _playerStats.MaxHp;
		_attackCooldownTimer = 0.0;

		_trialElapsedSeconds = 0.0;
		_trialDeaths = 0;
		_trialDamageTaken = 0;
		_trialEnemiesKilled = 0;
		_trialTrapsTriggered = 0;
		_trialChestsOpened = 0;
		_trialReported = false;
		_behaviorTelemetry.Reset();
		_botDangers.Clear();
		_botFlankDecisions.Clear();

		if (_duelMode)
		{
			GenerateArena();
		}
		else
		{
			GenerateDungeon();
		}
		BuildLevel();
		SetupCamera();
		SetupPlayer();
		SpawnEnemies();
		UpdateCameraPosition();
		UpdateTileVisibility();

		_trialTotalChests = CountTilesOfType(6);

		GenomeParams p = _currentGenome.Params;
		if (_duelMode)
		{
			return;
		}

		int elites = 0;
		foreach (EnemyController enemy in _enemies)
		{
			elites += enemy.IsElite ? 1 : 0;
		}

		GD.Print($"[Dungeon] seed={seed} rooms={_rooms.Count} enemies={_enemies.Count} elites={elites} traps={CountTilesOfType(4)} " +
			$"chests={_trialTotalChests} heals={CountTilesOfType(9)} | design enemy_density={p.EnemyDensity:F3} " +
			$"difficulty_scale={p.EnemyDifficultyScale:F3} tempo={p.EnemyTempo:F3} elite_rate={p.EliteRate:F3} pack_size={p.PackSize:F3}");
	}

	private void GenerateDungeon()
	{
		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				_tiles[y, x] = 0;
			}
		}

		_rooms.Clear();
		int roomAttempts = Mathf.Max(_generationSettings.RoomAttempts, (_mapWidth * _mapHeight) / 140);

		int maxRoomWidth = Mathf.Min(_generationSettings.MaxRoomWidth, _mapWidth - 4);
		int maxRoomHeight = Mathf.Min(_generationSettings.MaxRoomHeight, _mapHeight - 4);
		int minRoomWidth = Mathf.Min(_generationSettings.MinRoomWidth, maxRoomWidth);
		int minRoomHeight = Mathf.Min(_generationSettings.MinRoomHeight, maxRoomHeight);

		if (maxRoomWidth < 3 || maxRoomHeight < 3)
		{
			return;
		}

		for (int i = 0; i < roomAttempts; i++)
		{
			int width = _rng.RandiRange(minRoomWidth, maxRoomWidth);
			int height = _rng.RandiRange(minRoomHeight, maxRoomHeight);
			if (_mapWidth - width - 2 <= 1 || _mapHeight - height - 2 <= 1)
			{
				continue;
			}

			int x = _rng.RandiRange(1, _mapWidth - width - 2);
			int y = _rng.RandiRange(1, _mapHeight - height - 2);

			var room = (x, y, width, height);
			if (RoomOverlaps(room))
			{
				continue;
			}

			_rooms.Add(room);
			CarveRoom(room);
		}

		if (_rooms.Count == 0)
		{
			var fallbackRoom = (_mapWidth / 2 - 4, _mapHeight / 2 - 4, 8, 8);
			_rooms.Add(fallbackRoom);
			CarveRoom(fallbackRoom);
		}

		if (_rooms.Count > 1)
		{
			ConnectRooms();
		}

		var startRoom = _rooms[0];
		var exitRoom = _rooms[^1];
		Vector2I startPos = new(startRoom.X + startRoom.W / 2, startRoom.Y + startRoom.H / 2);
		Vector2I exitPos = new(exitRoom.X + exitRoom.W / 2, exitRoom.Y + exitRoom.H / 2);

		_startPos = startPos;
		_exitPos = exitPos;
		_playerPos = startPos;
		_tiles[startPos.Y, startPos.X] = 7;
		_tiles[exitPos.Y, exitPos.X] = 8;

		PlaceSpecialTiles();
	}

	private bool RoomOverlaps((int X, int Y, int W, int H) room)
	{
		for (int i = 0; i < _rooms.Count; i++)
		{
			var other = _rooms[i];
			if (room.X < other.X + other.W + 1 && room.X + room.W + 1 > other.X && room.Y < other.Y + other.H + 1 && room.Y + room.H + 1 > other.Y)
			{
				return true;
			}
		}

		return false;
	}

	private void CarveRoom((int X, int Y, int W, int H) room)
	{
		for (int y = room.Y; y < room.Y + room.H; y++)
		{
			for (int x = room.X; x < room.X + room.W; x++)
			{
				_tiles[y, x] = 1;
			}
		}
	}

	private void ConnectRooms()
	{
		var previousCenter = new Vector2I(_rooms[0].X + _rooms[0].W / 2, _rooms[0].Y + _rooms[0].H / 2);

		for (int i = 1; i < _rooms.Count; i++)
		{
			var room = _rooms[i];
			Vector2I center = new(room.X + room.W / 2, room.Y + room.H / 2);

			int x = previousCenter.X;
			while (x != center.X)
			{
				_tiles[previousCenter.Y, x] = 2;
				x += Math.Sign(center.X - x);
				if (x >= 0 && x < _mapWidth)
				{
					_tiles[previousCenter.Y, x] = 2;
				}
			}

			int y = previousCenter.Y;
			while (y != center.Y)
			{
				_tiles[y, center.X] = 2;
				y += Math.Sign(center.Y - y);
				if (y >= 0 && y < _mapHeight)
				{
					_tiles[y, center.X] = 2;
				}
			}

			previousCenter = center;
		}
	}

	private void PlaceSpecialTiles()
	{
		// Genome-driven: each room independently rolls for its special tile type
		// based on the current genome's density parameters (Backend/genome.py).
		// Rolling per-room instead of a fixed i%4 cycle makes these ratios
		// continuously evolvable — the default genome (0.25 each) reproduces
		// the original even split. See docs/EVOLUTIONARY_DESIGN.md.
		GenomeParams genomeParams = _currentGenome.Params;
		float trapCut = Mathf.Clamp(genomeParams.TrapDensity, 0f, 1f);
		float chestCut = trapCut + Mathf.Clamp(genomeParams.ChestDensity, 0f, 1f);
		float healCut = chestCut + Mathf.Clamp(genomeParams.HealDensity, 0f, 1f);
		float enemyCut = healCut + Mathf.Clamp(genomeParams.EnemyDensity, 0f, 1f);

		for (int i = 1; i < _rooms.Count; i++)
		{
			var room = _rooms[i];
			var center = new Vector2I(room.X + room.W / 2, room.Y + room.H / 2);

			if (_tiles[center.Y, center.X] != 1 && _tiles[center.Y, center.X] != 2)
			{
				continue;
			}

			float roll = _rng.Randf();
			if (roll < trapCut)
			{
				_tiles[center.Y, center.X] = 4;
			}
			else if (roll < chestCut)
			{
				_tiles[center.Y, center.X] = 6;
			}
			else if (roll < healCut)
			{
				_tiles[center.Y, center.X] = 9;
			}
			else if (roll < enemyCut)
			{
				_tiles[center.Y, center.X] = 5;
			}
		}
	}

	// Wall tiles stand taller than floor/corridor tiles; the remaining
	// special markers (door/trap/spawn/chest/start/exit/heal) sit at floor
	// height but slightly taller than plain floor to read as distinct —
	// same three heights BuildLevel used before, now the three buckets that
	// share a MultiMesh geometry.
	private enum TileGeometryBucket { Wall, Floor, Special }

	private static TileGeometryBucket GetTileBucket(int tileType) => tileType switch
	{
		0 => TileGeometryBucket.Wall,
		1 or 2 => TileGeometryBucket.Floor,
		_ => TileGeometryBucket.Special
	};

	private static MultiMesh CreateTileMultiMesh(int instanceCount, Vector3 boxSize)
	{
		return new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = true,
			Mesh = new BoxMesh { Size = boxSize },
			InstanceCount = Math.Max(instanceCount, 0)
		};
	}

	private static MultiMeshInstance3D CreateTileMultiMeshInstance(MultiMesh multiMesh)
	{
		return new MultiMeshInstance3D
		{
			Multimesh = multiMesh,
			MaterialOverride = new StandardMaterial3D
			{
				VertexColorUseAsAlbedo = true,
				Metallic = 0.08f,
				Roughness = 0.75f
			}
		};
	}

	private void BuildLevel()
	{
		_levelRoot = new Node3D();
		_markerRoot = new Node3D();
		AddChild(_levelRoot);
		AddChild(_markerRoot);
		_tileInstanceByPos.Clear();

		int wallCount = 0, floorCount = 0, specialCount = 0;
		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				_discoveredTiles[y, x] = false;
				switch (GetTileBucket(_tiles[y, x]))
				{
					case TileGeometryBucket.Wall: wallCount++; break;
					case TileGeometryBucket.Floor: floorCount++; break;
					default: specialCount++; break;
				}
			}
		}

		MultiMesh wallMultiMesh = CreateTileMultiMesh(wallCount, new Vector3(1.0f, 1.1f, 1.0f));
		MultiMesh floorMultiMesh = CreateTileMultiMesh(floorCount, new Vector3(1.0f, TileHeight, 1.0f));
		MultiMesh specialMultiMesh = CreateTileMultiMesh(specialCount, new Vector3(1.0f, TileHeight + 0.15f, 1.0f));

		_levelRoot.AddChild(CreateTileMultiMeshInstance(wallMultiMesh));
		_levelRoot.AddChild(CreateTileMultiMeshInstance(floorMultiMesh));
		_levelRoot.AddChild(CreateTileMultiMeshInstance(specialMultiMesh));

		int wallIndex = 0, floorIndex = 0, specialIndex = 0;
		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				int tileType = _tiles[y, x];
				float baseY = tileType == 0 ? 0.55f : 0.18f;
				Vector3 position = GridToWorld(new Vector2I(x, y)) + new Vector3(0, baseY, 0);
				Color color = _tileColors.GetValueOrDefault(tileType, Color.FromHtml("#1e2436"));

				(MultiMesh mesh, int index) = GetTileBucket(tileType) switch
				{
					TileGeometryBucket.Wall => (wallMultiMesh, wallIndex++),
					TileGeometryBucket.Floor => (floorMultiMesh, floorIndex++),
					_ => (specialMultiMesh, specialIndex++)
				};

				mesh.SetInstanceTransform(index, new Transform3D(Basis.Identity, position));
				mesh.SetInstanceColor(index, color);
				_tileInstanceByPos[new Vector2I(x, y)] = (mesh, index, color);
			}
		}

		var floor = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(_mapWidth + 4, 0.2f, _mapHeight + 4) },
			Position = new Vector3(0, -0.2f, 0),
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = Color.FromHtml("#030712"),
				Roughness = 1.0f
			}
		};

		_levelRoot.AddChild(floor);
		CreateSpecialMarkers();
		CreateFogOverlay();
	}

	private void CreateSpecialMarkers()
	{
		_markerRoot?.QueueFree();
		_markerRoot = new Node3D();
		AddChild(_markerRoot);
		_markersByPos.Clear();

		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				int tileType = _tiles[y, x];
				if (tileType is not (4 or 6 or 7 or 8 or 9))
				{
					continue;
				}

				Color markerColor = _tileColors.GetValueOrDefault(tileType, Colors.White);
				var marker = new MeshInstance3D
				{
					Mesh = CreateMarkerMesh(tileType),
					Position = GridToWorld(new Vector2I(x, y)) + new Vector3(0, MarkerHeight(tileType), 0),
					MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = markerColor,
						Metallic = 0.05f,
						Roughness = 0.45f
					}
				};
				_markerRoot.AddChild(marker);
				_markersByPos[new Vector2I(x, y)] = marker;
			}
		}
	}

	// Each special tile gets its own silhouette, not just its own color —
	// so it's still identifiable under fog, at a distance, or for anyone
	// who can't rely on hue to tell tiles apart.
	private static Mesh CreateMarkerMesh(int tileType)
	{
		return tileType switch
		{
			4 => new CylinderMesh { TopRadius = 0f, BottomRadius = 0.32f, Height = 0.5f },  // trap: spike
			6 => new BoxMesh { Size = new Vector3(0.48f, 0.48f, 0.48f) },                    // chest: crate
			7 => new SphereMesh { Radius = 0.26f, Height = 0.52f },                          // start: orb
			8 => new TorusMesh { InnerRadius = 0.2f, OuterRadius = 0.36f },                  // exit: ring/portal
			9 => new PrismMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) },                     // heal: wedge
			_ => new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.4f) }
		};
	}

	private static float MarkerHeight(int tileType)
	{
		return tileType switch
		{
			4 => 0.61f,
			6 => 0.68f,
			7 => 0.62f,
			8 => 0.45f,
			9 => 0.61f,
			_ => 0.5f
		};
	}

	private void CreateFogOverlay()
	{
		_fogOverlay?.QueueFree();

		var shader = new Shader
		{
			Code = """
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

uniform vec2 player_pos = vec2(0.0, 0.0);
uniform sampler2D discovered_map;
uniform vec2 map_size = vec2(1.0, 1.0);
uniform vec2 map_origin = vec2(0.0, 0.0);
uniform float reveal_radius = 6.5;
uniform float feather = 2.5;
uniform float fog_opacity_undiscovered = 0.88;
uniform float fog_opacity_discovered = 0.45;
uniform vec4 fog_color : source_color = vec4(0.02, 0.03, 0.09, 1.0);

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 map_local = world_pos.xz - map_origin;
	vec2 uv = map_local / max(map_size, vec2(0.001));
	float discovered = 0.0;
	if (uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0) {
		discovered = texture(discovered_map, uv).r;
	}

	float base_opacity = mix(fog_opacity_undiscovered, fog_opacity_discovered, discovered);
	float dist = distance(world_pos.xz, player_pos);
	float mask = smoothstep(reveal_radius, reveal_radius + max(feather, 0.01), dist);
	ALBEDO = fog_color.rgb;
	ALPHA = mask * base_opacity;
}
"""
		};

		_fogMaterial = new ShaderMaterial
		{
			Shader = shader
		};

		_fogMaterial.SetShaderParameter("reveal_radius", _generationSettings.FogRevealRadius);
		_fogMaterial.SetShaderParameter("feather", _generationSettings.FogFeather);
		_fogMaterial.SetShaderParameter("fog_opacity_undiscovered", _generationSettings.FogOpacity);
		_fogMaterial.SetShaderParameter("fog_opacity_discovered", _generationSettings.FogDiscoveredOpacity);
		_fogMaterial.SetShaderParameter("fog_color", new Color(0.02f, 0.03f, 0.09f, 1.0f));
		_fogMaterial.SetShaderParameter("discovered_map", _discoveryTexture);
		_fogMaterial.SetShaderParameter("map_size", new Vector2(_mapWidth - 1, _mapHeight - 1));
		_fogMaterial.SetShaderParameter("map_origin", GetGridWorldOrigin());

		_fogOverlay = new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(_mapWidth + 8.0f, _mapHeight + 8.0f) },
			Position = new Vector3(0, 1.4f, 0),
			RotationDegrees = new Vector3(-90, 0, 0),
			MaterialOverride = _fogMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};

		AddChild(_fogOverlay);
	}

	private void UpdateTileVisibility()
	{
		if (_fogMaterial == null || _playerMesh == null)
		{
			return;
		}

		if (RevealAroundPlayer(_playerPos))
		{
			_discoveryTexture.Update(_discoveryImage);
		}

		_fogMaterial.SetShaderParameter("player_pos", new Vector2(_playerMesh.Position.X, _playerMesh.Position.Z));
	}

	private bool RevealAroundPlayer(Vector2I center)
	{
		int revealRadius = Mathf.CeilToInt(_generationSettings.FogRevealRadius);
		float revealRadiusSq = _generationSettings.FogRevealRadius * _generationSettings.FogRevealRadius;
		bool changed = false;

		for (int y = center.Y - revealRadius; y <= center.Y + revealRadius; y++)
		{
			for (int x = center.X - revealRadius; x <= center.X + revealRadius; x++)
			{
				if (x < 0 || y < 0 || x >= _mapWidth || y >= _mapHeight)
				{
					continue;
				}

				int dx = x - center.X;
				int dy = y - center.Y;
				if (dx * dx + dy * dy > revealRadiusSq)
				{
					continue;
				}

				if (_discoveredTiles[y, x])
				{
					continue;
				}

				_discoveredTiles[y, x] = true;
				_discoveryImage.SetPixel(x, y, new Color(1, 1, 1, 1));
				changed = true;
			}
		}

		return changed;
	}

	// Tilted rather than a straight overhead look — reveals wall height and
	// lets directional-light shadows actually read as depth, so the level
	// looks like a 3D space rather than a flat painted map.
	private const float CameraPitchDegrees = -55f;
	private const float CameraDistance = 30f;

	private void SetupCamera()
	{
		if (_worldEnvironment == null)
		{
			SetupEnvironment();
		}

		_camera = new Camera3D
		{
			RotationDegrees = new Vector3(CameraPitchDegrees, 0, 0),
			Projection = Camera3D.ProjectionType.Orthogonal,
			Size = 22f,
			Current = true
		};

		AddChild(_camera);

		_sunLight = new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-50, 35, 0),
			LightColor = Color.FromHtml("#fff3e0"),
			LightEnergy = 1.1f,
			ShadowEnabled = true,
			// Tall wall tiles at this light angle were casting long, hard-
			// edged shadows across the floor — ShadowOpacity lightens them
			// instead of full black, ShadowBlur softens the edges so they
			// read as ambient occlusion rather than sharp shapes.
			ShadowOpacity = 0.45f,
			ShadowBlur = 3.0f
		};

		AddChild(_sunLight);
	}

	private void SetupEnvironment()
	{
		var sky = new Sky
		{
			SkyMaterial = new ProceduralSkyMaterial
			{
				SkyTopColor = Color.FromHtml("#120a26"),
				SkyHorizonColor = Color.FromHtml("#4c3b7a"),
				GroundBottomColor = Color.FromHtml("#0a0714"),
				GroundHorizonColor = Color.FromHtml("#2c2148")
			}
		};

		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Sky,
			Sky = sky,
			AmbientLightSource = Godot.Environment.AmbientSource.Sky,
			AmbientLightEnergy = 0.6f,
			// Aces' filmic highlight roll-off was blowing out bright, saturated
			// tile/marker colors into a soft glow-like halo (visible on plain,
			// non-emissive assets like the chest cube) — Linear tonemapping
			// removes that without needing per-material changes.
			TonemapMode = Godot.Environment.ToneMapper.Linear,
			// Explicit, not just relying on the default, so a soft bloom pass
			// can never sneak back in from an Environment default change.
			GlowEnabled = false
		};

		_worldEnvironment = new WorldEnvironment { Environment = environment };
		AddChild(_worldEnvironment);
	}

	private void SetupPlayer()
	{
		_playerMesh = new MeshInstance3D
		{
			Mesh = new CapsuleMesh
			{
				Radius = 0.25f,
				Height = 0.9f
			},
			Position = GridToWorld(_playerPos) + new Vector3(0, 0.45f, 0),
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = Color.FromHtml("#4cc9f0"),
				Metallic = 0.15f,
				Roughness = 0.35f
			}
		};

		AddChild(_playerMesh);
	}

	private void SetPlayerWorldPosition()
	{
		_playerMesh.Position = GridToWorld(_playerPos) + new Vector3(0, 0.45f, 0);
	}

	private void UpdateCameraPosition()
	{
		if (_camera == null || _playerMesh == null)
		{
			return;
		}

		// Follow at a fixed offset along the camera's own (tilted) forward
		// axis, rather than a hardcoded straight-down vector — this way the
		// pitch angle is only ever defined once, in SetupCamera.
		Vector3 forward = -_camera.Transform.Basis.Z;
		_camera.Position = _playerMesh.Position - (forward * CameraDistance);
	}

	// The main dungeon caps spawns here (mirrored by the simulated player,
	// Backend/simulated_player.py MAX_ENEMIES).
	private const int MaxEnemies = 160;

	private void SpawnEnemies()
	{
		_enemyRoot = new Node3D();
		AddChild(_enemyRoot);
		if (_duelMode)
		{
			SpawnDuelEnemies();
			return;
		}

		GenomeParams genes = _currentGenome.Params;

		var spawnPoints = new List<Vector2I>();
		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				if (_tiles[y, x] == 5)
				{
					spawnPoints.Add(new Vector2I(x, y));
				}
			}
		}

		// Orders are decided once per regeneration and handed to every enemy
		// spawned this cycle — the first milestone (section 15) only ever
		// adapts newly spawned enemies, never retasks an enemy already alive.
		_adaptationDirector.Reevaluate(_playerTelemetry, _playerProfile);
		_adaptationDirector.ConsumeChanges(); // the enemies these changes would re-task are being replaced
		if (_adaptationDirector.RearDefenseActive || _adaptationDirector.StealthCountermeasureActive)
		{
			GD.Print($"[AdaptiveAI] Spawning this dungeon's enemies with RearDefenseActive={_adaptationDirector.RearDefenseActive} " +
				$"StealthCountermeasureActive={_adaptationDirector.StealthCountermeasureActive}");
		}

		// Each spawn point holds a pack: its size is drawn from the evolved
		// pack_size gene, and every member independently draws its archetype
		// from the behaviour mix and whether it is an elite.
		int adaptedSpawnCount = 0;
		foreach (Vector2I spawnPoint in spawnPoints)
		{
			int packCount = EnemyArchetypes.PackCount(genes.PackSize, _rng.Randf());
			var tiles = new List<Vector2I> { spawnPoint };
			foreach (GridPoint extra in AttackGeometry.PackPlacement(ToGridPoint(spawnPoint), packCount - 1, _tiles,
				cell => IsEnemyAt(new Vector2I(cell.X, cell.Y)) || (cell.X == _playerPos.X && cell.Y == _playerPos.Y)))
			{
				tiles.Add(new Vector2I(extra.X, extra.Y));
			}

			foreach (Vector2I tile in tiles)
			{
				if (_enemies.Count >= MaxEnemies)
				{
					break;
				}

				string behavior = EnemyArchetypes.PickBehavior(genes.EnemyBehaviorMix, _rng.Randf());
				bool elite = EnemyArchetypes.RollElite(genes.EliteRate, _rng.Randf());
				if (SpawnEnemy(tile, behavior, genes.EnemyDifficultyScale, genes.EnemyTempo, elite))
				{
					adaptedSpawnCount++;
				}
			}
		}

		if (adaptedSpawnCount > 0)
		{
			GD.Print($"[AdaptiveAI] {adaptedSpawnCount}/{_enemies.Count} enemies spawned with an active tactical order this dungeon.");
		}
	}

	/// <summary>Spawns one enemy; returns whether it received an adaptive tactical order.</summary>
	private bool SpawnEnemy(Vector2I tile, string behavior, float difficultyScale, float tempo, bool elite)
	{
		ArchetypeDefinition archetype = EnemyArchetypes.WithModifiers(EnemyArchetypes.Get(behavior), difficultyScale, tempo, elite);
		var enemy = new EnemyController
		{
			WorldOffset = GetGridWorldOrigin(),
			// The 8x8 AI debug board is meant for the 2-enemy test scenes and
			// costs ~66 mesh instances per enemy.
			ShowDebugBoard = false,
			IsTileOccupied = IsEnemyAt
		};
		enemy.Initialize(tile, archetype, elite);
		// The director can be off (study sessions, --director=off; see GameManager.Study.cs).
		enemy.TacticalOrder = DirectorGivesOrders ? _adaptationDirector.CreateOrderForNewEnemy(_playerProfile) : EnemyTacticalOrder.None();
		enemy.RouteGoal = _exitPos.X >= 0 ? ToGridPoint(_exitPos) : null;
		bool isAdapted = IsAdapted(enemy);
		// Captured by value so each hit is attributed to the archetype that
		// actually threw it, for the per-archetype telemetry. Whether it is
		// "adapted" is read when the event happens, since live re-tasking can
		// change an enemy's order after it spawned.
		EnemyController spawnedEnemy = enemy;
		enemy.EnemyAttackedPlayer += damage => OnEnemyAttackedPlayer(damage, spawnedEnemy, IsAdapted(spawnedEnemy));
		enemy.TelegraphStarted += OnEnemyTelegraphStarted;
		enemy.AttackResolved += (source, hit, telegraphed) =>
		{
			_behaviorTelemetry.RecordAttackResolved(source.BehaviorType, IsAdapted(source), hit, telegraphed);
			_botDangers.RemoveAll(d => d.Source == source);
		};
		_enemyRoot.AddChild(enemy);
		_enemies.Add(enemy);
		_behaviorTelemetry.RecordSpawn(behavior, isAdapted, elite);
		return isAdapted;
	}

	private static bool IsAdapted(EnemyController enemy) =>
		enemy.TacticalOrder.GuardRear || enemy.TacticalOrder.SearchLastKnownPosition;

	/// <summary>
	/// Re-evaluates the director and applies live re-tasking: a countermeasure
	/// that has just switched on is offered to the enemies already near the
	/// player (AdaptationDirector.SelectForRetask), and one that has just
	/// switched off is dropped by every enemy. Study sessions keep the
	/// director off.
	/// </summary>
	private void ReevaluateDirector()
	{
		_adaptationDirector.Reevaluate(_playerTelemetry, _playerProfile);
		AdaptationDirector.CountermeasureChanges changes = _adaptationDirector.ConsumeChanges();
		if (!DirectorRetasks || !changes.Any)
		{
			return;
		}

		var living = new List<EnemyController>();
		foreach (EnemyController enemy in _enemies)
		{
			if (enemy.CurrentHp > 0)
			{
				living.Add(enemy);
			}
		}

		if (changes.RearDeactivated || changes.StealthDeactivated)
		{
			foreach (EnemyController enemy in living)
			{
				if (changes.RearDeactivated)
				{
					AdaptationDirector.RevokeRearDefense(enemy.TacticalOrder);
				}

				if (changes.StealthDeactivated)
				{
					AdaptationDirector.RevokeStealthCountermeasure(enemy.TacticalOrder);
				}
			}
		}

		foreach ((bool activated, string name, Action<EnemyController> grant) in new (bool, string, Action<EnemyController>)[]
		{
			(changes.RearActivated, "rear defence", e => AdaptationDirector.GrantRearDefense(e.TacticalOrder, _playerProfile)),
			(changes.StealthActivated, "stealth countermeasure", e => AdaptationDirector.GrantStealthCountermeasure(e.TacticalOrder, _playerProfile, _adaptationDirector.InterceptRoute)),
		})
		{
			if (!activated)
			{
				continue;
			}

			var candidates = new List<AdaptationDirector.RetaskCandidate>();
			for (int i = 0; i < living.Count; i++)
			{
				int distance = Math.Abs(living[i].GridPos.X - _playerPos.X) + Math.Abs(living[i].GridPos.Y - _playerPos.Y);
				candidates.Add(new AdaptationDirector.RetaskCandidate(i, distance));
			}

			List<int> chosen = _adaptationDirector.SelectForRetask(candidates, _playerProfile);
			foreach (int index in chosen)
			{
				grant(living[index]);
			}

			GD.Print($"[AdaptiveAI] Live re-tasking: {chosen.Count} enemies near the player took the {name}.");
		}
	}

	private bool IsEnemyAt(Vector2I tile)
	{
		foreach (EnemyController enemy in _enemies)
		{
			if (enemy.CurrentHp > 0 && enemy.GridPos == tile)
			{
				return true;
			}
		}

		return false;
	}

	// The scripted players react to a telegraph with probability BotDodgeSkill,
	// rolled once per telegraph, after a human-like reaction delay.
	private void OnEnemyTelegraphStarted(EnemyController source, IReadOnlyCollection<Vector2I> cells)
	{
		if (!AutoplayEnabled || _botRng.Randf() >= BotDodgeSkill)
		{
			return;
		}

		var set = new HashSet<GridPoint>();
		foreach (Vector2I cell in cells)
		{
			set.Add(ToGridPoint(cell));
		}

		_botDangers.Add((source, set, _trialElapsedSeconds + BotReactionSeconds));
	}

	private void HandlePlayerInput(double delta)
	{
		_moveTimer += delta;
		float stepDelay = Mathf.Max(0.05f, 0.18f / Mathf.Max(_playerStats.MoveSpeed, 0.1f));
		if (_moveTimer < stepDelay)
		{
			return;
		}

		Vector2I direction = AutoplayEnabled ? GetBotMovementDirection() : GetMovementDirection();
		if (direction == Vector2I.Zero)
		{
			_moveTimer = 0;
			return;
		}

		// docs/ENEMY_AI_ADAPTATION_PLAN.md section 4: "update it whenever a
		// valid movement input is applied" — a legitimate single-axis
		// direction counts even if the move into it is then blocked (the
		// player is still facing that way).
		_playerFacing = direction;

		Vector2I nextPos = _playerPos + direction;
		if (IsWalkable(nextPos))
		{
			int distanceBefore = ComputeNearestEnemyDistance(_playerPos);
			_playerPos = nextPos;
			SetPlayerWorldPosition();
			HandleTileEffect();

			int distanceAfter = ComputeNearestEnemyDistance(_playerPos);
			if (distanceBefore != int.MaxValue && distanceAfter > distanceBefore)
			{
				bool wasKiting = _sinceLastAttackSeconds <= KitingWindowSeconds;
				_playerTelemetry.RecordRetreat(wasKiting);
				_playerProfile.Update(_playerTelemetry);
			}
		}

		_moveTimer = 0;
	}

	private int ComputeNearestEnemyDistance(Vector2I from)
	{
		int best = int.MaxValue;
		foreach (EnemyController enemy in _enemies)
		{
			if (enemy.CurrentHp <= 0)
			{
				continue;
			}

			int distance = Math.Abs(enemy.GridPos.X - from.X) + Math.Abs(enemy.GridPos.Y - from.Y);
			if (distance < best)
			{
				best = distance;
			}
		}

		return best;
	}

	// --- Scripted bot evaluator movement (see AutoplayEnabled above) ---

	private Vector2I GetBotMovementDirection()
	{
		EnemyController? nearestEnemy = FindNearestAliveEnemy(_playerPos);
		GridPoint player = ToGridPoint(_playerPos);
		HashSet<GridPoint> danger = ActiveBotDangerCells();
		FighterBotPolicy.Target? target = nearestEnemy != null
			? new FighterBotPolicy.Target(ToGridPoint(nearestEnemy.GridPos), ToGridPoint(nearestEnemy.Facing),
				nearestEnemy.Archetype.Pattern == AttackPattern.ShieldBash)
			: null;
		Func<GridPoint, bool> occupied = cell => IsEnemyAt(new Vector2I(cell.X, cell.Y));

		GridPoint step;
		if (_duelMode)
		{
			bool flank = nearestEnemy != null && ShouldFlank(nearestEnemy);
			step = FighterBotPolicy.DecideMove(player, target, danger, flank, _tiles, occupied);
		}
		else
		{
			step = danger.Contains(player) ? FighterBotPolicy.DodgeStep(player, target, danger, _tiles, occupied) : GridPoint.Zero;
			if (step == GridPoint.Zero && _botStyle == "hide")
			{
				var watchers = new List<GridPoint>();
				foreach (EnemyController enemy in _enemies)
				{
					if (enemy.CurrentHp > 0 && enemy.Perception.State == PerceptionState.Tracking
						&& Math.Abs(enemy.GridPos.X - _playerPos.X) + Math.Abs(enemy.GridPos.Y - _playerPos.Y) <= HideTriggerDistance)
					{
						watchers.Add(ToGridPoint(enemy.GridPos));
					}
				}

				step = BotPlayerPolicy.HideStep(player, ToGridPoint(_exitPos), watchers, _tiles, occupied);
			}

			if (step == GridPoint.Zero)
			{
				step = BotPlayerPolicy.DecideMove(player, ToGridPoint(_exitPos), target?.Position,
					_playerCurrentHp, _playerStats.MaxHp, _tiles);
			}
		}

		return new Vector2I(step.X, step.Y);
	}

	private HashSet<GridPoint> ActiveBotDangerCells()
	{
		var cells = new HashSet<GridPoint>();
		foreach ((EnemyController source, HashSet<GridPoint> danger, double activeAt) in _botDangers)
		{
			if (activeAt <= _trialElapsedSeconds && source.CurrentHp > 0)
			{
				cells.UnionWith(danger);
			}
		}

		return cells;
	}

	// Whether the fighter works around this shieldbearer: rolled once per enemy.
	private bool ShouldFlank(EnemyController enemy)
	{
		if (enemy.Archetype.Pattern != AttackPattern.ShieldBash)
		{
			return false;
		}

		if (!_botFlankDecisions.TryGetValue(enemy, out bool flank))
		{
			flank = _botRng.Randf() < BotDodgeSkill;
			_botFlankDecisions[enemy] = flank;
		}

		return flank;
	}

	private EnemyController? FindNearestAliveEnemy(Vector2I from)
	{
		EnemyController? nearest = null;
		int bestDistance = int.MaxValue;
		foreach (EnemyController enemy in _enemies)
		{
			if (enemy.CurrentHp <= 0)
			{
				continue;
			}

			int distance = Math.Abs(enemy.GridPos.X - from.X) + Math.Abs(enemy.GridPos.Y - from.Y);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				nearest = enemy;
			}
		}

		return nearest;
	}

	private void HandlePlayerAttack(double delta)
	{
		_attackCooldownTimer = Math.Max(0.0, _attackCooldownTimer - delta);

		if (_attackRequested && _attackCooldownTimer <= 0.0)
		{
			TryAttackNearestEnemy();
			_attackCooldownTimer = AttackCooldown;
		}

		// Consumed every HandlePlayerAttack call regardless of whether an
		// attack actually fired, so a press that arrives while still on
		// cooldown doesn't queue up and fire late once the cooldown clears —
		// matches the original poll-based behavior's timing, just without
		// the poll's dropped-tap failure mode.
		_attackRequested = false;
	}

	private void TryAttackNearestEnemy()
	{
		EnemyController? target = null;
		int bestDistance = int.MaxValue;

		foreach (EnemyController enemy in _enemies)
		{
			if (enemy.CurrentHp <= 0)
			{
				continue;
			}

			// Chebyshev distance (max of the two axis deltas), not Manhattan:
			// an enemy diagonally adjacent to the player is one step away in
			// any 4-directional grid sense a player actually perceives, but
			// Manhattan distance counts it as 2 and put it out of reach of
			// AttackRange = 1 — the original source of "can't attack an
			// enemy standing right next to me."
			int distance = Math.Max(Math.Abs(enemy.GridPos.X - _playerPos.X), Math.Abs(enemy.GridPos.Y - _playerPos.Y));
			if (distance <= _playerStats.AttackRange && distance < bestDistance)
			{
				bestDistance = distance;
				target = enemy;
			}
		}

		if (target == null)
		{
			return;
		}

		PlayPlayerAttackFx(target.Position);
		PlaySfx(Sfx.Attack);

		Vector2I toTarget = target.GridPos - _playerPos;
		bool targetWasAware = target.Perception.State != PerceptionState.Unaware;
		AttackDirection direction = AttackDirectionClassifier.Classify(
			ToGridPoint(_playerFacing), ToGridPoint(toTarget), targetWasAware);

		_playerTelemetry.RecordAttack(hit: true, direction);
		_playerProfile.Update(_playerTelemetry);
		ReevaluateDirector();
		GD.Print($"[AdaptiveAI] Attack on {target.ClassDefinition.ClassId} classified {direction} " +
			$"(targetWasAware={targetWasAware}) — attacks={_playerTelemetry.Attacks} rear={_playerTelemetry.BackAttacks} " +
			$"BackAttackPreference={_playerProfile.BackAttackPreference:F2} Confidence={_playerProfile.Confidence:F2}");
		LogAdaptationTransitions();
		_sinceLastAttackSeconds = 0.0;

		bool targetIsAdapted = IsAdapted(target);
		EnemyController.HitResult hit = target.TakeDamage(_playerStats.Attack, direction, _playerPos);
		_behaviorTelemetry.RecordPlayerHitOnEnemy(target.BehaviorType, targetIsAdapted, hit.Damage, direction == AttackDirection.Rear, hit.Blocked);

		if (hit.Killed)
		{
			_score += target.Stats.Reward;
			_trialEnemiesKilled++;
			_playerTelemetry.RecordEnemyKilled();
			_behaviorTelemetry.RecordKilledByPlayer(target.BehaviorType, targetIsAdapted);
			PlaySfx(Sfx.EnemyDied);
		}
		else
		{
			PlaySfx(Sfx.Hit);
		}
	}

	private static GridPoint ToGridPoint(Vector2I v) => new(v.X, v.Y);

	// Parented directly under GameManager (this), not _levelRoot/_enemyRoot —
	// those get QueueFree'd on every RegenerateDungeon (exit reached, R
	// pressed, timeout), which would cut off a cue like Sfx.Exit mid-playback
	// since regeneration happens the same frame the exit is reached.
	private void PlaySfx(AudioStreamWav stream)
	{
		var player = new AudioStreamPlayer
		{
			Stream = stream,
			VolumeDb = -6f
		};
		AddChild(player);
		player.Finished += player.QueueFree;
		player.Play();
	}

	private void PlayPlayerAttackFx(Vector3 targetWorldPos)
	{
		Tween punchTween = CreateTween();
		punchTween.TweenProperty(_playerMesh, "scale", new Vector3(1.3f, 0.8f, 1.3f), 0.06).SetTrans(Tween.TransitionType.Sine);
		punchTween.TweenProperty(_playerMesh, "scale", Vector3.One, 0.12).SetTrans(Tween.TransitionType.Sine);

		Vector3 origin = _playerMesh.Position;
		Vector3 toTarget = targetWorldPos - origin;
		toTarget.Y = 0;
		float length = Mathf.Max(toTarget.Length(), 0.2f);
		Vector3 midpoint = origin + (toTarget.Normalized() * (length * 0.5f)) + new Vector3(0, 0.5f, 0);
		float angle = Mathf.Atan2(toTarget.X, toTarget.Z);

		var slashMaterial = new StandardMaterial3D
		{
			AlbedoColor = Color.FromHtml("#e0f2fe"),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha
		};

		var slash = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.08f, length * 0.8f) },
			MaterialOverride = slashMaterial,
			Position = midpoint,
			RotationDegrees = new Vector3(0, Mathf.RadToDeg(angle), 0)
		};
		AddChild(slash);

		Tween slashTween = CreateTween();
		slashTween.TweenProperty(slashMaterial, "albedo_color:a", 0.0f, 0.15).SetDelay(0.03);
		slashTween.TweenCallback(Callable.From(slash.QueueFree));
	}

	private void OnEnemyAttackedPlayer(int rawDamage, EnemyController source, bool sourceIsAdapted)
	{
		int mitigated = Math.Max(1, rawDamage - _playerStats.Defense);
		_playerCurrentHp = Math.Max(0, _playerCurrentHp - mitigated);
		_trialDamageTaken += mitigated;
		_playerTelemetry.RecordPlayerDamaged();
		_behaviorTelemetry.RecordAttackOnPlayer(source.BehaviorType, sourceIsAdapted, mitigated);
		PlaySfx(Sfx.PlayerHurt);

		if (_playerCurrentHp <= 0)
		{
			HandlePlayerDeath();
		}
	}

	private void HandlePlayerDeath()
	{
		if (_duelMode)
		{
			// Duels measure damage, so the fighter keeps fighting in place.
			_trialDeaths++;
			_playerCurrentHp = _playerStats.MaxHp;
			return;
		}

		_trialDeaths++;
		_score = Mathf.Max(0, _score - 20);
		_playerPos = _startPos;
		SetPlayerWorldPosition();
		_playerCurrentHp = _playerStats.MaxHp;
		PlaySfx(Sfx.PlayerDeath);
	}

	private Vector2I GetMovementDirection()
	{
		bool right = Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D);
		bool left = Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A);
		bool down = Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S);
		bool up = Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W);

		// Opposite keys on the same axis (A+D, W+S) genuinely cancel out —
		// that's correct. Two keys from *different* axes (e.g. W+A) used to
		// also cancel out to no movement at all, since each branch below
		// required every other key to be released; now one resolved
		// direction per axis, and if both axes have one, the more recently
		// pressed axis (tracked in _Input) wins instead of neither moving.
		Vector2I horizontal = right == left ? Vector2I.Zero : (right ? Vector2I.Right : Vector2I.Left);
		Vector2I vertical = up == down ? Vector2I.Zero : (up ? Vector2I.Up : Vector2I.Down);

		if (horizontal != Vector2I.Zero && vertical != Vector2I.Zero)
		{
			return _lastMovementAxisWasHorizontal ? horizontal : vertical;
		}

		return horizontal != Vector2I.Zero ? horizontal : vertical;
	}

	private void HandleTileEffect()
	{
		int tileType = _tiles[_playerPos.Y, _playerPos.X];
		switch (tileType)
		{
			case 6:
				if (!_chestClaimed[_playerPos.Y, _playerPos.X])
				{
					_chestClaimed[_playerPos.Y, _playerPos.X] = true;
					_score += 25;
					_rewardCollected = true;
					_trialChestsOpened++;
					LightenTile(_playerPos, 0.55f);
					PlaySfx(Sfx.Chest);
				}
				break;
			case 8:
				_score += 50;
				PlaySfx(Sfx.Exit);
				EndTrialAndAdvanceAsync(exitReached: true, quitEarly: false);
				break;
		}
	}

	private void HandleStandingTileEffects(double delta)
	{
		int tileType = _tiles[_playerPos.Y, _playerPos.X];
		if (tileType != _lastStandingTileType)
		{
			_tileEffectAccumulator = 0.0;
			_lastStandingTileType = tileType;

			// Signaled once on stepping onto the tile, not once per accumulator
			// tick below — that fires up to TrapDamagePerSecond/HealPerSecond
			// times a second, which would sound like a siren instead of a cue.
			if (tileType == 4)
			{
				_trialTrapsTriggered++;
				PlaySfx(Sfx.Trap);
			}
			else if (tileType == 9)
			{
				PlaySfx(Sfx.Heal);
			}
		}

		if (tileType == 4)
		{
			_tileEffectAccumulator += TrapDamagePerSecond * delta;
			if (_tileEffectAccumulator >= 1.0)
			{
				int whole = (int)_tileEffectAccumulator;
				_tileEffectAccumulator -= whole;
				_playerCurrentHp = Math.Max(0, _playerCurrentHp - whole);

				if (_playerCurrentHp <= 0)
				{
					HandlePlayerDeath();
				}
			}
		}
		else if (tileType == 9)
		{
			_tileEffectAccumulator += HealPerSecond * delta;
			if (_tileEffectAccumulator >= 1.0)
			{
				int whole = (int)_tileEffectAccumulator;
				_tileEffectAccumulator -= whole;
				_playerCurrentHp = Math.Min(_playerStats.MaxHp, _playerCurrentHp + whole);
			}
		}
	}

	private void LightenTile(Vector2I pos, float amount)
	{
		if (_tileInstanceByPos.TryGetValue(pos, out var tile))
		{
			Color lightened = tile.BaseColor.Lerp(Colors.White, amount);
			tile.Mesh.SetInstanceColor(tile.Index, lightened);
			_tileInstanceByPos[pos] = (tile.Mesh, tile.Index, lightened);
		}

		if (_markersByPos.TryGetValue(pos, out MeshInstance3D? marker) && marker.MaterialOverride is StandardMaterial3D markerMaterial)
		{
			markerMaterial.AlbedoColor = markerMaterial.AlbedoColor.Lerp(Colors.White, amount);
		}
	}

	private bool IsWalkable(Vector2I position)
	{
		if (position.X < 0 || position.Y < 0 || position.X >= _mapWidth || position.Y >= _mapHeight)
		{
			return false;
		}

		if (_tiles[position.Y, position.X] == 0)
		{
			return false;
		}

		foreach (EnemyController enemy in _enemies)
		{
			if (enemy.CurrentHp > 0 && enemy.GridPos == position)
			{
				return false;
			}
		}

		return true;
	}

	private Vector3 GridToWorld(Vector2I gridPos)
	{
		float worldX = gridPos.X - ((_mapWidth - 1) * 0.5f);
		float worldZ = gridPos.Y - ((_mapHeight - 1) * 0.5f);
		return new Vector3(worldX, 0, worldZ);
	}

	private Vector2 GetGridWorldOrigin()
	{
		return new Vector2(-((_mapWidth - 1) * 0.5f), -((_mapHeight - 1) * 0.5f));
	}

	private Vector2I WorldToGrid(Vector3 worldPosition)
	{
		int gridX = Mathf.RoundToInt(worldPosition.X + ((_mapWidth - 1) * 0.5f));
		int gridY = Mathf.RoundToInt(worldPosition.Z + ((_mapHeight - 1) * 0.5f));
		return new Vector2I(gridX, gridY);
	}
}
