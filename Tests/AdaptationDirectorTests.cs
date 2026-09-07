using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class AdaptationDirectorTests
{
    private static (PlayerBehaviorTelemetry telemetry, PlayerPlaystyleProfile profile) RecordRearAttacks(int count)
    {
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();
        for (int i = 0; i < count; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Rear);
            profile.Update(telemetry);
        }
        return (telemetry, profile);
    }

    [Fact]
    public void Reevaluate_DoesNotActivateRearDefense_BelowTheEvidenceThreshold()
    {
        // Fewer than the milestone's "three successful rear attacks" (section 15).
        var (telemetry, profile) = RecordRearAttacks(2);
        var director = new AdaptationDirector();

        director.Reevaluate(telemetry, profile);

        Assert.False(director.RearDefenseActive);
    }

    [Fact]
    public void Reevaluate_ActivatesRearDefense_AtTheMilestoneThreshold()
    {
        // "After three successful rear attacks..." (section 15).
        var (telemetry, profile) = RecordRearAttacks(3);
        var director = new AdaptationDirector();

        director.Reevaluate(telemetry, profile);

        Assert.True(director.RearDefenseActive);
    }

    [Fact]
    public void CreateOrderForNewEnemy_NeverAssignsGuardRear_WhenRearDefenseIsInactive()
    {
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();
        var director = new AdaptationDirector();

        for (int i = 0; i < 20; i++)
        {
            EnemyTacticalOrder order = director.CreateOrderForNewEnemy(profile);
            Assert.False(order.GuardRear);
        }
    }

    [Fact]
    public void CreateOrderForNewEnemy_SometimesAssignsGuardRear_WhenActiveWithHighConfidence()
    {
        var (telemetry, profile) = RecordRearAttacks(10);
        var director = new AdaptationDirector();
        director.Reevaluate(telemetry, profile);
        Assert.True(director.RearDefenseActive);

        // Randomized coverage (section 9) means not every spawn gets it,
        // but across many spawns at least one should.
        bool anyGuardedRear = false;
        for (int i = 0; i < 50; i++)
        {
            if (director.CreateOrderForNewEnemy(profile).GuardRear)
            {
                anyGuardedRear = true;
                break;
            }
        }

        Assert.True(anyGuardedRear);
    }

    [Fact]
    public void Reevaluate_DeactivatesRearDefense_AfterProfileDecaysBelowThreshold()
    {
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();
        var director = new AdaptationDirector();

        for (int i = 0; i < 10; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Rear);
            profile.Update(telemetry);
        }
        director.Reevaluate(telemetry, profile);
        Assert.True(director.RearDefenseActive);

        for (int i = 0; i < 30; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Front);
            profile.Update(telemetry);
            director.Reevaluate(telemetry, profile);
        }

        Assert.False(director.RearDefenseActive);
    }
}
