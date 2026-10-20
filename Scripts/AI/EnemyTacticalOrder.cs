namespace DungeonCrawler.AI;

/// <summary>
/// A bounded countermeasure assigned to an enemy by the
/// <see cref="AdaptationDirector"/>. Field names match
/// docs/ENEMY_AI_ADAPTATION_PLAN.md section 9. Assigned once, at spawn,
/// per the plan's first milestone (section 15) — existing enemies keep
/// whatever order they already have rather than being retroactively
/// changed mid-encounter.
/// </summary>
public sealed class EnemyTacticalOrder
{
    public bool GuardRear;
    public bool SearchLastKnownPosition;
    // The intercept form of the stealth countermeasure (InterceptPlanner):
    // head for a cell ahead of the player on their route to the exit.
    public bool InterceptRoute;
    public bool PreferAlternateRoute;
    public float TurnSpeedMultiplier = 1f;
    public float SearchRadius = 4f;

    // Present for shape-fidelity with the plan's own EnemyTacticalOrder
    // snippet (section 9) but not currently read anywhere: per-enemy order
    // expiry isn't part of the first milestone (section 15), where an
    // order is simply whatever the enemy was spawned with, for its whole
    // lifetime. What DOES expire, at the population level, is which orders
    // AdaptationDirector hands out to newly-spawned enemies going forward
    // — see its RearDefenseActive/StealthCountermeasureActive plus the
    // minimum-tactic-duration safeguard. Wiring per-enemy expiry (e.g. an
    // order reverting mid-lifetime) is Phase 6 territory.
    public double Duration;

    public static EnemyTacticalOrder None() => new();
}
