using System;
using System.Collections.Generic;

namespace DungeonCrawler.AI;

/// <summary>How an archetype delivers its attack (see docs/ENEMY_AI.md).</summary>
public enum AttackPattern
{
    /// <summary>Short wind-up, then a strike that lands if the player is still within reach.</summary>
    Melee,
    /// <summary>Short wind-up, then a tracking shot that lands if the player is still within range.</summary>
    Ranged,
    /// <summary>Long telegraphed wind-up along a row or column, then a dash that hits whatever is on the line; stunned afterwards.</summary>
    Charge,
    /// <summary>Long telegraphed wind-up marking an area around the player's tile; the blast hits only if the player is still inside it.</summary>
    Blast,
    /// <summary>Melee bash from behind a shield that blocks most damage from the front.</summary>
    ShieldBash
}

/// <summary>
/// Base definition of one enemy archetype. Every combat number an enemy uses
/// comes from here, so the game, the in-engine duel measurements and the
/// simulated player (Backend/simulated_player.py) all describe the same enemy.
/// </summary>
public sealed record ArchetypeDefinition(
    string Behavior,
    string ClassId,
    string DisplayName,
    AttackPattern Pattern,
    int MaxHp,
    int Attack,
    int Defense,
    float Speed,
    int VisionRange,
    int AttackRange,
    double WindupSeconds,
    double CooldownSeconds,
    double RecoverySeconds,
    int Reward,
    // Ranged/Blast archetypes hold a distance band from the player.
    int PreferredMinDistance = 0,
    int PreferredMaxDistance = 0,
    // Blast: Chebyshev radius of the marked area (1 = 3x3 tiles).
    int AreaRadius = 0,
    // ShieldBash: fraction of damage that still gets through a frontal hit.
    float FrontDamageFactor = 1f,
    // ShieldBash: seconds per 90-degree turn toward the player.
    double TurnSeconds = 0);

/// <summary>
/// The archetype catalog plus the encounter modifiers the genome evolves
/// (difficulty scale, tempo, elite variants, pack size). Kept free of Godot
/// types so it is covered by plain `dotnet test` (Tests/EnemyArchetypesTests.cs).
/// </summary>
public static class EnemyArchetypes
{
    public const string MeleeChaser = "melee_chaser";
    public const string MeleeHitRun = "melee_hit_run";
    public const string RangedKite = "ranged_kite";
    public const string RangedStrafe = "ranged_strafe";
    public const string MeleeCharger = "melee_charger";
    public const string RangedBomber = "ranged_bomber";
    public const string MeleeShield = "melee_shield";

    // Elite variants: tougher, hit harder, and become faster ("enraged")
    // once below half health.
    public const float EliteHpMultiplier = 1.5f;
    public const float EliteAttackMultiplier = 1.25f;
    public const float EliteEnrageTempo = 1.3f;
    public const float EliteEnrageHealthFraction = 0.5f;

    public const int MaxPackSize = 3;

    // The four original archetypes keep their original stats (HP, attack,
    // defence, speed, reach, cooldown); the wind-ups are new.
    private static readonly ArchetypeDefinition[] Definitions =
    [
        new(MeleeChaser, "brute", "Brute", AttackPattern.Melee,
            MaxHp: 28, Attack: 12, Defense: 4, Speed: 0.9f, VisionRange: 6, AttackRange: 1,
            WindupSeconds: 0.30, CooldownSeconds: 0.85, RecoverySeconds: 0, Reward: 18),
        new(MeleeHitRun, "skirmisher", "Skirmisher", AttackPattern.Melee,
            MaxHp: 18, Attack: 8, Defense: 1, Speed: 1.3f, VisionRange: 5, AttackRange: 1,
            WindupSeconds: 0.20, CooldownSeconds: 0.85, RecoverySeconds: 0, Reward: 12),
        new(RangedKite, "archer", "Archer", AttackPattern.Ranged,
            MaxHp: 24, Attack: 10, Defense: 6, Speed: 0.8f, VisionRange: 6, AttackRange: 5,
            WindupSeconds: 0.35, CooldownSeconds: 0.85, RecoverySeconds: 0, Reward: 15,
            PreferredMinDistance: 3, PreferredMaxDistance: 5),
        new(RangedStrafe, "sentinel", "Sentinel", AttackPattern.Ranged,
            MaxHp: 24, Attack: 10, Defense: 6, Speed: 0.8f, VisionRange: 6, AttackRange: 5,
            WindupSeconds: 0.35, CooldownSeconds: 0.85, RecoverySeconds: 0, Reward: 15,
            PreferredMinDistance: 3, PreferredMaxDistance: 6),
        new(MeleeCharger, "charger", "Charger", AttackPattern.Charge,
            MaxHp: 30, Attack: 18, Defense: 3, Speed: 1.0f, VisionRange: 7, AttackRange: 6,
            WindupSeconds: 0.70, CooldownSeconds: 2.40, RecoverySeconds: 1.20, Reward: 20),
        new(RangedBomber, "bomber", "Bomber", AttackPattern.Blast,
            MaxHp: 16, Attack: 16, Defense: 2, Speed: 0.8f, VisionRange: 7, AttackRange: 7,
            WindupSeconds: 0.90, CooldownSeconds: 2.20, RecoverySeconds: 0, Reward: 16,
            PreferredMinDistance: 4, PreferredMaxDistance: 7, AreaRadius: 1),
        new(MeleeShield, "shieldbearer", "Shieldbearer", AttackPattern.ShieldBash,
            MaxHp: 34, Attack: 11, Defense: 4, Speed: 0.7f, VisionRange: 5, AttackRange: 1,
            WindupSeconds: 0.45, CooldownSeconds: 1.30, RecoverySeconds: 0, Reward: 22,
            FrontDamageFactor: 0.25f, TurnSeconds: 0.5),
    ];

    private static readonly Dictionary<string, ArchetypeDefinition> ByBehavior = BuildIndex();

    /// <summary>Every archetype, in the order of the backend's BEHAVIOR_TYPES.</summary>
    public static IReadOnlyList<ArchetypeDefinition> All => Definitions;

    public static IReadOnlyList<string> Behaviors { get; } = Array.ConvertAll(Definitions, d => d.Behavior);

    public static bool IsKnown(string behavior) => ByBehavior.ContainsKey(behavior);

    /// <summary>The archetype for a behaviour key; unknown keys fall back to the chaser.</summary>
    public static ArchetypeDefinition Get(string behavior) =>
        ByBehavior.TryGetValue(behavior, out ArchetypeDefinition? definition) ? definition : ByBehavior[MeleeChaser];

    public static bool IsRangedPattern(AttackPattern pattern) => pattern is AttackPattern.Ranged or AttackPattern.Blast;

    /// <summary>
    /// Applies the evolved modifiers. Hit points and attack scale with the
    /// difficulty scale (and the elite multipliers), rounded half-to-even as
    /// .NET and Godot's Mathf.RoundToInt do; tempo speeds up movement and
    /// shortens every wind-up, cooldown and recovery.
    /// </summary>
    public static ArchetypeDefinition WithModifiers(ArchetypeDefinition baseDefinition, float difficultyScale, float tempo, bool elite)
    {
        float scale = Math.Max(0.1f, difficultyScale);
        float pace = Math.Max(0.1f, tempo);
        float hpMultiplier = elite ? EliteHpMultiplier : 1f;
        float attackMultiplier = elite ? EliteAttackMultiplier : 1f;

        return baseDefinition with
        {
            MaxHp = Math.Max(1, (int)Math.Round(baseDefinition.MaxHp * scale * hpMultiplier)),
            Attack = Math.Max(1, (int)Math.Round(baseDefinition.Attack * scale * attackMultiplier)),
            Speed = baseDefinition.Speed * pace,
            WindupSeconds = baseDefinition.WindupSeconds / pace,
            CooldownSeconds = baseDefinition.CooldownSeconds / pace,
            RecoverySeconds = baseDefinition.RecoverySeconds / pace,
            TurnSeconds = baseDefinition.TurnSeconds / pace,
        };
    }

    /// <summary>
    /// Picks an archetype from the genome's behaviour mix with one uniform
    /// draw in [0, 1). Unknown keys and non-positive weights are ignored;
    /// an empty or all-zero mix gives the chaser.
    /// </summary>
    public static string PickBehavior(IReadOnlyDictionary<string, float> mix, float roll)
    {
        float total = 0f;
        foreach (string behavior in Behaviors)
        {
            if (mix.TryGetValue(behavior, out float weight) && weight > 0f)
            {
                total += weight;
            }
        }

        if (total <= 0f)
        {
            return MeleeChaser;
        }

        float cumulative = 0f;
        string last = MeleeChaser;
        foreach (string behavior in Behaviors)
        {
            if (!mix.TryGetValue(behavior, out float weight) || weight <= 0f)
            {
                continue;
            }

            cumulative += weight / total;
            last = behavior;
            if (roll < cumulative)
            {
                return behavior;
            }
        }

        return last;
    }

    /// <summary>
    /// Enemies at one spawn point: the integer part of the evolved pack
    /// size, plus one more with probability equal to its fractional part,
    /// clamped to [1, MaxPackSize]. The expected count equals the gene.
    /// </summary>
    public static int PackCount(float packSize, float roll)
    {
        float clamped = Math.Clamp(packSize, 1f, MaxPackSize);
        int whole = (int)Math.Floor(clamped);
        float fraction = clamped - whole;
        int count = roll < fraction ? whole + 1 : whole;
        return Math.Clamp(count, 1, MaxPackSize);
    }

    public static bool RollElite(float eliteRate, float roll) => roll < Math.Clamp(eliteRate, 0f, 1f);

    private static Dictionary<string, ArchetypeDefinition> BuildIndex()
    {
        var index = new Dictionary<string, ArchetypeDefinition>();
        foreach (ArchetypeDefinition definition in Definitions)
        {
            index[definition.Behavior] = definition;
        }

        return index;
    }
}
