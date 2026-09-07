namespace DungeonCrawler.AI;

/// <summary>
/// Where an attack landed relative to the target's awareness/orientation.
/// See docs/ENEMY_AI_ADAPTATION_PLAN.md section 4 ("Detecting a rear attack").
/// </summary>
public enum AttackDirection
{
    Front,
    Side,
    Rear
}

public static class AttackDirectionClassifier
{
    /// <summary>
    /// Classifies an attack on a target using the attacker's facing
    /// direction relative to the vector toward the target, gated by
    /// whether the target had already noticed the attacker.
    ///
    /// Per the plan: "A rear attack should only count strongly when the
    /// enemy was not already facing or tracking the player." Since this
    /// game's attacks are omnidirectional-in-range rather than aimed, the
    /// primary signal is awareness (was the target actively tracking the
    /// attacker), not raw geometry — geometry only sharpens Front vs. Side
    /// for an already-aware target. An unaware target is always Rear,
    /// regardless of facing, matching "attacked from behind" in the
    /// colloquial sense of "didn't see it coming."
    /// </summary>
    public static AttackDirection Classify(GridPoint attackerFacing, GridPoint attackerToTargetDelta, bool targetWasAware)
    {
        if (!targetWasAware)
        {
            return AttackDirection.Rear;
        }

        if (attackerFacing == GridPoint.Zero || attackerToTargetDelta == GridPoint.Zero)
        {
            return AttackDirection.Side;
        }

        // Normalized dot product between facing and direction-to-target:
        // close to 1 => attacker was looking at the target (Front), close
        // to -1 => target is behind the attacker's facing (Rear even
        // though aware — e.g. the target circled around during the swing),
        // near 0 => perpendicular (Side).
        float facingLen = System.MathF.Sqrt((attackerFacing.X * attackerFacing.X) + (attackerFacing.Y * attackerFacing.Y));
        float deltaLen = System.MathF.Sqrt((attackerToTargetDelta.X * attackerToTargetDelta.X) + (attackerToTargetDelta.Y * attackerToTargetDelta.Y));
        float dot = ((attackerFacing.X * attackerToTargetDelta.X) + (attackerFacing.Y * attackerToTargetDelta.Y)) / (facingLen * deltaLen);

        if (dot > 0.3f)
        {
            return AttackDirection.Front;
        }

        if (dot < -0.3f)
        {
            return AttackDirection.Rear;
        }

        return AttackDirection.Side;
    }
}
