using System;
using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Cell-traversal line of sight between two grid points, used to give
/// enemies a real "can I currently see the player" signal instead of the
/// omniscient exact-position access described as a gap in
/// docs/ENEMY_AI_ADAPTATION_PLAN.md section 3. Deliberately simple (a
/// Bresenham walk checking for walls) — the plan only requires
/// distinguishing direct visibility from mere knowledge of position, not a
/// physically-accurate raycast.
/// </summary>
public static class GridLineOfSight
{
    public static bool HasLineOfSight(GridPoint from, GridPoint to, int[,] walkableGrid)
    {
        if (from == to)
        {
            return true;
        }

        foreach (GridPoint cell in WalkLine(from, to))
        {
            if (cell == from || cell == to)
            {
                continue;
            }

            if (!IsWalkable(cell, walkableGrid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Bresenham's line algorithm over integer grid cells, inclusive of both endpoints.</summary>
    private static IEnumerable<GridPoint> WalkLine(GridPoint from, GridPoint to)
    {
        int x0 = from.X, y0 = from.Y;
        int x1 = to.X, y1 = to.Y;

        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;

        int x = x0, y = y0;

        while (true)
        {
            yield return new GridPoint(x, y);

            if (x == x1 && y == y1)
            {
                yield break;
            }

            int doubledError = 2 * error;
            if (doubledError >= dy)
            {
                error += dy;
                x += sx;
            }

            if (doubledError <= dx)
            {
                error += dx;
                y += sy;
            }
        }
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
