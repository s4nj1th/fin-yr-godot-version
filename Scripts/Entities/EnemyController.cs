using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.Data;

namespace DungeonCrawler.Entities;

public partial class EnemyController : Node3D
{
	[Signal] public delegate void EnemyAttackedPlayerEventHandler(int damage);

	public Vector2I GridPos { get; private set; }
	public string BehaviorType { get; private set; } = "patrol";
	public EnemyClassDefinition ClassDefinition { get; private set; } = EnemyClassDefinition.CreateDefault();
	public EnemyStats Stats => ClassDefinition.Stats;
	public int[,] AiPixelGrid { get; private set; } = new int[8, 8];
	public int CurrentHp { get; private set; }
	public int MaxHp => Stats.MaxHp;
	public Vector2 WorldOffset { get; set; } = Vector2.Zero;

	private const int AiGridSize = 8;
	private readonly List<MeshInstance3D> _pixelCells = new();
	private MeshInstance3D _body = null!;
	private Node3D _nnWallRoot = null!;
	private double _aiTimer;
	private double _attackCooldown;
	private int _postAttackSteps;
	private bool _postAttackRetreat;
	private int _strafeSign = 1;

	public void Initialize(Vector2I startGridPos, string behaviorType, EnemyClassDefinition? definition = null)
	{
		GridPos = startGridPos;
		BehaviorType = behaviorType;
		ClassDefinition = definition ?? EnemyClassDefinition.CreateDefault(behaviorType);
		CurrentHp = Stats.MaxHp;

		BuildVisuals();
		UpdatePosition();
		RefreshAiGridVisualization();
	}

	public void Tick(double delta, Vector2I playerPos, int[,] walkableGrid)
	{
		if (CurrentHp <= 0)
		{
			return;
		}

		_aiTimer += delta;
		_attackCooldown = Math.Max(0.0, _attackCooldown - delta);
		float stepDelay = Mathf.Max(0.25f, 1.0f / Math.Max(Stats.Speed, 0.5f));
		if (_aiTimer < stepDelay)
		{
			return;
		}

		_aiTimer = 0;

		Vector2I nextStep = ChooseNextStep(playerPos, walkableGrid);
		if (nextStep != Vector2I.Zero)
		{
			GridPos += nextStep;
			UpdatePosition();
		}

		BuildAiPixelGrid(playerPos, walkableGrid);
		RefreshAiGridVisualization();
	}

	public int[,] BuildAiPixelGrid(Vector2I playerPos, int[,] walkableGrid)
	{
		AiPixelGrid = new int[AiGridSize, AiGridSize];

		for (int y = 0; y < AiGridSize; y++)
		{
			for (int x = 0; x < AiGridSize; x++)
			{
				int worldX = GridPos.X + x - (AiGridSize / 2);
				int worldY = GridPos.Y + y - (AiGridSize / 2);

				if (worldX < 0 || worldY < 0 || worldX >= walkableGrid.GetLength(1) || worldY >= walkableGrid.GetLength(0))
				{
					AiPixelGrid[y, x] = 1;
					continue;
				}

				int tileType = walkableGrid[worldY, worldX];
				if (tileType == 0)
				{
					AiPixelGrid[y, x] = 1;
				}
				else if (worldX == playerPos.X && worldY == playerPos.Y)
				{
					AiPixelGrid[y, x] = 2;
				}
				else if (Math.Abs(worldX - playerPos.X) <= 1 && Math.Abs(worldY - playerPos.Y) <= 1)
				{
					AiPixelGrid[y, x] = 3;
				}
				else
				{
					AiPixelGrid[y, x] = 0;
				}
			}
		}

		return AiPixelGrid;
	}

	public Vector2I ChooseNextStep(Vector2I playerPos, int[,] walkableGrid)
	{
		int distanceX = playerPos.X - GridPos.X;
		int distanceY = playerPos.Y - GridPos.Y;
		int manhattanDistance = Math.Abs(distanceX) + Math.Abs(distanceY);
		int attackDistance = GetAttackDistance();

		if (_attackCooldown <= 0.0 && manhattanDistance <= attackDistance)
		{
			EmitSignal(SignalName.EnemyAttackedPlayer, Stats.Attack);
			_attackCooldown = 0.85;
			if (BehaviorType is "melee_hit_run" or "ranged_kite")
			{
				_postAttackRetreat = true;
				_postAttackSteps = 2;
			}
			else if (BehaviorType == "ranged_strafe")
			{
				_postAttackRetreat = false;
				_postAttackSteps = 2;
				_strafeSign *= -1;
			}
		}

		Vector2I[] directions =
		[
			new Vector2I(1, 0),
			new Vector2I(-1, 0),
			new Vector2I(0, 1),
			new Vector2I(0, -1)
		];

		if (_postAttackSteps > 0)
		{
			_postAttackSteps--;
			Vector2I postAttackMove = _postAttackRetreat
				? FindBestStep(playerPos, walkableGrid, directions, preferCloser: false)
				: FindStrafeStep(playerPos, walkableGrid, directions);

			if (postAttackMove != Vector2I.Zero)
			{
				return postAttackMove;
			}
		}

		if (BehaviorType is "melee_chaser" or "ambush")
		{
			return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
		}

		if (BehaviorType == "melee_hit_run")
		{
			if (manhattanDistance <= attackDistance + 1)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
			}

			return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
		}

		if (BehaviorType == "ranged_kite")
		{
			if (manhattanDistance < 3)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
			}

			if (manhattanDistance > 5)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
			}

			return FindStrafeStep(playerPos, walkableGrid, directions);
		}

		if (BehaviorType == "ranged_strafe")
		{
			if (manhattanDistance > 6)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
			}

			if (manhattanDistance < 3)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
			}

			return FindStrafeStep(playerPos, walkableGrid, directions);
		}

		if (BehaviorType == "patrol")
		{
			for (int i = 0; i < directions.Length; i++)
			{
				Vector2I direction = directions[i];
				Vector2I candidate = new(GridPos.X + direction.X, GridPos.Y + direction.Y);
				if (IsWalkable(candidate, walkableGrid))
				{
					return direction;
				}
			}
		}

		return Vector2I.Zero;
	}

	private int GetAttackDistance()
	{
		return BehaviorType.StartsWith("ranged", StringComparison.Ordinal) ? 4 : Stats.AttackRange + 1;
	}

	private Vector2I FindBestStep(Vector2I playerPos, int[,] walkableGrid, Vector2I[] directions, bool preferCloser)
	{
		int currentDistance = Math.Abs(playerPos.X - GridPos.X) + Math.Abs(playerPos.Y - GridPos.Y);
		int bestDistance = preferCloser ? int.MaxValue : int.MinValue;
		Vector2I bestDirection = Vector2I.Zero;

		for (int i = 0; i < directions.Length; i++)
		{
			Vector2I direction = directions[i];
			Vector2I candidate = new(GridPos.X + direction.X, GridPos.Y + direction.Y);
			if (!IsWalkable(candidate, walkableGrid))
			{
				continue;
			}

			int distance = Math.Abs(playerPos.X - candidate.X) + Math.Abs(playerPos.Y - candidate.Y);
			if (preferCloser)
			{
				if (distance < bestDistance)
				{
					bestDistance = distance;
					bestDirection = direction;
				}
			}
			else
			{
				if (distance > bestDistance)
				{
					bestDistance = distance;
					bestDirection = direction;
				}
			}
		}

		if (bestDirection == Vector2I.Zero)
		{
			return Vector2I.Zero;
		}

		if ((preferCloser && bestDistance >= currentDistance) || (!preferCloser && bestDistance <= currentDistance))
		{
			return Vector2I.Zero;
		}

		return bestDirection;
	}

	private Vector2I FindStrafeStep(Vector2I playerPos, int[,] walkableGrid, Vector2I[] directions)
	{
		Vector2I toPlayer = new(playerPos.X - GridPos.X, playerPos.Y - GridPos.Y);
		Vector2I primary = Math.Abs(toPlayer.X) >= Math.Abs(toPlayer.Y)
			? new Vector2I(0, Math.Sign(toPlayer.X) * _strafeSign)
			: new Vector2I(Math.Sign(toPlayer.Y) * -_strafeSign, 0);

		Vector2I secondary = new(-primary.X, -primary.Y);
		Vector2I[] preferred = [primary, secondary];
		for (int i = 0; i < preferred.Length; i++)
		{
			Vector2I direction = preferred[i];
			if (direction == Vector2I.Zero)
			{
				continue;
			}

			Vector2I candidate = new(GridPos.X + direction.X, GridPos.Y + direction.Y);
			if (IsWalkable(candidate, walkableGrid))
			{
				return direction;
			}
		}

		return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
	}

	private bool IsWalkable(Vector2I targetPos, int[,] walkableGrid)
	{
		if (targetPos.X < 0 || targetPos.Y < 0 || targetPos.X >= walkableGrid.GetLength(1) || targetPos.Y >= walkableGrid.GetLength(0))
		{
			return false;
		}

		return walkableGrid[targetPos.Y, targetPos.X] != 0;
	}

	private void BuildVisuals()
	{
		_body = new MeshInstance3D
		{
			Mesh = new CapsuleMesh
			{
				Radius = 0.26f,
				Height = 0.85f
			},
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = GetClassColor(),
				Metallic = 0.15f,
				Roughness = 0.45f
			}
		};

		AddChild(_body);

		_nnWallRoot = new Node3D
		{
			Position = new Vector3(0, 1.0f, -0.9f)
		};
		AddChild(_nnWallRoot);

		var boardBacking = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(1.35f, 1.35f, 0.06f) },
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.07f, 0.09f, 0.16f),
				Roughness = 0.9f
			}
		};
		_nnWallRoot.AddChild(boardBacking);

		for (int y = 0; y < AiGridSize; y++)
		{
			for (int x = 0; x < AiGridSize; x++)
			{
				var pixel = new MeshInstance3D
				{
					Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.035f) },
					MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.Transparent }
				};

				pixel.Position = new Vector3((x - ((AiGridSize - 1) / 2.0f)) * 0.145f, (y - ((AiGridSize - 1) / 2.0f)) * 0.145f, 0.03f);
				_nnWallRoot.AddChild(pixel);
				_pixelCells.Add(pixel);
			}
		}
	}

	private void RefreshAiGridVisualization()
	{
		for (int index = 0; index < _pixelCells.Count; index++)
		{
			int y = index / AiGridSize;
			int x = index % AiGridSize;
			int value = AiPixelGrid[y, x];

			var material = (StandardMaterial3D)_pixelCells[index].MaterialOverride;
			material.AlbedoColor = value switch
			{
				0 => new Color(0.15f, 0.15f, 0.25f),
				1 => new Color(0.9f, 0.25f, 0.25f),
				2 => new Color(0.2f, 0.8f, 0.95f),
				3 => new Color(0.7f, 0.5f, 0.2f),
				_ => new Color(0.15f, 0.15f, 0.25f)
			};
		}
	}

	private void UpdatePosition()
	{
		Position = new Vector3(GridPos.X + WorldOffset.X, 0.4f, GridPos.Y + WorldOffset.Y);
	}

	private Color GetClassColor()
	{
		return ClassDefinition.ClassId switch
		{
			"brute" => Color.FromHtml("#f87171"),
			"archer" => Color.FromHtml("#a78bfa"),
			"guardian" => Color.FromHtml("#34d399"),
			_ => Color.FromHtml("#f43f5e")
		};
	}
}
