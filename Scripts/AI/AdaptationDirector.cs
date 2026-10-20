using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonCrawler.AI;

/// <summary>
/// Converts the player's playstyle profile into bounded tactical orders
/// for newly spawned enemies. See docs/ENEMY_AI_ADAPTATION_PLAN.md
/// section 9 (director + safeguards) and section 15 (first milestone).
///
/// Implements the first milestone's two countermeasures (rear defense,
/// last-known-position investigation), handed to newly spawned enemies,
/// plus live re-tasking: when a countermeasure switches on, the enemies
/// already near the player can pick it up (<see cref="SelectForRetask"/>),
/// and when it switches off, every enemy drops it. Ally coordination and
/// per-encounter difficulty limits remain out of scope.
/// </summary>
public sealed class AdaptationDirector
{
    // --- Safeguards (section 9) ---
    // Activation is gated on evidence count + confidence only — "is this
    // pattern real at all." The preference ratios (BackAttackPreference /
    // StealthPreference) are deliberately NOT also a hard gate here; they
    // instead modulate intensity in CreateOrderForNewEnemy. Requiring both
    // a ratio threshold AND a raw count to line up exactly at the
    // milestone's stated "three events" is over-constrained — with
    // section 5's decay applied to a ratio starting from zero, a ratio
    // threshold high enough to be meaningful (avoiding a false positive
    // from a few rear hits diluted across many unrelated attacks) doesn't
    // cross until well past 3 samples, contradicting section 15 directly.
    private const int MinRearAttackEvidence = 3;   // matches the milestone's "three successful rear attacks"
    private const float MinRearAttackConfidence = 0.5f;

    private const int MinLineOfSightBreakEvidence = 3;    // matches the milestone's "three successful line-of-sight breaks"
    private const float MinStealthConfidence = 0.5f;

    // Floor below which a ratio is too diluted among unrelated events to
    // trust, even once the raw count and confidence gates pass (e.g. 3
    // rear hits out of 100 total attacks shouldn't read as a rear-attack
    // playstyle). Comfortably below what 3 *consecutive, undiluted*
    // observations produce under the decay in PlayerPlaystyleProfile.
    private const float MinPreferenceRatioToAvoidDilution = 0.35f;

    private const double AdaptationCooldownSeconds = 8.0;
    // Section 9's "minimum tactic duration" safeguard, distinct from the
    // reactivation cooldown above even though they share the same timer
    // field (see the comment at its use site) — kept as one named
    // constant so the two concepts are still visible as separate reasons.
    private const double MinTacticDurationSeconds = AdaptationCooldownSeconds;
    private const float MaxTurnSpeedMultiplier = 2.0f;
    private const float MinTacticCoverage = 0.3f;  // even a fully-confident profile doesn't make every enemy react
    private const float MaxTacticCoverage = 0.9f;

    // Live re-tasking safeguards: only enemies near the player, nearest
    // first, and only a handful per activation, so an activation changes
    // the fight around the player without flipping the whole map at once.
    public const int RetaskRadius = 10;             // tiles, Manhattan distance
    public const int MaxRetaskedPerActivation = 6;

    private readonly Random _random;

    private double _timeSinceLastRearAdaptation = double.MaxValue;
    private double _timeSinceLastStealthAdaptation = double.MaxValue;

    public AdaptationDirector() : this(new Random())
    {
    }

    public AdaptationDirector(Random random)
    {
        _random = random;
    }

    public bool RearDefenseActive { get; private set; }
    public bool StealthCountermeasureActive { get; private set; }

    /// <summary>Which countermeasures switched on or off since the last <see cref="ConsumeChanges"/>.</summary>
    public readonly record struct CountermeasureChanges(bool RearActivated, bool RearDeactivated,
        bool StealthActivated, bool StealthDeactivated)
    {
        public bool Any => RearActivated || RearDeactivated || StealthActivated || StealthDeactivated;
    }

    /// <summary>An enemy already on the map that could be re-tasked.</summary>
    public readonly record struct RetaskCandidate(int Index, int DistanceToPlayer);

    private bool _rearActivated, _rearDeactivated, _stealthActivated, _stealthDeactivated;

    public void Tick(double delta)
    {
        _timeSinceLastRearAdaptation += delta;
        _timeSinceLastStealthAdaptation += delta;
    }

    /// <summary>
    /// Re-evaluates which countermeasures are currently justified by the
    /// evidence. Call this after telemetry changes, before spawning new
    /// enemies — orders are only ever handed out at spawn time (section 15).
    /// </summary>
    public void Reevaluate(PlayerBehaviorTelemetry telemetry, PlayerPlaystyleProfile profile)
    {
        bool rearEvidenceMet = telemetry.BackAttacks >= MinRearAttackEvidence
            && profile.BackAttackPreference > MinPreferenceRatioToAvoidDilution
            && profile.Confidence > MinRearAttackConfidence;

        if (rearEvidenceMet && !RearDefenseActive && _timeSinceLastRearAdaptation >= AdaptationCooldownSeconds)
        {
            RearDefenseActive = true;
            _rearActivated = true;
            _timeSinceLastRearAdaptation = 0.0;
        }
        else if (!rearEvidenceMet && RearDefenseActive && _timeSinceLastRearAdaptation >= MinTacticDurationSeconds)
        {
            // Profile decayed back down (player changed tactics) — matches
            // section 9's "tactic decay when the player changes style." The
            // MinTacticDurationSeconds floor is section 9's separate
            // "minimum tactic duration" safeguard: a single stray
            // observation right after activation can't immediately flip it
            // back off — _timeSinceLastRearAdaptation is reused here as
            // "time since this last turned on," doing double duty as both
            // the reactivation cooldown above and this deactivation floor.
            RearDefenseActive = false;
            _rearDeactivated = true;
        }

        bool stealthEvidenceMet = telemetry.LineOfSightBreaks >= MinLineOfSightBreakEvidence
            && profile.Confidence > MinStealthConfidence;

        if (stealthEvidenceMet && !StealthCountermeasureActive && _timeSinceLastStealthAdaptation >= AdaptationCooldownSeconds)
        {
            StealthCountermeasureActive = true;
            _stealthActivated = true;
            _timeSinceLastStealthAdaptation = 0.0;
        }
        else if (!stealthEvidenceMet && StealthCountermeasureActive && _timeSinceLastStealthAdaptation >= MinTacticDurationSeconds)
        {
            StealthCountermeasureActive = false;
            _stealthDeactivated = true;
        }
    }

    /// <summary>
    /// Returns the countermeasure switches since the previous call and
    /// clears them. An activation and a deactivation of the same
    /// countermeasure between two calls cancel out.
    /// </summary>
    public CountermeasureChanges ConsumeChanges()
    {
        var changes = new CountermeasureChanges(
            _rearActivated && RearDefenseActive, _rearDeactivated && !RearDefenseActive,
            _stealthActivated && StealthCountermeasureActive, _stealthDeactivated && !StealthCountermeasureActive);
        _rearActivated = _rearDeactivated = _stealthActivated = _stealthDeactivated = false;
        return changes;
    }

    /// <summary>
    /// Live re-tasking: which of the enemies already on the map take a
    /// countermeasure that has just switched on. Candidates within
    /// <see cref="RetaskRadius"/> of the player are considered nearest
    /// first; each takes the order with the same probability as a new spawn
    /// (the profile's confidence, clamped), and at most
    /// <see cref="MaxRetaskedPerActivation"/> are chosen. Returns their indices.
    /// </summary>
    public List<int> SelectForRetask(IEnumerable<RetaskCandidate> candidates, PlayerPlaystyleProfile profile)
    {
        float coverage = Math.Clamp(profile.Confidence, MinTacticCoverage, MaxTacticCoverage);
        var chosen = new List<int>();
        foreach (RetaskCandidate candidate in candidates.Where(c => c.DistanceToPlayer <= RetaskRadius)
                     .OrderBy(c => c.DistanceToPlayer).ThenBy(c => c.Index))
        {
            if (chosen.Count >= MaxRetaskedPerActivation)
            {
                break;
            }

            if (_random.NextDouble() < coverage)
            {
                chosen.Add(candidate.Index);
            }
        }

        return chosen;
    }

    /// <summary>Adds the rear-defense part of an order, scaled by the profile.</summary>
    public static void GrantRearDefense(EnemyTacticalOrder order, PlayerPlaystyleProfile profile)
    {
        order.GuardRear = true;
        order.TurnSpeedMultiplier = Lerp(1.2f, MaxTurnSpeedMultiplier, profile.BackAttackPreference);
    }

    /// <summary>Adds the stealth-countermeasure part of an order, scaled by the profile.</summary>
    public static void GrantStealthCountermeasure(EnemyTacticalOrder order, PlayerPlaystyleProfile profile,
        bool intercept = false)
    {
        order.SearchLastKnownPosition = true;
        order.InterceptRoute = intercept;
        order.PreferAlternateRoute = profile.StealthPreference > 0.7f;
        order.SearchRadius = Lerp(3f, 6f, profile.StealthPreference);
    }

    public static void RevokeRearDefense(EnemyTacticalOrder order)
    {
        order.GuardRear = false;
        order.TurnSpeedMultiplier = 1f;
    }

    public static void RevokeStealthCountermeasure(EnemyTacticalOrder order)
    {
        order.SearchLastKnownPosition = false;
        order.InterceptRoute = false;
        order.PreferAlternateRoute = false;
    }

    /// <summary>
    /// Produces the order a newly-spawned enemy starts with. Randomized
    /// per-enemy coverage (section 9) means not every spawn gets the active
    /// countermeasure even when one is active.
    /// </summary>
    /// <summary>
    /// Whether the stealth countermeasure intercepts the player on their
    /// route to the exit (true) or searches where they were last seen.
    /// </summary>
    public bool InterceptRoute { get; set; }

    public EnemyTacticalOrder CreateOrderForNewEnemy(PlayerPlaystyleProfile profile)
    {
        var order = EnemyTacticalOrder.None();
        float coverage = Math.Clamp(profile.Confidence, MinTacticCoverage, MaxTacticCoverage);

        if (RearDefenseActive && _random.NextDouble() < coverage)
        {
            GrantRearDefense(order, profile);
        }

        if (StealthCountermeasureActive && _random.NextDouble() < coverage)
        {
            GrantStealthCountermeasure(order, profile, InterceptRoute);
        }

        return order;
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
