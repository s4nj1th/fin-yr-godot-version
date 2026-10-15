using System.Collections.Generic;
using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class FighterBotPolicyTests
{
    private static readonly HashSet<GridPoint> NoDanger = new();

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
    public void ClosesOnTheTarget()
    {
        int[,] grid = OpenRoom(9, 5);
        var target = new FighterBotPolicy.Target(new GridPoint(6, 2), new GridPoint(-1, 0), false);
        GridPoint step = FighterBotPolicy.DecideMove(new GridPoint(1, 2), target, NoDanger, false, grid, _ => false);
        Assert.Equal(new GridPoint(1, 0), step);
    }

    [Fact]
    public void StandsAndFights_WhenAdjacent()
    {
        int[,] grid = OpenRoom(9, 5);
        var target = new FighterBotPolicy.Target(new GridPoint(3, 3), new GridPoint(-1, 0), false);
        Assert.Equal(GridPoint.Zero, FighterBotPolicy.DecideMove(new GridPoint(2, 2), target, NoDanger, false, grid, _ => false));
    }

    [Fact]
    public void StepsOutOfDanger_TowardTheTarget()
    {
        int[,] grid = OpenRoom(9, 5);
        var target = new FighterBotPolicy.Target(new GridPoint(6, 2), new GridPoint(-1, 0), false);
        var line = new HashSet<GridPoint> { new(1, 2), new(2, 2), new(3, 2), new(4, 2), new(5, 2) };
        GridPoint step = FighterBotPolicy.DecideMove(new GridPoint(3, 2), target, line, false, grid, _ => false);
        Assert.Contains(step, new[] { new GridPoint(0, 1), new GridPoint(0, -1) });
    }

    [Fact]
    public void DoesNotWalkIntoDanger()
    {
        int[,] grid = OpenRoom(9, 5);
        var target = new FighterBotPolicy.Target(new GridPoint(6, 2), new GridPoint(-1, 0), false);
        var area = new HashSet<GridPoint> { new(2, 2) };
        Assert.Equal(GridPoint.Zero, FighterBotPolicy.DecideMove(new GridPoint(1, 2), target, area, false, grid, _ => false));
    }

    [Fact]
    public void LeavesABlastArea_EvenWhenEveryNeighbourIsInIt()
    {
        int[,] grid = OpenRoom(9, 9);
        var blast = new HashSet<GridPoint>(AttackGeometry.BlastArea(new GridPoint(4, 4), 1, grid));
        GridPoint step = FighterBotPolicy.DecideMove(new GridPoint(4, 4), null, blast, false, grid, _ => false);
        Assert.NotEqual(GridPoint.Zero, step);
        Assert.False(blast.Contains(new GridPoint(4, 4) + step + step));
    }

    [Fact]
    public void FlanksAShieldbearer_InsteadOfAttackingIntoTheShield()
    {
        int[,] grid = OpenRoom(9, 9);
        // Shield faces west, toward the fighter standing directly in front.
        var shield = new FighterBotPolicy.Target(new GridPoint(5, 4), new GridPoint(-1, 0), true);
        GridPoint step = FighterBotPolicy.DecideMove(new GridPoint(4, 4), shield, NoDanger, flank: true, grid,
            cell => cell == shield.Position);
        Assert.Contains(step, new[] { new GridPoint(0, 1), new GridPoint(0, -1) });

        // Without the flank decision it just attacks from the front.
        Assert.Equal(GridPoint.Zero, FighterBotPolicy.DecideMove(new GridPoint(4, 4), shield, NoDanger, flank: false, grid,
            cell => cell == shield.Position));
    }

    [Fact]
    public void StaysPut_OnceAtTheShieldbearersSide()
    {
        int[,] grid = OpenRoom(9, 9);
        var shield = new FighterBotPolicy.Target(new GridPoint(5, 4), new GridPoint(-1, 0), true);
        Assert.Equal(GridPoint.Zero, FighterBotPolicy.DecideMove(new GridPoint(5, 3), shield, NoDanger, flank: true, grid,
            cell => cell == shield.Position));
    }
}
