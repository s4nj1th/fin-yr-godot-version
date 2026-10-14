"""
How the fixed evaluation map (Backend/config.py, map_seed) was chosen.

Only the enemies evolve, so the fixed map matters: if the exit is too close
to the start, every design is crossed in under the 15 s minimum and fails as
"too fast"; if the route meets no enemies, every design passes. This script
plays the scripted bot through candidate seeds, each with the same fixed
encounter design served by a small built-in stand-in for the backend, and
prints what the bot experienced.

    python tools/scan_map_seeds.py                                  # hardest design on the default seed list
    python tools/scan_map_seeds.py --seeds 123 --designs 0.1:0.6 0.25:1.0 0.35:1.2 0.5:1.6

A design is given as enemy_density:enemy_difficulty_scale, with an even
behaviour mix. Port 8001 must be free (the game always connects there).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import threading
import uuid
from http.server import BaseHTTPRequestHandler, HTTPServer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENVIRONMENT = {"map_width": 160, "map_height": 160, "room_attempts": 280,
               "trap_density": 0.15, "chest_density": 0.15, "heal_density": 0.15}
EVEN_MIX = {"melee_chaser": 0.25, "melee_hit_run": 0.25, "ranged_kite": 0.25, "ranged_strafe": 0.25}


def serve(params: dict, reports: list) -> HTTPServer:
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
                gid = uuid.uuid4().hex
                self._send({"genome_id": gid, "genome": {"id": gid, "generation": 0, "params": params}})
            else:
                reports.append(json.loads(body)["telemetry"])
                self._send({"passed": True, "hall_of_fame_size": 0, "player_id": "bot"})

        def log_message(self, *args):
            pass

    server = HTTPServer(("127.0.0.1", 8001), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--seeds", type=int, nargs="+", default=[1, 7, 42, 99, 123, 555, 808, 2024, 31337, 20261014])
    parser.add_argument("--designs", nargs="+", default=["0.5:1.6"])
    parser.add_argument("--trials", type=int, default=2)
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    args = parser.parse_args()

    print("| seed | rooms | design (density:scale) | time (s) | damage | deaths | exit reached |")
    print("|---:|---:|---|---|---|---|---|")
    for seed in args.seeds:
        for design in args.designs:
            density, scale = map(float, design.split(":"))
            params = {**ENVIRONMENT, "map_seed": seed, "enemy_density": density,
                      "enemy_difficulty_scale": scale, "enemy_behavior_mix": EVEN_MIX}
            reports: list = []
            server = serve(params, reports)
            try:
                game = subprocess.run([args.godot, "--headless", "--path", ROOT, "--", "--autoplay",
                                       f"--autoplay-max-trials={args.trials}", "--autoplay-timescale=8"],
                                      capture_output=True, text=True, timeout=600)
            finally:
                server.shutdown()
                server.server_close()
            rooms = re.search(r"\[Dungeon\] seed=\d+ rooms=(\d+)", game.stdout)
            cols = [" / ".join(str(round(r[k])) for r in reports) for k in ("time_taken_sec", "damage_taken", "deaths_retries")]
            exits = " / ".join("yes" if r["exit_reached"] else "no" for r in reports)
            print(f"| {seed} | {rooms.group(1) if rooms else '?'} | {design} | {cols[0]} | {cols[1]} | {cols[2]} | {exits} |",
                  flush=True)


if __name__ == "__main__":
    main()
