# Adaptive Enemy AI — Design and Implementation

This document is the design of the within-session adaptive enemy AI (the
*tactical director*). It was written as a plan before the system existed;
phases 1 to 6 below are now implemented, and the in-game evaluation of
phase 7 has been run. The section **Implementation status** says what was
built and where it differs from the plan; the rest of the document is kept
as the design rationale. The baseline enemy behaviour is documented in
[ENEMY_AI.md](ENEMY_AI.md).

## Implementation status

| Plan | Implemented as |
|---|---|
| Player telemetry (§4–5) | `Scripts/AI/PlayerBehaviorTelemetry.cs`: hits by direction (front, side, rear), line-of-sight breaks, retreats, kiting. A hit counts as a rear hit when the struck enemy lies behind the player's heading and had not yet noticed the player (`AttackDirection.cs`). |
| Playstyle profile (§6) | `PlayerPlaystyleProfile.cs`: tendencies blended with exponential decay 0.8; a confidence that rises linearly to 1 after three hits or line-of-sight breaks. |
| Perception and last-known position (§7) | `EnemyPerception.cs`: unaware → tracking → investigating (3 s) → searching (5 s) → returning (3 s), by grid line of sight (`GridLineOfSight.cs`). |
| Pathfinding (§12) | Breadth-first search (`GridPathfinder.cs`). |
| Director and orders (§9) | `AdaptationDirector.cs`, `EnemyTacticalOrder.cs`. Safeguards as planned: evidence and confidence thresholds, an 8 s reactivation cooldown and equal minimum duration, and coverage equal to the confidence clamped to [0.3, 0.9]. |
| Rear defence (§10) | Activates after at least three rear hits with a rear-hit preference above 0.35 and confidence above 0.5 (not the 0.6 sketched below); shortens an enemy's remaining attack cooldown after a rear hit. |
| Hiding countermeasure (§11) | Activates after at least three line-of-sight breaks with confidence above 0.5, in two forms (`--stealth=`): **search** — go to the last known position and search around it; **intercept** (the default) — head for the earliest cell of the player's shortest route to the exit that the enemy can reach no later than the player, at least three cells ahead, or else guard the exit, and wait there for up to 20 s (`InterceptPlanner.cs`). Intercept is the plan's "guard a likely exit" / `CutOffEscape`. |
| Live re-tasking | When a countermeasure switches on, up to 6 enemies within 10 tiles of the player, nearest first, take it; all drop it when it switches off (`--director=live`, the default; `spawn` = new enemies only; `off`). |
| Evaluation (§14, phase 7) | `tools/evaluate_director.py`: a scripted bot that hides, 30 paired sessions per condition. Intercept lowered the exit rate from 92% to 83–85% and lengthened crossings (Holm-corrected p ≤ 0.017); search had no effect that survived correction ([INTEGRATION_TESTING.md](INTEGRATION_TESTING.md), section 5). |
| Not implemented | The composable `EnemyTactic` vocabulary of §8 (orders are flags on `EnemyTacticalOrder` instead); rolling windows other than the exponential decay; class-specific responses (scout, boss); group coordination such as sharing the last known position; occupancy checks for ordinary movement; LLM commentary (§13) — the director is fully deterministic and needs no network. |

In the human study the director is off in study 1 and on (with live
re-tasking) in one of the two conditions of study 2
([HUMAN_STUDY_PROTOCOL.md](HUMAN_STUDY_PROTOCOL.md)).

## 1. Design Goal

Enemies should respond to demonstrated player habits without becoming omniscient, unfair, or dependent on an LLM response during gameplay.

Examples:

- If the player repeatedly attacks enemies from behind, later enemies become better at protecting their rear and turning toward threats.
- If the player repeatedly breaks line of sight and hides behind obstacles, later enemies investigate the last known position, search nearby cells, and sometimes approach from another route.
- If the player changes tactics, the enemy response should weaken and eventually adapt again.

The system should learn tendencies from evidence, not make permanent conclusions after one event.

## 2. Recommended Architecture

Use four layers:

```text
Player actions and game events
              |
              v
       Telemetry collector
              |
              v
      Player playstyle profile
              |
              v
      Adaptation director
              |
              v
       Enemy tactical orders
              |
              v
       Enemy grid controller
```

### Telemetry collector

Records structured gameplay events such as movement, attacks, damage, line-of-sight changes, and enemy encounters.

### Player playstyle profile

Converts recent telemetry into normalized tendencies, such as rear-attack preference, stealth preference, aggression, retreat preference, and kiting.

### Adaptation director

Chooses which countermeasures to activate, how strong they should be, which enemies receive them, and how long they last.

### Enemy grid controller

Executes the tactical order through deterministic movement, perception, combat, and pathfinding rules. This remains the final authority for movement and combat decisions.

## 3. What the Code Was Missing (before this work)

At the time of the plan, [EnemyController.cs](../Scripts/Entities/EnemyController.cs) knows:

- Its integer `GridPos`.
- The player's exact position.
- The complete walkability grid.
- Its fixed behavior string and combat stats.

It did not know (all but the occupancy check are now implemented):

- Which direction the player is facing.
- Whether an attack came from the front, side, or rear.
- Whether the player was visible before an attack.
- Whether an obstacle caused the player to disappear.
- The player's last known position.
- Whether another enemy occupies a destination cell.
- How to route around an obstacle using a global path.

The current [GameManager.cs](../Scripts/Core/GameManager.cs) also attacks the nearest enemy in range and does not emit a structured player-attack event. These are the first instrumentation gaps to close.

## 4. Player Telemetry

Start with structured events instead of free-form commentary. Commentary can be generated from these events later, but the game should not try to infer important facts from text when the facts are available directly in code.

Useful events include:

```text
PlayerMoved
PlayerAttackStarted
PlayerAttackHit
PlayerAttackMissed
PlayerBrokeLineOfSight
PlayerEnteredCover
PlayerExitedCover
PlayerRetreated
EnemyEncounterStarted
EnemyEncounterEnded
PlayerKilledEnemy
PlayerTookDamage
```

Each event should include enough context to reconstruct the situation:

```text
run id
encounter id
timestamp
player grid position
player facing direction
enemy grid position, when relevant
enemy id and class, when relevant
line-of-sight state
obstacle or cover state
damage and outcome, when relevant
```

### Player facing direction

The player currently moves on a grid, but the main runtime does not store a facing direction. Add a direction field and update it whenever a valid movement input is applied:

```csharp
public Vector2I FacingDirection { get; private set; }
```

An attack event should capture the direction at the time of the attack. Do not infer facing from the enemy position after the attack, because the player may have moved or multiple enemies may be nearby.

### Detecting a rear attack

For an enemy at `enemyPosition`, player at `playerPosition`, and player facing vector `facingDirection`:

1. Calculate the direction from the player to the enemy.
2. Compare it with the player's facing direction.
3. Classify the attack as front, side, or rear.
4. Record whether the attack hit and how much damage it caused.

A rear attack should only count strongly when the enemy was not already facing or tracking the player. This prevents every attack from behind a currently engaged enemy from being labeled as a successful ambush.

## 5. Telemetry Data Model

Add a session-level collector, for example:

```csharp
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
    public int DirectionChanges;

    public double TimeObserved;
}
```

Use rolling windows rather than only lifetime totals:

- Recent 30 seconds.
- Recent three encounters.
- Current dungeon.
- Entire run.

Recent behavior should matter most. A simple weighted update is sufficient for the first implementation:

```text
new tendency = old tendency * 0.8 + latest observation * 0.2
```

The exact values should be tunable. The important properties are recency, bounded memory, and predictable decay.

## 6. Player Playstyle Profile

Convert telemetry into scores between `0.0` and `1.0`, with a separate confidence value.

```csharp
public sealed class PlayerPlaystyleProfile
{
    public float BackAttackPreference;
    public float StealthPreference;
    public float DirectAttackPreference;
    public float Aggression;
    public float RetreatPreference;
    public float KitingPreference;
    public float ExplorationPreference;

    public float Confidence;
}
```

Example measures:

```text
BackAttackPreference = successful rear attacks / successful attacks
StealthPreference = time hidden near enemies / time observed near enemies
Aggression = attacks initiated / encounters entered
KitingPreference = attacks followed by retreat movement / total attacks
```

Protect every score with minimum sample counts. Five rear attacks out of five attempts should not automatically produce a permanent high-confidence conclusion. Confidence should grow with observations and fall when recent behavior contradicts the old profile.

Expose the profile in a debug panel during development:

```text
Rear attack: 0.72, confidence 0.81
Stealth: 0.64, confidence 0.58
Aggression: 0.43, confidence 0.76
Kiting: 0.29, confidence 0.66
```

## 7. Enemy Perception and Last-Known Position

Adaptive cover behavior is impossible while enemies receive the player's exact current position every tick. Add an enemy perception object instead:

```csharp
public sealed class EnemyPerception
{
    public bool HasLineOfSight;
    public Vector2I ObservedPlayerPosition;
    public Vector2I LastKnownPlayerPosition;
    public int Distance;
    public bool PlayerRecentlySeen;
}
```

Enemies should move through explicit perception states:

```text
Unaware -> Tracking -> Investigating -> Searching -> Returning
```

A typical obstacle encounter should work like this:

1. The enemy sees the player.
2. The player moves behind a wall or obstacle.
3. The enemy stores the last visible position.
4. The enemy travels to that position.
5. If the player is absent, the enemy searches nearby cells.
6. The enemy may choose an alternate route or guard a likely exit.
7. The search ends after a timeout or when the player is seen again.

Line of sight should use the grid and a ray or cell traversal between the enemy and player. The exact implementation can start simple, but it must distinguish direct visibility from mere knowledge of the player's position.

## 8. Composable Enemy Tactics

Keep the existing behavior types as baseline movement styles, but stop treating every tactical combination as a new behavior string.

Use independent goals such as:

```csharp
public enum EnemyTactic
{
    DirectApproach,
    GuardRear,
    SearchLastKnownPosition,
    CircleObstacle,
    HoldDistance,
    CutOffEscape,
    ProtectAlly,
    Retreat
}
```

An enemy can then have a combination such as:

```text
base archetype: melee
movement style: aggressive
current tactic: guard rear
target knowledge: last known position
```

This allows adaptation without creating a separate hard-coded branch for every combination of class, range, and player tendency.

## 9. Adaptation Director and Tactical Orders

The director sits above individual enemies and converts the playstyle profile into bounded orders:

```csharp
public sealed class EnemyTacticalOrder
{
    public bool GuardRear;
    public bool SearchLastKnownPosition;
    public bool PreferAlternateRoute;
    public float TurnSpeedMultiplier;
    public float SearchRadius;
    public double Duration;
}
```

The director should control:

- Which countermeasure is active.
- How strong it is.
- Which enemies receive it.
- How long it lasts.
- When it expires.
- How quickly it decays.

Use safeguards:

- Minimum evidence threshold.
- Minimum confidence threshold.
- Cooldown between adaptations.
- Minimum tactic duration.
- Maximum adaptation strength.
- Tactic decay when the player changes style.
- Randomized enemy coverage so every enemy does not react identically.

Enemies should appear to learn from encounters, not instantly read the player's mind.

**Implemented — live re-tasking.** Besides handing orders to newly spawned
enemies, the director now re-tasks enemies already on the map
(`AdaptationDirector.ConsumeChanges`, `SelectForRetask`, `GameManager.ReevaluateDirector`):
when a countermeasure switches on, the living enemies within
`RetaskRadius` (10 tiles) of the player, nearest first, each take it with
the same probability as a new spawn (the profile's confidence, clamped to
[0.3, 0.9]), at most `MaxRetaskedPerActivation` (6) per activation; when a
countermeasure switches off, every enemy drops it. In the human study the
director is off in study 1 and on in one condition of study 2.

## 10. Countermeasure: Repeated Rear Attacks

Detect the following:

- Player facing direction.
- Enemy position relative to the player.
- Whether the enemy was aware of the player.
- Whether the attack hit.
- Whether the attack came from behind.

When the profile passes a threshold such as:

```text
BackAttackPreference > 0.6
Confidence > 0.5
```

The director can activate rear-defense tactics:

- Turn toward the player faster.
- Check behind after taking damage.
- Avoid standing with the back facing an open corridor.
- Guard the rear side while investigating.
- Use a nearby ally to cover the vulnerable side.
- Reduce the time spent in a vulnerable attack state.

Different enemy classes should respond differently:

```text
common enemy: turns faster
scout: checks flanks
elite: periodically guards its rear
boss: uses a clearly telegraphed anti-ambush phase
```

Do not give all enemies perfect 360-degree awareness. The response should improve awareness, not remove the player's opportunity to outplay the enemy.

## 11. Countermeasure: Repeated Obstacle Hiding

Detect:

- Line-of-sight breaks.
- Time spent hidden.
- Attacks made immediately after hiding.
- Successful escapes from an encounter.
- Repeated use of the same obstacle or route.

When the stealth profile passes its threshold, enemies can:

- Investigate the last known position.
- Search neighboring cells.
- Use an alternate path around an obstacle.
- Have one enemy investigate while another guards a likely exit.
- Reposition ranged enemies to create a clear firing lane.
- Remember recently searched cells.
- Share the last known location with nearby enemies.

The enemy should target a belief, not the player's exact current position:

```text
last seen at: (24, 17)
probable direction: east
confidence: 0.65
```

## 12. Pathfinding Requirement

The current enemy movement is greedy Manhattan movement. It can stall when a route around a wall temporarily increases or preserves distance.

Adaptive obstacle behavior requires a pathfinding service:

```text
GridPathfinder.FindPath(start, target, walkableGrid)
```

Use BFS initially because grid movement has uniform cost. Move to A\* when the map grows, tile costs are introduced, or many enemies need routes.

Possible tactical targets include:

- Direct player position.
- Last known player position.
- A cell on the opposite side of an obstacle.
- A predicted escape cell.
- A flank position.
- A guard position.
- A search position.

For the first version, pathfinding can be requested only when greedy movement fails or when an adaptive tactic is active. This keeps the normal case inexpensive.

## 13. Role of Gameplay Commentary and LLMs

Gameplay commentary is useful as a summary and debugging layer, but it should not be the primary runtime input for critical decisions.

Recommended flow:

1. The game records structured telemetry.
2. At the end of an encounter or at a safe interval, telemetry is summarized.
3. The summary is converted into approved playstyle labels.
4. The deterministic adaptation director applies bounded tactical orders.
5. Enemy controllers execute those orders locally.

Example commentary:

```text
The player broke line of sight after attacking three times.
The player has attacked from the rear five times in the current run.
The player usually retreats after dealing damage.
```

An LLM can help summarize or suggest labels, but it should not be called every frame or be required for a movement decision. The game must remain playable if the model is unavailable, slow, wrong, or disconnected.

## 14. Implementation Phases

### Phase 1: Instrumentation

Add player facing, attack events, hit/miss events, movement events, line-of-sight changes, last-known positions, and encounter boundaries. Do not change enemy behavior yet.

### Phase 2: Telemetry and profile

Add `PlayerBehaviorTelemetry`, rolling windows, `PlayerPlaystyleProfile`, confidence values, and a debug display. Confirm that the scores match recorded test scenarios.

### Phase 3: Perception

Replace perfect player knowledge with visible position, last-known position, and search states. Test this in the isolated AI scenes.

### Phase 4: Pathfinding

Add BFS or A\* and test direct routes, blocked routes, alternate routes, last-known-position searches, and multiple enemies.

### Phase 5: First adaptive tactics

Implement only rear defense and last-known-position investigation. Keep the effects mild and log every adaptation decision.

### Phase 6: Encounter director

Add tactical orders, group coordination, adaptation cooldowns, tactic decay, enemy variety, and difficulty limits.

### Phase 7: Evaluation

Create repeatable scenarios:

- Ten attacks from behind.
- Repeated hiding behind one obstacle.
- Switching from stealth to aggression.
- Random movement and attacks.
- Mixed tactics throughout one run.

Measure both whether the enemy adapts and whether the response remains fair and understandable.

Built: [BEHAVIOR_ANALYTICS.md](BEHAVIOR_ANALYTICS.md) instruments and
visualizes per-archetype combat outcomes split by baseline vs. adapted, and
`tools/evaluate_director.py` runs the repeatable scripted scenario for
repeated hiding (a bot that breaks line of sight, the same designs in every
condition) — see Implementation status above.

## 15. First Milestone

The first complete milestone should be:

> After three successful rear attacks, newly spawned enemies receive a higher rear-awareness value and turn toward the player more quickly. After three successful line-of-sight breaks, newly spawned enemies investigate the last known position using BFS or A\*.

This milestone is small enough to test while establishing the full architecture for richer adaptive behavior.

## 16. Success Criteria

The adaptive system is working when:

- The same player behavior produces a measurable profile change.
- Profile changes are visible in debug output.
- Enemies react only after sufficient evidence.
- New enemies receive the adaptation, and a bounded number of nearby existing enemies are re-tasked when a countermeasure switches on or off.
- A player can change strategy and reduce the previous countermeasure.
- Enemies use last-known information rather than omniscient current position.
- Obstacles can be navigated around intentionally.
- The game remains playable without an LLM connection.
- Automated scenarios produce repeatable, explainable results.
