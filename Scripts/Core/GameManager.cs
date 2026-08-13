using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.Data;
using DungeonCrawler.Entities;

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
	private readonly Dictionary<int, Color> _tileColors = new()
	{
		{ 0, Color.FromHtml("#111422") },
		{ 1, Color.FromHtml("#1e2436") },
		{ 2, Color.FromHtml("#181d2e") },
		{ 3, Color.FromHtml("#8b5cf6") },
		{ 4, Color.FromHtml("#ef4444") },
		{ 5, Color.FromHtml("#f43f5e") },
		{ 6, Color.FromHtml("#f59e0b") },
		{ 7, Color.FromHtml("#10b981") },
		{ 8, Color.FromHtml("#06b6d4") },
		{ 9, Color.FromHtml("#050609") }
	};

	private int[,] _tiles = null!;
	private bool[,] _discoveredTiles = null!;
	private Image _discoveryImage = null!;
	private ImageTexture _discoveryTexture = null!;
	private int _mapWidth;
	private int _mapHeight;
	private ulong _generationCounter;

	private Node3D _levelRoot = null!;
	private Node3D _enemyRoot = null!;
	private Node3D _markerRoot = null!;
	private MeshInstance3D _playerMesh = null!;
	private MeshInstance3D _fogOverlay = null!;
	private ShaderMaterial _fogMaterial = null!;
	private Camera3D _camera = null!;
	private Vector2I _playerPos;
	private Vector2I _startPos;
	private double _moveTimer;
	private int _score;
	private bool _rewardCollected;

	public override void _Ready()
	{
		RegenerateDungeon();
	}

	public override void _Process(double delta)
	{
		if (Input.IsKeyPressed(Key.R))
		{
			RegenerateDungeon();
			return;
		}

		HandlePlayerInput(delta);
		UpdateTileVisibility();
		UpdateCameraPosition();

		foreach (EnemyController enemy in _enemies)
		{
			enemy.Tick(delta, _playerPos, _tiles);
			if (_playerPos == enemy.GridPos)
			{
				_score = Mathf.Max(0, _score - 5);
				_playerPos = _startPos;
				SetPlayerWorldPosition();
			}
		}
	}

	private void RegenerateDungeon()
	{
		_mapWidth = Mathf.Max(40, _generationSettings.MapWidth);
		_mapHeight = Mathf.Max(40, _generationSettings.MapHeight);
		_tiles = new int[_mapHeight, _mapWidth];
		_discoveredTiles = new bool[_mapHeight, _mapWidth];
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

		GenerateDungeon();
		BuildLevel();
		SetupCamera();
		SetupPlayer();
		SpawnEnemies();
		UpdateCameraPosition();
		UpdateTileVisibility();
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
		var trapPositions = new List<Vector2I>();
		var chestPositions = new List<Vector2I>();
		var enemyPositions = new List<Vector2I>();

		for (int i = 1; i < _rooms.Count; i++)
		{
			var room = _rooms[i];
			var center = new Vector2I(room.X + room.W / 2, room.Y + room.H / 2);

			if (i % 3 == 0)
			{
				trapPositions.Add(center);
			}
			else if (i % 2 == 0)
			{
				chestPositions.Add(center);
			}
			else
			{
				enemyPositions.Add(center);
			}
		}

		for (int i = 0; i < trapPositions.Count; i++)
		{
			var pos = trapPositions[i];
			if (_tiles[pos.Y, pos.X] == 1 || _tiles[pos.Y, pos.X] == 2)
			{
				_tiles[pos.Y, pos.X] = 4;
			}
		}

		for (int i = 0; i < chestPositions.Count; i++)
		{
			var pos = chestPositions[i];
			if (_tiles[pos.Y, pos.X] == 1 || _tiles[pos.Y, pos.X] == 2)
			{
				_tiles[pos.Y, pos.X] = 6;
			}
		}

		for (int i = 0; i < enemyPositions.Count; i++)
		{
			var pos = enemyPositions[i];
			if (_tiles[pos.Y, pos.X] == 1 || _tiles[pos.Y, pos.X] == 2)
			{
				_tiles[pos.Y, pos.X] = 5;
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

		for (int y = 0; y < _mapHeight; y++)
		{
			for (int x = 0; x < _mapWidth; x++)
			{
				int tileType = _tiles[y, x];
				if (tileType is not (4 or 6 or 7 or 8))
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
						AlbedoColor = tileType switch
						{
							4 => Color.FromHtml("#ef4444"),
							6 => Color.FromHtml("#f59e0b"),
							7 => Color.FromHtml("#10b981"),
							8 => Color.FromHtml("#06b6d4"),
							_ => Color.FromHtml("#ffffff")
						},
						Metallic = 0.0f,
						Roughness = 0.4f
					}
				};
				_markerRoot.AddChild(marker);
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

		var definitions = new[]
		{
			EnemyClassDefinition.Create("scout", "Scout", "patrol", new EnemyStats { MaxHp = 18, Speed = 1.3f, Attack = 8, VisionRange = 5, AttackRange = 1, Reward = 12 }),
			EnemyClassDefinition.Create("brute", "Brute", "ambush", new EnemyStats { MaxHp = 28, Speed = 0.9f, Attack = 12, VisionRange = 6, AttackRange = 1, Reward = 18 }),
			EnemyClassDefinition.Create("guardian", "Guardian", "patrol", new EnemyStats { MaxHp = 24, Speed = 0.8f, Attack = 10, VisionRange = 4, AttackRange = 2, Reward = 15 })
		};

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
			var enemy = new EnemyController();
			enemy.WorldOffset = worldOffset;
			enemy.Initialize(spawnPoints[i], definitions[i % definitions.Length].Behavior, definitions[i % definitions.Length]);
			enemy.Position = GridToWorld(spawnPoints[i]) + new Vector3(0, 0.4f, 0);
			_enemyRoot.AddChild(enemy);
			_enemies.Add(enemy);
		}
	}

	private void HandlePlayerInput(double delta)
	{
		_moveTimer += delta;
		if (_moveTimer < 0.18)
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
			case 4:
				_tiles[_playerPos.Y, _playerPos.X] = 1;
				_score = Mathf.Max(0, _score - 5);
				break;
			case 6:
				_tiles[_playerPos.Y, _playerPos.X] = 1;
				_score += 25;
				_rewardCollected = true;
				break;
			case 8:
				_score += 50;
				break;
		}
	}

	private bool IsWalkable(Vector2I position)
	{
		if (position.X < 0 || position.Y < 0 || position.X >= _mapWidth || position.Y >= _mapHeight)
		{
			return false;
		}

		return _tiles[position.Y, position.X] != 0;
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
