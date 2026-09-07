namespace DungeonCrawler.AI;

/// <summary>
/// Independent tactical goals composable on top of an enemy's base
/// behavior/movement style, rather than a new hard-coded behavior string
/// per combination. See docs/ENEMY_AI_ADAPTATION_PLAN.md section 8.
/// Not all values are consumed by the first adaptive-tactics milestone
/// (Phase 5 only wires GuardRear and SearchLastKnownPosition, via
/// <see cref="EnemyTacticalOrder"/>) — the rest are named here so future
/// phases (section 14, Phase 6) have a stable vocabulary to extend into.
/// </summary>
public enum EnemyTactic
{
    DirectApproach,
    GuardRear,
    SearchLastKnownPosition,
    CircleObstacle,
    HoldDistance,
    CutOffEscape,
    ProtectAlly,
    Retreat
}
