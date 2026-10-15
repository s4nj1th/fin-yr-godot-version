using System.Collections.Generic;
using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class AttackGeometryTests
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
    public void ChargeDirection_RequiresSharedRowOrColumn()
    {
        int[,] grid = OpenRoom(10, 10);
        Assert.Equal(new GridPoint(1, 0), AttackGeometry.ChargeDirection(new GridPoint(1, 5), new GridPoint(5, 5), 6, grid));
        Assert.Equal(new GridPoint(0, -1), AttackGeometry.ChargeDirection(new GridPoint(3, 8), new GridPoint(3, 4), 6, grid));
        Assert.Equal(GridPoint.Zero, AttackGeometry.ChargeDirection(new GridPoint(1, 5), new GridPoint(5, 6), 6, grid));
    }

    [Fact]
    public void ChargeDirection_RespectsRangeAndWalls()
    {
        int[,] grid = OpenRoom(10, 10);
        Assert.Equal(GridPoint.Zero, AttackGeometry.ChargeDirection(new GridPoint(0, 5), new GridPoint(9, 5), 6, grid));
        grid[5, 3] = 0;
        Assert.Equal(GridPoint.Zero, AttackGeometry.ChargeDirection(new GridPoint(1, 5), new GridPoint(5, 5), 6, grid));
    }

    [Fact]
    public void ChargePath_RunsThroughThePlayer_ButStopsAtWallsAndAllies()
    {
        int[,] grid = OpenRoom(10, 3);
        List<GridPoint> path = AttackGeometry.ChargePath(new GridPoint(1, 1), new GridPoint(1, 0), 6, grid);
        Assert.Equal(6, path.Count);
        Assert.Equal(new GridPoint(7, 1), path[^1]);

        List<GridPoint> blocked = AttackGeometry.ChargePath(new GridPoint(1, 1), new GridPoint(1, 0), 6, grid, cell => cell == new GridPoint(4, 1));
        Assert.Equal(new[] { new GridPoint(2, 1), new GridPoint(3, 1) }, blocked);

        List<GridPoint> walled = AttackGeometry.ChargePath(new GridPoint(6, 1), new GridPoint(1, 0), 6, grid);
        Assert.Equal(3, walled.Count); // x = 7, 8, 9, then the map edge
    }

    [Fact]
    public void ChargeEnd_StopsBeforeThePlayer()
    {
        var path = new List<GridPoint> { new(2, 1), new(3, 1), new(4, 1), new(5, 1) };
        Assert.Equal(new GridPoint(3, 1), AttackGeometry.ChargeEnd(new GridPoint(1, 1), path, new GridPoint(4, 1)));
        Assert.Equal(new GridPoint(5, 1), AttackGeometry.ChargeEnd(new GridPoint(1, 1), path, new GridPoint(9, 9)));
        Assert.Equal(new GridPoint(1, 1), AttackGeometry.ChargeEnd(new GridPoint(1, 1), path, new GridPoint(2, 1)));
    }

    [Fact]
    public void BlastArea_IsASquareClippedToWalkableTiles()
    {
        int[,] grid = OpenRoom(5, 5);
        Assert.Equal(9, AttackGeometry.BlastArea(new GridPoint(2, 2), 1, grid).Count);
        Assert.Equal(4, AttackGeometry.BlastArea(new GridPoint(0, 0), 1, grid).Count);
        Assert.True(AttackGeometry.BlastContains(new GridPoint(2, 2), 1, new GridPoint(3, 3)));
        Assert.False(AttackGeometry.BlastContains(new GridPoint(2, 2), 1, new GridPoint(4, 2)));
    }

    [Fact]
    public void FrontArc_CoversTheFacingSideOnly()
    {
        var facing = new GridPoint(1, 0);
        Assert.True(AttackGeometry.IsInFrontArc(facing, new GridPoint(1, 0)));
        Assert.True(AttackGeometry.IsInFrontArc(facing, new GridPoint(1, 1)));
        Assert.False(AttackGeometry.IsInFrontArc(facing, new GridPoint(0, 1)));
        Assert.False(AttackGeometry.IsInFrontArc(facing, new GridPoint(-1, 0)));
        Assert.False(AttackGeometry.IsInFrontArc(GridPoint.Zero, new GridPoint(1, 0)));
    }

    [Fact]
    public void TurnToward_TakesAQuarterTurnWhenTheTargetIsBehind()
    {
        var east = new GridPoint(1, 0);
        Assert.Equal(east, AttackGeometry.TurnToward(east, new GridPoint(3, 1)));
        Assert.Equal(new GridPoint(0, 1), AttackGeometry.TurnToward(east, new GridPoint(0, 2)));
        Assert.Equal(new GridPoint(0, 1), AttackGeometry.TurnToward(east, new GridPoint(-2, 0)));
    }

    [Fact]
    public void AlignStep_MovesAlongTheSmallerAxis()
    {
        Assert.Equal(new GridPoint(0, 1), AttackGeometry.AlignStep(new GridPoint(0, 0), new GridPoint(5, 1)));
        Assert.Equal(new GridPoint(-1, 0), AttackGeometry.AlignStep(new GridPoint(3, 0), new GridPoint(2, 6)));
        Assert.Equal(GridPoint.Zero, AttackGeometry.AlignStep(new GridPoint(3, 0), new GridPoint(3, 6)));
    }

    [Fact]
    public void PackPlacement_FillsNearestFreeTiles()
    {
        int[,] grid = OpenRoom(7, 7);
        var occupied = new HashSet<GridPoint> { new(4, 3) };
        List<GridPoint> tiles = AttackGeometry.PackPlacement(new GridPoint(3, 3), 2, grid, occupied.Contains);

        Assert.Equal(2, tiles.Count);
        Assert.DoesNotContain(new GridPoint(4, 3), tiles);
        Assert.All(tiles, t => Assert.Equal(1, AttackGeometry.Manhattan(t, new GridPoint(3, 3))));
    }

    [Fact]
    public void PackPlacement_ReturnsFewerTilesInATightSpot()
    {
        var grid = new int[3, 3];
        grid[1, 1] = 1;
        grid[1, 2] = 1;
        Assert.Single(AttackGeometry.PackPlacement(new GridPoint(1, 1), 3, grid, _ => false));
    }
}
