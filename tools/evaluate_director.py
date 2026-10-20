"""
Evaluates the within-session tactical director in the real game with a
scripted player: random encounter designs on the fixed map, played by the exit bot
with the "hide" playstyle (it steps out of the line of sight of enemies that
spot it — the habit the director's stealth countermeasure reacts to), with
the director off, or with one of two stealth countermeasures — "search"
(enemies that lose the player search where they last saw them) or
"intercept" (they head for a cell ahead of the player on the route to the
exit and wait there) — given to new enemies only ("-spawn") or also to
enemies already on the map ("-live").

Each condition runs several game sessions of consecutive trials, so the
player profile builds up over a session as it would for a person. Session
s plays the same sequence of random designs in every condition, so the
three conditions are compared trial by trial. Every trial's telemetry and
the director's log lines are recorded.

    Backend/.venv/bin/python tools/evaluate_director.py --sessions 30 --trials 15 \\
        --out Review_2/paper/experiments/results/director_eval.jsonl

A scripted player is not a person: this shows what the director does in
the game and whether it changes the outcome for this playstyle, not how
people experience it (that is study 2 in docs/HUMAN_STUDY_PROTOCOL.md).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
sys.path.insert(0, os.path.join(ROOT, "Backend"))
import random  # noqa: E402

import genome as genome_lib  # noqa: E402
from validate_simulator import serve  # noqa: E402

# condition -> (--director, --stealth)
CONDITIONS = {"off": ("off", "search"), "search-spawn": ("spawn", "search"), "search-live": ("live", "search"),
              "intercept-spawn": ("spawn", "intercept"), "intercept-live": ("live", "intercept")}


def session_designs(session: int, trials: int) -> list[dict]:
    """The random designs session `session` plays, identical for every condition."""
    state = random.getstate()
    random.seed(50_000 + session)
    designs = [genome_lib.make_random_genome(genome_lib.FULL_SPACE)["params"] for _ in range(trials)]
    random.setstate(state)
    return designs


def parse_log(stdout: str) -> list[dict]:
    """Per trial: line-of-sight breaks, countermeasure activations and
    re-tasked enemies (logged after the trial's [Dungeon] line), and enemies
    spawned with an order (logged just before it)."""
    segments = re.split(r"\[Dungeon\] seed=", stdout)
    out = []
    for k in range(len(segments) - 1):
        before, during = segments[k], segments[k + 1]
        spawned = re.findall(r"(\d+)/(\d+) enemies spawned with an active tactical order", before)
        # The next trial's spawn line sits at the end of `during`; it is not counted here.
        out.append({
            "los_breaks": len(re.findall(r"Line-of-sight break #", during)),
            "activations": len(re.findall(r"countermeasure ACTIVATED", during)),
            "retasked": sum(int(m) for m in re.findall(r"Live re-tasking: (\d+) enemies", during)),
            "intercepts": sum(int(m) for m in re.findall(r"Intercepts planned this trial: (\d+)", during)),
            "spawned_with_order": int(spawned[-1][0]) if spawned else 0,
        })
    return out


def run_session(godot: str, condition: str, session: int, trials: int, port: int, dodge: float) -> list[dict]:
    reports: list[dict] = []
    designs = session_designs(session, trials)
    server = serve(designs, reports, port)
    try:
        game = subprocess.run([godot, "--headless", "--fixed-fps", "60", "--path", ROOT, "--", "--autoplay",
                               "--autoplay-timescale=1", f"--autoplay-max-trials={trials}", f"--bot-dodge={dodge}",
                               "--bot-style=hide", f"--director={CONDITIONS[condition][0]}",
                               f"--stealth={CONDITIONS[condition][1]}", f"--backend-url=http://127.0.0.1:{port}"],
                              capture_output=True, text=True, timeout=7200)
    finally:
        server.shutdown()
        server.server_close()
    per_trial = parse_log(game.stdout)
    records = []
    for i, report in enumerate(reports):
        tel = report["telemetry"]
        breakdown = tel.get("enemy_behavior_breakdown") or {}
        adapted = sum(int((s.get("adapted") or {}).get("damage_dealt_to_player", 0)) for s in breakdown.values())
        records.append({"condition": condition, "session": session, "trial": i + 1, "params": designs[i],
                        "damage": tel["damage_taken"], "deaths": tel["deaths_retries"], "time_sec": tel["time_taken_sec"],
                        "exit_reached": tel["exit_reached"], "kills": tel["enemies_killed"],
                        "adapted_damage": adapted, **(per_trial[i] if i < len(per_trial) else {})})
    return records


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sessions", type=int, default=8, help="game sessions per condition")
    parser.add_argument("--first-session", type=int, default=0,
                        help="number of the first session (to extend an earlier run with --append)")
    parser.add_argument("--append", action="store_true", help="append to --out instead of overwriting it")
    parser.add_argument("--conditions", default=",".join(CONDITIONS), help="comma-separated subset of " + ", ".join(CONDITIONS))
    parser.add_argument("--trials", type=int, default=15, help="trials per session")
    parser.add_argument("--bot-dodge", type=float, default=0.5)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--base-port", type=int, default=8201)
    parser.add_argument("--out", default=os.path.join(ROOT, "Backend", "data", "director_eval.jsonl"))
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    args = parser.parse_args()

    conditions = [c for c in args.conditions.split(",") if c]
    jobs = [(c, s) for s in range(args.first_session, args.first_session + args.sessions) for c in conditions]
    print(f"{len(jobs)} sessions x {args.trials} trials on {args.workers} games...", flush=True)
    with ThreadPoolExecutor(args.workers) as pool:
        results = list(pool.map(lambda a: run_session(args.godot, a[1][0], a[1][1], args.trials,
                                                        args.base_port + a[0], args.bot_dodge), enumerate(jobs)))
    records = [r for session in results for r in session]
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "a" if args.append else "w") as f:
        for r in records:
            f.write(json.dumps(r) + "\n")
    for condition in conditions:
        rs = [r for r in records if r["condition"] == condition]
        if rs:
            print(f"{condition:15s}: {len(rs)} trials, damage {sum(r['damage'] for r in rs) / len(rs):.1f}, "
                  f"deaths {sum(r['deaths'] for r in rs) / len(rs):.2f}, exit {sum(r['exit_reached'] for r in rs) / len(rs):.0%}, "
                  f"activations {sum(r.get('activations', 0) for r in rs)}, re-tasked {sum(r.get('retasked', 0) for r in rs)}")
    print(f"Wrote {args.out}")


if __name__ == "__main__":
    main()
