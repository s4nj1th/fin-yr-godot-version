namespace DungeonCrawler.AI;

/// <summary>
/// Structured, lifetime-total counters of demonstrated player behavior.
/// Field names and shape match docs/ENEMY_AI_ADAPTATION_PLAN.md section 5.
///
/// Per that section, a single exponentially-weighted running tendency is
/// "sufficient for the first implementation" in place of the fuller
/// multi-window scheme (recent 30s / recent 3 encounters / per-dungeon /
/// per-run) also described there — this class keeps the raw lifetime
/// counts (useful for debug display and minimum-evidence checks) while
/// <see cref="PlayerPlaystyleProfile"/> applies the decay.
/// </summary>
public sealed class PlayerBehaviorTelemetry
{
    public int Attacks;
    public int Hits;
    public int Misses;
    public int BackAttacks;
    public int FrontAttacks;
    public int SideAttacks;

    public int LineOfSightBreaks;
    public int HiddenAttackAttempts;
    public int SuccessfulAmbushes;
    public int ObstacleApproaches;
    public int Retreats;
    public int KitingEvents;
    public int DirectionChanges;

    public int EncountersStarted;
    public int EncountersEnded;
    public int EnemiesKilled;
    public int TimesDamaged;

    public double TimeObserved;
    public double TimeObservedNearEnemies;
    public double TimeHiddenNearEnemies;

    public void RecordAttack(bool hit, AttackDirection direction)
    {
        Attacks++;
        if (hit)
        {
            Hits++;
            switch (direction)
            {
                case AttackDirection.Rear:
                    BackAttacks++;
                    break;
                case AttackDirection.Front:
                    FrontAttacks++;
                    break;
                default:
                    SideAttacks++;
                    break;
            }
        }
        else
        {
            Misses++;
        }
    }

    public void RecordLineOfSightBreak() => LineOfSightBreaks++;

    public void RecordAmbush(bool succeeded)
    {
        HiddenAttackAttempts++;
        if (succeeded)
        {
            SuccessfulAmbushes++;
        }
    }

    public void RecordRetreat(bool wasKiting)
    {
        Retreats++;
        if (wasKiting)
        {
            KitingEvents++;
        }
    }

    public void RecordProximityTick(double delta, bool hiddenFromAwareEnemy, bool observedByAnyEnemy)
    {
        TimeObserved += delta;
        if (observedByAnyEnemy)
        {
            TimeObservedNearEnemies += delta;
        }
        else if (hiddenFromAwareEnemy)
        {
            TimeHiddenNearEnemies += delta;
        }
    }

    public void RecordDirectionChange() => DirectionChanges++;

    public void RecordEncounterStarted() => EncountersStarted++;

    public void RecordEncounterEnded() => EncountersEnded++;

    public void RecordEnemyKilled() => EnemiesKilled++;

    public void RecordPlayerDamaged() => TimesDamaged++;
}
