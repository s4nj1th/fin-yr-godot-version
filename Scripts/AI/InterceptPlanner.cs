using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// The "intercept" form of the stealth countermeasure: instead of searching
/// where the player was last seen, an enemy that loses the player heads for
/// a cell ahead of them on their shortest route from that position to the
/// goal (the exit), and waits there. It picks the earliest cell of the route
/// it can reach no later than the player (moving at the same speed), at
/// least <c>minLead</c> cells ahead; if it cannot get ahead of the player
/// anywhere, it guards the goal itself.
/// </summary>
public static class InterceptPlanner
{
    private static readonly GridPoint[] Directions =
    [
        new GridPoint(1, 0),
        new GridPoint(-1, 0),
        new GridPoint(0, 1),
        new GridPoint(0, -1)
    ];

    private const int MaxVisitedNodes = 20_000;

    public static GridPoint ChooseInterceptPoint(GridPoint enemy, GridPoint lastKnown, GridPoint goal,
        int[,] walkableGrid, int minLead = 3)
    {
        List<GridPoint>? route = GridPathfinder.FindPath(lastKnown, goal, walkableGrid);
        if (route == null || route.Count == 0)
        {
            return lastKnown;
        }

        Dictionary<GridPoint, int> distance = DistancesFrom(enemy, walkableGrid);
        for (int k = minLead - 1; k < route.Count; k++)
        {
            // route[k] is k + 1 steps ahead of the player's last known position.
            if (distance.TryGetValue(route[k], out int d) && d <= k + 1)
            {
                return route[k];
            }
        }

        return route[^1];
    }

    /// <summary>Breadth-first distances from <paramref name="start"/> to every reachable cell (within a node budget).</summary>
    public static Dictionary<GridPoint, int> DistancesFrom(GridPoint start, int[,] walkableGrid)
    {
        var distance = new Dictionary<GridPoint, int> { [start] = 0 };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(start);
        while (queue.Count > 0 && distance.Count < MaxVisitedNodes)
        {
            GridPoint current = queue.Dequeue();
            foreach (GridPoint direction in Directions)
            {
                GridPoint next = current + direction;
                if (distance.ContainsKey(next) || !IsWalkable(next, walkableGrid))
                {
                    continue;
                }

                distance[next] = distance[current] + 1;
                queue.Enqueue(next);
            }
        }

        return distance;
    }

    private static bool IsWalkable(GridPoint position, int[,] walkableGrid) =>
        position.X >= 0 && position.Y >= 0 && position.X < walkableGrid.GetLength(1)
        && position.Y < walkableGrid.GetLength(0) && walkableGrid[position.Y, position.X] != 0;
}
