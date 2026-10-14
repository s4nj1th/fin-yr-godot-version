# Integration Testing

Unit tests check each module on its own (`Backend/tests/`, `Tests/`). The
two scripts in [`tools/`](../tools/) check the real game and the real
backend working together. Both run the Godot game headless with the scripted
bot, so they need a Godot 4 .NET binary (`--godot PATH` or the `GODOT`
environment variable) and a free port 8001.

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
| backend recorded every trial | outcomes reach `/api/report_outcome` and are evaluated |
| all trials counted as bot evaluations | bot trials are labelled `evaluator_type=bot` |
| only the bot's archive was written | per-player archives: bot data never touches a human player |

**Result (8 trials, 15 Oct 2026): all checks passed.**

```
Fixed dungeon: seed 123, 116 rooms; enemies per trial: [3, 3, 3, 3, 17, 27, 12, 0]
Outcomes: ['pass', 'pass', 'pass', 'pass', 'pass', 'pass', 'pass', 'pass']

[PASS] game exited normally                               exit code 0
[PASS] game built one dungeon per trial                   8 dungeons for 8 trials
[PASS] every dungeon used the fixed seed                  seeds [123]
[PASS] every dungeon had the same layout and resources    (rooms, traps, chests, heals) = [(116, 17, 18, 22)]
[PASS] every played design carried the fixed environment  8 reported trials
[PASS] backend recorded every trial                       real_evaluations = 8
[PASS] all trials counted as bot evaluations              bot = 8, human = 0
[PASS] only the bot's archive was written                 players with state: ['bot']
```

The enemy count follows the evolved enemy density while the dungeon stays
the same.

## 2. Choosing the fixed map — `tools/scan_map_seeds.py`

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

## What these tests do not show

The bot knows where the exit is, walks straight to it and does not fight on
purpose, so it meets far fewer enemies than a person exploring under fog of
war; on seed 123 it clears even the hardest design. It is right for testing
the integration, but its outcomes say little about how hard a design is for
a human player. That question is answered with the simulated player
(see the research paper) and, next, with the human study described in the paper.
