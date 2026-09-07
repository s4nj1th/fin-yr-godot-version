using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class PlayerPlaystyleProfileTests
{
    [Fact]
    public void Update_RaisesBackAttackPreference_AfterRepeatedRearAttacks()
    {
        // Scenario from docs/ENEMY_AI_ADAPTATION_PLAN.md section 14, Phase 7:
        // "Ten attacks from behind."
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();

        for (int i = 0; i < 10; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Rear);
            profile.Update(telemetry);
        }

        Assert.True(profile.BackAttackPreference > 0.6f, $"expected > 0.6, got {profile.BackAttackPreference}");
        Assert.True(profile.Confidence > 0.5f, $"expected > 0.5, got {profile.Confidence}");
    }

    [Fact]
    public void Update_DoesNotSpikeConfidence_FromASingleObservation()
    {
        // "Five rear attacks out of five attempts should not automatically
        // produce a permanent high-confidence conclusion" (section 6).
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();

        telemetry.RecordAttack(hit: true, AttackDirection.Rear);
        profile.Update(telemetry);

        Assert.True(profile.Confidence < 0.5f, $"expected sub-'coin flip' confidence after one sample, got {profile.Confidence}");
    }

    [Fact]
    public void Update_DecaysBackAttackPreference_WhenPlayerSwitchesToFrontAttacks()
    {
        // "If the player changes tactics, the enemy response should
        // weaken and eventually adapt again" (section 1).
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();

        for (int i = 0; i < 10; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Rear);
            profile.Update(telemetry);
        }

        float peakBackAttackPreference = profile.BackAttackPreference;

        for (int i = 0; i < 15; i++)
        {
            telemetry.RecordAttack(hit: true, AttackDirection.Front);
            profile.Update(telemetry);
        }

        Assert.True(profile.BackAttackPreference < peakBackAttackPreference,
            $"expected decay below {peakBackAttackPreference}, got {profile.BackAttackPreference}");
    }

    [Fact]
    public void Update_RaisesStealthPreference_WhenMostNearEnemyTimeIsHidden()
    {
        var telemetry = new PlayerBehaviorTelemetry();
        var profile = new PlayerPlaystyleProfile();

        for (int i = 0; i < 20; i++)
        {
            telemetry.RecordProximityTick(1.0, hiddenFromAwareEnemy: true, observedByAnyEnemy: false);
            profile.Update(telemetry);
        }

        Assert.True(profile.StealthPreference > 0.5f, $"expected > 0.5, got {profile.StealthPreference}");
    }
}
