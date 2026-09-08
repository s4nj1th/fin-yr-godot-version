using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class EnemyBehaviorTelemetryTests
{
    [Fact]
    public void Snapshot_TracksCountersSeparately_PerBehaviorType()
    {
        var telemetry = new EnemyBehaviorTelemetry();

        telemetry.RecordSpawn("melee_chaser", adapted: false);
        telemetry.RecordSpawn("ranged_kite", adapted: true);
        telemetry.RecordAttackOnPlayer("melee_chaser", adapted: false, damageDealt: 12);
        telemetry.RecordAttackOnPlayer("ranged_kite", adapted: true, damageDealt: 5);

        var snapshot = telemetry.Snapshot();

        Assert.Equal(1, snapshot["melee_chaser"]["baseline"]["spawned"]);
        Assert.Equal(0, snapshot["melee_chaser"]["adapted"]["spawned"]);
        Assert.Equal(12, snapshot["melee_chaser"]["baseline"]["damage_dealt_to_player"]);

        Assert.Equal(0, snapshot["ranged_kite"]["baseline"]["spawned"]);
        Assert.Equal(1, snapshot["ranged_kite"]["adapted"]["spawned"]);
        Assert.Equal(5, snapshot["ranged_kite"]["adapted"]["damage_dealt_to_player"]);
    }

    [Fact]
    public void RecordPlayerHitOnEnemy_CountsRearHits_SeparatelyFromTotalHits_AndBySituation()
    {
        var telemetry = new EnemyBehaviorTelemetry();

        telemetry.RecordPlayerHitOnEnemy("melee_hit_run", adapted: true, damageDealt: 8, wasRearHit: true);
        telemetry.RecordPlayerHitOnEnemy("melee_hit_run", adapted: false, damageDealt: 8, wasRearHit: false);

        var stats = telemetry.Snapshot()["melee_hit_run"];

        Assert.Equal(1, stats["adapted"]["hits_taken_from_player"]);
        Assert.Equal(8, stats["adapted"]["damage_taken_from_player"]);
        Assert.Equal(1, stats["adapted"]["rear_hits_taken"]);

        Assert.Equal(1, stats["baseline"]["hits_taken_from_player"]);
        Assert.Equal(0, stats["baseline"]["rear_hits_taken"]);
    }

    [Fact]
    public void RecordKilledByPlayer_IncrementsOnlyTheTargetedArchetypeAndSituation()
    {
        var telemetry = new EnemyBehaviorTelemetry();

        telemetry.RecordSpawn("ranged_strafe", adapted: false);
        telemetry.RecordKilledByPlayer("ranged_strafe", adapted: false);

        var snapshot = telemetry.Snapshot();

        Assert.Equal(1, snapshot["ranged_strafe"]["baseline"]["killed_by_player"]);
        Assert.Equal(0, snapshot["ranged_strafe"]["adapted"]["killed_by_player"]);
        Assert.False(snapshot.ContainsKey("melee_chaser"));
    }

    [Fact]
    public void Reset_ClearsAllArchetypes()
    {
        var telemetry = new EnemyBehaviorTelemetry();
        telemetry.RecordSpawn("melee_chaser", adapted: false);

        telemetry.Reset();

        Assert.Empty(telemetry.Snapshot());
    }
}
