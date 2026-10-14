"""
End-to-end integration test: the real backend and the real game, together.

Starts the backend (Backend/main.py) on a free port with a throwaway data
folder, runs the Godot game headless with the scripted bot for N trials,
and checks that:

  1. the game played every trial against the backend;
  2. every trial was played on the same fixed dungeon (same seed, same room,
     trap, chest and heal counts) — only the enemies changed;
  3. every design the backend handed out carried the fixed environment;
  4. the backend counted every trial as a bot evaluation for player "bot",
     and no other player's archive was touched.

    python tools/integration_test.py --trials 10
    python tools/integration_test.py --godot /path/to/godot-mono --trials 5

Exit code 0 if every check passes, 1 otherwise. Needs only the standard
library (run it with any Python 3.9+), the Backend virtualenv, and a Godot
4 .NET editor binary.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BACKEND = os.path.join(ROOT, "Backend")
DUNGEON_LINE = re.compile(
    r"\[Dungeon\] seed=(\d+) rooms=(\d+) enemies=(\d+) traps=(\d+) chests=(\d+) heals=(\d+)")


def backend_python() -> str:
    for candidate in (os.path.join(BACKEND, ".venv", "bin", "python"),
                      os.path.join(BACKEND, ".venv", "Scripts", "python.exe")):
        if os.path.exists(candidate):
            return candidate
    return sys.executable


def port_is_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def get_json(url: str) -> dict:
    with urllib.request.urlopen(url, timeout=5) as response:
        return json.loads(response.read())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--trials", type=int, default=10)
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    parser.add_argument("--port", type=int, default=8001, help="must match ApiClient.BaseUrl in the game")
    parser.add_argument("--timescale", type=float, default=8.0)
    parser.add_argument("--keep", action="store_true", help="keep the temporary folder with logs and data")
    args = parser.parse_args()

    if not port_is_free(args.port):
        print(f"Port {args.port} is in use — stop the running backend first.")
        return 1

    work = tempfile.mkdtemp(prefix="integration-")
    env = {**os.environ, "EVOLUTION_DATA_DIR": os.path.join(work, "data"), "LLM_MUTATION_MODE": "off"}
    base = f"http://127.0.0.1:{args.port}"
    backend_log = open(os.path.join(work, "backend.log"), "w")
    backend = subprocess.Popen(
        [backend_python(), "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", str(args.port)],
        cwd=BACKEND, env=env, stdout=backend_log, stderr=subprocess.STDOUT)

    results: list[tuple[str, bool, str]] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        results.append((name, ok, detail))

    try:
        for _ in range(50):
            try:
                fixed = get_json(f"{base}/api/config")["fixed_environment"]
                break
            except OSError:
                time.sleep(0.2)
        else:
            print("Backend did not start; see", os.path.join(work, "backend.log"))
            return 1

        print(f"Running {args.trials} bot trials in the real game ({args.godot})...")
        started = time.time()
        game = subprocess.run(
            [args.godot, "--headless", "--path", ROOT, "--", "--autoplay",
             f"--autoplay-max-trials={args.trials}", f"--autoplay-timescale={args.timescale}"],
            capture_output=True, text=True, timeout=120 + 120 * args.trials)
        with open(os.path.join(work, "game.log"), "w") as f:
            f.write(game.stdout + game.stderr)
        print(f"Game finished in {time.time() - started:.0f} s (exit code {game.returncode}).")

        dungeons = [tuple(map(int, m.groups())) for m in DUNGEON_LINE.finditer(game.stdout)]
        stats = get_json(f"{base}/api/stats?player_id=bot")
        players = get_json(f"{base}/api/players")["players"]

        log_path = os.path.join(work, "data", "session_log.jsonl")
        reports = []
        if os.path.exists(log_path):
            with open(log_path) as f:
                reports = [r for r in map(json.loads, f) if r.get("event") == "report_outcome"]

        check("game exited normally", game.returncode == 0, f"exit code {game.returncode}")
        check("game built one dungeon per trial", len(dungeons) == args.trials,
              f"{len(dungeons)} dungeons for {args.trials} trials")
        check("every dungeon used the fixed seed", bool(dungeons) and all(d[0] == fixed["map_seed"] for d in dungeons),
              f"seeds {sorted({d[0] for d in dungeons})}")
        layouts = {(d[1], d[3], d[4], d[5]) for d in dungeons}
        check("every dungeon had the same layout and resources", len(layouts) == 1,
              f"(rooms, traps, chests, heals) = {sorted(layouts)}")
        check("every played design carried the fixed environment",
              bool(reports) and all(all(r["params"][k] == v for k, v in fixed.items()) for r in reports),
              f"{len(reports)} reported trials")
        check("backend recorded every trial", stats["real_evaluations"] == args.trials,
              f"real_evaluations = {stats['real_evaluations']}")
        check("all trials counted as bot evaluations",
              stats["bot_evaluations"] == args.trials and stats["human_evaluations"] == 0,
              f"bot = {stats['bot_evaluations']}, human = {stats['human_evaluations']}")
        check("only the bot's archive was written", players == ["bot"], f"players with state: {players}")

        enemies = [d[2] for d in dungeons]
        outcomes = [("pass" if r["passed"] else r["fail_reason"]) for r in reports]
        print(f"\nFixed dungeon: seed {fixed['map_seed']}, {dungeons[0][1] if dungeons else '?'} rooms; "
              f"enemies per trial: {enemies}")
        print(f"Outcomes: {outcomes}")
        print(f"Archive (player 'bot'): {stats['hall_of_fame_size']} designs, "
              f"surrogate decisions: {stats['auto_accepted'] + stats['auto_rejected']}\n")
    finally:
        backend.terminate()
        try:
            backend.wait(timeout=10)
        except subprocess.TimeoutExpired:
            backend.kill()
        backend_log.close()

    width = max(len(name) for name, _, _ in results)
    for name, ok, detail in results:
        print(f"[{'PASS' if ok else 'FAIL'}] {name.ljust(width)}  {detail}")
    passed = all(ok for _, ok, _ in results)
    print(f"\n{'ALL CHECKS PASSED' if passed else 'SOME CHECKS FAILED'} — logs in {work}" if args.keep or not passed
          else "\nALL CHECKS PASSED")
    if passed and not args.keep:
        shutil.rmtree(work, ignore_errors=True)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
