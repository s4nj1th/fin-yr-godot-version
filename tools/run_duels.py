"""
Measures, in the real game, how much damage it costs to defeat each kind of
enemy, and writes the table the simulated player is built from
(Backend/duel_table.json).

Every duel is one scripted fighter against a pack of identical enemies in an
open arena (GameManager duel mode, Scripts/Core/GameManager.Duel.cs), using
the game's own enemy AI, attack resolution and damage mitigation. The game
runs headless with a fixed frame time (--fixed-fps), so a duel takes a few
milliseconds of wall time and every duel is reproducible from its seed.

The fighter's one skill parameter, `dodge`, is the probability that it
reacts to a telegraphed attack (steps out of a charge line or blast area)
or works around a shieldbearer's shield.

    python tools/run_duels.py                    # full grid, writes Backend/duel_table.json
    python tools/run_duels.py --reps 5 --out /tmp/duels.jsonl --no-table

Grid (per archetype): difficulty scale {0.6, 1.0, 1.6} x tempo {0.75, 1.0,
1.5} x elite {no, yes} x dodge {0, 1}, one enemy; packs of 2 and 3 at scale
1, tempo 1 for both dodge levels; and held-out validation conditions
(intermediate scales and tempos, dodge 0.5, mixed packs) that are not used
to build the table but to check it.
"""

from __future__ import annotations

import argparse
import itertools
import json
import os
import shutil
import statistics
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BEHAVIORS = ["melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe",
             "melee_charger", "ranged_bomber", "melee_shield"]
SCALES = [0.6, 1.0, 1.6]
TEMPOS = [0.75, 1.0, 1.5]
VALIDATION_SCALES = [0.8, 1.3]
VALIDATION_TEMPOS = [0.9, 1.25]


def build_plan(reps: int) -> list[dict]:
    duels: list[dict] = []
    seed = 1

    def add(kind: str, behavior: str, scale: float, tempo: float, elite: bool, count: int, dodge: float) -> None:
        nonlocal seed
        for rep in range(reps):
            duels.append({"id": f"{kind}|{behavior}|{scale}|{tempo}|{int(elite)}|{count}|{dodge}|{rep}",
                          "behavior": behavior, "scale": scale, "tempo": tempo, "elite": elite,
                          "count": count, "dodge": dodge, "seed": seed})
            seed += 1

    for behavior in BEHAVIORS:
        for scale, tempo, elite, dodge in itertools.product(SCALES, TEMPOS, (False, True), (0.0, 1.0)):
            add("grid", behavior, scale, tempo, elite, 1, dodge)
        for count, dodge in itertools.product((2, 3), (0.0, 1.0)):
            add("pack", behavior, 1.0, 1.0, False, count, dodge)
        for scale, tempo in itertools.product(VALIDATION_SCALES, VALIDATION_TEMPOS):
            add("holdout", behavior, scale, tempo, False, 1, 0.5)
            add("holdout", behavior, scale, tempo, True, 2, 0.0)
    return duels


def run_shard(godot: str, duels: list[dict], workdir: str, index: int, timeout: float) -> list[dict]:
    plan_path = os.path.join(workdir, f"plan_{index}.json")
    out_path = os.path.join(workdir, f"out_{index}.jsonl")
    with open(plan_path, "w") as f:
        json.dump({"timeout_sec": timeout, "duels": duels}, f)
    subprocess.run([godot, "--headless", "--fixed-fps", "60", "--path", ROOT, "--",
                    f"--duel-plan={plan_path}", f"--duel-out={out_path}"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=3600, check=False)
    with open(out_path) as f:
        return [json.loads(line) for line in f if line.strip()]


def summarize(records: list[dict]) -> dict:
    """Mean damage per condition. Grid and pack conditions form the table;
    holdout conditions are kept separately for validation."""
    groups: dict[tuple, list[dict]] = {}
    for r in records:
        kind = r["id"].split("|", 1)[0]
        key = (kind, r["behavior"], r["scale"], r["tempo"], bool(r["elite"]), r["count"], r["dodge"])
        groups.setdefault(key, []).append(r)

    rows = []
    for (kind, behavior, scale, tempo, elite, count, dodge), rs in sorted(groups.items()):
        damage = [r["damage_taken"] for r in rs]
        rows.append({
            "kind": kind, "behavior": behavior, "scale": scale, "tempo": tempo, "elite": elite,
            "count": count, "dodge": dodge, "n": len(rs),
            "damage_mean": statistics.fmean(damage),
            "damage_sd": statistics.pstdev(damage),
            "time_mean": statistics.fmean(r["time_sec"] for r in rs),
            "timeouts": sum(bool(r["timed_out"]) for r in rs),
            "dodged": sum(r["attacks_dodged"] for r in rs),
            "telegraphed": sum(r["telegraphed_attacks"] for r in rs),
            "blocked": sum(r["hits_blocked"] for r in rs),
        })
    return {
        "description": "Mean damage the scripted fighter takes to defeat a pack of identical enemies "
                       "in the game's duel arena (tools/run_duels.py).",
        "behaviors": BEHAVIORS, "scales": SCALES, "tempos": TEMPOS,
        "rows": rows,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--reps", type=int, default=40, help="duels per condition (different start positions)")
    parser.add_argument("--workers", type=int, default=max(1, (os.cpu_count() or 4) // 2))
    parser.add_argument("--timeout", type=float, default=60.0, help="seconds of game time per duel")
    parser.add_argument("--out", default=os.path.join(ROOT, "Backend", "data", "duels.jsonl"), help="raw per-duel records")
    parser.add_argument("--table", default=os.path.join(ROOT, "Backend", "duel_table.json"))
    parser.add_argument("--no-table", action="store_true", help="do not overwrite the table")
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    args = parser.parse_args()

    plan = build_plan(args.reps)
    shards = [plan[i::args.workers] for i in range(args.workers)]
    print(f"{len(plan)} duels on {args.workers} workers...", flush=True)
    with tempfile.TemporaryDirectory(prefix="duels-") as workdir:
        with ThreadPoolExecutor(args.workers) as pool:
            results = list(pool.map(lambda a: run_shard(args.godot, a[1], workdir, a[0], args.timeout), enumerate(shards)))
    records = [r for shard in results for r in shard]
    if len(records) != len(plan):
        print(f"Only {len(records)} of {len(plan)} duels finished", file=sys.stderr)
        sys.exit(1)

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w") as f:
        for r in sorted(records, key=lambda r: r["seed"]):
            f.write(json.dumps(r) + "\n")
    print(f"Wrote {len(records)} duels to {args.out}")

    if not args.no_table:
        with open(args.table, "w") as f:
            json.dump(summarize(records), f, indent=1)
        print(f"Wrote {args.table}")


if __name__ == "__main__":
    main()
