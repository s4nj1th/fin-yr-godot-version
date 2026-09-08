using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>
/// Per-archetype combat counters for the current trial, keyed by
/// <see cref="Entities.EnemyController.BehaviorType"/> (melee_chaser,
/// melee_hit_run, ranged_kite, ranged_strafe, ...) and split into
/// "baseline" vs "adapted" — whether the specific enemy involved in the
/// event carried an active AdaptationDirector tactical order at the time.
/// This is deliberately separate from <see cref="PlayerBehaviorTelemetry"/>:
/// that class answers "what has the player been doing" for the adaptation
/// director; this one answers "what did each enemy archetype actually do,
/// in each situation, in this trial" for offline analysis (see
/// docs/BEHAVIOR_ANALYTICS.md). Nothing here feeds back into runtime AI
/// decisions.
///
/// The baseline/adapted split is recorded at the moment of each event
/// (spawn, attack, hit-taken, kill) from that specific enemy's own
/// TacticalOrder — not approximated after the fact from trial-level
/// totals — so "how does an adapted ranged_kite differ from a baseline
/// one" is a real, per-enemy-attributed comparison, not a fabricated one.
/// </summary>
public sealed class SituationalCombatStats
{
    public int Spawned;
    public int AttacksOnPlayer;
    public int DamageDealtToPlayer;
    public int HitsTakenFromPlayer;
    public int DamageTakenFromPlayer;
    public int RearHitsTaken;
    public int KilledByPlayer;
}

public sealed class ArchetypeCombatStats
{
    public readonly SituationalCombatStats Baseline = new();
    public readonly SituationalCombatStats Adapted = new();

    public SituationalCombatStats For(bool adapted) => adapted ? Adapted : Baseline;
}

public sealed class EnemyBehaviorTelemetry
{
    private readonly Dictionary<string, ArchetypeCombatStats> _byBehavior = new();

    /// <summary>Call at the start of every trial — this telemetry is per-trial, not lifetime.</summary>
    public void Reset() => _byBehavior.Clear();

    public void RecordSpawn(string behaviorType, bool adapted) => GetOrCreate(behaviorType).For(adapted).Spawned++;

    public void RecordAttackOnPlayer(string behaviorType, bool adapted, int damageDealt)
    {
        SituationalCombatStats stats = GetOrCreate(behaviorType).For(adapted);
        stats.AttacksOnPlayer++;
        stats.DamageDealtToPlayer += damageDealt;
    }

    public void RecordPlayerHitOnEnemy(string behaviorType, bool adapted, int damageDealt, bool wasRearHit)
    {
        SituationalCombatStats stats = GetOrCreate(behaviorType).For(adapted);
        stats.HitsTakenFromPlayer++;
        stats.DamageTakenFromPlayer += damageDealt;
        if (wasRearHit)
        {
            stats.RearHitsTaken++;
        }
    }

    public void RecordKilledByPlayer(string behaviorType, bool adapted) => GetOrCreate(behaviorType).For(adapted).KilledByPlayer++;

    /// <summary>
    /// Flat, JSON-friendly snapshot sent up alongside the trial's
    /// TelemetryMetrics (enemy_behavior_breakdown — see
    /// Backend/models.py and docs/BEHAVIOR_ANALYTICS.md). Shape:
    /// {behaviorType: {"baseline": {counters}, "adapted": {counters}}}.
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> Snapshot()
    {
        var result = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();
        foreach (KeyValuePair<string, ArchetypeCombatStats> entry in _byBehavior)
        {
            result[entry.Key] = new Dictionary<string, Dictionary<string, int>>
            {
                ["baseline"] = ToDict(entry.Value.Baseline),
                ["adapted"] = ToDict(entry.Value.Adapted),
            };
        }

        return result;
    }

    private static Dictionary<string, int> ToDict(SituationalCombatStats stats) => new()
    {
        ["spawned"] = stats.Spawned,
        ["attacks_on_player"] = stats.AttacksOnPlayer,
        ["damage_dealt_to_player"] = stats.DamageDealtToPlayer,
        ["hits_taken_from_player"] = stats.HitsTakenFromPlayer,
        ["damage_taken_from_player"] = stats.DamageTakenFromPlayer,
        ["rear_hits_taken"] = stats.RearHitsTaken,
        ["killed_by_player"] = stats.KilledByPlayer,
    };

    private ArchetypeCombatStats GetOrCreate(string behaviorType)
    {
        if (!_byBehavior.TryGetValue(behaviorType, out ArchetypeCombatStats? stats))
        {
            stats = new ArchetypeCombatStats();
            _byBehavior[behaviorType] = stats;
        }

        return stats;
    }
}
