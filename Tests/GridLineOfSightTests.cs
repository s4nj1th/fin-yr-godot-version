using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class GridLineOfSightTests
{
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
    public void HasLineOfSight_IsTrue_AlongAnUnobstructedRow()
    {
        int[,] grid = ParseGrid(new[] { "....." });
        Assert.True(GridLineOfSight.HasLineOfSight(new GridPoint(0, 0), new GridPoint(4, 0), grid));
    }

    [Fact]
    public void HasLineOfSight_IsFalse_WhenAWallSitsBetweenThem()
    {
        int[,] grid = ParseGrid(new[] { "..#.." });
        Assert.False(GridLineOfSight.HasLineOfSight(new GridPoint(0, 0), new GridPoint(4, 0), grid));
    }

    [Fact]
    public void HasLineOfSight_IsTrue_ForTheSameCell()
    {
        int[,] grid = ParseGrid(new[] { "..." });
        Assert.True(GridLineOfSight.HasLineOfSight(new GridPoint(1, 0), new GridPoint(1, 0), grid));
    }

    [Fact]
    public void HasLineOfSight_WorksDiagonally_WhenUnobstructed()
    {
        int[,] grid = ParseGrid(new[]
        {
            ".....",
            ".....",
            ".....",
            ".....",
            "....."
        });

        Assert.True(GridLineOfSight.HasLineOfSight(new GridPoint(0, 0), new GridPoint(4, 4), grid));
    }

    [Fact]
    public void HasLineOfSight_IsBlocked_ByADiagonalWallCluster()
    {
        int[,] grid = ParseGrid(new[]
        {
            ".....",
            ".#...",
            "..#..",
            "...#.",
            "....."
        });

        Assert.False(GridLineOfSight.HasLineOfSight(new GridPoint(0, 0), new GridPoint(4, 4), grid));
    }
}
