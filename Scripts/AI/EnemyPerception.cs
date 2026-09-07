using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Perception states an enemy moves through, replacing perfect knowledge
/// of the player's current position. See
/// docs/ENEMY_AI_ADAPTATION_PLAN.md section 7.
/// </summary>
public enum PerceptionState
{
    Unaware,
    Tracking,
    Investigating,
    Searching,
    Returning
}

/// <summary>
/// One enemy's belief about the player: whether it currently has line of
/// sight, the observed/last-known position, and the perception state
/// machine driving investigate/search behavior. Field names match
/// docs/ENEMY_AI_ADAPTATION_PLAN.md section 7.
/// </summary>
public sealed class EnemyPerception
{
    private const double InvestigateTimeoutSeconds = 3.0;
    private const double SearchTimeoutSeconds = 5.0;
    private const double RecentlySeenWindowSeconds = 2.0;

    public bool HasLineOfSight { get; private set; }
    public GridPoint ObservedPlayerPosition { get; private set; }
    public GridPoint LastKnownPlayerPosition { get; private set; }
    public int Distance { get; private set; }
    public bool PlayerRecentlySeen => _timeSinceLastSeen <= RecentlySeenWindowSeconds;

    public PerceptionState State { get; private set; } = PerceptionState.Unaware;

    /// <summary>Cells visited by an active search, so it doesn't loop over the same ground.</summary>
    public HashSet<GridPoint> SearchedCells { get; } = new();

    private double _timeSinceLastSeen = double.MaxValue;
    private double _stateTimer;

    /// <summary>
    /// Advances the perception state machine by one tick. Call once per
    /// enemy update with the current (already-computed) line-of-sight
    /// result and player position/distance.
    /// </summary>
    public void Update(double delta, bool hasLineOfSight, GridPoint playerPos, int distance, float visionRange)
    {
        Distance = distance;
        _stateTimer += delta;

        bool inRange = distance <= visionRange;
        HasLineOfSight = hasLineOfSight && inRange;

        if (HasLineOfSight)
        {
            _timeSinceLastSeen = 0.0;
            ObservedPlayerPosition = playerPos;
            LastKnownPlayerPosition = playerPos;

            if (State != PerceptionState.Tracking)
            {
                _stateTimer = 0.0;
                SearchedCells.Clear();
            }
            State = PerceptionState.Tracking;
            return;
        }

        _timeSinceLastSeen += delta;

        switch (State)
        {
            case PerceptionState.Tracking:
                State = PerceptionState.Investigating;
                _stateTimer = 0.0;
                break;

            case PerceptionState.Investigating:
                if (_stateTimer >= InvestigateTimeoutSeconds)
                {
                    State = PerceptionState.Searching;
                    _stateTimer = 0.0;
                }
                break;

            case PerceptionState.Searching:
                if (_stateTimer >= SearchTimeoutSeconds)
                {
                    State = PerceptionState.Returning;
                    _stateTimer = 0.0;
                }
                break;

            case PerceptionState.Returning:
                if (_stateTimer >= InvestigateTimeoutSeconds)
                {
                    State = PerceptionState.Unaware;
                    SearchedCells.Clear();
                }
                break;

            case PerceptionState.Unaware:
            default:
                break;
        }
    }

    public void MarkCellSearched(GridPoint cell) => SearchedCells.Add(cell);
}
