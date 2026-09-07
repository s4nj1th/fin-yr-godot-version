using System;

namespace DungeonCrawler.AI;

/// <summary>
/// Normalized (0.0-1.0) tendencies derived from <see cref="PlayerBehaviorTelemetry"/>,
/// each paired with a confidence value. Field names match
/// docs/ENEMY_AI_ADAPTATION_PLAN.md section 6.
///
/// Recency is applied via a simple exponential-decay blend on every update
/// (`new = old * DecayFactor + observation * (1 - DecayFactor)`), which
/// section 5 explicitly calls "sufficient for the first implementation" in
/// place of the fuller multi-window scheme also described there.
/// </summary>
public sealed class PlayerPlaystyleProfile
{
    private const float DecayFactor = 0.8f;
    // 3 matches the plan's own first-milestone acceptance criterion
    // (section 15: "after three successful rear attacks" / "three
    // successful line-of-sight breaks") — confidence should be usably
    // high by exactly that many events, not still ramping up.
    private const int MinSamplesForFullConfidence = 3;

    public float BackAttackPreference;
    public float StealthPreference;
    public float DirectAttackPreference;
    public float Aggression;
    public float RetreatPreference;
    public float KitingPreference;
    public float ExplorationPreference;

    public float Confidence;

    /// <summary>
    /// Recomputes every score from the current telemetry snapshot and
    /// blends it into the running profile. Cheap enough to call after
    /// every relevant event rather than on a fixed timer.
    /// </summary>
    public void Update(PlayerBehaviorTelemetry telemetry)
    {
        Blend(ref BackAttackPreference, SafeRatio(telemetry.BackAttacks, telemetry.Hits));
        Blend(ref DirectAttackPreference, SafeRatio(telemetry.FrontAttacks, telemetry.Hits));
        Blend(ref StealthPreference, SafeRatio(telemetry.TimeHiddenNearEnemies, telemetry.TimeHiddenNearEnemies + telemetry.TimeObservedNearEnemies));
        Blend(ref Aggression, SafeRatio(telemetry.Attacks, Math.Max(1, telemetry.EncountersStarted)));
        Blend(ref RetreatPreference, SafeRatio(telemetry.Retreats, Math.Max(1, telemetry.Attacks + telemetry.Retreats)));
        Blend(ref KitingPreference, SafeRatio(telemetry.KitingEvents, Math.Max(1, telemetry.Attacks)));

        // Confidence is a direct (not further-decayed) function of evidence
        // quantity — it should already be usable right at the milestone's
        // stated evidence count, not lag behind it through an additional
        // smoothing pass. Hits and line-of-sight breaks are counted
        // together since either is a meaningful, countable interaction the
        // director's two countermeasures key off of (section 15).
        double evidence = telemetry.Hits + telemetry.LineOfSightBreaks;
        Confidence = Clamp01((float)(evidence / MinSamplesForFullConfidence));
    }

    public void UpdateExploration(float explorationPercent)
    {
        Blend(ref ExplorationPreference, Clamp01(explorationPercent / 100f));
    }

    private static void Blend(ref float score, float observation)
    {
        score = (score * DecayFactor) + (observation * (1f - DecayFactor));
    }

    private static float SafeRatio(double numerator, double denominator)
    {
        return denominator <= 0 ? 0f : Clamp01((float)(numerator / denominator));
    }

    private static float SafeRatio(int numerator, int denominator)
    {
        return denominator <= 0 ? 0f : Clamp01(numerator / (float)denominator);
    }

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

    public string DebugSummary()
    {
        return $"Rear attack: {BackAttackPreference:F2}, confidence {Confidence:F2}\n" +
               $"Stealth: {StealthPreference:F2}, confidence {Confidence:F2}\n" +
               $"Aggression: {Aggression:F2}, confidence {Confidence:F2}\n" +
               $"Kiting: {KitingPreference:F2}, confidence {Confidence:F2}";
    }
}
