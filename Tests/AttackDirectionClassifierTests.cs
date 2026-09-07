using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class AttackDirectionClassifierTests
{
    [Fact]
    public void Classify_IsFront_ForAFrontalHitOnAnUnawareTarget()
    {
        // A clean frontal-geometry sneak-hit on an unaware target is still
        // a frontal hit, not a rear one — awareness must not override
        // geometry the other way (docs/ENEMY_AI_ADAPTATION_PLAN.md section 4).
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(1, 0),
            targetWasAware: false);

        Assert.Equal(AttackDirection.Front, direction);
    }

    [Fact]
    public void Classify_IsRear_ForARearGeometryHitOnAnUnawareTarget()
    {
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(-1, 0),
            targetWasAware: false);

        Assert.Equal(AttackDirection.Rear, direction);
    }

    [Fact]
    public void Classify_IsFront_WhenAwareAndFacingDirectlyTowardTarget()
    {
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(1, 0),
            targetWasAware: true);

        Assert.Equal(AttackDirection.Front, direction);
    }

    [Fact]
    public void Classify_DowngradesToSide_WhenRearGeometryButTargetWasAlreadyAware()
    {
        // Section 4: "A rear attack should only count strongly when the
        // enemy was not already facing or tracking the player. This
        // prevents every attack from behind a currently engaged enemy
        // from being labeled as a successful ambush."
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(-1, 0),
            targetWasAware: true);

        Assert.Equal(AttackDirection.Side, direction);
    }

    [Fact]
    public void Classify_IsSide_WhenAwareAndTargetIsPerpendicular()
    {
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(0, 1),
            targetWasAware: true);

        Assert.Equal(AttackDirection.Side, direction);
    }

    [Fact]
    public void Classify_IsSide_WhenAwareAndFacingOrDeltaIsZero()
    {
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: GridPoint.Zero,
            attackerToTargetDelta: new GridPoint(1, 0),
            targetWasAware: true);

        Assert.Equal(AttackDirection.Side, direction);
    }
}
