using System.Collections.Generic;
using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class GridPathfinderTests
{
    // 0 = wall, 1 = floor (matches the real game's tile convention).
    private static int[,] ParseGrid(string[] rows)
    {
        var grid = new int[rows.Length, rows[0].Length];
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                grid[y, x] = rows[y][x] == '#' ? 0 : 1;
            }
        }
        return grid;
    }

    [Fact]
    public void FindPath_ReturnsEmptyList_WhenStartEqualsTarget()
    {
        int[,] grid = ParseGrid(new[] { "...", "...", "..." });
        List<GridPoint>? path = GridPathfinder.FindPath(new GridPoint(1, 1), new GridPoint(1, 1), grid);

        Assert.NotNull(path);
        Assert.Empty(path);
    }

    [Fact]
    public void FindPath_FindsDirectPath_InOpenRoom()
    {
        int[,] grid = ParseGrid(new[] { ".....", ".....", "....." });
        List<GridPoint>? path = GridPathfinder.FindPath(new GridPoint(0, 0), new GridPoint(4, 2), grid);

        Assert.NotNull(path);
        Assert.Equal(new GridPoint(4, 2), path[^1]);
        // Manhattan-optimal: 4 + 2 = 6 steps.
        Assert.Equal(6, path.Count);
    }

    [Fact]
    public void FindPath_RoutesAroundAWall_WhereGreedyMovementWouldStall()
    {
        // A wall spans the middle row except for a gap on the far right,
        // so the shortest route must detour — the scenario section 12
        // calls out as breaking pure greedy Manhattan movement.
        int[,] grid = ParseGrid(new[]
        {
            ".....",
            "####.",
            "....."
        });

        List<GridPoint>? path = GridPathfinder.FindPath(new GridPoint(0, 0), new GridPoint(0, 2), grid);

        Assert.NotNull(path);
        Assert.Equal(new GridPoint(0, 2), path[^1]);
        foreach (GridPoint cell in path)
        {
            Assert.NotEqual(0, grid[cell.Y, cell.X]);
        }
    }

    [Fact]
    public void FindPath_ReturnsNull_WhenTargetIsCompletelyEnclosed()
    {
        int[,] grid = ParseGrid(new[]
        {
            "#####",
            "#.#.#",
            "#####"
        });

        List<GridPoint>? path = GridPathfinder.FindPath(new GridPoint(1, 1), new GridPoint(3, 1), grid);

        Assert.Null(path);
    }

    [Fact]
    public void FindPath_ReturnsNull_WhenTargetIsAWall()
    {
        List<GridPoint>? path = GridPathfinder.FindPath(new GridPoint(0, 0), new GridPoint(1, 1), ParseGrid(new[] { "...", ".#.", "..." }));

        Assert.Null(path);
    }

    [Fact]
    public void FindNextStepDirection_PointsTowardTarget_InOpenRoom()
    {
        int[,] grid = ParseGrid(new[] { ".....", ".....", "....." });
        GridPoint direction = GridPathfinder.FindNextStepDirection(new GridPoint(2, 2), new GridPoint(4, 2), grid);

        Assert.Equal(new GridPoint(1, 0), direction);
    }

    [Fact]
    public void FindNextStepDirection_IsZero_WhenAlreadyAtTarget()
    {
        int[,] grid = ParseGrid(new[] { "...", "...", "..." });
        GridPoint direction = GridPathfinder.FindNextStepDirection(new GridPoint(1, 1), new GridPoint(1, 1), grid);

        Assert.Equal(GridPoint.Zero, direction);
    }
}
