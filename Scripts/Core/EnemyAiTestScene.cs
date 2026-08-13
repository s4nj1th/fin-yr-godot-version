using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.Data;
using DungeonCrawler.Entities;

namespace DungeonCrawler.Core;

public partial class EnemyAiTestScene : Node3D
{
	[Export] public bool UseRangedSet { get; set; }

	private const int ArenaWidth = 20;
	private const int ArenaHeight = 16;
	private const float TileHeight = 0.2f;
	private const double MoveDelay = 0.14;

	private readonly List<EnemyController> _enemies = new();
	private readonly int[,] _walkable = new int[ArenaHeight, ArenaWidth];
	private Node3D _arenaRoot = null!;
	private MeshInstance3D _playerMesh = null!;
	private Camera3D _camera = null!;
	private Label3D _infoLabel = null!;
	private Vector2I _playerPos = new(ArenaWidth / 2, ArenaHeight / 2);
	private double _moveTimer;

	public override void _Ready()
	{
		BuildArena();
		SetupPlayer();
		SpawnEnemies();
		SetupCameraAndLight();
		CreateInfoLabel();
	}

	public override void _Process(double delta)
	{
		HandlePlayerInput(delta);
		UpdateCameraPosition();

		for (int i = 0; i < _enemies.Count; i++)
		{
			EnemyController enemy = _enemies[i];
			enemy.Tick(delta, _playerPos, _walkable);
		}
	}

	private void BuildArena()
	{
		_arenaRoot = new Node3D();
		AddChild(_arenaRoot);

		for (int y = 0; y < ArenaHeight; y++)
		{
			for (int x = 0; x < ArenaWidth; x++)
			{
				bool borderWall = x == 0 || y == 0 || x == ArenaWidth - 1 || y == ArenaHeight - 1;
				bool centerPillar = (x == ArenaWidth / 2 || x == ArenaWidth / 2 - 1) && y > 4 && y < ArenaHeight - 5;
				bool sidePillar = (y == ArenaHeight / 2) && (x > 3 && x < 7 || x > ArenaWidth - 8 && x < ArenaWidth - 4);
				bool wall = borderWall || centerPillar || sidePillar;
				_walkable[y, x] = wall ? 0 : 1;

				var tile = new MeshInstance3D
				{
					Mesh = new BoxMesh
					{
						Size = new Vector3(1.0f, wall ? 1.2f : TileHeight, 1.0f)
					},
					Position = GridToWorld(new Vector2I(x, y)) + new Vector3(0, wall ? 0.6f : 0.1f, 0),
					MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = wall ? Color.FromHtml("#182033") : Color.FromHtml("#111827"),
						Roughness = 0.85f
					}
				};
				_arenaRoot.AddChild(tile);
			}
		}
	}

	private void SetupPlayer()
	{
		_playerMesh = new MeshInstance3D
		{
			Mesh = new CapsuleMesh
			{
				Radius = 0.24f,
				Height = 0.92f
			},
			Position = GridToWorld(_playerPos) + new Vector3(0, 0.48f, 0),
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = Color.FromHtml("#38bdf8"),
				Metallic = 0.15f,
				Roughness = 0.35f
			}
		};

		AddChild(_playerMesh);
	}

	private void SpawnEnemies()
	{
		EnemyStats commonStats = new()
		{
			MaxHp = 22,
			Speed = 1.0f,
			Attack = 8,
			VisionRange = 6,
			AttackRange = 1,
			Reward = 10
		};

		EnemyClassDefinition[] defs = UseRangedSet
			? new[]
			{
				EnemyClassDefinition.Create("archer", "Ranged Kite", "ranged_kite", commonStats),
				EnemyClassDefinition.Create("guardian", "Ranged Strafe", "ranged_strafe", commonStats)
			}
			: new[]
			{
				EnemyClassDefinition.Create("brute", "Melee Chaser", "melee_chaser", commonStats),
				EnemyClassDefinition.Create("scout", "Melee Hit-Run", "melee_hit_run", commonStats)
			};

		Vector2I[] spawnPoints =
		[
			new Vector2I(4, 4),
			new Vector2I(ArenaWidth - 5, ArenaHeight - 5)
		];

		Vector2 worldOffset = GetGridWorldOrigin();
		for (int i = 0; i < defs.Length; i++)
		{
			var enemy = new EnemyController
			{
				WorldOffset = worldOffset
			};
			enemy.Initialize(spawnPoints[i], defs[i].Behavior, defs[i]);
			enemy.EnemyAttackedPlayer += OnEnemyAttackedPlayer;
			AddChild(enemy);
			_enemies.Add(enemy);
		}
	}

	private void OnEnemyAttackedPlayer(int damage)
	{
		GD.Print($"Enemy hit player for {damage} damage in {(UseRangedSet ? "Ranged" : "Melee")} AI test scene.");
	}

	private void SetupCameraAndLight()
	{
		_camera = new Camera3D
		{
			Position = new Vector3(0, 18f, 15f),
			RotationDegrees = new Vector3(-48, 0, 0),
			Current = true
		};
		AddChild(_camera);

		var light = new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-50, 25, 0),
			LightEnergy = 1.45f
		};
		AddChild(light);
	}

	private void CreateInfoLabel()
	{
		_infoLabel = new Label3D
		{
			Text = UseRangedSet
				? "Ranged Scene: ranged_kite + ranged_strafe (same stats)"
				: "Melee Scene: melee_chaser + melee_hit_run (same stats)",
			Position = new Vector3(0, 2.5f, -7.0f),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			PixelSize = 0.008f,
			Modulate = Color.FromHtml("#e2e8f0")
		};
		AddChild(_infoLabel);
	}

	private void HandlePlayerInput(double delta)
	{
		_moveTimer += delta;
		if (_moveTimer < MoveDelay)
		{
			return;
		}

		Vector2I direction = Vector2I.Zero;
		if (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D)) direction = Vector2I.Right;
		else if (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A)) direction = Vector2I.Left;
		else if (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S)) direction = Vector2I.Down;
		else if (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W)) direction = Vector2I.Up;

		if (direction == Vector2I.Zero)
		{
			_moveTimer = 0;
			return;
		}

		Vector2I next = _playerPos + direction;
		if (IsWalkable(next))
		{
			_playerPos = next;
			_playerMesh.Position = GridToWorld(_playerPos) + new Vector3(0, 0.48f, 0);
		}

		_moveTimer = 0;
	}

	private bool IsWalkable(Vector2I p)
	{
		if (p.X < 0 || p.Y < 0 || p.X >= ArenaWidth || p.Y >= ArenaHeight)
		{
			return false;
		}

		return _walkable[p.Y, p.X] != 0;
	}

	private void UpdateCameraPosition()
	{
		if (_camera == null || _playerMesh == null)
		{
			return;
		}

		Vector3 focus = _playerMesh.Position;
		_camera.Position = focus + new Vector3(0, 18f, 15f);
		_camera.LookAt(focus + new Vector3(0, 0.8f, 0), Vector3.Up);
	}

	private Vector3 GridToWorld(Vector2I gridPos)
	{
		Vector2 offset = GetGridWorldOrigin();
		return new Vector3(gridPos.X + offset.X, 0, gridPos.Y + offset.Y);
	}

	private static Vector2 GetGridWorldOrigin()
	{
		return new Vector2(-((ArenaWidth - 1) * 0.5f), -((ArenaHeight - 1) * 0.5f));
	}
}
