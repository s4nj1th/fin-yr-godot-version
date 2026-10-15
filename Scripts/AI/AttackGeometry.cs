using System;
using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Grid geometry of the telegraphed attacks: the charger's dash line, the
/// bomber's blast area, and the shieldbearer's facing and front arc.
/// Engine-free so it is unit-tested (Tests/AttackGeometryTests.cs).
/// A grid cell is walkable when its value is non-zero.
/// </summary>
public static class AttackGeometry
{
    public static readonly GridPoint[] Cardinals =
    [
        new GridPoint(1, 0),
        new GridPoint(0, 1),
        new GridPoint(-1, 0),
        new GridPoint(0, -1)
    ];

    public static int Manhattan(GridPoint a, GridPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    public static int Chebyshev(GridPoint a, GridPoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static bool IsWalkable(GridPoint cell, int[,] grid) =>
        cell.X >= 0 && cell.Y >= 0 && cell.Y < grid.GetLength(0) && cell.X < grid.GetLength(1) && grid[cell.Y, cell.X] != 0;

    /// <summary>The cardinal direction along the larger axis of the offset (x wins ties); Zero for no offset.</summary>
    public static GridPoint DominantDirection(GridPoint delta)
    {
        if (delta == GridPoint.Zero)
        {
            return GridPoint.Zero;
        }

        return Math.Abs(delta.X) >= Math.Abs(delta.Y)
            ? new GridPoint(Math.Sign(delta.X), 0)
            : new GridPoint(0, Math.Sign(delta.Y));
    }

    /// <summary>
    /// The direction a charger can dash in to reach the target: the target
    /// shares its row or column, lies within maxLength tiles, and every tile
    /// in between is walkable. Zero otherwise.
    /// </summary>
    public static GridPoint ChargeDirection(GridPoint from, GridPoint target, int maxLength, int[,] grid)
    {
        GridPoint delta = target - from;
        if (delta == GridPoint.Zero || (delta.X != 0 && delta.Y != 0) || Manhattan(from, target) > maxLength)
        {
            return GridPoint.Zero;
        }

        GridPoint direction = DominantDirection(delta);
        for (GridPoint cell = from + direction; cell != target; cell += direction)
        {
            if (!IsWalkable(cell, grid))
            {
                return GridPoint.Zero;
            }
        }

        return direction;
    }

    /// <summary>
    /// The tiles a dash sweeps, in order: up to maxLength steps from the
    /// origin, stopping before a wall or a blocked tile (another enemy).
    /// The player's tile is not a blocker — a dash runs through it.
    /// </summary>
    public static List<GridPoint> ChargePath(GridPoint from, GridPoint direction, int maxLength, int[,] grid, Func<GridPoint, bool>? isBlocked = null)
    {
        var path = new List<GridPoint>();
        if (direction == GridPoint.Zero)
        {
            return path;
        }

        GridPoint cell = from;
        for (int i = 0; i < maxLength; i++)
        {
            GridPoint next = cell + direction;
            if (!IsWalkable(next, grid) || (isBlocked != null && isBlocked(next)))
            {
                break;
            }

            path.Add(next);
            cell = next;
        }

        return path;
    }

    /// <summary>
    /// Where a dash along the path ends: the tile before the player if the
    /// player is on the path (the charger stops on impact), else the last tile.
    /// Returns the origin for an empty path.
    /// </summary>
    public static GridPoint ChargeEnd(GridPoint from, IReadOnlyList<GridPoint> path, GridPoint player)
    {
        GridPoint end = from;
        foreach (GridPoint cell in path)
        {
            if (cell == player)
            {
                return end;
            }

            end = cell;
        }

        return end;
    }

    /// <summary>The walkable tiles of a square blast of the given Chebyshev radius.</summary>
    public static List<GridPoint> BlastArea(GridPoint center, int radius, int[,] grid)
    {
        var area = new List<GridPoint>();
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                var cell = new GridPoint(center.X + dx, center.Y + dy);
                if (IsWalkable(cell, grid))
                {
                    area.Add(cell);
                }
            }
        }

        return area;
    }

    public static bool BlastContains(GridPoint center, int radius, GridPoint cell) => Chebyshev(center, cell) <= radius;

    /// <summary>
    /// True when the attacker stands in the defender's front arc: the angle
    /// between the defender's facing and the direction to the attacker is
    /// under about 72 degrees (normalized dot product above 0.3, the same
    /// threshold as <see cref="AttackDirectionClassifier"/>).
    /// </summary>
    public static bool IsInFrontArc(GridPoint facing, GridPoint toAttacker)
    {
        if (facing == GridPoint.Zero || toAttacker == GridPoint.Zero)
        {
            return false;
        }

        float facingLength = MathF.Sqrt((facing.X * facing.X) + (facing.Y * facing.Y));
        float attackerLength = MathF.Sqrt((toAttacker.X * toAttacker.X) + (toAttacker.Y * toAttacker.Y));
        float dot = ((facing.X * toAttacker.X) + (facing.Y * toAttacker.Y)) / (facingLength * attackerLength);
        return dot > 0.3f;
    }

    /// <summary>
    /// One 90-degree turn of a slow-turning enemy toward the target: unchanged
    /// if already facing it, a quarter turn (clockwise) if the target is
    /// directly behind, otherwise straight to the target's dominant direction.
    /// </summary>
    public static GridPoint TurnToward(GridPoint facing, GridPoint toTarget)
    {
        GridPoint desired = DominantDirection(toTarget);
        if (desired == GridPoint.Zero || desired == facing)
        {
            return facing;
        }

        if (facing == GridPoint.Zero)
        {
            return desired;
        }

        bool opposite = desired.X == -facing.X && desired.Y == -facing.Y;
        return opposite ? new GridPoint(-facing.Y, facing.X) : desired;
    }

    /// <summary>
    /// A step that lines a charger up with the target on a row or column:
    /// it moves along the smaller axis of the offset. Zero if already aligned.
    /// </summary>
    public static GridPoint AlignStep(GridPoint from, GridPoint target)
    {
        GridPoint delta = target - from;
        if (delta.X == 0 || delta.Y == 0)
        {
            return GridPoint.Zero;
        }

        return Math.Abs(delta.X) <= Math.Abs(delta.Y)
            ? new GridPoint(Math.Sign(delta.X), 0)
            : new GridPoint(0, Math.Sign(delta.Y));
    }

    /// <summary>
    /// Free tiles for the extra members of a pack around a spawn point,
    /// nearest first (breadth-first over walkable tiles, at most maxRadius
    /// steps away), skipping occupied tiles. May return fewer than requested.
    /// </summary>
    public static List<GridPoint> PackPlacement(GridPoint center, int count, int[,] grid, Func<GridPoint, bool> isOccupied, int maxRadius = 2)
    {
        var result = new List<GridPoint>();
        if (count <= 0)
        {
            return result;
        }

        var visited = new HashSet<GridPoint> { center };
        var frontier = new Queue<(GridPoint Cell, int Depth)>();
        frontier.Enqueue((center, 0));
        while (frontier.Count > 0 && result.Count < count)
        {
            (GridPoint cell, int depth) = frontier.Dequeue();
            if (depth >= maxRadius)
            {
                continue;
            }

            foreach (GridPoint direction in Cardinals)
            {
                GridPoint next = cell + direction;
                if (!visited.Add(next) || !IsWalkable(next, grid))
                {
                    continue;
                }

                if (!isOccupied(next))
                {
                    result.Add(next);
                    if (result.Count == count)
                    {
                        break;
                    }
                }

                frontier.Enqueue((next, depth + 1));
            }
        }

        return result;
    }
}
