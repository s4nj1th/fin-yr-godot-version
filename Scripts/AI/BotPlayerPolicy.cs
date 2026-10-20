using System;
using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Pure movement decision logic for the optional scripted bot evaluator
/// (docs/EVOLUTIONARY_DESIGN.md "Scripted bot evaluator"). Deliberately
/// simple — this is a first version meant to generate volume for
/// docs/BEHAVIOR_ANALYTICS.md's per-archetype telemetry, not a
/// sophisticated player-behavior stand-in: it beelines for the exit via
/// the same BFS pathfinder enemies already use
/// (<see cref="GridPathfinder"/>), and backs away from the nearest threat
/// only once health is low. It does not seek chests, avoid traps, feint,
/// juke, or use cover — see docs/EVOLUTIONARY_DESIGN.md for the honest
/// scope of what this bot is (and isn't) a proxy for.
///
/// Kept Godot-free (GridPoint, not Vector2I) so it's covered by
/// Tests/BotPlayerPolicyTests.cs without needing the Godot engine host —
/// same reasoning as every other class in this namespace.
/// </summary>
public static class BotPlayerPolicy
{
    // Below this fraction of max HP, the bot prioritizes putting distance
    // between itself and the nearest threat over continuing toward the
    // exit. Not tuned against real playtesting — a starting point, same
    // spirit as the constants in Backend/config.py.
    public const float LowHealthFraction = 0.3f;

    private static readonly GridPoint[] Directions =
    [
        new GridPoint(1, 0),
        new GridPoint(-1, 0),
        new GridPoint(0, 1),
        new GridPoint(0, -1)
    ];

    /// <summary>
    /// Returns the next single-cell step direction (or GridPoint.Zero for
    /// "don't move"). <paramref name="nearestThreatPos"/> is null when no
    /// enemy is currently alive.
    /// </summary>
    public static GridPoint DecideMove(GridPoint playerPos, GridPoint exitPos, GridPoint? nearestThreatPos,
        int currentHp, int maxHp, int[,] walkableGrid)
    {
        bool lowHealth = maxHp > 0 && currentHp <= maxHp * LowHealthFraction;

        if (lowHealth && nearestThreatPos.HasValue)
        {
            GridPoint retreatStep = FindRetreatStep(playerPos, nearestThreatPos.Value, walkableGrid);
            if (retreatStep != GridPoint.Zero)
            {
                return retreatStep;
            }
            // No step increases distance from the threat (e.g. cornered) —
            // fall through to the normal exit-seeking path rather than
            // freezing in place.
        }

        return GridPathfinder.FindNextStepDirection(playerPos, exitPos, walkableGrid);
    }

    /// <summary>
    /// The "hide" playstyle: a step that takes the player out of the line of
    /// sight of every enemy currently watching it, preferring the step that
    /// keeps it closest to the exit. Zero if no neighbouring tile is hidden
    /// from all of them. Used to evaluate the tactical director's stealth
    /// countermeasure with a scripted player (tools/evaluate_director.py).
    /// </summary>
    public static GridPoint HideStep(GridPoint playerPos, GridPoint exitPos, IReadOnlyList<GridPoint> watchers,
        int[,] walkableGrid, Func<GridPoint, bool>? isOccupied = null)
    {
        if (watchers.Count == 0)
        {
            return GridPoint.Zero;
        }

        GridPoint best = GridPoint.Zero;
        int bestDistance = int.MaxValue;
        foreach (GridPoint direction in Directions)
        {
            GridPoint candidate = playerPos + direction;
            if (!IsWalkable(candidate, walkableGrid) || (isOccupied?.Invoke(candidate) ?? false))
            {
                continue;
            }

            bool hidden = true;
            foreach (GridPoint watcher in watchers)
            {
                if (GridLineOfSight.HasLineOfSight(watcher, candidate, walkableGrid))
                {
                    hidden = false;
                    break;
                }
            }

            if (!hidden)
            {
                continue;
            }

            int distance = ManhattanDistance(candidate, exitPos);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = direction;
            }
        }

        return best;
    }

    private static GridPoint FindRetreatStep(GridPoint from, GridPoint threat, int[,] walkableGrid)
    {
        int currentDistance = ManhattanDistance(from, threat);
        GridPoint best = GridPoint.Zero;
        int bestDistance = currentDistance;

        foreach (GridPoint direction in Directions)
        {
            GridPoint candidate = from + direction;
            if (!IsWalkable(candidate, walkableGrid))
            {
                continue;
            }

            int distance = ManhattanDistance(candidate, threat);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = direction;
            }
        }

        return best;
    }

    private static int ManhattanDistance(GridPoint a, GridPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static bool IsWalkable(GridPoint position, int[,] walkableGrid)
    {
        if (position.X < 0 || position.Y < 0 || position.X >= walkableGrid.GetLength(1) || position.Y >= walkableGrid.GetLength(0))
        {
            return false;
        }

        return walkableGrid[position.Y, position.X] != 0;
    }
}
