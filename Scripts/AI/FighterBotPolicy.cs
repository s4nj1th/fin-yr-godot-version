using System;
using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Movement of the scripted fighter used for the in-engine duel
/// measurements (GameManager duel mode, tools/run_duels.py). Unlike
/// <see cref="BotPlayerPolicy"/>, which walks to the exit, the fighter
/// engages: it closes on the nearest enemy and stays adjacent so the
/// player's auto-attack lands. Its skill is one number, the probability of
/// reacting to a telegraph — stepping out of a charge line or blast area,
/// or flanking a shieldbearer. The caller rolls that probability once per
/// telegraph and passes the tiles the fighter has decided to avoid.
/// </summary>
public static class FighterBotPolicy
{
    public readonly record struct Target(GridPoint Position, GridPoint Facing, bool HasShield);

    /// <summary>
    /// The next single-tile step (or Zero to stand and attack).
    /// </summary>
    /// <param name="dangerCells">Tiles the fighter is dodging (empty if it is not reacting to any telegraph).</param>
    /// <param name="flank">Whether to work around a shieldbearer's front instead of attacking into the shield.</param>
    /// <param name="isOccupied">Tiles holding an enemy (cannot be entered).</param>
    public static GridPoint DecideMove(GridPoint player, Target? target, IReadOnlySet<GridPoint> dangerCells, bool flank,
        int[,] grid, Func<GridPoint, bool> isOccupied)
    {
        if (dangerCells.Contains(player))
        {
            return DodgeStep(player, target, dangerCells, grid, isOccupied);
        }

        if (target is not { } enemy)
        {
            return GridPoint.Zero;
        }

        if (flank && enemy.HasShield && AttackGeometry.Chebyshev(player, enemy.Position) <= 2
            && AttackGeometry.IsInFrontArc(enemy.Facing, player - enemy.Position))
        {
            GridPoint flankStep = StepToward(player, FlankCells(enemy), dangerCells, grid, isOccupied);
            if (flankStep != GridPoint.Zero)
            {
                return flankStep;
            }
        }

        if (AttackGeometry.Chebyshev(player, enemy.Position) <= 1)
        {
            return GridPoint.Zero;
        }

        GridPoint step = GridPathfinder.FindNextStepDirection(player, enemy.Position, grid);
        GridPoint next = player + step;
        if (step == GridPoint.Zero || isOccupied(next) || dangerCells.Contains(next))
        {
            return GridPoint.Zero;
        }

        return step;
    }

    /// <summary>A step out of the danger tiles, toward the target where possible; Zero if no step helps.</summary>
    public static GridPoint DodgeStep(GridPoint player, Target? target, IReadOnlySet<GridPoint> dangerCells, int[,] grid,
        Func<GridPoint, bool> isOccupied)
    {
        GridPoint best = GridPoint.Zero;
        int bestDistance = int.MaxValue;
        foreach (GridPoint direction in AttackGeometry.Cardinals)
        {
            GridPoint next = player + direction;
            if (!AttackGeometry.IsWalkable(next, grid) || isOccupied(next) || dangerCells.Contains(next))
            {
                continue;
            }

            int distance = target is { } enemy ? AttackGeometry.Chebyshev(next, enemy.Position) : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = direction;
            }
        }

        if (best != GridPoint.Zero)
        {
            return best;
        }

        // Every neighbour is in danger too (e.g. the middle of a blast):
        // move anyway, toward the tile that leaves the area soonest.
        foreach (GridPoint direction in AttackGeometry.Cardinals)
        {
            GridPoint next = player + direction;
            if (AttackGeometry.IsWalkable(next, grid) && !isOccupied(next))
            {
                GridPoint beyond = next + direction;
                if (AttackGeometry.IsWalkable(beyond, grid) && !dangerCells.Contains(beyond))
                {
                    return direction;
                }
            }
        }

        return GridPoint.Zero;
    }

    /// <summary>The two tiles beside a shieldbearer, perpendicular to its facing.</summary>
    private static GridPoint[] FlankCells(Target enemy)
    {
        GridPoint side = new(-enemy.Facing.Y, enemy.Facing.X);
        return [enemy.Position + side, enemy.Position - side];
    }

    // Greedy, not breadth-first: the flank tiles are at most a few steps
    // away, and a shortest path would often run through the enemy itself.
    private static GridPoint StepToward(GridPoint player, GridPoint[] goals, IReadOnlySet<GridPoint> dangerCells, int[,] grid,
        Func<GridPoint, bool> isOccupied)
    {
        GridPoint best = GridPoint.Zero;
        int bestDistance = int.MaxValue;
        foreach (GridPoint goal in goals)
        {
            if (!AttackGeometry.IsWalkable(goal, grid) || isOccupied(goal))
            {
                continue;
            }

            if (goal == player)
            {
                return GridPoint.Zero;
            }

            foreach (GridPoint direction in AttackGeometry.Cardinals)
            {
                GridPoint next = player + direction;
                if (!AttackGeometry.IsWalkable(next, grid) || isOccupied(next) || dangerCells.Contains(next))
                {
                    continue;
                }

                int distance = AttackGeometry.Manhattan(next, goal);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = direction;
                }
            }
        }

        return best;
    }
}
