"""
Checks the simulated player against the real game: does a design's nominal
threat (Backend/simulated_player.py) predict how much damage the scripted
bot actually takes when it plays that design in the full dungeon?

Plays N random designs from the 12-gene space, `--trials` times each, with
the exit-seeking bot (Scripts/AI/BotPlayerPolicy.cs) at a given reaction
skill (--bot-dodge), several games in parallel, each against a small
stand-in for the backend that serves its share of the designs. Writes one
JSON record per trial and prints the rank correlation between nominal
threat and the damage taken.

    python tools/validate_simulator.py --designs 80 --trials 2 --out Backend/data/validation.jsonl
    python tools/validate_simulator.py --designs 80 --trials 2 --map-seed 1 --out validation_map1.jsonl

The same --seed gives the same designs, so runs on different layouts can be
compared design by design.

The bot is not a model of a person either: it walks straight to the exit
and fights only what is in its way, so it meets a fraction of the enemies.
The check is whether the *ordering* of designs by threat carries over to
the real game, and how well one factor (kappa) scales threat to damage.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import shutil
import subprocess
import sys
import threading
import uuid
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "Backend"))

import genome as genome_lib  # noqa: E402
import simulated_player as sp  # noqa: E402


def serve(designs: list[dict], reports: list, port: int) -> ThreadingHTTPServer:
    queue = list(designs)
    handed: dict[str, dict] = {}

    class Handler(BaseHTTPRequestHandler):
        def _send(self, body: dict) -> None:
            data = json.dumps(body).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_POST(self):  # noqa: N802
            body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
            if self.path.startswith("/api/next_candidate"):
                params = queue.pop(0) if queue else designs[-1]
                gid = uuid.uuid4().hex
                handed[gid] = params
                self._send({"genome_id": gid, "genome": {"id": gid, "generation": 0, "params": params}})
            else:
                request = json.loads(body)
                reports.append({"params": handed.get(request["genome_id"]), "telemetry": request["telemetry"]})
                self._send({"passed": True, "hall_of_fame_size": 0, "player_id": "bot"})

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def spearman(x: list[float], y: list[float]) -> float:
    def ranks(v):
        order = sorted(range(len(v)), key=lambda i: v[i])
        r = [0.0] * len(v)
        i = 0
        while i < len(order):
            j = i
            while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
                j += 1
            for k in range(i, j + 1):
                r[order[k]] = (i + j) / 2
            i = j + 1
        return r
    rx, ry = ranks(x), ranks(y)
    mx, my = sum(rx) / len(rx), sum(ry) / len(ry)
    cov = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    return cov / (sum((a - mx) ** 2 for a in rx) * sum((b - my) ** 2 for b in ry)) ** 0.5


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--designs", type=int, default=80)
    parser.add_argument("--trials", type=int, default=2)
    parser.add_argument("--bot-dodge", type=float, default=0.5)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--seed", type=int, default=2026)
    parser.add_argument("--map-seed", type=int, default=None,
                        help="play on another layout than the fixed evaluation map (generality check)")
    parser.add_argument("--base-port", type=int, default=8101)
    parser.add_argument("--out", default=os.path.join(ROOT, "Backend", "data", "validation.jsonl"))
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    args = parser.parse_args()

    random.seed(args.seed)
    designs = [genome_lib.make_random_genome(genome_lib.FULL_SPACE)["params"] for _ in range(args.designs)]
    if args.map_seed is not None:
        designs = [{**d, "map_seed": args.map_seed} for d in designs]
    plays = [d for d in designs for _ in range(args.trials)]
    shards = [plays[i::args.workers] for i in range(args.workers)]

    def run(index: int) -> list[dict]:
        reports: list[dict] = []
        port = args.base_port + index
        server = serve(shards[index], reports, port)
        try:
            subprocess.run([args.godot, "--headless", "--fixed-fps", "60", "--path", ROOT, "--", "--autoplay",
                            "--autoplay-timescale=1", f"--autoplay-max-trials={len(shards[index])}",
                            f"--bot-dodge={args.bot_dodge}", f"--backend-url=http://127.0.0.1:{port}"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=7200, check=False)
        finally:
            server.shutdown()
            server.server_close()
        return reports

    print(f"Playing {len(plays)} trials ({args.designs} designs x {args.trials}) on {args.workers} games...", flush=True)
    with ThreadPoolExecutor(args.workers) as pool:
        records = [r for shard in pool.map(run, range(args.workers)) for r in shard]

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w") as f:
        for r in records:
            r["threat"] = sp.raw_threat(r["params"], dodge=args.bot_dodge)
            r["bot_dodge"] = args.bot_dodge
            r["map_seed"] = r["params"]["map_seed"]
            f.write(json.dumps(r) + "\n")

    threat = [r["threat"] for r in records]
    damage = [r["telemetry"]["damage_taken"] for r in records]
    print(f"{len(records)} trials; Spearman rho(threat, damage) = {spearman(threat, damage):.3f}")
    print(f"Wrote {args.out}")


if __name__ == "__main__":
    main()
