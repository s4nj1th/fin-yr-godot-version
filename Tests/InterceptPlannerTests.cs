using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class InterceptPlannerTests
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
    public void ChoosesTheEarliestRouteCellTheEnemyReachesFirst()
    {
        // The player was last seen at (0,0) and heads along row 0 to the exit
        // at (9,0); the enemy waits in a side corridor that joins row 0 at x=6.
        int[,] grid = ParseGrid(new[]
        {
            "..........",
            "######.###",
            "######.###",
        });
        GridPoint point = InterceptPlanner.ChooseInterceptPoint(new GridPoint(6, 2), new GridPoint(0, 0),
            new GridPoint(9, 0), grid);
        // (4,0) is 4 steps from both; (3,0) and earlier are closer to the player.
        Assert.Equal(new GridPoint(4, 0), point);
    }

    [Fact]
    public void GuardsTheGoalWhenItCannotGetAhead()
    {
        int[,] grid = ParseGrid(new[]
        {
            "..........",
        });
        // The enemy is behind the player on the same corridor.
        GridPoint point = InterceptPlanner.ChooseInterceptPoint(new GridPoint(0, 0), new GridPoint(2, 0),
            new GridPoint(9, 0), grid);
        Assert.Equal(new GridPoint(9, 0), point);
    }

    [Fact]
    public void FallsBackToTheLastKnownPositionWithoutARoute()
    {
        int[,] grid = ParseGrid(new[]
        {
            "...#..",
        });
        GridPoint point = InterceptPlanner.ChooseInterceptPoint(new GridPoint(1, 0), new GridPoint(0, 0),
            new GridPoint(5, 0), grid);
        Assert.Equal(new GridPoint(0, 0), point);
    }
}
