# Human Study Protocol

This is the protocol for the two studies that have to precede any claim
about how real players experience the adaptive enemy evolution. Every
quantitative result so far comes from the simulated player
([EVOLUTIONARY_DESIGN.md](EVOLUTIONARY_DESIGN.md), research paper) and from
scripted bots in the real game. The tooling below is implemented and tested
end to end with the scripted bot (`python tools/integration_test.py --study`),
and both studies have been run in full with a panel of scripted participants
(section 7); **no human data has been collected yet.**

The protocol must be approved by the institutional ethics committee, and
should be preregistered (for example on OSF) before the first participant.

## 1. Questions and hypotheses

### Study 1, "search": does the surrogate save playthroughs?

Conditions, compared within participants:

- **MC**: minimal criterion search without the surrogate (every design is played);
- **GP**: minimal criterion search with the epistemic uncertainty gate (the
  default system), including the stretch fallback: a player who clears eight
  designs in a row without any failure is served one harder version of a
  cleared design, so the surrogate sees both outcomes and can train (the
  scripted dry run showed that otherwise a player who never fails gets no
  benefit from the gate).

Both use the random cold start, the fixed map and the 12-gene encounter
space. The adaptive tactical director is **off** in both, so within-session
adaptation cannot confound the comparison.

| | Hypothesis | Measure | Test |
|---|---|---|---|
| H1 | GP needs fewer playthroughs per archived design than MC | played trials / archived designs, per participant and condition | Wilcoxon signed-rank, one-sided, α = 0.05 |
| H2 | GP's archive is not much less clearable than MC's | pass rate in the replay block | paired bootstrap 95% CI of GP − MC; non-inferior if the lower bound is above −10 points |

Exploratory: played pass/fail by condition (logistic GEE with participants
as clusters, trial number as covariate); questionnaire items by condition
(Wilcoxon, Holm-corrected); and the fit of the simulated player (κ per
participant), which tells us how far the simulation results can be trusted.

### Study 2, "director": what does within-session tactical adaptation do?

Conditions, compared within participants:

- **DOFF**: the tactical director off;
- **DON**: the tactical director on, with orders at spawn and live re-tasking.

Evolution is replaced by a **fixed sequence of random designs** that depends
only on the participant number, so both sessions of a participant play the
same encounters in the same order and the director is the only difference.
The analysis uses the trials both sessions reached (sessions end on time, so
their lengths differ).

| | Hypothesis | Measure | Test |
|---|---|---|---|
| D1 | the director changes how often players clear what they play | pass rate per participant and condition | Wilcoxon signed-rank, two-sided, α = 0.05 |
| D2 | ... and how much damage they take | damage per trial | same test |

Also reported: deaths and time per trial, the share of damage dealt by
enemies carrying a tactical order, played pass/fail by condition (GEE), and
the questionnaire items.

## 2. Participants

- Adults (18+) with any amount of action-game experience, recruited by
  announcement; no compensation that depends on performance.
- Target **48 participants** (at least 24). The power analysis is
  `Review_2/paper/experiments/power.py` (results in `results/power.json`):
  - **H1** is expected to have a large effect (in the simulation the GP
    condition archives several times more designs per playthrough). For a
    standardized paired effect of 0.7, 24 pairs give the one-sided Wilcoxon
    signed-rank test about 95% power (paired t-test power with the Wilcoxon
    test's asymptotic relative efficiency of 0.955; statsmodels `TTestPower`).
  - **H2** is much harder. Simulated from the simulated players' own
    archives after a 20-playthrough session, the gate's archive is about 3.4
    points less clearable than MC's, and players differ, so more replays
    alone do not suffice. With 12 replays per session, power is 37% for 12
    participants, 54% for 24, 70% for 36 and **79% for 48** (82% with 16
    replays). Four replays — the first version of the protocol — gave only
    34% with 24 participants. H2 is reported with its confidence interval
    whatever the outcome.
  - **Study 2**: with 24 participants the two-sided signed-rank tests detect
    a standardized effect of 0.61 with 80% power (0.42 with 48); at the
    spread the scripted dry run showed, that is about 4.6 points of pass rate
    or 30 damage per trial.
- Exclusion: participants who do not finish both sessions are reported but
  excluded from the paired tests.
- Run **2–3 pilot participants** first (their data are not analysed) to check
  the session length, the instructions and the questionnaire wording.

## 3. Procedure (per session, about 50 minutes)

1. The researcher starts the backend (`uvicorn main:app --port 8001` in
   `Backend/`) and the game in study mode:
   `godot --path . -- --study` (session length `--study-minutes=30` by default).
2. The researcher enters the participant code (`P01`, `P02`, …), the
   session number and the study (Study 1 or Study 2). **No names are typed
   into the game.** The backend assigns the condition: odd-numbered
   participants play the reference condition (MC, DOFF) first, even-numbered
   the other (GP, DON) first. A participant can take part in both studies,
   in four sessions.
3. The participant reads the information sheet
   ([study/participant_information.txt](../study/participant_information.txt) —
   replace the template with the approved text) and ticks consent. Declining
   ends the session; nothing is recorded.
4. A briefing explains the controls, the goal and the seven enemy types
   (the same descriptions as in [Scenes/EnemyGallery.tscn](../Scenes/EnemyGallery.tscn)).
5. **Play**: 30 minutes of game time (pausing stops the clock); the dungeon
   in progress when time runs out is finished. Each dungeon is one trial
   reported to the backend under the session's player id (`P01-MC`, …).
6. **Replay block**: up to twelve designs drawn at random from the session's
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
`auto_decisions.csv`, `replays.csv` and `questionnaires.csv`, each with a
`study` column; `analyze_study.py` runs exactly the tests in section 1 for
each study and prints a report (`--json` also writes the numbers). A test
that cannot be computed is reported as such: a Wilcoxon test when no
participant differs between conditions, a GEE when a condition passed or
failed every trial (perfect separation). Both scripts are covered by
`Backend/tests/test_study_tools.py` on a fabricated fixture.

## 6. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Fatigue or frustration | 30-minute sessions on separate days; participants may stop at any time; the game has no flashing lights or graphic content |
| Learning effects between sessions | counterbalanced order; trial number as covariate |
| The researcher's expectations | the condition is assigned by the backend and not shown on screen |
| Too few archived designs for the replay block | the replay sample is whatever the archive holds (at most twelve); sessions with an empty archive are reported separately |

## 7. Dry run with scripted participants

Both studies have been run end to end with a panel of 12 scripted
participants: the exit bot with a reaction skill of 0.2, 0.5 or 0.8 (the
chance of stepping out of a telegraphed attack) and one of two playstyles
(walk to the exit; or also step out of sight of enemies that spot it), two
participants per combination. Each played all four sessions of 30 game
minutes through the real study mode, backend, replay block and
questionnaire, and the data went through the export and analysis scripts
above:

```bash
Backend/.venv/bin/python tools/run_bot_study.py --minutes 30 \
    --out Review_2/paper/experiments/results/bot_study    # needs port 8001 free
Backend/.venv/bin/python tools/run_bot_study.py --reanalyze --out ...   # redo export + analysis only
```

**This is not human data.** The questionnaire answers are fixed
placeholders, and the bots' behaviour is not people's. The runs test the
protocol and show what the analysis reports.

**First dry run** (`results/bot_study_v1`; four replays per session, no
stretch fallback, the "search" countermeasure). It exposed two weaknesses,
both fixed in this protocol:

- Three participants cleared every trial of their GP session, so the
  surrogate never trained and the session ran as MC → the stretch fallback.
- H2's interval was far wider than the margin (−2.1 points, 95% CI −12.5 to
  +8.3) → twelve replays per session and the power analysis of section 2.

**Final dry run** (`results/bot_study`; the protocol as specified here):

- All 48 sessions completed (1,105 trials; 11–60 per session in study 1).
- The surrogate trained in all 12 GP sessions; 4 of them were served one or
  two stretched designs.
- Study 1, H1: playthroughs per archived design fell from a median of 1.07
  (MC) to 0.16 (GP), Wilcoxon p = 0.002, for 10 of 12 participants.
- Study 1, H2: not shown (96.2% vs 90.9%, difference −5.3 points, 95% CI
  −14.3 to +2.6). With 12 participants the test has only 37% power, so this
  is expected.
- The two-factor fit placed every bot's reaction skill between 0.00 and
  0.20. The bots' reaction skill does not lower their per-archetype damage
  in the dungeon (telegraphed vs other archetypes per enemy: 0.86, 0.89 and
  0.94 at skills 0.2, 0.5 and 0.8), because the exit bot takes damage while
  walking past enemies. On synthetic participants played by the simulated
  player, where reaction does change what each enemy costs, the fit
  recovers q (rank correlation 0.96 from 35 trials;
  `Review_2/paper/experiments/skill_recovery.py`).
- Study 2 (intercept countermeasure): on the 454 trials both sessions
  reached, neither the pass rate (86.3% vs 86.9%, p = 0.91) nor damage per
  trial (141 vs 125, p = 0.57) changed detectably, although enemies with an
  order dealt 87% of the damage. The in-game evaluation of the director
  (`tools/evaluate_director.py`, paper Sec. V) shows that intercept lowers
  the exit rate for a hiding bot; with 12 participants study 2 detects only
  large effects.

The full report is `results/bot_study/report.txt` and the numbers are in
`results/bot_study/results.json`.
