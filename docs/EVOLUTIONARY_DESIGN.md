# Evolutionary Design — Adaptive Enemy Encounters

This document is the in-repo source of truth for the evolution system under
`Backend/`. The quantitative claims below come from the simulation study in
the project's research paper (kept with the Phase 2 review materials).

## In one paragraph

The game adapts its **enemy encounters** to each player. A player-in-the-loop
evolutionary search proposes encounter designs; the player plays each one on
a **fixed dungeon**; a design is kept if and only if the player clears it (a
*minimal criterion*); new designs are mutated from kept ones. An online
Gaussian-process **surrogate** learns which designs this player can clear and
decides the confident cases itself, so the player's time is spent only where
the model is unsure. Within a session, a separate rule-based director adapts
enemy *tactics* to the player's habits ([ENEMY_AI.md](ENEMY_AI.md)).

## Terminology (Phase 2 panel review)

The panel noted that **"coevolution" is the wrong word** for this project and
suggested describing it as adaptive learning. We agree, and the code, docs
and paper no longer use it:

- In *minimal criterion coevolution* (Brant & Stanley 2017) two populations
  evolve — e.g. mazes and maze-solving agents — and each sets the criterion
  the other must meet. Here only the encounter designs evolve. The player
  adapts, but is not a population under selection.
- We therefore call the method **adaptive enemy evolution by minimal
  criterion search**: an evolutionary search whose only selection pressure is
  a pass/fail criterion, judged by the player. The project as a whole —
  search plus surrogate plus tactical director — is an **adaptive learning**
  system: it learns what each player can handle and adapts the enemies to it.
- The archive is still called the "hall of fame" in code and API field names
  (`hall_of_fame_size`) for backward compatibility; it is a plain archive.

## What evolves — and what does not (Phase 2 panel review)

The panel also noted that **evolving the enemies and the map together
defeats the purpose of evolution**, because the environment keeps changing
under the search. We agree: if the map changes, a failed trial cannot be
attributed to the enemies. So only the enemies evolve.

A **genome** (`Backend/genome.py`) has three evolved genes:

| Gene | Range | Effect in the game |
|---|---|---|
| `enemy_density` | 0 – 0.5 | share of rooms that receive an enemy |
| `enemy_difficulty_scale` | 0.6 – 1.6 | multiplier on every enemy's HP and attack |
| `enemy_behavior_mix` | weights summing to 1 | how often each archetype spawns (`melee_chaser`, `melee_hit_run`, `ranged_kite`, `ranged_strafe`) |

Everything else is **fixed** (`config.FIXED_ENVIRONMENT`) and sent unchanged
with every genome, so the client builds the same dungeon every trial:

- **Map**: 160×160 tiles, 280 room attempts, and **`map_seed = 123`**, which
  fixes the layout itself (116 rooms). The client seeds its generator with it
  (`GameManager.RegenerateDungeon`) and logs `[Dungeon] seed=… rooms=…` on
  every trial, so the fixed layout can be checked in the game log.
- **Resources**: trap, chest and heal densities of 0.15 each. The client rolls
  each room against cumulative thresholds (trap, chest, heal, enemy), so the
  enemy share is capped at 1 − 0.45 = 0.55, above `enemy_density`'s upper
  bound — every density value is expressed. (With the old defaults of 0.25
  each, every density above 0.25 produced the same dungeon.)
- **Why seed 123**: the seed was chosen by playing the bot through candidate
  seeds ([INTEGRATION_TESTING.md](INTEGRATION_TESTING.md)). On seed 123 the
  start-to-exit route passes enough rooms that damage taken grows with
  encounter difficulty, and even an easy crossing takes longer than the 15 s
  minimum. On some seeds the exit is so close that every design fails as
  "too fast"; on others the enemies are never met.

Mutation, random generation and LLM output all go through code that resets
the environment fields (`genome.with_fixed_environment`,
`genome.sanitize_params`), so no design can carry a different map.

## The search loop

`Backend/adaptive_evolution.py`, `AdaptiveEvolutionManager`:

1. **Propose.** Pick a uniformly random parent from the player's archive and
   mutate it (single parent, no crossover — as in minimal criterion search).
   While the archive is still empty there is no parent: a **random design**
   is served instead, until one passes (`BOOTSTRAP_STRATEGY=random`).
2. **Gate.** If the surrogate is ready, predict the pass probability `p` and
   its uncertainty. The candidate is **played** if `0.4 < p < 0.6` or its
   **epistemic** uncertainty exceeds 0.25; otherwise it is **auto-decided**
   (accepted if `p ≥ 0.5`) and the next candidate is proposed. After 25
   auto-decisions in a row a candidate is played anyway.
3. **Play.** The player plays the candidate; its telemetry is checked against
   the minimal criterion.
4. **Accept / reject.** A pass appends the design to the archive; a fail
   discards it. Nothing is ever removed or ranked.
5. **Learn.** The played outcome — and only played outcomes — is added to the
   surrogate's training data and the surrogate is refitted.

### Minimal criterion

A trial passes if and only if, checked in this order: the player did not
quit; reached the exit; took at least 15 s; and died at most 3 times
(`config.py`). Each failure reason is logged separately
(`quit_early`, `too_slow_abandoned`, `did_not_reach_exit`,
`too_fast_trivial`, `too_many_deaths`).

### Mutation

Each scalar enemy gene is perturbed with probability 0.6 by Gaussian noise
with standard deviation 15% of its range, then clamped; with probability 0.6
one behaviour-mix weight is perturbed by N(0, 0.2²), clipped at zero, and the
mix renormalized.

## Surrogate and the uncertainty gate

`Backend/surrogate.py` fits a Gaussian-process regressor (constant × RBF +
white-noise kernel, scikit-learn) to the player's played pass/fail outcomes,
once there are at least 5 labels of both classes.

**Why the gate uses epistemic uncertainty.** The GP's predictive variance is
exactly the sum of two parts: *epistemic* variance (what more data would
remove) and the fitted *noise* (how random pass/fail is for a design near the
player's limit). The original version gated on the total. That fails in
practice: the gate sends the hardest-to-predict designs to the player, those
outcomes are the noisiest, the fitted noise grows, and once its square root
passes the 0.25 threshold the total-uncertainty gate can never auto-decide
again. In the simulation study (50 runs per condition) this happened in 94%
of the intermediate player's runs; with the original settings the surrogate
decided only 15 designs in 100 playthroughs. Gating on the epistemic part
(`SURROGATE_GATE_UNCERTAINTY=epistemic`, the default) gave an eight-fold
larger archive for the same playthroughs, with archive contamination not
significantly different from playing everything. `total` remains available for
comparison.

**The trade-off to know about.** Because the gate plays exactly the designs
the surrogate cannot predict, those designs sit near the player's limit: in
the simulation the intermediate player failed 34% of played designs, against
12% when every design is played. This is the price of learning fast.

**Honest limitation**: a GP *classifier* would give better-calibrated
probabilities for binary labels; the regressor on {0, 1} labels is a
pragmatic proxy that exposes a per-point variance the gate needs.

## Players

Each player has their own manager — archive, surrogate, counters — saved in
`Backend/data/players/<player_id>.json`. The client sends its player id
(`GameManager.PlayerId`, or `--player=NAME`); every endpoint takes a
`player_id` query parameter (default `default`). Designs evolved for one
player do not suit another (in the simulation, designs evolved for an expert
were failed 56% of the time by a novice), so separate archives are what
makes the adaptation personal.

The scripted bot plays as player `bot` by default, so **bot trials never
train a human player's surrogate**. Trials still carry
`evaluator_type = human | bot`, counted separately in `/api/stats`.

`data/mcc_state.json`, the single archive of the first version, is not
loaded: its designs were judged on maps that changed between trials, so
their outcomes do not carry over to the fixed environment.

## Non-stationary players (optional)

An archive never forgets, so if a player's skill drops, designs that were
clearable stay in it. `SURROGATE_WINDOW=N` fits the surrogate on the last N
outcomes only, and `FILTER_PARENTS_BY_SURROGATE=1` then draws parents only
from archived designs it still predicts the player can clear. In the
simulation (window 30) this lowered the played failure rate after a skill
drop from 68% to 44%, but did not clean the archive. Both are off by default.

## LLM-guided mutation — optional, off by default, not load-bearing

`Backend/llm_mutation.py` can replace Gaussian mutation with a language-model
call (Anthropic, OpenAI or a local Ollama model) that sees the parent's
enemy genes, their bounds and the last 10 played outcomes, and returns one
mutated design as JSON.

- `LLM_MUTATION_MODE` = `auto` (use it only if configured) / `on` / `off`.
  A fresh checkout with no `.env` never calls an LLM.
- The reply is untrusted: parsed defensively, clamped to bounds, environment
  fields ignored, mix renormalized; any failure falls back to Gaussian
  mutation. Uses and failures are counted in `/api/stats`.
- **Measured effect**: in a 5-seed pilot with a local 4-billion-parameter
  model, LLM offspring passed 83% of the time against 90% for Gaussian
  mutation — no detectable difference. (Before the review, with a prompt that
  also exposed the map genes, it did significantly worse: 58% against 80%.)
  It stays an ablation arm.

## Scripted bot evaluator

`Scripts/AI/BotPlayerPolicy.cs` can drive the player instead of the keyboard
(`--autoplay`), through the same game code and the same API calls as a human.
`AutoplayTimeScale` speeds up the game clock; `AutoplayMaxTrials` makes the
process quit after N trials.

**Honest limitation**: the bot is deliberately simple — breadth-first search
straight to the exit, backing away from the nearest enemy below 30% health.
It knows where the exit is, does not fight on purpose and does not explore,
so it meets far fewer enemies than a person exploring under fog of war. Its
results are useful for testing the integration and for volume, not as a
stand-in for human players.

## Telemetry → minimal criterion

`GameManager.cs` reports a trial on exactly one of three endings: reaching
the exit, pressing R (quit), or the 240 s timeout without reaching the exit.
`_evaluate_minimal_criterion` keeps them apart (`quit_early` vs
`too_slow_abandoned`).

## Scope boundaries

- Parameter evolution over a fixed generator and a fixed map, by design.
- A pass/fail archive, not a quality-diversity grid; the criterion bounds
  difficulty only from above, so archives contain many comfortable designs.
- All quantitative results so far come from the simulated player; a human
  study (protocol in the paper, Sec. VI) is the next step.
