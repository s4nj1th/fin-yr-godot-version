using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
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
	private readonly List<MeshInstance3D> _tileMeshes = new();
	// Tile IDs: 0 wall, 1 floor, 2 corridor, 3 door (unused by generation yet),
	// 4 trap (drains HP), 5 enemy spawn marker (no effect), 6 chest (one-time score),
	// 7 start, 8 exit, 9 heal (restores HP). Colors are deliberately spread across
	// distinct hues — 4/5 and 7/9 used to share near-identical reds/greens, which
	// made trap-vs-spawn and heal-vs-start indistinguishable at a glance.
	private readonly Dictionary<int, Color> _tileColors = new()
	{
		{ 0, Color.FromHtml("#111422") },
		{ 1, Color.FromHtml("#1e2436") },
		{ 2, Color.FromHtml("#181d2e") },
		{ 3, Color.FromHtml("#8b5cf6") },
		{ 4, Color.FromHtml("#dc2626") },
		{ 5, Color.FromHtml("#a855f7") },
		{ 6, Color.FromHtml("#f59e0b") },
		{ 7, Color.FromHtml("#38bdf8") },
		{ 8, Color.FromHtml("#06b6d4") },
		{ 9, Color.FromHtml("#22c55e") }
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
	private Vector2I _playerPos;
	private Vector2I _startPos;
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
	private bool _attackKeyWasDown;
	private const double AttackCooldown = 0.35;

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

	public int PlayerCurrentHp => _playerCurrentHp;
	public int PlayerMaxHp => _playerStats.MaxHp;
	public int Score => _score;
	public string? LastTrialResultMessage { get; private set; }

	public override void _Ready()
	{
		_apiClient = new ApiClient();
		AddChild(_apiClient);
		RequestNextGenomeAndRegenerateAsync();
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

		if (_isFetchingGenome)
		{
			return;
		}

		_trialElapsedSeconds += delta;
		if (_trialElapsedSeconds > MaxTrialSeconds && !_trialReported)
		{
			// A passive timeout is a distinct signal from a manual quit — see
			// docs/EVOLUTIONARY_DESIGN.md and mcc.py's _evaluate_minimal_criterion.
			EndTrialAndAdvanceAsync(exitReached: false, quitEarly: false);
			return;
		}

		HandlePlayerInput(delta);
		HandlePlayerAttack(delta);
		HandleStandingTileEffects(delta);
		UpdateTileVisibility();
		UpdateCameraPosition();

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
		}
	}

	private async Task RequestNextGenomeAsync()
	{
		NextCandidateResponse? response = await _apiClient.RequestNextGenomeAsync();
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

		try
		{
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
				ExplorationPercent = ComputeExplorationPercent(),
				ExitReached = exitReached,
				QuitEarly = quitEarly
			};

			if (_backendAvailable && !string.IsNullOrEmpty(_currentGenomeId))
			{
				ReportOutcomeResponse? result = await _apiClient.ReportOutcomeAsync(new ReportOutcomeRequest
				{
					GenomeId = _currentGenomeId,
					Telemetry = telemetry
				});

				LastTrialResultMessage = result != null
					? (result.Passed ? $"GENOME ACCEPTED — HALL OF FAME: {result.HallOfFameSize}" : $"GENOME REJECTED ({result.FailReason})")
					: "BACKEND UNREACHABLE — RESULT NOT RECORDED";
			}
			else
			{
				LastTrialResultMessage = "LOCAL MODE — BACKEND OFFLINE";
			}

			await RequestNextGenomeAsync();
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

		ulong seed = (ulong)Time.GetTicksUsec() + _generationCounter;
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

		if (_playerMesh != null)
		{
			_playerMesh.QueueFree();
		}

		_tileMeshes.Clear();
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

		GenerateDungeon();
		BuildLevel();
		SetupCamera();
		SetupPlayer();
		SpawnEnemies();
		UpdateCameraPosition();
		UpdateTileVisibility();

		_trialTotalChests = CountTilesOfType(6);
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

	private void BuildLevel()
	{
		_levelRoot = new Node3D();
		_markerRoot = new Node3D();
		AddChild(_levelRoot);
		AddChild(_markerRoot);
		_tileMeshes.Clear();

		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				int tileType = _tiles[y, x];
				_discoveredTiles[y, x] = false;
				float verticalScale = tileType == 0 ? 1.1f : (tileType == 1 || tileType == 2 ? TileHeight : TileHeight + 0.15f);
				float baseY = tileType == 0 ? 0.55f : 0.18f;

				var tile = new MeshInstance3D
				{
					Mesh = new BoxMesh
					{
						Size = new Vector3(1.0f, verticalScale, 1.0f)
					},
					Position = GridToWorld(new Vector2I(x, y)) + new Vector3(0, baseY, 0),
					MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = _tileColors.GetValueOrDefault(tileType, Color.FromHtml("#1e2436")),
						Metallic = 0.08f,
						Roughness = 0.75f
					}
				};

				_levelRoot.AddChild(tile);
				_tileMeshes.Add(tile);
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

				var marker = new MeshInstance3D
				{
					Mesh = tileType == 6
						? new BoxMesh { Size = new Vector3(0.48f, 0.48f, 0.48f) }
						: new BoxMesh { Size = new Vector3(0.55f, 0.18f, 0.55f) },
					Position = GridToWorld(new Vector2I(x, y)) + new Vector3(0, tileType == 6 ? 0.68f : 0.52f, 0),
					MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = _tileColors.GetValueOrDefault(tileType, Colors.White),
						Metallic = 0.0f,
						Roughness = 0.4f
					}
				};
				_markerRoot.AddChild(marker);
				_markersByPos[new Vector2I(x, y)] = marker;
			}
		}
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

	private void SetupCamera()
	{
		_camera = new Camera3D
		{
			Position = new Vector3(0, 26f, 0),
			RotationDegrees = new Vector3(-90, 0, 0),
			Projection = Camera3D.ProjectionType.Orthogonal,
			Size = 20f,
			Current = true
		};

		AddChild(_camera);

		var light = new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-45, 30, 0),
			LightEnergy = 1.5f
		};

		AddChild(light);
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

		Vector3 playerWorld = _playerMesh.Position;
		_camera.Position = new Vector3(playerWorld.X, 26.0f, playerWorld.Z);
		_camera.RotationDegrees = new Vector3(-90, 0, 0);
	}

	private void SpawnEnemies()
	{
		_enemyRoot = new Node3D();
		AddChild(_enemyRoot);
		Vector2 worldOffset = GetGridWorldOrigin();
		float difficultyScale = Mathf.Max(0.1f, _currentGenome.Params.EnemyDifficultyScale);

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

		int maxEnemies = Mathf.Min(spawnPoints.Count, 120);
		for (int i = 0; i < maxEnemies; i++)
		{
			EnemyClassDefinition definition = CreateEnemyDefinitionForRoll(_rng.Randf(), difficultyScale);

			var enemy = new EnemyController();
			enemy.WorldOffset = worldOffset;
			enemy.Initialize(spawnPoints[i], definition.Behavior, definition);
			enemy.Position = GridToWorld(spawnPoints[i]) + new Vector3(0, 0.4f, 0);
			enemy.EnemyAttackedPlayer += OnEnemyAttackedPlayer;
			_enemyRoot.AddChild(enemy);
			_enemies.Add(enemy);
		}
	}

	// Which behavior gets spawned is drawn from the current genome's
	// enemy_behavior_mix weights (Backend/genome.py); EnemyDifficultyScale
	// (also genome-evolved) scales MaxHp/Attack on top of that. See
	// EnemyController.IsRanged / GetAttackDistance for how behavior +
	// AttackRange together decide movement pattern and attack distance/FX.
	private EnemyClassDefinition CreateEnemyDefinitionForRoll(float roll, float difficultyScale)
	{
		Dictionary<string, float> mix = _currentGenome.Params.EnemyBehaviorMix;
		float total = 0f;
		foreach (float weight in mix.Values)
		{
			total += Mathf.Max(0f, weight);
		}

		if (total <= 0f)
		{
			total = 1f;
		}

		float cumulative = 0f;
		string chosenBehavior = "melee_chaser";
		foreach (KeyValuePair<string, float> entry in mix)
		{
			cumulative += Mathf.Max(0f, entry.Value) / total;
			if (roll <= cumulative)
			{
				chosenBehavior = entry.Key;
				break;
			}
		}

		return chosenBehavior switch
		{
			"melee_hit_run" => EnemyClassDefinition.Create("scout", "Scout", "melee_hit_run", ScaleStats(new EnemyStats { MaxHp = 18, Speed = 1.3f, Attack = 8, Defense = 1, VisionRange = 5, AttackRange = 1, Reward = 12 }, difficultyScale)),
			"ranged_kite" => EnemyClassDefinition.Create("guardian", "Guardian", "ranged_kite", ScaleStats(new EnemyStats { MaxHp = 24, Speed = 0.8f, Attack = 10, Defense = 6, VisionRange = 6, AttackRange = 5, Reward = 15 }, difficultyScale)),
			"ranged_strafe" => EnemyClassDefinition.Create("sentinel", "Sentinel", "ranged_strafe", ScaleStats(new EnemyStats { MaxHp = 24, Speed = 0.8f, Attack = 10, Defense = 6, VisionRange = 6, AttackRange = 5, Reward = 15 }, difficultyScale)),
			_ => EnemyClassDefinition.Create("brute", "Brute", "melee_chaser", ScaleStats(new EnemyStats { MaxHp = 28, Speed = 0.9f, Attack = 12, Defense = 4, VisionRange = 6, AttackRange = 1, Reward = 18 }, difficultyScale)),
		};
	}

	private static EnemyStats ScaleStats(EnemyStats baseStats, float difficultyScale)
	{
		baseStats.MaxHp = Mathf.Max(1, Mathf.RoundToInt(baseStats.MaxHp * difficultyScale));
		baseStats.Attack = Mathf.Max(1, Mathf.RoundToInt(baseStats.Attack * difficultyScale));
		return baseStats;
	}

	private void HandlePlayerInput(double delta)
	{
		_moveTimer += delta;
		float stepDelay = Mathf.Max(0.05f, 0.18f / Mathf.Max(_playerStats.MoveSpeed, 0.1f));
		if (_moveTimer < stepDelay)
		{
			return;
		}

		Vector2I direction = GetMovementDirection();
		if (direction == Vector2I.Zero)
		{
			_moveTimer = 0;
			return;
		}

		Vector2I nextPos = _playerPos + direction;
		if (IsWalkable(nextPos))
		{
			_playerPos = nextPos;
			SetPlayerWorldPosition();
			HandleTileEffect();
		}

		_moveTimer = 0;
	}

	private void HandlePlayerAttack(double delta)
	{
		_attackCooldownTimer = Math.Max(0.0, _attackCooldownTimer - delta);

		bool attackKeyDown = Input.IsKeyPressed(Key.Space);
		if (attackKeyDown && !_attackKeyWasDown && _attackCooldownTimer <= 0.0)
		{
			TryAttackNearestEnemy();
			_attackCooldownTimer = AttackCooldown;
		}

		_attackKeyWasDown = attackKeyDown;
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

			int distance = Math.Abs(enemy.GridPos.X - _playerPos.X) + Math.Abs(enemy.GridPos.Y - _playerPos.Y);
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

		int damage = Math.Max(1, _playerStats.Attack - target.Stats.Defense);
		target.TakeDamage(damage);

		if (target.CurrentHp <= 0)
		{
			_score += target.Stats.Reward;
			_trialEnemiesKilled++;
		}
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

	private void OnEnemyAttackedPlayer(int rawDamage)
	{
		int mitigated = Math.Max(1, rawDamage - _playerStats.Defense);
		_playerCurrentHp = Math.Max(0, _playerCurrentHp - mitigated);
		_trialDamageTaken += mitigated;

		if (_playerCurrentHp <= 0)
		{
			HandlePlayerDeath();
		}
	}

	private void HandlePlayerDeath()
	{
		_trialDeaths++;
		_score = Mathf.Max(0, _score - 20);
		_playerPos = _startPos;
		SetPlayerWorldPosition();
		_playerCurrentHp = _playerStats.MaxHp;
	}

	private Vector2I GetMovementDirection()
	{
		bool right = Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D);
		bool left = Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A);
		bool down = Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S);
		bool up = Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W);

		if (right && !left && !up && !down) return Vector2I.Right;
		if (left && !right && !up && !down) return Vector2I.Left;
		if (up && !down && !left && !right) return Vector2I.Up;
		if (down && !up && !left && !right) return Vector2I.Down;

		return Vector2I.Zero;
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
				}
				break;
			case 8:
				_score += 50;
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

			if (tileType == 4)
			{
				_trialTrapsTriggered++;
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
		int index = (pos.Y * _mapWidth) + pos.X;
		if (index >= 0 && index < _tileMeshes.Count && _tileMeshes[index].MaterialOverride is StandardMaterial3D tileMaterial)
		{
			tileMaterial.AlbedoColor = tileMaterial.AlbedoColor.Lerp(Colors.White, amount);
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
