using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class AttackDirectionClassifierTests
{
    [Fact]
    public void Classify_IsAlwaysRear_WhenTargetWasUnaware()
    {
        // Even if the geometry looks like a clean "front" approach, an
        // unaware target didn't see it coming — that's what "rear attack"
        // means colloquially per docs/ENEMY_AI_ADAPTATION_PLAN.md section 4.
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(1, 0),
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
    public void Classify_IsRear_WhenAwareButTargetIsOppositeOfFacing()
    {
        var direction = AttackDirectionClassifier.Classify(
            attackerFacing: new GridPoint(1, 0),
            attackerToTargetDelta: new GridPoint(-1, 0),
            targetWasAware: true);

        Assert.Equal(AttackDirection.Rear, direction);
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
}
