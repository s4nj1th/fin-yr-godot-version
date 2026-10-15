# Human Study Protocol

This is the protocol for the study that has to precede any claim about how
real players experience the adaptive enemy evolution. Every quantitative
result so far comes from the simulated player
([EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md), research paper). The
tooling below is implemented and tested end to end with the scripted bot
(`python tools/integration_test.py --study`); **no human data has been
collected yet.**

The protocol must be approved by the institutional ethics committee, and
should be preregistered (for example on OSF) before the first participant.

## 1. Questions and hypotheses

Conditions, compared within participants:

- **MC**: minimal criterion search without the surrogate (every design is played);
- **GP**: minimal criterion search with the epistemic uncertainty gate (the default system).

Both use the random cold start, the fixed map and the 12-gene encounter
space. The adaptive tactical director is **off** in both (study mode turns it
off), so within-session adaptation cannot confound the comparison.

| | Hypothesis | Measure | Test |
|---|---|---|---|
| H1 | GP needs fewer playthroughs per archived design than MC | played trials / archived designs, per participant and condition | Wilcoxon signed-rank, one-sided, α = 0.05 |
| H2 | GP's archive is not much less clearable than MC's | pass rate in the replay block | paired bootstrap 95% CI of GP − MC; non-inferior if the lower bound is above −10 points |

Exploratory: played pass/fail by condition (logistic GEE with participants
as clusters, trial number as covariate); questionnaire items by condition
(Wilcoxon, Holm-corrected); and the fit of the simulated player (κ per
participant), which tells us how far the simulation results can be trusted.

## 2. Participants

- Adults (18+) with any amount of action-game experience, recruited by
  announcement; no compensation that depends on performance.
- Target **24 participants**. In the simulation the GP condition archives
  several times more designs per playthrough than MC, so H1 is expected to
  have a large effect; for a standardized paired effect of 0.7, 24 pairs
  give the one-sided Wilcoxon signed-rank test about 94% power (paired
  t-test power with the Wilcoxon test's asymptotic relative efficiency of
  0.955; statsmodels `TTestPower`). H2 is the harder one and is
  reported with its confidence interval whatever the outcome.
- Exclusion: participants who do not finish both sessions are reported but
  excluded from the paired tests.
- Run **2–3 pilot participants** first (their data are not analysed) to check
  the session length, the instructions and the questionnaire wording.

## 3. Procedure (per session, about 40 minutes)

1. The researcher starts the backend (`uvicorn main:app --port 8001` in
   `Backend/`) and the game in study mode:
   `godot --path . -- --study` (session length `--study-minutes=30` by default).
2. The researcher enters the participant code (`P01`, `P02`, …) and the
   session number. **No names are typed into the game.** The backend assigns
   the condition: odd-numbered participants play MC first, even-numbered GP
   first.
3. The participant reads the information sheet
   ([study/participant_information.txt](../study/participant_information.txt) —
   replace the template with the approved text) and ticks consent. Declining
   ends the session; nothing is recorded.
4. A briefing explains the controls, the goal and the seven enemy types
   (the same descriptions as in [Scenes/EnemyGallery.tscn](../Scenes/EnemyGallery.tscn)).
5. **Play**: 30 minutes of game time (pausing stops the clock); the dungeon
   in progress when time runs out is finished. Each dungeon is one trial
   reported to the backend under the session's player id (`P01-MC`, …).
6. **Replay block**: up to four designs drawn at random from the session's
   archive (including any the surrogate accepted without a playthrough)
   are played again. Their outcomes are stored for H2 and do not change the
   archive or the surrogate.
7. **Questionnaire**: the in-game items in
   [Backend/study_items.json](../Backend/study_items.json) (7-point
   agreement scale; written for this project, not a validated instrument),
   followed outside the game by a validated player-experience questionnaire
   (for example the Game Experience Questionnaire core module).
8. The second session is on a different day, in the other condition.

## 4. Data

- Stored by the backend under `Backend/data/` (`EVOLUTION_DATA_DIR`):
  `study/sessions.jsonl`, `study/replays.jsonl`,
  `study/questionnaires.jsonl`, the per-player archives in `players/`, and
  every trial in `session_log.jsonl`.
- Pseudonymous: participant codes only. The key linking codes to people,
  if one is kept at all, stays on paper with the researcher, separate from
  the data, and is destroyed after analysis.
- Withdrawal: delete every record whose `participant_id` (or player id
  prefix) is the participant's code.

## 5. Analysis

```bash
python tools/export_study_data.py --data Backend/data --out study_export
Backend/.venv/bin/python tools/analyze_study.py study_export
```

`export_study_data.py` writes `sessions.csv`, `trials.csv`,
`auto_decisions.csv`, `replays.csv` and `questionnaires.csv`;
`analyze_study.py` runs exactly the tests in section 1 and prints a report.
Both are covered by `Backend/tests/test_study_tools.py` on a fabricated
fixture.

## 6. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Fatigue or frustration | 30-minute sessions on separate days; participants may stop at any time; the game has no flashing lights or graphic content |
| Learning effects between sessions | counterbalanced order; trial number as covariate |
| The researcher's expectations | the condition is assigned by the backend and not shown on screen |
| Too few archived designs for the replay block | the replay sample is whatever the archive holds (at most four); sessions with an empty archive are reported separately |
