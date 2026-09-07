using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Global routing to replace the greedy Manhattan movement that can stall
/// around obstacles. See docs/ENEMY_AI_ADAPTATION_PLAN.md section 12.
/// BFS is used throughout since grid movement here has uniform cost — the
/// plan explicitly names BFS as the right starting point and defers A* to
/// when tile costs or map scale justify it.
/// </summary>
public static class GridPathfinder
{
    private static readonly GridPoint[] Directions =
    [
        new GridPoint(1, 0),
        new GridPoint(-1, 0),
        new GridPoint(0, 1),
        new GridPoint(0, -1)
    ];

    // walkableGrid can be up to ~200x200 (40,000 cells) per Backend/config.py's
    // GENOME_BOUNDS; this safety cap only matters if a target is unreachable
    // and BFS would otherwise exhaust the entire connected region every call.
    private const int MaxVisitedNodes = 20_000;

    /// <summary>
    /// Returns the sequence of cells from (but not including) <paramref name="start"/>
    /// through <paramref name="target"/> inclusive, or null if no path exists
    /// (or the search exceeded its node budget). Returns an empty list if
    /// start already equals target.
    /// </summary>
    public static List<GridPoint>? FindPath(GridPoint start, GridPoint target, int[,] walkableGrid)
    {
        if (start == target)
        {
            return new List<GridPoint>();
        }

        if (!IsWalkable(target, walkableGrid))
        {
            return null;
        }

        var cameFrom = new Dictionary<GridPoint, GridPoint>();
        var visited = new HashSet<GridPoint> { start };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(start);

        bool found = false;
        int visitedCount = 1;

        while (queue.Count > 0 && visitedCount < MaxVisitedNodes)
        {
            GridPoint current = queue.Dequeue();

            if (current == target)
            {
                found = true;
                break;
            }

            foreach (GridPoint direction in Directions)
            {
                GridPoint next = current + direction;
                if (visited.Contains(next) || !IsWalkable(next, walkableGrid))
                {
                    continue;
                }

                visited.Add(next);
                cameFrom[next] = current;
                queue.Enqueue(next);
                visitedCount++;
            }
        }

        if (!found && !visited.Contains(target))
        {
            return null;
        }

        return ReconstructPath(cameFrom, start, target);
    }

    /// <summary>Convenience wrapper returning only the first step's direction (GridPoint.Zero if no path or already there).</summary>
    public static GridPoint FindNextStepDirection(GridPoint start, GridPoint target, int[,] walkableGrid)
    {
        List<GridPoint>? path = FindPath(start, target, walkableGrid);
        if (path == null || path.Count == 0)
        {
            return GridPoint.Zero;
        }

        return path[0] - start;
    }

    private static List<GridPoint> ReconstructPath(Dictionary<GridPoint, GridPoint> cameFrom, GridPoint start, GridPoint target)
    {
        var path = new List<GridPoint> { target };
        GridPoint cursor = target;

        while (cursor != start)
        {
            if (!cameFrom.TryGetValue(cursor, out GridPoint previous))
            {
                break;
            }

            cursor = previous;
            if (cursor != start)
            {
                path.Add(cursor);
            }
        }

        path.Reverse();
        return path;
    }

    private static bool IsWalkable(GridPoint position, int[,] walkableGrid)
    {
        if (position.X < 0 || position.Y < 0 || position.X >= walkableGrid.GetLength(1) || position.Y >= walkableGrid.GetLength(0))
        {
            return false;
        }

        return walkableGrid[position.Y, position.X] != 0;
    }
}
