# Evolutionary Design — Human-Coupled Minimal Criterion Coevolution

This document is the in-repo source of truth for the evolutionary system under
`Backend/`. Earlier drafts of this code cited an external, untracked planning
document (`Idea/Idea.md`, gitignored, never committed); those citations have
been replaced with references to this file so the rationale actually resolves
for anyone who clones the repo.

## What evolves

A **genome** (`Backend/genome.py`) is a flat dict of dungeon-generation and
encounter parameters — map size, room-attempt count, trap/chest/heal/enemy
tile density, an `enemy_behavior_mix` distribution over the four enemy AI
archetypes (`melee_chaser`, `melee_hit_run`, `ranged_kite`, `ranged_strafe`),
and an `enemy_difficulty_scale` multiplier on enemy HP/attack. The Godot
client's existing procedural room-and-corridor generator
(`Scripts/Core/GameManager.cs`) is the **phenotype decoder**: it consumes a
genome's params directly instead of the old hardcoded constants.

**Honest limitation, stated plainly**: this is parameter evolution over a
fixed generator, not structural/topological evolution of level layouts (a
room-graph genome with evolvable connectivity would be a stronger claim, and
is a natural next step — not yet built). Say this precisely in the thesis
rather than overclaiming "evolves levels."

## Algorithm: Minimal Criterion Coevolution (MCC)

Following Brant & Stanley (2017), *"Minimal Criterion Coevolution: A New
Approach to Open-Ended Search"*. Rather than a fixed-size population with
tournament selection, `Backend/mcc.py`'s `MCCManager` maintains an
ever-growing **Hall of Fame** (archive) of genomes that have passed a minimal
pass/fail criterion:

1. **Propose**: pick a uniformly random parent from the Hall of Fame (or a
   seeded default genome if it's empty), mutate it.
2. **Evaluate**: hand the candidate to the live player as the next dungeon.
   When the trial ends, its telemetry is checked against the minimal
   criterion (`_evaluate_minimal_criterion`): reached the exit, wasn't
   manually quit, wasn't trivially fast or abandoned-slow, didn't rack up too
   many deaths.
3. **Accept/reject**: a pass appends the genome to the Hall of Fame; a fail
   discards it. There is no death/replacement of existing members and no
   crossover — MCC mutates a single parent per offspring by design (Brant &
   Stanley 2017), not as a missing feature.

This is deliberately **not** a textbook population GA: the "population" is a
monotonically growing archive, and the "second coevolving species" MCC
normally pairs content against (Brant & Stanley's original paper coevolves
mazes against maze-solving agents) is replaced here by a live human player.
Describe it in exactly these terms in the writeup — as human-coupled MCC, not
as generic coevolution.

## Mutation

Per-field Gaussian perturbation (`genome.mutate`), probability
`MUTATION_RATE` per field, clamped to `GENOME_BOUNDS`, plus a probabilistic
nudge-and-renormalize of one `enemy_behavior_mix` weight.

## Surrogate-gated evaluation (reduces real-playthrough cost)

A real playthrough costs the player several minutes; illuminating a useful
archive needs many evaluations. `Backend/surrogate.py` fits a
`GaussianProcessRegressor` (RBF + White kernel) on real pass/fail outcomes
once at least `SURROGATE_MIN_LABELS` exist, and predicts `(prob, std)` for
new candidates. If the surrogate is confident (`prob` far from 0.5, low
`std`), the candidate is auto-accepted/rejected for free; only genuinely
uncertain candidates are spent on the player (`MCCManager._needs_real_evaluation`,
`_auto_decide`). Only real, human-verified outcomes are ever used to retrain
the surrogate — auto-decided outcomes are never fed back in, so calibration
error can't compound against itself.

**Honest limitation**: a proper Gaussian Process *classifier* would give
calibrated probabilities for binary labels; `GaussianProcessRegressor` fit
directly on `{0,1}` labels is a pragmatic proxy that also happens to expose
per-point variance, which the classifier doesn't. This is a documented
simplification, not an oversight — see `surrogate.py`'s module docstring.

## LLM-guided mutation — optional, off by default, not load-bearing

`Backend/llm_mutation.py` can replace random Gaussian mutation with an LLM
call (Anthropic / OpenAI / local Ollama) that proposes a targeted mutation
based on recent pass/fail history. This exists as an **optional, ablatable
arm**, not part of the core evolutionary claim:

- Controlled by `LLM_MUTATION_MODE` (`auto` / `on` / `off`) — `off` is the
  ablation control, and `auto` (the default) only activates if a key is
  actually configured. **A fresh checkout with no `.env` file runs Phase 1+2
  only — zero LLM calls, ever.**
- Every LLM output is untrusted and passed through `genome.sanitize_params`
  before use — a malformed or adversarial response can't produce an
  out-of-bounds genome or crash the server.
- Any failure (disabled, no key, network error, bad JSON) falls back
  transparently to `genome.mutate(parent)`; `MCCManager` counts
  `llm_mutations_used` vs `random_mutations_used` vs `llm_mutation_failures`
  so "LLM-guided vs. random mutation" stays a measurable comparison in
  `/api/stats`, not an assumed improvement.

This keeps the project's "no LLM in the core system" requirement true by
construction: the archive, the minimal criterion, the surrogate, and the
default mutation operator are all LLM-free, and the LLM path is a strictly
optional, independently-toggleable extra that the core loop never depends on.

## Telemetry → minimal criterion mapping

`Scripts/Core/GameManager.cs` accumulates telemetry for the current trial
(elapsed time, damage taken, deaths, kills, chests, traps, exploration %) and
reports it via `ApiClient.ReportOutcomeAsync` on exactly one of three trial
endings: reaching the exit tile (`exit_reached=true, quit_early=false`),
pressing R to manually regenerate (`exit_reached=false, quit_early=true`), or
hitting the `MAX_TRIAL_SECONDS` timeout without finding the exit
(`exit_reached=false, quit_early=false`) — a passive timeout is not the same
signal as a deliberate quit, and `_evaluate_minimal_criterion` distinguishes
them (`quit_early` vs. `too_slow_abandoned`) accordingly.

## Known scope boundaries (for the thesis, not swept under the rug)

- Flat-parameter genome, not a structural/topological one — see above.
- No behavioral-descriptor archive (MAP-Elites-style quality-diversity) yet;
  this is a pass/fail archive, not an illuminated grid. A natural extension.
- Single real evaluator (the live player) — no scripted bot agents yet to
  scale up archive growth or to validate bot-vs-human telemetry as a proxy.
- No automated C# tests (Godot's Node lifecycle makes `GameManager`/
  `EnemyController` hard to unit-test without a Godot test runner); the
  Python backend has automated tests (`Backend/tests/`), the C# side does not.
