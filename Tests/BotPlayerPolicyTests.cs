using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class BotPlayerPolicyTests
{
    private static int[,] OpenRoom(int width, int height)
    {
        var grid = new int[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                grid[y, x] = 1;
            }
        }
        return grid;
    }

    [Fact]
    public void DecideMove_HeadsTowardExit_WhenHealthyAndNoThreat()
    {
        int[,] grid = OpenRoom(5, 5);

        GridPoint step = BotPlayerPolicy.DecideMove(
            playerPos: new GridPoint(0, 2), exitPos: new GridPoint(4, 2), nearestThreatPos: null,
            currentHp: 100, maxHp: 100, walkableGrid: grid);

        Assert.Equal(new GridPoint(1, 0), step);
    }

    [Fact]
    public void DecideMove_IgnoresThreat_WhenHealthIsNotLow()
    {
        int[,] grid = OpenRoom(5, 5);

        GridPoint step = BotPlayerPolicy.DecideMove(
            playerPos: new GridPoint(2, 2), exitPos: new GridPoint(4, 2), nearestThreatPos: new GridPoint(2, 1),
            currentHp: 90, maxHp: 100, walkableGrid: grid);

        // 90/100 is well above LowHealthFraction (0.3) — should path toward
        // the exit exactly as if there were no threat at all.
        Assert.Equal(new GridPoint(1, 0), step);
    }

    [Fact]
    public void DecideMove_RetreatsFromThreat_WhenHealthIsLow()
    {
        int[,] grid = OpenRoom(5, 5);

        GridPoint step = BotPlayerPolicy.DecideMove(
            playerPos: new GridPoint(2, 2), exitPos: new GridPoint(4, 2), nearestThreatPos: new GridPoint(2, 1),
            currentHp: 10, maxHp: 100, walkableGrid: grid);

        // Threat is directly "north" (y-1) of the player; stepping toward
        // the exit (+x) increases distance from it just as much as any
        // other legal candidate, and is checked first, so it wins.
        Assert.Equal(new GridPoint(1, 0), step);
    }

    [Fact]
    public void DecideMove_FallsBackToExitPath_WhenCorneredWithNoRetreatOption()
    {
        // A single-cell-wide 1x3 corridor: player at x=0, threat blocking
        // the only exit at x=1, exit itself at x=2. No candidate step can
        // increase distance from the threat, so this must not return
        // GridPoint.Zero (freeze) — it should fall through to pathing
        // toward the exit, even though that means moving toward the threat.
        var corridor = new int[1, 3] { { 1, 1, 1 } };

        GridPoint step = BotPlayerPolicy.DecideMove(
            playerPos: new GridPoint(0, 0), exitPos: new GridPoint(2, 0), nearestThreatPos: new GridPoint(1, 0),
            currentHp: 5, maxHp: 100, walkableGrid: corridor);

        Assert.Equal(new GridPoint(1, 0), step);
    }

    [Fact]
    public void DecideMove_DoesNotThrow_WhenMaxHpIsZero()
    {
        int[,] grid = OpenRoom(3, 3);

        GridPoint step = BotPlayerPolicy.DecideMove(
            playerPos: new GridPoint(0, 0), exitPos: new GridPoint(2, 0), nearestThreatPos: new GridPoint(1, 0),
            currentHp: 0, maxHp: 0, walkableGrid: grid);

        Assert.Equal(new GridPoint(1, 0), step);
    }

    [Fact]
    public void HideStep_StepsAroundACorner_OutOfSight()
    {
        // A wall column at x = 4 (rows 0-3) leaves only the bottom row open.
        // The watcher at (6, 4) sees the player at (3, 4) along that row; the
        // tile (3, 3), around the corner, is hidden behind the wall.
        int[,] grid = OpenRoom(7, 5);
        for (int y = 0; y < 4; y++)
        {
            grid[y, 4] = 0;
        }

        var watcher = new GridPoint(6, 4);
        var player = new GridPoint(3, 4);
        Assert.True(GridLineOfSight.HasLineOfSight(watcher, player, grid));
        Assert.False(GridLineOfSight.HasLineOfSight(watcher, new GridPoint(3, 3), grid));

        GridPoint step = BotPlayerPolicy.HideStep(player, new GridPoint(0, 0), new[] { watcher }, grid);

        Assert.Equal(new GridPoint(0, -1), step);
    }

    [Fact]
    public void HideStep_DoesNothingWithoutWatchers_OrWithoutCover()
    {
        int[,] grid = OpenRoom(5, 5);
        Assert.Equal(GridPoint.Zero, BotPlayerPolicy.HideStep(new GridPoint(2, 2), new GridPoint(0, 0), new GridPoint[0], grid));
        // In an open room every neighbour stays in sight of a nearby watcher.
        Assert.Equal(GridPoint.Zero, BotPlayerPolicy.HideStep(new GridPoint(2, 2), new GridPoint(0, 0), new[] { new GridPoint(2, 0) }, grid));
    }
}
