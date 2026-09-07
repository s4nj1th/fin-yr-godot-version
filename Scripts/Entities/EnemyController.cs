using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.AI;
using DungeonCrawler.Data;

namespace DungeonCrawler.Entities;

public partial class EnemyController : Node3D
{
	[Signal] public delegate void EnemyAttackedPlayerEventHandler(int damage);
	[Signal] public delegate void EnemyDiedEventHandler(int rewardScore);

	public Vector2I GridPos { get; private set; }
	public string BehaviorType { get; private set; } = "patrol";
	public EnemyClassDefinition ClassDefinition { get; private set; } = EnemyClassDefinition.CreateDefault();
	public EnemyStats Stats => ClassDefinition.Stats;
	public int[,] AiPixelGrid { get; private set; } = new int[8, 8];
	public int CurrentHp { get; private set; }
	public int MaxHp => Stats.MaxHp;
	public Vector2 WorldOffset { get; set; } = Vector2.Zero;

	// The 8x8 debug board (ENEMY_AI.md section 5) is 66 extra MeshInstance3D
	// nodes and materials per enemy — fine for the 2-enemy AI test scenes it
	// was built for, ruinous for the main dungeon's up-to-120-enemy spawns
	// (120 x 66 ~= 7,900 nodes rebuilt/recolored on every AI step). Must be
	// set before Initialize() — BuildVisuals reads it once, at construction.
	public bool ShowDebugBoard { get; set; } = true;

	// --- Adaptive AI (docs/ENEMY_AI_ADAPTATION_PLAN.md) ---
	// Perception is tracked for every enemy regardless of whether it has an
	// active tactical order — it's cheap and keeps the belief state ready
	// for whatever order a later spawn might carry. Only enemies that
	// actually received an order from the AdaptationDirector let that
	// belief state change their movement (see ChooseNextStep); a baseline
	// enemy with no order behaves exactly as before, unchanged difficulty.
	public EnemyPerception Perception { get; } = new();
	public EnemyTacticalOrder TacticalOrder { get; set; } = EnemyTacticalOrder.None();

	private readonly RandomNumberGenerator _searchRng = new();
	private PerceptionState _previousPerceptionState = PerceptionState.Unaware;
	private bool _lineOfSightJustLost;

	private const int AiGridSize = 8;

	// --- Tunable AI attack-pattern parameters (grid distances in tiles) ---
	// Tweak these to change how each behavior "feels" without touching logic below.
	private const int MeleeEngageBuffer = 1;      // melee enemies stop chasing this many tiles before Stats.AttackRange
	private const int KiteRetreatDistance = 3;    // ranged_kite: closer than this -> back away
	private const int KiteAdvanceDistance = 5;    // ranged_kite: farther than this -> close in
	private const int StrafeRetreatDistance = 3;  // ranged_strafe: closer than this -> back away
	private const int StrafeAdvanceDistance = 6;  // ranged_strafe: farther than this -> close in
	private const int PostAttackRetreatSteps = 2; // melee_hit_run / ranged_kite: steps to disengage after landing a hit
	private const int PostAttackStrafeSteps = 2;  // ranged_strafe: steps to strafe after landing a hit
	private const double AttackCooldownSeconds = 0.85;

	private readonly List<MeshInstance3D> _pixelCells = new();
	private MeshInstance3D _body = null!;
	private StandardMaterial3D _bodyMaterial = null!;
	private Node3D _nnWallRoot = null!;
	private Tween? _flashTween;
	private bool _isDying;
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
		if (ShowDebugBoard)
		{
			RefreshAiGridVisualization();
		}
	}

	public void TakeDamage(int rawDamage, AttackDirection direction = AttackDirection.Front)
	{
		if (CurrentHp <= 0 || _isDying)
		{
			return;
		}

		int mitigated = Math.Max(1, rawDamage - Stats.Defense);
		CurrentHp = Math.Max(0, CurrentHp - mitigated);
		PlayHitFlash();

		// Rear-defense countermeasure (docs/ENEMY_AI_ADAPTATION_PLAN.md
		// section 10): "turn toward the player faster" is implemented as a
		// faster re-engage — a rear hit while GuardRear is active cuts the
		// remaining attack cooldown, since this game has no separate facing
		// rotation to literally turn. Only fires for genuinely-Rear hits,
		// not Front/Side, so it doesn't just make the enemy hit harder
		// across the board.
		if (TacticalOrder.GuardRear && direction == AttackDirection.Rear && CurrentHp > 0)
		{
			_attackCooldown = Math.Max(0.0, _attackCooldown / Math.Max(1f, TacticalOrder.TurnSpeedMultiplier));
		}

		if (CurrentHp <= 0)
		{
			EmitSignal(SignalName.EnemyDied, Stats.Reward);
			PlayDeathAndFree();
		}
	}

	public void Tick(double delta, Vector2I playerPos, int[,] walkableGrid)
	{
		if (CurrentHp <= 0)
		{
			return;
		}

		// Perception updates every frame, independent of the movement step
		// delay below — awareness/last-known-position shouldn't lag behind
		// by up to a full step just because the enemy hasn't chosen to move.
		int manhattanDistance = Math.Abs(playerPos.X - GridPos.X) + Math.Abs(playerPos.Y - GridPos.Y);
		// Cheap distance check before the O(distance) Bresenham trace below —
		// with up to 120 enemies on the map and only a handful ever within
		// VisionRange (4-6 tiles) of the player at once, skipping the trace
		// entirely for the rest is the difference between this running every
		// frame comfortably and pegging the CPU tracing lines for enemies
		// nowhere near seeing the player anyway.
		bool withinVisionRange = manhattanDistance <= Stats.VisionRange;
		bool hasLineOfSight = withinVisionRange && GridLineOfSight.HasLineOfSight(ToGridPoint(GridPos), ToGridPoint(playerPos), walkableGrid);
		Perception.Update(delta, hasLineOfSight, ToGridPoint(playerPos), manhattanDistance, Stats.VisionRange);

		if (_previousPerceptionState == PerceptionState.Tracking && Perception.State != PerceptionState.Tracking)
		{
			_lineOfSightJustLost = true;
		}
		_previousPerceptionState = Perception.State;

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

		if (ShowDebugBoard)
		{
			BuildAiPixelGrid(playerPos, walkableGrid);
			RefreshAiGridVisualization();
		}
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
		// Adaptive stealth countermeasure (docs/ENEMY_AI_ADAPTATION_PLAN.md
		// sections 7 and 11): only enemies that actually received the
		// SearchLastKnownPosition order deviate from the baseline behavior
		// below, and only while they've genuinely lost the player — an
		// enemy currently Tracking (has line of sight) still just chases
		// normally. This keeps every enemy without an active order, and
		// every enemy that still sees the player, exactly as before.
		if (TacticalOrder.SearchLastKnownPosition
			&& Perception.State is PerceptionState.Investigating or PerceptionState.Searching)
		{
			Vector2I investigateStep = ChooseInvestigateOrSearchStep(walkableGrid);
			if (investigateStep != Vector2I.Zero)
			{
				return investigateStep;
			}
		}

		int distanceX = playerPos.X - GridPos.X;
		int distanceY = playerPos.Y - GridPos.Y;
		int manhattanDistance = Math.Abs(distanceX) + Math.Abs(distanceY);
		int attackDistance = GetAttackDistance();

		if (_attackCooldown <= 0.0 && manhattanDistance <= attackDistance)
		{
			ExecuteAttack(playerPos);
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
			if (manhattanDistance < KiteRetreatDistance)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
			}

			if (manhattanDistance > KiteAdvanceDistance)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
			}

			return FindStrafeStep(playerPos, walkableGrid, directions);
		}

		if (BehaviorType == "ranged_strafe")
		{
			if (manhattanDistance > StrafeAdvanceDistance)
			{
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
			}

			if (manhattanDistance < StrafeRetreatDistance)
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
				if (IsWalkable(candidate, walkableGrid) && candidate != playerPos)
				{
					return direction;
				}
			}
		}

		return Vector2I.Zero;
	}

	// Routes toward the last place the player was seen (Investigating), or
	// once there without success, toward a nearby unsearched cell
	// (Searching) — the pathfinding investigate/search behavior from
	// docs/ENEMY_AI_ADAPTATION_PLAN.md sections 7, 11, and 12. BFS is
	// recomputed each call rather than cached: it only runs for the
	// (typically few) enemies actively investigating, at their normal
	// movement cadence, which is cheap enough not to need caching.
	private Vector2I ChooseInvestigateOrSearchStep(int[,] walkableGrid)
	{
		GridPoint here = ToGridPoint(GridPos);
		GridPoint target = Perception.State == PerceptionState.Investigating
			? Perception.LastKnownPlayerPosition
			: PickSearchTarget(walkableGrid);

		if (target == here)
		{
			if (Perception.State == PerceptionState.Searching)
			{
				Perception.MarkCellSearched(here);
			}
			return Vector2I.Zero;
		}

		GridPoint stepDirection = GridPathfinder.FindNextStepDirection(here, target, walkableGrid);
		return ToVector2I(stepDirection);
	}

	private GridPoint PickSearchTarget(int[,] walkableGrid)
	{
		GridPoint center = Perception.LastKnownPlayerPosition;
		int radius = Math.Max(1, Mathf.RoundToInt(TacticalOrder.SearchRadius));

		for (int attempt = 0; attempt < 8; attempt++)
		{
			int offsetX = _searchRng.RandiRange(-radius, radius);
			int offsetY = _searchRng.RandiRange(-radius, radius);
			var candidate = new GridPoint(center.X + offsetX, center.Y + offsetY);

			if (Perception.SearchedCells.Contains(candidate) || !IsWalkable(ToVector2I(candidate), walkableGrid))
			{
				continue;
			}

			return candidate;
		}

		// Nothing new nearby — fall back to the last known position itself
		// so the enemy at least ends up somewhere sensible.
		return center;
	}

	/// <summary>Reads and clears the "line of sight lost this frame" flag — call once per frame from GameManager.</summary>
	public bool ConsumeLineOfSightLostFlag()
	{
		bool value = _lineOfSightJustLost;
		_lineOfSightJustLost = false;
		return value;
	}

	private static GridPoint ToGridPoint(Vector2I v) => new(v.X, v.Y);

	private static Vector2I ToVector2I(GridPoint p) => new(p.X, p.Y);

	private bool IsRanged => BehaviorType.StartsWith("ranged", StringComparison.Ordinal);

	private int GetAttackDistance()
	{
		return IsRanged ? Stats.AttackRange : Stats.AttackRange + MeleeEngageBuffer;
	}

	private void ExecuteAttack(Vector2I playerPos)
	{
		EmitSignal(SignalName.EnemyAttackedPlayer, Stats.Attack);
		_attackCooldown = AttackCooldownSeconds;
		PlayAttackFx(playerPos);

		if (BehaviorType is "melee_hit_run" or "ranged_kite")
		{
			_postAttackRetreat = true;
			_postAttackSteps = PostAttackRetreatSteps;
		}
		else if (BehaviorType == "ranged_strafe")
		{
			_postAttackRetreat = false;
			_postAttackSteps = PostAttackStrafeSteps;
			_strafeSign *= -1;
		}
	}

	private void PlayAttackFx(Vector2I playerPos)
	{
		if (IsRanged)
		{
			PlayRangedAttackFx(playerPos);
		}
		else
		{
			PlayMeleeAttackFx(playerPos);
		}
	}

	private void PlayMeleeAttackFx(Vector2I playerPos)
	{
		Vector2I toPlayer = playerPos - GridPos;
		Vector3 direction = new Vector3(toPlayer.X, 0, toPlayer.Y);
		if (direction.LengthSquared() > 0.0001f)
		{
			direction = direction.Normalized();
		}

		Vector3 lungeOffset = direction * 0.32f;

		Tween tween = CreateTween();
		tween.TweenProperty(_body, "position", lungeOffset, 0.08).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(_body, "position", Vector3.Zero, 0.12).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
	}

	private void PlayRangedAttackFx(Vector2I playerPos)
	{
		Node? parent = GetParent();
		if (parent == null)
		{
			return;
		}

		Vector3 start = Position + new Vector3(0, 0.15f, 0);
		Vector3 end = new Vector3(playerPos.X + WorldOffset.X, 0.5f, playerPos.Y + WorldOffset.Y);
		Color color = GetClassColor();

		var projectile = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f },
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = color,
				EmissionEnabled = true,
				Emission = color,
				EmissionEnergyMultiplier = 2.0f
			},
			Position = start
		};

		parent.AddChild(projectile);

		Tween tween = projectile.CreateTween();
		tween.TweenProperty(projectile, "position", end, 0.18).SetTrans(Tween.TransitionType.Linear);
		tween.TweenCallback(Callable.From(projectile.QueueFree));
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
			if (!IsWalkable(candidate, walkableGrid) || candidate == playerPos)
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
			if (IsWalkable(candidate, walkableGrid) && candidate != playerPos)
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
		_bodyMaterial = new StandardMaterial3D
		{
			AlbedoColor = GetClassColor(),
			Metallic = 0.15f,
			Roughness = 0.45f
		};

		_body = new MeshInstance3D
		{
			Mesh = new CapsuleMesh
			{
				Radius = 0.26f,
				Height = 0.85f
			},
			MaterialOverride = _bodyMaterial
		};

		AddChild(_body);

		if (!ShowDebugBoard)
		{
			return;
		}

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

	private Color GetHealthTintedColor()
	{
		float ratio = MaxHp > 0 ? Mathf.Clamp((float)CurrentHp / MaxHp, 0f, 1f) : 0f;
		return GetClassColor().Lerp(Colors.White, (1f - ratio) * 0.9f);
	}

	private void PlayHitFlash()
	{
		_flashTween?.Kill();
		_bodyMaterial.AlbedoColor = Colors.White;

		_flashTween = CreateTween();
		_flashTween.TweenProperty(_bodyMaterial, "albedo_color", GetHealthTintedColor(), 0.15).SetDelay(0.03);
	}

	private void PlayDeathAndFree()
	{
		_isDying = true;

		Tween tween = CreateTween();
		tween.TweenProperty(this, "scale", Vector3.Zero, 0.25).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
		tween.TweenCallback(Callable.From(QueueFree));
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
