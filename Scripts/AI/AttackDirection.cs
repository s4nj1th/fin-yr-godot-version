using System;

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
    /// Classifies an attack using the attacker's facing direction relative
    /// to the vector toward the target — steps 1-3 of section 4, computed
    /// unconditionally rather than being overridden by awareness.
    ///
    /// The one place awareness changes the result: a *geometrically* Rear
    /// hit lands, but the target was already aware/tracking the attacker.
    /// Per section 4's own caveat — "A rear attack should only count
    /// strongly when the enemy was not already facing or tracking the
    /// player. This prevents every attack from behind a currently engaged
    /// enemy from being labeled as a successful ambush" — that specific
    /// case is downgraded to Side rather than credited as a full ambush.
    /// An unaware target does NOT get its geometry overridden the other
    /// way: a clean Front-geometry hit on an unaware target stays Front
    /// (it's a frontal sneak-hit, not a rear one) — only Rear-geometry
    /// benefits from the awareness gate.
    /// </summary>
    public static AttackDirection Classify(GridPoint attackerFacing, GridPoint attackerToTargetDelta, bool targetWasAware)
    {
        AttackDirection geometric = ClassifyGeometry(attackerFacing, attackerToTargetDelta);

        if (geometric == AttackDirection.Rear && targetWasAware)
        {
            return AttackDirection.Side;
        }

        return geometric;
    }

    private static AttackDirection ClassifyGeometry(GridPoint attackerFacing, GridPoint attackerToTargetDelta)
    {
        if (attackerFacing == GridPoint.Zero || attackerToTargetDelta == GridPoint.Zero)
        {
            return AttackDirection.Side;
        }

        // Normalized dot product between facing and direction-to-target:
        // close to 1 => attacker was looking at the target (Front), close
        // to -1 => target is behind the attacker's facing (Rear), near 0
        // => perpendicular (Side).
        float facingLen = MathF.Sqrt((attackerFacing.X * attackerFacing.X) + (attackerFacing.Y * attackerFacing.Y));
        float deltaLen = MathF.Sqrt((attackerToTargetDelta.X * attackerToTargetDelta.X) + (attackerToTargetDelta.Y * attackerToTargetDelta.Y));
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
