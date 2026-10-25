# Integration Testing

Unit tests check each module on its own (`Backend/tests/`, 154 tests;
`Tests/`, 82 tests). The scripts in [`tools/`](../tools/) check the real game
and the real backend working together and measure the game for the
simulated player. They run the Godot game headless with a fixed frame time
(`--fixed-fps 60`), so a run plays as fast as the machine allows and is
reproducible. They need a Godot 4 .NET binary (`--godot PATH` or the `GODOT`
environment variable); the end-to-end tests also need a free port 8001.

## 1. End-to-end test — `tools/integration_test.py`

```bash
python tools/integration_test.py --trials 10
```

Starts the backend with a throwaway data folder, plays N bot trials in the
game, and checks:

| Check | What it guards |
|---|---|
| game exited normally | client, HTTP calls and trial loop run without errors |
| one dungeon per trial | every trial fetched a design and built a dungeon |
| every dungeon used the fixed seed | the map is not evolved (panel review) |
| same layout and resources every trial | rooms, traps, chests and heals are identical — only the enemies change |
| every played design carried the fixed environment | the backend never hands out a different map |
| each dungeon used the design's tempo, elites and packs | the new genes reach the game unchanged |
| backend recorded every trial | outcomes reach `/api/report_outcome` and are evaluated |
| all trials counted as bot evaluations | bot trials are labelled `evaluator_type=bot` |
| only the bot's archive was written | per-player archives: bot data never touches a human player |

**Result (8 trials, 15 Oct 2026): all checks passed.**

```
Fixed dungeon: seed 123, 116 rooms; enemies per trial: [39, 63, 46, 51, 51, 34, 75, 34], elites: [13, 17, 7, 9, 9, 5, 17, 9]
Outcomes: ['pass', 'too_slow_abandoned', 'too_slow_abandoned', 'pass', 'pass', 'pass', 'pass', 'pass']

[PASS] game exited normally                                    exit code 0
[PASS] game built one dungeon per trial                        8 dungeons for 8 trials
[PASS] every dungeon used the fixed seed                       seeds [123]
[PASS] every dungeon had the same layout and resources         (rooms, traps, chests, heals) = [('116', '17', '18', '22')]
[PASS] every played design carried the fixed environment       8 reported trials
[PASS] each dungeon used the design's tempo, elites and packs  (tempo, elite_rate, pack_size) of trial 1: ('1.147', '0.338', '1.253')
[PASS] backend recorded every trial                            real_evaluations = 8
[PASS] all trials counted as bot evaluations                   bot = 8, human = 0
[PASS] only the bot's archive was written                      players with state: ['bot']
```

The enemy count follows the evolved density and pack size while the dungeon
stays the same.

In a 10-trial run on 16 Oct 2026 (all checks passed) the tactical director
also activated its stealth countermeasure during bot play, and live
re-tasking handed it to two enemies near the player (`[AdaptiveAI] Live
re-tasking: 2 enemies near the player took the stealth countermeasure.`).

### The study session — `tools/integration_test.py --study`

Runs one complete human-study session with the study screens filled in
automatically (`--study-auto=P01:1`) and the bot playing, and checks that
participant P01's first session was assigned condition MC and player id
`P01-MC`, that only that archive was trained, that the MC condition made no
surrogate decisions, that the replay block replayed a sample of the archive
without changing the archive or the surrogate, that the questionnaire was
stored with every item answered, and that no tactical orders were given.
**Result (15 Oct 2026): all checks passed.**

## 2. Choosing the fixed map — `tools/scan_map_seeds.py`

(The scan was run with the original four archetypes, before the enemy
update; the map, and so the choice of seed, did not change.)

Because only the enemies evolve, the fixed map has to be one on which the
enemies matter. The scan plays the bot through candidate seeds with the same
encounter design (here the hardest: density 0.5, difficulty 1.6, even mix)
and records what it experienced (two trials each).

```bash
python tools/scan_map_seeds.py
```

| seed | rooms | time (s) | damage | deaths | exit reached |
|---:|---:|---|---|---|---|
| 1 | 117 | 69 / 63 | 159 / 159 | 1 / 1 | yes / yes |
| 7 | 117 | 12 / 11 | 14 / 8 | 0 / 0 | yes / yes |
| 42 | 113 | 111 / 240 | 151 / 519 | 1 / 5 | yes / no |
| 99 | 110 | 27 / 26 | 8 / 16 | 0 / 0 | yes / yes |
| **123** | **116** | **146 / 146** | **127 / 113** | **1 / 1** | **yes / yes** |
| 555 | 115 | 24 / 23 | 28 / 28 | 0 / 0 | yes / yes |
| 808 | 118 | 12 / 10 | 55 / 44 | 0 / 0 | yes / yes |
| 2024 | 111 | 12 / 11 | 8 / 8 | 0 / 0 | yes / yes |
| 31337 | 115 | 240 / 100 | 717 / 154 | 6 / 1 | no / yes |
| 20261014 | 119 | 31 / 30 | 0 / 11 | 0 / 0 | yes / yes |

- Seeds 7, 808 and 2024 are crossed in 10–12 s: under the 15 s minimum, so
  **every** design would fail as "too fast".
- Seeds 99, 555 and 20261014 cost almost no damage even with the hardest
  design: the enemies barely matter, so every design would pass.
- Seeds 42 and 31337 swing between an easy pass and a timeout, too erratic
  for a stable evaluation map.
- Seed 1 and **seed 123** have a route on which the hardest encounter costs
  real damage and time, consistently.

On seed 123 the damage grows steadily with encounter difficulty (three
trials per design):

```bash
python tools/scan_map_seeds.py --seeds 123 --designs 0.1:0.6 0.25:1.0 0.35:1.2 0.5:1.6 --trials 3
```

| design (density : scale) | time (s) | damage | deaths |
|---|---|---|---|
| 0.10 : 0.6 (easy) | 27 / 27 / 27 | 2 / 2 / 2 | 0 / 0 / 0 |
| 0.25 : 1.0 (default) | 30 / 29 / 29 | 27 / 27 / 27 | 0 / 0 / 0 |
| 0.35 : 1.2 | 32 / 31 / 31 | 53 / 51 / 51 | 0 / 0 / 0 |
| 0.50 : 1.6 (hardest) | 178 / 165 / 155 | 256 / 105 / 102 | 2 / 1 / 1 |

Even the easiest crossing (27 s) is above the 15 s minimum, so the trivial
clause never decides an outcome on this map. Seed 123 is therefore the
default (`MAP_SEED` in `Backend/config.py`, `GenomeParams.MapSeed` in the
client). It also has 116 rooms, i.e. 114 rooms that can hold an enemy, which
is the value the simulated player uses (`Backend/simulated_player.py`).

## 3. Measuring the enemies — `tools/run_duels.py`

```bash
python tools/run_duels.py            # 13,440 duels on 10 parallel games, about a minute
```

Plays the scripted fighter (`Scripts/AI/FighterBotPolicy.cs`) against packs
of each archetype in the game's duel arena (`-- --duel-plan=... --duel-out=...`)
over difficulty scale, tempo, elite status, pack size and the fighter's
reaction skill, and writes the mean damage per condition to
`Backend/duel_table.json` — the simulated player's damage model. Held-out
conditions check the interpolation (R² = 0.98).

## 4. Checking the simulator in the full dungeon — `tools/validate_simulator.py`

```bash
Backend/.venv/bin/python tools/validate_simulator.py --designs 80 --trials 2
```

Plays random designs with the exit bot in the real dungeon, eight games in
parallel (each against its own stand-in backend, `--backend-url`), and
compares the damage taken with the simulator's predicted threat.
**Result: Spearman ρ = 0.79 over 160 trials.**

## 5. The tactical director in the game — `tools/evaluate_director.py`

```bash
Backend/.venv/bin/python tools/evaluate_director.py --sessions 30 --trials 15 \
    --out Review_2/paper/experiments/results/director_eval.jsonl
```

Plays sessions of 15 consecutive random designs with the exit bot in the
"hide" playstyle (`--bot-style=hide`: it steps out of the line of sight of
tracking enemies within six tiles, the habit the stealth countermeasure
reacts to), with the director off or with one of two stealth
countermeasures — `search` (enemies that lose the player search where they
last saw them) or `intercept` (they head for a cell ahead of the player on
the route to the exit and wait there) — given to new enemies only or also
re-tasked live (`--director=off|spawn|live --stealth=search|intercept`).
Session *s* plays the same designs in every condition. **Result (30
sessions per condition):** in the first trial, before any enemy can spawn
with an order, every condition without live re-tasking played exactly as
with the director off in all 30 sessions, which confirms the pairing.
Damage and deaths did not change detectably. The exit rate fell from 92%
to 83–85% with intercept (Holm-corrected p = 0.006 and 0.017), whose
crossings also took longer (101 s → 114 s, Holm p = 0.001); search's
effects (86–87% exit rate) did not survive the correction. Intercept is
therefore the default.

## 6. Both human studies with scripted participants — `tools/run_bot_study.py`

```bash
Backend/.venv/bin/python tools/run_bot_study.py --minutes 30 \
    --out Review_2/paper/experiments/results/bot_study
```

Runs the full protocol ([HUMAN_STUDY_PROTOCOL.md](HUMAN_STUDY_PROTOCOL.md))
with 12 scripted participants (three reaction skills × two playstyles, two
each), four sessions each, against the real backend on port 8001, then
exports and analyses the data with the study's own scripts. **Result: all
48 sessions completed** with a stored questionnaire; the results are in
section 7 of the protocol. Not human data.

## What these tests do not show

The bot knows where the exit is, walks straight to it and does not fight on
purpose, so it meets far fewer enemies than a person exploring under fog of
war; it is right for testing the integration and for checking that the
simulator ranks designs as the game does, but its outcomes say little about
how hard a design is for a human player. That question is answered with the simulated player
(see the research paper) and, next, with the human study described in the paper.
