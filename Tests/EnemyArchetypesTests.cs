using System.Collections.Generic;
using DungeonCrawler.AI;
using Xunit;

namespace DungeonCrawler.AI.Tests;

public class EnemyArchetypesTests
{
    [Fact]
    public void Catalog_ListsSevenArchetypes_InBackendOrder()
    {
        Assert.Equal(
            new[] { "melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe", "melee_charger", "ranged_bomber", "melee_shield" },
            EnemyArchetypes.Behaviors);
    }

    [Fact]
    public void OriginalArchetypes_KeepTheirOriginalStats()
    {
        ArchetypeDefinition chaser = EnemyArchetypes.Get(EnemyArchetypes.MeleeChaser);
        Assert.Equal((28, 12, 4, 1), (chaser.MaxHp, chaser.Attack, chaser.Defense, chaser.AttackRange));
        ArchetypeDefinition hitRun = EnemyArchetypes.Get(EnemyArchetypes.MeleeHitRun);
        Assert.Equal((18, 8, 1), (hitRun.MaxHp, hitRun.Attack, hitRun.Defense));
        ArchetypeDefinition kite = EnemyArchetypes.Get(EnemyArchetypes.RangedKite);
        Assert.Equal((24, 10, 6, 5), (kite.MaxHp, kite.Attack, kite.Defense, kite.AttackRange));
        Assert.Equal(0.85, kite.CooldownSeconds);
    }

    [Fact]
    public void UnknownBehavior_FallsBackToChaser()
    {
        Assert.Equal(EnemyArchetypes.MeleeChaser, EnemyArchetypes.Get("dragon").Behavior);
        Assert.False(EnemyArchetypes.IsKnown("dragon"));
    }

    [Fact]
    public void WithModifiers_ScalesStatsAndTimings()
    {
        ArchetypeDefinition chaser = EnemyArchetypes.Get(EnemyArchetypes.MeleeChaser);
        ArchetypeDefinition modified = EnemyArchetypes.WithModifiers(chaser, difficultyScale: 1.5f, tempo: 2f, elite: false);

        Assert.Equal(42, modified.MaxHp);
        Assert.Equal(18, modified.Attack);
        Assert.Equal(chaser.Defense, modified.Defense);
        Assert.Equal(1.8f, modified.Speed, 3);
        Assert.Equal(0.15, modified.WindupSeconds, 6);
        Assert.Equal(0.425, modified.CooldownSeconds, 6);
    }

    [Fact]
    public void WithModifiers_RoundsHalfToEven_LikeTheEngine()
    {
        // 18 * 0.75 = 13.5 -> 14 (even); 8 * 0.75 = 6.
        ArchetypeDefinition hitRun = EnemyArchetypes.WithModifiers(EnemyArchetypes.Get(EnemyArchetypes.MeleeHitRun), 0.75f, 1f, false);
        Assert.Equal(14, hitRun.MaxHp);
        Assert.Equal(6, hitRun.Attack);
        // 30 * 0.85 = 25.5 -> 26 (even).
        ArchetypeDefinition charger = EnemyArchetypes.WithModifiers(EnemyArchetypes.Get(EnemyArchetypes.MeleeCharger), 0.85f, 1f, false);
        Assert.Equal(26, charger.MaxHp);
    }

    [Fact]
    public void Elite_IsTougherAndHitsHarder()
    {
        ArchetypeDefinition kite = EnemyArchetypes.Get(EnemyArchetypes.RangedKite);
        ArchetypeDefinition elite = EnemyArchetypes.WithModifiers(kite, 1f, 1f, elite: true);
        Assert.Equal(36, elite.MaxHp);
        Assert.Equal(12, elite.Attack); // 12.5 -> 12 (even)
    }

    [Fact]
    public void PickBehavior_FollowsCumulativeWeights_AndIgnoresUnknownKeys()
    {
        var mix = new Dictionary<string, float>
        {
            ["melee_chaser"] = 0.25f,
            ["dragon"] = 5f,
            ["ranged_bomber"] = 0.75f
        };

        Assert.Equal("melee_chaser", EnemyArchetypes.PickBehavior(mix, 0.1f));
        Assert.Equal("ranged_bomber", EnemyArchetypes.PickBehavior(mix, 0.3f));
        Assert.Equal("ranged_bomber", EnemyArchetypes.PickBehavior(mix, 0.999f));
    }

    [Fact]
    public void PickBehavior_EmptyOrZeroMix_GivesChaser()
    {
        Assert.Equal("melee_chaser", EnemyArchetypes.PickBehavior(new Dictionary<string, float>(), 0.5f));
        Assert.Equal("melee_chaser", EnemyArchetypes.PickBehavior(new Dictionary<string, float> { ["ranged_kite"] = 0f }, 0.5f));
    }

    [Theory]
    [InlineData(1.0f, 0.99f, 1)]
    [InlineData(1.4f, 0.39f, 2)]
    [InlineData(1.4f, 0.41f, 1)]
    [InlineData(2.5f, 0.2f, 3)]
    [InlineData(3.0f, 0.0f, 3)]
    [InlineData(0.2f, 0.0f, 1)]
    [InlineData(9.0f, 0.0f, 3)]
    public void PackCount_UsesFractionalPartAsProbability(float packSize, float roll, int expected)
    {
        Assert.Equal(expected, EnemyArchetypes.PackCount(packSize, roll));
    }

    [Fact]
    public void RollElite_ComparesAgainstRate()
    {
        Assert.True(EnemyArchetypes.RollElite(0.3f, 0.29f));
        Assert.False(EnemyArchetypes.RollElite(0.3f, 0.31f));
        Assert.False(EnemyArchetypes.RollElite(0f, 0f));
    }
}
