using System;
using System.Collections.Generic;
using Godot;
using DungeonCrawler.AI;
using DungeonCrawler.Data;

namespace DungeonCrawler.Entities;

/// <summary>
/// One enemy: movement, perception, the attack state machine (wind-up,
/// strike or telegraphed attack, recovery) and damage intake. Combat
/// numbers come from <see cref="EnemyArchetypes"/>; the look and animation
/// live in <see cref="EnemyVisuals"/> and <see cref="TelegraphFx"/>.
/// See docs/ENEMY_AI.md.
/// </summary>
public partial class EnemyController : Node3D
{
	[Signal] public delegate void EnemyAttackedPlayerEventHandler(int damage);
	[Signal] public delegate void EnemyDiedEventHandler(int rewardScore);

	/// <summary>Raised when a telegraphed attack (charge or blast) starts; carries the tiles it will hit.</summary>
	public event Action<EnemyController, IReadOnlyCollection<Vector2I>>? TelegraphStarted;

	/// <summary>Raised when any attack resolves: whether it hit, and whether it was telegraphed.</summary>
	public event Action<EnemyController, bool, bool>? AttackResolved;

	public readonly record struct HitResult(int Damage, bool Blocked, bool Killed);

	public Vector2I GridPos { get; private set; }
	public string BehaviorType { get; private set; } = "patrol";
	public EnemyClassDefinition ClassDefinition { get; private set; } = EnemyClassDefinition.CreateDefault();
	public ArchetypeDefinition Archetype { get; private set; } = EnemyArchetypes.Get(EnemyArchetypes.MeleeChaser);
	public bool IsElite { get; private set; }
	public EnemyStats Stats => ClassDefinition.Stats;
	public int[,] AiPixelGrid { get; private set; } = new int[8, 8];
	public int CurrentHp { get; private set; }
	public int MaxHp => Stats.MaxHp;
	public Vector2 WorldOffset { get; set; } = Vector2.Zero;
	/// <summary>Cardinal facing on the grid (drives the model's rotation and the shieldbearer's block).</summary>
	public Vector2I Facing { get; private set; } = Vector2I.Down;
	public bool IsStunned => _phase == AttackPhase.Recovering;
	/// <summary>The enemy's model and animations (the gallery scene poses them directly).</summary>
	public EnemyVisuals Visuals => _visuals;
	/// <summary>Tiles of the telegraphed attack in progress (empty when none).</summary>
	public IReadOnlyCollection<Vector2I> ActiveTelegraphCells => _telegraphCells;
	public double TelegraphTimeRemaining => _phase == AttackPhase.WindingUp ? _phaseTimer : 0.0;
	/// <summary>Whether another enemy stands on a tile; set by the owner so dashes stop before allies.</summary>
	public Func<Vector2I, bool>? IsTileOccupied { get; set; }

	// The 8x8 debug board (ENEMY_AI.md section 5) is 66 extra MeshInstance3D
	// nodes and materials per enemy — fine for the 2-enemy AI test scenes it
	// was built for, ruinous for the main dungeon's spawns. Must be set
	// before Initialize() — BuildVisuals reads it once, at construction.
	public bool ShowDebugBoard { get; set; } = true;

	// --- Adaptive AI (docs/ENEMY_AI_ADAPTATION_PLAN.md) ---
	// Perception is tracked for every enemy regardless of whether it has an
	// active tactical order. Only enemies that received an order from the
	// AdaptationDirector let that belief state change their movement (see
	// ChooseNextStep); a baseline enemy with no order behaves as before.
	public EnemyPerception Perception { get; } = new();
	public EnemyTacticalOrder TacticalOrder { get; set; } = EnemyTacticalOrder.None();

	private enum AttackPhase { Ready, WindingUp, Recovering }

	private const float FloorTopY = 0.36f;
	private const int AiGridSize = 8;
	private const int MeleeEngageBuffer = 1;      // melee_chaser / melee_hit_run strike from this many tiles beyond AttackRange
	private const int PostAttackRetreatSteps = 2; // melee_hit_run / ranged_kite: steps to disengage after an attack
	private const int PostAttackStrafeSteps = 2;  // ranged_strafe / ranged_bomber: steps to strafe after an attack
	private const float StunnedDamageMultiplier = 1.5f; // a stunned charger takes extra damage
	private const double DeathSeconds = 0.45;

	private readonly RandomNumberGenerator _searchRng = new();
	private readonly List<MeshInstance3D> _pixelCells = new();
	private readonly List<Vector2I> _telegraphCells = new();
	private PerceptionState _previousPerceptionState = PerceptionState.Unaware;
	private bool _lineOfSightJustLost;
	// The intercept countermeasure: where to wait for the player, and for how
	// much longer (set when the enemy loses sight of the player).
	private const double InterceptHoldSeconds = 20.0;
	private GridPoint _interceptPoint;
	private double _interceptTimeLeft;

	/// <summary>Intercept points planned by all enemies since the last reset (logged per trial).</summary>
	public static int InterceptsPlanned;

	/// <summary>Where the player is heading (the exit); null when there is none (duel arena).</summary>
	public GridPoint? RouteGoal { get; set; }
	private EnemyVisuals _visuals = null!;
	private Node3D _nnWallRoot = null!;
	private TelegraphFx? _telegraph;
	private MeshInstance3D? _bomb;
	private Tween? _moveTween;
	private bool _isDying;
	private double _aiTimer;
	private double _attackCooldown;
	private double _turnTimer;
	private AttackPhase _phase = AttackPhase.Ready;
	private double _phaseTimer;
	private double _phaseDuration;
	private Vector2I _chargeDirection;
	private Vector2I _blastCenter;
	private int _postAttackSteps;
	private bool _postAttackRetreat;
	private int _strafeSign = 1;
	// The player's recent movement, so the bomber can lead its throw.
	private double _clock;
	private Vector2I _trackedPlayerPos = new(int.MinValue, int.MinValue);
	private Vector2I _playerMoveDirection;
	private double _playerLastMoveTime = double.NegativeInfinity;
	private double _playerStepInterval = 1.0;
	private const int MaxBlastLead = 3;

	/// <summary>Spawns an archetype from the catalog with the encounter's modifiers already applied.</summary>
	public void Initialize(Vector2I startGridPos, ArchetypeDefinition archetype, bool elite)
	{
		Archetype = archetype;
		IsElite = elite;
		BehaviorType = archetype.Behavior;
		ClassDefinition = EnemyClassDefinition.Create(archetype.ClassId, elite ? $"Elite {archetype.DisplayName}" : archetype.DisplayName,
			archetype.Behavior, new EnemyStats
			{
				MaxHp = archetype.MaxHp,
				Speed = archetype.Speed,
				Attack = archetype.Attack,
				Defense = archetype.Defense,
				VisionRange = archetype.VisionRange,
				AttackRange = archetype.AttackRange,
				Reward = elite ? archetype.Reward * 2 : archetype.Reward
			});
		Setup(startGridPos);
	}

	/// <summary>
	/// Spawns with explicit stats (the AI test scenes use equal stats for
	/// every archetype); the attack pattern and timings come from the catalog.
	/// </summary>
	public void Initialize(Vector2I startGridPos, string behaviorType, EnemyClassDefinition? definition = null)
	{
		BehaviorType = behaviorType;
		ClassDefinition = definition ?? EnemyClassDefinition.CreateDefault(behaviorType);
		ArchetypeDefinition catalog = EnemyArchetypes.Get(behaviorType);
		Archetype = catalog with
		{
			MaxHp = Stats.MaxHp,
			Attack = Stats.Attack,
			Defense = Stats.Defense,
			Speed = Stats.Speed,
			VisionRange = Stats.VisionRange,
			// Melee reach comes from the given stats; ranged archetypes keep
			// their catalog range, so they still behave as ranged enemies.
			AttackRange = catalog.Pattern is AttackPattern.Melee or AttackPattern.ShieldBash ? Math.Max(1, Stats.AttackRange) : catalog.AttackRange
		};
		Setup(startGridPos);
	}

	private void Setup(Vector2I startGridPos)
	{
		GridPos = startGridPos;
		CurrentHp = Stats.MaxHp;
		_searchRng.Seed = (ulong)((startGridPos.X * 73856093) ^ (startGridPos.Y * 19349663)) + 1;
		BuildVisuals();
		UpdatePosition(animate: false);
		if (ShowDebugBoard)
		{
			RefreshAiGridVisualization();
		}
	}

	public HitResult TakeDamage(int rawDamage, AttackDirection direction = AttackDirection.Front, Vector2I? attackerPos = null)
	{
		if (CurrentHp <= 0 || _isDying)
		{
			return new HitResult(0, false, false);
		}

		float damage = Math.Max(1, rawDamage - Stats.Defense);
		bool blocked = false;
		if (Archetype.Pattern == AttackPattern.ShieldBash && attackerPos is { } from && _phase != AttackPhase.Recovering
			&& AttackGeometry.IsInFrontArc(ToGridPoint(Facing), ToGridPoint(from - GridPos)))
		{
			damage *= Archetype.FrontDamageFactor;
			blocked = true;
		}
		else if (_phase == AttackPhase.Recovering && Archetype.Pattern == AttackPattern.Charge)
		{
			damage *= StunnedDamageMultiplier;
		}

		int dealt = Math.Max(1, (int)Math.Round(damage));
		CurrentHp = Math.Max(0, CurrentHp - dealt);

		Vector3 away = attackerPos is { } source ? new Vector3(GridPos.X - source.X, 0, GridPos.Y - source.Y) : Vector3.Zero;
		_visuals.PlayHit(away, blocked);
		_visuals.SetHealth(CurrentHp, MaxHp);
		Node? parent = GetParent();
		if (parent != null)
		{
			TelegraphFx.FloatingText(parent, Position, blocked ? $"BLOCK {dealt}" : dealt.ToString(),
				blocked ? Color.FromHtml("#bfdbfe") : Color.FromHtml("#fef3c7"));
		}

		if (IsElite && CurrentHp > 0 && CurrentHp <= MaxHp * EnemyArchetypes.EliteEnrageHealthFraction)
		{
			_visuals.SetEnraged(true);
		}

		// Rear-defense countermeasure (docs/ENEMY_AI_ADAPTATION_PLAN.md
		// section 10): a rear hit while GuardRear is active cuts the
		// remaining attack cooldown — a faster re-engage.
		if (TacticalOrder.GuardRear && direction == AttackDirection.Rear && CurrentHp > 0)
		{
			_attackCooldown = Math.Max(0.0, _attackCooldown / Math.Max(1f, TacticalOrder.TurnSpeedMultiplier));
		}

		bool killed = CurrentHp <= 0;
		if (killed)
		{
			EmitSignal(SignalName.EnemyDied, Stats.Reward);
			PlayDeathAndFree();
		}

		return new HitResult(dealt, blocked, killed);
	}

	public void Tick(double delta, Vector2I playerPos, int[,] walkableGrid)
	{
		if (CurrentHp <= 0 || _isDying)
		{
			return;
		}

		_visuals.Process(delta);
		TrackPlayerMovement(delta, playerPos);

		// Perception updates every frame, independent of the movement step.
		int manhattanDistance = Math.Abs(playerPos.X - GridPos.X) + Math.Abs(playerPos.Y - GridPos.Y);
		// Cheap distance check before the O(distance) line trace: only a
		// handful of enemies are ever within VisionRange of the player.
		bool withinVisionRange = manhattanDistance <= Stats.VisionRange;
		bool hasLineOfSight = withinVisionRange && GridLineOfSight.HasLineOfSight(ToGridPoint(GridPos), ToGridPoint(playerPos), walkableGrid);
		Perception.Update(delta, hasLineOfSight, ToGridPoint(playerPos), manhattanDistance, Stats.VisionRange);

		if (_previousPerceptionState == PerceptionState.Tracking && Perception.State != PerceptionState.Tracking)
		{
			_lineOfSightJustLost = true;
			if (TacticalOrder.InterceptRoute && RouteGoal is GridPoint goal)
			{
				_interceptPoint = InterceptPlanner.ChooseInterceptPoint(ToGridPoint(GridPos),
					Perception.LastKnownPlayerPosition, goal, walkableGrid);
				_interceptTimeLeft = InterceptHoldSeconds;
				InterceptsPlanned++;
			}
		}
		if (Perception.State == PerceptionState.Tracking || !TacticalOrder.InterceptRoute)
		{
			_interceptTimeLeft = 0.0;
		}
		else
		{
			_interceptTimeLeft = Math.Max(0.0, _interceptTimeLeft - delta);
		}
		_previousPerceptionState = Perception.State;

		// Elites speed up once below half health; every timer runs faster.
		double paced = delta * (IsElite && CurrentHp <= MaxHp * EnemyArchetypes.EliteEnrageHealthFraction ? EnemyArchetypes.EliteEnrageTempo : 1.0);
		_attackCooldown = Math.Max(0.0, _attackCooldown - paced);

		switch (_phase)
		{
			case AttackPhase.WindingUp:
				_phaseTimer -= paced;
				float progress = _phaseDuration > 0 ? (float)(1.0 - (_phaseTimer / _phaseDuration)) : 1f;
				_visuals.SetWindup(progress);
				_telegraph?.SetProgress(progress, delta);
				FaceToward(playerPos);
				if (_phaseTimer <= 0)
				{
					ResolveAttack(playerPos, walkableGrid);
				}
				return;
			case AttackPhase.Recovering:
				_phaseTimer -= paced;
				if (_phaseTimer <= 0)
				{
					_phase = AttackPhase.Ready;
					_visuals.SetStunned(false);
				}
				return;
		}

		if (_attackCooldown <= 0.0 && TryStartAttack(playerPos, walkableGrid, manhattanDistance, hasLineOfSight))
		{
			return;
		}

		UpdateIdleFacing(paced, playerPos, withinVisionRange);

		_aiTimer += paced;
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
			Facing = nextStep;
			_visuals.FaceDirection(nextStep);
			UpdatePosition(animate: true, Math.Min(0.18, stepDelay * 0.7));
		}

		if (ShowDebugBoard)
		{
			BuildAiPixelGrid(playerPos, walkableGrid);
			RefreshAiGridVisualization();
		}
	}

	// --- Attacks ---------------------------------------------------------------

	private bool TryStartAttack(Vector2I playerPos, int[,] walkableGrid, int manhattanDistance, bool hasLineOfSight)
	{
		switch (Archetype.Pattern)
		{
			case AttackPattern.Melee:
				if (manhattanDistance > Archetype.AttackRange + MeleeEngageBuffer)
				{
					return false;
				}
				break;
			case AttackPattern.ShieldBash:
				// Bashes only with the shield toward the player.
				if (manhattanDistance > Archetype.AttackRange
					|| !AttackGeometry.IsInFrontArc(ToGridPoint(Facing), ToGridPoint(playerPos - GridPos)))
				{
					return false;
				}
				break;
			case AttackPattern.Ranged:
				if (manhattanDistance > Archetype.AttackRange || !hasLineOfSight)
				{
					return false;
				}
				break;
			case AttackPattern.Charge:
			{
				GridPoint direction = AttackGeometry.ChargeDirection(ToGridPoint(GridPos), ToGridPoint(playerPos), Archetype.AttackRange, walkableGrid);
				if (direction == GridPoint.Zero)
				{
					return false;
				}

				_chargeDirection = ToVector2I(direction);
				_telegraphCells.Clear();
				foreach (GridPoint cell in AttackGeometry.ChargePath(ToGridPoint(GridPos), direction, Archetype.AttackRange, walkableGrid, IsBlockedByAlly))
				{
					_telegraphCells.Add(ToVector2I(cell));
				}
				break;
			}
			case AttackPattern.Blast:
				if (manhattanDistance > Archetype.AttackRange || !hasLineOfSight)
				{
					return false;
				}

				_blastCenter = PredictPlayerTile(playerPos, Archetype.WindupSeconds, walkableGrid);
				_telegraphCells.Clear();
				foreach (GridPoint cell in AttackGeometry.BlastArea(ToGridPoint(_blastCenter), Archetype.AreaRadius, walkableGrid))
				{
					_telegraphCells.Add(ToVector2I(cell));
				}
				break;
		}

		_phase = AttackPhase.WindingUp;
		_phaseDuration = Archetype.WindupSeconds;
		_phaseTimer = Archetype.WindupSeconds;
		// The cooldown runs from the start of the wind-up, so it is the
		// archetype's attack period.
		_attackCooldown = Archetype.CooldownSeconds;
		FaceToward(playerPos);

		if (_telegraphCells.Count > 0)
		{
			Node? parent = GetParent();
			if (parent != null)
			{
				var centres = new List<Vector3>(_telegraphCells.Count);
				foreach (Vector2I cell in _telegraphCells)
				{
					centres.Add(CellToWorld(cell));
				}

				_telegraph = TelegraphFx.Create(parent, centres, Archetype.Pattern == AttackPattern.Blast
					? Color.FromHtml("#f97316") : Color.FromHtml("#ef4444"));
				if (Archetype.Pattern == AttackPattern.Blast)
				{
					LobBomb(parent, CellToWorld(_blastCenter), Archetype.WindupSeconds);
				}
			}

			TelegraphStarted?.Invoke(this, _telegraphCells);
		}

		return true;
	}

	private void ResolveAttack(Vector2I playerPos, int[,] walkableGrid)
	{
		_visuals.SetWindup(0f);
		_phase = AttackPhase.Ready;
		int manhattanDistance = Math.Abs(playerPos.X - GridPos.X) + Math.Abs(playerPos.Y - GridPos.Y);
		bool hit = false;
		bool telegraphed = false;

		switch (Archetype.Pattern)
		{
			case AttackPattern.Melee:
				hit = manhattanDistance <= Archetype.AttackRange + MeleeEngageBuffer;
				_visuals.PlayLunge(DirectionTo(playerPos), 0.32f);
				if (BehaviorType == EnemyArchetypes.MeleeHitRun)
				{
					_postAttackRetreat = true;
					_postAttackSteps = PostAttackRetreatSteps;
				}
				break;
			case AttackPattern.ShieldBash:
				hit = manhattanDistance <= Archetype.AttackRange;
				_visuals.PlayLunge(DirectionTo(playerPos), 0.4f);
				break;
			case AttackPattern.Ranged:
				hit = manhattanDistance <= Archetype.AttackRange
					&& GridLineOfSight.HasLineOfSight(ToGridPoint(GridPos), ToGridPoint(playerPos), walkableGrid);
				if (hit)
				{
					PlayProjectile(playerPos);
				}

				if (BehaviorType == EnemyArchetypes.RangedKite)
				{
					_postAttackRetreat = true;
					_postAttackSteps = PostAttackRetreatSteps;
				}
				else
				{
					_postAttackRetreat = false;
					_postAttackSteps = PostAttackStrafeSteps;
					_strafeSign *= -1;
				}
				break;
			case AttackPattern.Charge:
			{
				telegraphed = true;
				List<GridPoint> path = AttackGeometry.ChargePath(ToGridPoint(GridPos), ToGridPoint(_chargeDirection), Archetype.AttackRange,
					walkableGrid, IsBlockedByAlly);
				hit = path.Contains(ToGridPoint(playerPos));
				GridPos = ToVector2I(AttackGeometry.ChargeEnd(ToGridPoint(GridPos), path, ToGridPoint(playerPos)));
				Facing = _chargeDirection;
				UpdatePosition(animate: true, 0.12);
				_phase = AttackPhase.Recovering;
				_phaseTimer = Archetype.RecoverySeconds;
				_phaseDuration = Archetype.RecoverySeconds;
				_visuals.SetStunned(true);
				break;
			}
			case AttackPattern.Blast:
				telegraphed = true;
				hit = AttackGeometry.BlastContains(ToGridPoint(_blastCenter), Archetype.AreaRadius, ToGridPoint(playerPos));
				Node? parent = GetParent();
				if (parent != null)
				{
					TelegraphFx.Explosion(parent, CellToWorld(_blastCenter), Archetype.AreaRadius, Color.FromHtml("#fdba74"));
				}

				_postAttackRetreat = false;
				_postAttackSteps = 1;
				_strafeSign *= -1;
				break;
		}

		_telegraph?.Release();
		_telegraph = null;
		_telegraphCells.Clear();

		if (hit)
		{
			EmitSignal(SignalName.EnemyAttackedPlayer, Stats.Attack);
		}

		AttackResolved?.Invoke(this, hit, telegraphed);
	}

	private void TrackPlayerMovement(double delta, Vector2I playerPos)
	{
		_clock += delta;
		if (_trackedPlayerPos.X == int.MinValue)
		{
			_trackedPlayerPos = playerPos;
			return;
		}

		if (playerPos == _trackedPlayerPos)
		{
			return;
		}

		Vector2I step = playerPos - _trackedPlayerPos;
		if (Math.Abs(step.X) + Math.Abs(step.Y) == 1)
		{
			_playerStepInterval = Math.Clamp(_clock - _playerLastMoveTime, 0.05, 2.0);
			_playerMoveDirection = step;
		}
		else
		{
			_playerMoveDirection = Vector2I.Zero; // respawned or teleported
		}

		_playerLastMoveTime = _clock;
		_trackedPlayerPos = playerPos;
	}

	/// <summary>
	/// Where a moving player will be once the wind-up ends: the current tile
	/// pushed along their last step direction, by as many tiles as they cover
	/// in that time (at most MaxBlastLead), stopping at walls. A player who
	/// has stood still for a moment is aimed at directly.
	/// </summary>
	private Vector2I PredictPlayerTile(Vector2I playerPos, double seconds, int[,] walkableGrid)
	{
		bool movingNow = _clock - _playerLastMoveTime <= Math.Max(0.3, _playerStepInterval * 1.5);
		if (!movingNow || _playerMoveDirection == Vector2I.Zero)
		{
			return playerPos;
		}

		int lead = Math.Min(MaxBlastLead, (int)Math.Round(seconds / _playerStepInterval));
		Vector2I predicted = playerPos;
		for (int i = 0; i < lead; i++)
		{
			Vector2I next = predicted + _playerMoveDirection;
			if (!IsWalkable(next, walkableGrid))
			{
				break;
			}

			predicted = next;
		}

		return predicted;
	}

	private bool IsBlockedByAlly(GridPoint cell) => IsTileOccupied?.Invoke(ToVector2I(cell)) ?? false;

	private void UpdateIdleFacing(double paced, Vector2I playerPos, bool withinVisionRange)
	{
		if (!withinVisionRange)
		{
			return;
		}

		if (Archetype.Pattern != AttackPattern.ShieldBash)
		{
			FaceToward(playerPos);
			return;
		}

		// The shieldbearer turns slowly, one quarter turn at a time, which is
		// what lets a player work around the shield.
		_turnTimer += paced;
		if (_turnTimer >= Archetype.TurnSeconds)
		{
			_turnTimer = 0;
			Facing = ToVector2I(AttackGeometry.TurnToward(ToGridPoint(Facing), ToGridPoint(playerPos - GridPos)));
			_visuals.FaceDirection(Facing);
		}
	}

	private void FaceToward(Vector2I playerPos)
	{
		if (Archetype.Pattern == AttackPattern.ShieldBash)
		{
			return; // turns only at its own pace (UpdateIdleFacing)
		}

		Vector2I direction = ToVector2I(AttackGeometry.DominantDirection(ToGridPoint(playerPos - GridPos)));
		if (direction != Vector2I.Zero)
		{
			Facing = direction;
			_visuals.FaceDirection(direction);
		}
	}

	// --- Movement ----------------------------------------------------------------

	public Vector2I ChooseNextStep(Vector2I playerPos, int[,] walkableGrid)
	{
		// Adaptive stealth countermeasure (docs/ENEMY_AI_ADAPTATION_PLAN.md
		// sections 7 and 11): only enemies with the SearchLastKnownPosition
		// order deviate, and only while they have lost the player.
		// Intercept: go to the cell ahead of the player on their route to the
		// exit, then wait there in ambush until the player shows up or the
		// hold expires.
		if (_interceptTimeLeft > 0.0)
		{
			GridPoint here = ToGridPoint(GridPos);
			return here == _interceptPoint
				? Vector2I.Zero
				: ToVector2I(GridPathfinder.FindNextStepDirection(here, _interceptPoint, walkableGrid));
		}

		if (TacticalOrder.SearchLastKnownPosition
			&& Perception.State is PerceptionState.Investigating or PerceptionState.Searching)
		{
			Vector2I investigateStep = ChooseInvestigateOrSearchStep(walkableGrid);
			if (investigateStep != Vector2I.Zero)
			{
				return investigateStep;
			}
		}

		int manhattanDistance = Math.Abs(playerPos.X - GridPos.X) + Math.Abs(playerPos.Y - GridPos.Y);
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

		switch (BehaviorType)
		{
			case EnemyArchetypes.MeleeChaser or "ambush" or EnemyArchetypes.MeleeShield:
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
			case EnemyArchetypes.MeleeHitRun:
				// Hover just outside reach; the strike comes when the player closes in.
				if (manhattanDistance <= Archetype.AttackRange + MeleeEngageBuffer + 1)
				{
					return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
				}
				return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
			case EnemyArchetypes.MeleeCharger:
			{
				// Line up on the player's row or column, then close in.
				Vector2I align = ToVector2I(AttackGeometry.AlignStep(ToGridPoint(GridPos), ToGridPoint(playerPos)));
				if (align != Vector2I.Zero && manhattanDistance <= Archetype.AttackRange + 3 && CanStep(GridPos + align, playerPos, walkableGrid))
				{
					return align;
				}
				return manhattanDistance > 2 ? FindBestStep(playerPos, walkableGrid, directions, preferCloser: true) : Vector2I.Zero;
			}
			case EnemyArchetypes.RangedKite or EnemyArchetypes.RangedStrafe or EnemyArchetypes.RangedBomber:
				if (manhattanDistance < Archetype.PreferredMinDistance)
				{
					return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
				}

				if (manhattanDistance > Archetype.PreferredMaxDistance)
				{
					return FindBestStep(playerPos, walkableGrid, directions, preferCloser: true);
				}

				return FindStrafeStep(playerPos, walkableGrid, directions);
			case "patrol":
				foreach (Vector2I direction in directions)
				{
					if (CanStep(GridPos + direction, playerPos, walkableGrid))
					{
						return direction;
					}
				}
				break;
		}

		return Vector2I.Zero;
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

	// Routes toward the last place the player was seen (Investigating), or
	// once there without success, toward a nearby unsearched cell
	// (Searching) — docs/ENEMY_AI_ADAPTATION_PLAN.md sections 7, 11, 12.
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

	private bool CanStep(Vector2I candidate, Vector2I playerPos, int[,] walkableGrid) =>
		IsWalkable(candidate, walkableGrid) && candidate != playerPos;

	private Vector2I FindBestStep(Vector2I playerPos, int[,] walkableGrid, Vector2I[] directions, bool preferCloser)
	{
		int currentDistance = Math.Abs(playerPos.X - GridPos.X) + Math.Abs(playerPos.Y - GridPos.Y);
		int bestDistance = preferCloser ? int.MaxValue : int.MinValue;
		Vector2I bestDirection = Vector2I.Zero;

		foreach (Vector2I direction in directions)
		{
			Vector2I candidate = new(GridPos.X + direction.X, GridPos.Y + direction.Y);
			if (!CanStep(candidate, playerPos, walkableGrid))
			{
				continue;
			}

			int distance = Math.Abs(playerPos.X - candidate.X) + Math.Abs(playerPos.Y - candidate.Y);
			if (preferCloser ? distance < bestDistance : distance > bestDistance)
			{
				bestDistance = distance;
				bestDirection = direction;
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
		foreach (Vector2I direction in new[] { primary, secondary })
		{
			if (direction != Vector2I.Zero && CanStep(GridPos + direction, playerPos, walkableGrid))
			{
				return direction;
			}
		}

		return FindBestStep(playerPos, walkableGrid, directions, preferCloser: false);
	}

	private static bool IsWalkable(Vector2I targetPos, int[,] walkableGrid)
	{
		if (targetPos.X < 0 || targetPos.Y < 0 || targetPos.X >= walkableGrid.GetLength(1) || targetPos.Y >= walkableGrid.GetLength(0))
		{
			return false;
		}

		return walkableGrid[targetPos.Y, targetPos.X] != 0;
	}

	// --- Visuals ---------------------------------------------------------------------

	private void BuildVisuals()
	{
		_visuals = new EnemyVisuals();
		AddChild(_visuals);
		_visuals.Build(BehaviorType, IsElite, (GridPos.X * 31) + GridPos.Y);
		_visuals.FaceDirection(Facing);

		if (!ShowDebugBoard)
		{
			return;
		}

		_nnWallRoot = new Node3D
		{
			Position = new Vector3(0, 1.4f, -0.9f)
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

	private Vector3 DirectionTo(Vector2I playerPos) => new(playerPos.X - GridPos.X, 0, playerPos.Y - GridPos.Y);

	private Vector3 CellToWorld(Vector2I cell) => new(cell.X + WorldOffset.X, FloorTopY, cell.Y + WorldOffset.Y);

	private void PlayProjectile(Vector2I playerPos)
	{
		Node? parent = GetParent();
		if (parent == null)
		{
			return;
		}

		Color color = _visuals.ClassColor;
		var projectile = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f },
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = color,
				EmissionEnabled = true,
				Emission = color,
				EmissionEnergyMultiplier = 2.5f
			},
			Position = Position + new Vector3(0, 0.5f, 0),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		parent.AddChild(projectile);

		Tween tween = projectile.CreateTween();
		tween.TweenProperty(projectile, "position", CellToWorld(playerPos) + new Vector3(0, 0.5f, 0), 0.16).SetTrans(Tween.TransitionType.Linear);
		tween.TweenCallback(Callable.From(projectile.QueueFree));
	}

	// The bomber's bomb arcs from its hands to the marked tile over the wind-up.
	private void LobBomb(Node parent, Vector3 target, double seconds)
	{
		if (_bomb != null && IsInstanceValid(_bomb))
		{
			_bomb.QueueFree();
		}

		Vector3 start = Position + new Vector3(0, 0.7f, 0);
		var bomb = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f },
			MaterialOverride = EnemyVisuals.Shared("#1c1917", 0f, 0.4f),
			Position = start,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
		};
		parent.AddChild(bomb);
		_bomb = bomb;
		Tween tween = bomb.CreateTween();
		tween.TweenMethod(Callable.From<float>(t =>
		{
			Vector3 flat = start.Lerp(target, t);
			bomb.Position = new Vector3(flat.X, Mathf.Lerp(start.Y, target.Y + 0.15f, t) + (2.2f * t * (1f - t)), flat.Z);
		}), 0f, 1f, Math.Max(0.05, seconds));
		tween.TweenCallback(Callable.From(bomb.QueueFree));
	}

	private void PlayDeathAndFree()
	{
		_isDying = true;
		_telegraph?.Release();
		_telegraph = null;
		_telegraphCells.Clear();
		if (_bomb != null && IsInstanceValid(_bomb))
		{
			_bomb.QueueFree();
		}

		_bomb = null;
		_visuals.PlayDeath(DeathSeconds);

		Tween tween = CreateTween();
		tween.TweenInterval(DeathSeconds);
		tween.TweenCallback(Callable.From(QueueFree));
	}

	private void UpdatePosition(bool animate, double duration = 0.15)
	{
		Vector3 target = CellToWorld(GridPos);
		_moveTween?.Kill();
		if (!animate || !IsInsideTree())
		{
			Position = target;
			return;
		}

		_moveTween = CreateTween();
		_moveTween.TweenProperty(this, "position", target, duration).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
	}
}
