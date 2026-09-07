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
    public bool PreferAlternateRoute;
    public float TurnSpeedMultiplier = 1f;
    public float SearchRadius = 4f;
    public double Duration;

    public static EnemyTacticalOrder None() => new();
}
