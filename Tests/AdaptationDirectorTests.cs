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
    public void Reevaluate_DeactivatesRearDefense_AfterProfileDecaysBelowThreshold_OnceMinDurationHasPassed()
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

        // Past the minimum-tactic-duration floor (section 9) before the
        // player's tactic change is allowed to actually flip it back off.
        director.Tick(30.0);

        for (int i = 0; i < 30; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Front);
            profile.Update(telemetry);
            director.Reevaluate(telemetry, profile);
        }

        Assert.False(director.RearDefenseActive);
    }

    [Fact]
    public void Reevaluate_DoesNotDeactivateRearDefense_BeforeMinimumTacticDurationHasPassed()
    {
        // Section 9's "minimum tactic duration" safeguard: a countermeasure
        // that just turned on shouldn't be able to flip back off from a
        // single contradicting observation moments later.
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();
        var director = new AdaptationDirector();

        for (int i = 0; i < 3; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Rear);
            profile.Update(telemetry);
        }
        director.Reevaluate(telemetry, profile);
        Assert.True(director.RearDefenseActive);

        // No time advanced — well within the minimum-duration floor.
        telemetry.RecordAttack(hit: true, AttackDirection.Front);
        profile.Update(telemetry);
        director.Reevaluate(telemetry, profile);

        Assert.True(director.RearDefenseActive);
    }

    // --- Live re-tasking ---------------------------------------------------

    [Fact]
    public void ConsumeChanges_ReportsAnActivationOnce()
    {
        var (telemetry, profile) = RecordRearAttacks(10);
        var director = new AdaptationDirector();
        director.Reevaluate(telemetry, profile);

        AdaptationDirector.CountermeasureChanges first = director.ConsumeChanges();
        Assert.True(first.RearActivated);
        Assert.False(first.StealthActivated);
        Assert.False(director.ConsumeChanges().Any);
    }

    [Fact]
    public void ConsumeChanges_ReportsADeactivation()
    {
        var (telemetry, profile) = RecordRearAttacks(10);
        var director = new AdaptationDirector();
        director.Reevaluate(telemetry, profile);
        director.ConsumeChanges();
        director.Tick(30.0);
        for (int i = 0; i < 30; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Front);
            profile.Update(telemetry);
            director.Reevaluate(telemetry, profile);
        }

        AdaptationDirector.CountermeasureChanges changes = director.ConsumeChanges();
        Assert.True(changes.RearDeactivated);
        Assert.False(changes.RearActivated);
    }

    [Fact]
    public void SelectForRetask_OnlyConsidersNearbyEnemies_NearestFirst_AndIsCapped()
    {
        var (_, profile) = RecordRearAttacks(10); // high confidence: coverage clamps to 0.9
        var director = new AdaptationDirector(new System.Random(1));
        var candidates = new List<AdaptationDirector.RetaskCandidate>();
        for (int i = 0; i < 20; i++)
        {
            candidates.Add(new AdaptationDirector.RetaskCandidate(i, DistanceToPlayer: i));
        }

        List<int> chosen = director.SelectForRetask(candidates, profile);

        Assert.InRange(chosen.Count, 1, AdaptationDirector.MaxRetaskedPerActivation);
        Assert.All(chosen, index => Assert.True(index <= AdaptationDirector.RetaskRadius));
        Assert.Equal(chosen.OrderBy(i => i).ToList(), chosen);
    }

    [Fact]
    public void SelectForRetask_IgnoresEnemiesOutsideTheRadius()
    {
        var (_, profile) = RecordRearAttacks(10);
        var director = new AdaptationDirector(new System.Random(2));
        var far = new[] { new AdaptationDirector.RetaskCandidate(0, AdaptationDirector.RetaskRadius + 1) };
        Assert.Empty(director.SelectForRetask(far, profile));
    }

    [Fact]
    public void GrantAndRevoke_ChangeOnlyTheirOwnPartOfTheOrder()
    {
        var (_, profile) = RecordRearAttacks(10);
        var order = EnemyTacticalOrder.None();
        AdaptationDirector.GrantRearDefense(order, profile);
        AdaptationDirector.GrantStealthCountermeasure(order, profile);
        Assert.True(order.GuardRear && order.SearchLastKnownPosition);
        Assert.True(order.TurnSpeedMultiplier > 1f);

        AdaptationDirector.RevokeRearDefense(order);
        Assert.False(order.GuardRear);
        Assert.Equal(1f, order.TurnSpeedMultiplier);
        Assert.True(order.SearchLastKnownPosition);

        AdaptationDirector.RevokeStealthCountermeasure(order);
        Assert.False(order.SearchLastKnownPosition);
    }

    [Fact]
    public void StealthCountermeasure_CanIntercept_AndRevokingClearsIt()
    {
        var (_, profile) = RecordRearAttacks(10);
        var order = EnemyTacticalOrder.None();
        AdaptationDirector.GrantStealthCountermeasure(order, profile);
        Assert.False(order.InterceptRoute);

        AdaptationDirector.GrantStealthCountermeasure(order, profile, intercept: true);
        Assert.True(order.SearchLastKnownPosition && order.InterceptRoute);

        AdaptationDirector.RevokeStealthCountermeasure(order);
        Assert.False(order.SearchLastKnownPosition || order.InterceptRoute);
    }
}
