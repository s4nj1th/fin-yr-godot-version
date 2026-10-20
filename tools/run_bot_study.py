"""
Runs both human studies (docs/HUMAN_STUDY_PROTOCOL.md) end to end with
scripted participants instead of people: the real game in study mode, the
real backend with its counterbalanced condition assignment, replay block
and questionnaire endpoints, and the same export and analysis scripts a
human study would use.

Each scripted participant is the exit bot with a reaction skill (the chance
of stepping out of a telegraphed attack) and a playstyle ("exit": walk to
the exit; "hide": also step out of sight of enemies that spot it). Every
participant plays two sessions of each study, as in the protocol.

    python tools/run_bot_study.py --minutes 30 --out Review_2/paper/experiments/results/bot_study

This is a full dry run of the protocol and a test of how the system behaves
with players of different skill and style. It is NOT a substitute for the
human study: the participants are bots, and their questionnaire answers are
fixed placeholders (the scale midpoint), so the questionnaire analysis says
nothing.
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
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BACKEND = os.path.join(ROOT, "Backend")
sys.path.insert(0, os.path.join(ROOT, "tools"))
import analyze_study  # noqa: E402
import export_study_data  # noqa: E402

# Reaction skill x playstyle, two participants each.
PROFILES = [(0.2, "exit"), (0.5, "exit"), (0.8, "exit"), (0.2, "hide"), (0.5, "hide"), (0.8, "hide")]
PANEL = {f"P{i + 1:02d}": PROFILES[i % len(PROFILES)] for i in range(12)}


def backend_python() -> str:
    candidate = os.path.join(BACKEND, ".venv", "bin", "python")
    return candidate if os.path.exists(candidate) else sys.executable


def port_is_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def run_session(godot: str, participant: str, session: int, study: str, minutes: float, log_dir: str) -> dict:
    dodge, style = PANEL[participant]
    auto = f"{participant}:{session}" + (":director" if study == "director" else "")
    started = time.time()
    game = subprocess.run([godot, "--headless", "--fixed-fps", "60", "--path", ROOT, "--", "--autoplay",
                           "--autoplay-timescale=1", "--study", f"--study-auto={auto}", f"--study-minutes={minutes}",
                           f"--bot-dodge={dodge}", f"--bot-style={style}"],
                          capture_output=True, text=True, timeout=7200)
    log = game.stdout + game.stderr
    with open(os.path.join(log_dir, f"{participant}-{study}-{session}.log"), "w") as f:
        f.write(log)
    return {
        "participant_id": participant, "study": study, "session_number": session, "dodge": dodge, "style": style,
        "exit_code": game.returncode, "wall_seconds": round(time.time() - started, 1),
        "dungeons": len(re.findall(r"\[Dungeon\] seed=", log)),
        "director_activations": len(re.findall(r"countermeasure ACTIVATED", log)),
        "retasked": sum(int(m) for m in re.findall(r"Live re-tasking: (\d+) enemies", log)),
        "line_of_sight_breaks": len(re.findall(r"Line-of-sight break #", log)),
        "questionnaire_stored": "Questionnaire stored" in log,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--minutes", type=float, default=30.0, help="session length in game minutes (protocol: 30)")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--participants", type=int, default=len(PANEL), help="use only the first N (for a quick test)")
    parser.add_argument("--out", default=os.path.join(ROOT, "Backend", "data", "bot_study"))
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    parser.add_argument("--reanalyze", action="store_true", help="only redo the export and analysis of an earlier run in --out")
    args = parser.parse_args()
    # Absolute, because the backend runs in Backend/ and resolves EVOLUTION_DATA_DIR there.
    args.out = os.path.abspath(args.out)
    data_dir = os.path.join(args.out, "data")
    if args.reanalyze:
        with open(os.path.join(args.out, "sessions_run.jsonl")) as f:
            sessions = [json.loads(line) for line in f if line.strip()]
        return summarize(args.out, data_dir, sessions)

    if not port_is_free(8001):
        print("Port 8001 is in use — stop the running backend first.")
        return 1
    if os.path.exists(args.out):
        shutil.rmtree(args.out)
    log_dir = os.path.join(args.out, "logs")
    os.makedirs(log_dir)
    env = {**os.environ, "EVOLUTION_DATA_DIR": data_dir, "LLM_MUTATION_MODE": "off"}
    backend_log = open(os.path.join(args.out, "backend.log"), "w")
    backend = subprocess.Popen([backend_python(), "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", "8001"],
                               cwd=BACKEND, env=env, stdout=backend_log, stderr=subprocess.STDOUT)
    try:
        for _ in range(100):
            try:
                urllib.request.urlopen("http://127.0.0.1:8001/api/health", timeout=2)
                break
            except OSError:
                time.sleep(0.2)
        panel = list(PANEL)[: args.participants]
        jobs = [(p, s, study) for study in ("search", "director") for p in panel for s in (1, 2)]
        print(f"{len(jobs)} sessions of {args.minutes} game minutes on {args.workers} games...", flush=True)
        with ThreadPoolExecutor(args.workers) as pool:
            sessions = list(pool.map(lambda j: run_session(args.godot, j[0], j[1], j[2], args.minutes, log_dir), jobs))
    finally:
        backend.terminate()
        try:
            backend.wait(timeout=10)
        except subprocess.TimeoutExpired:
            backend.kill()
        backend_log.close()

    with open(os.path.join(args.out, "sessions_run.jsonl"), "w") as f:
        for s in sessions:
            f.write(json.dumps(s) + "\n")
    with open(os.path.join(args.out, "panel.json"), "w") as f:
        json.dump({p: {"dodge": d, "style": st} for p, (d, st) in PANEL.items()}, f, indent=2)
    return summarize(args.out, data_dir, sessions)


def summarize(out: str, data_dir: str, sessions: list[dict]) -> int:
    failed = [s for s in sessions if s["exit_code"] != 0 or not s["questionnaire_stored"]]
    print(f"{len(sessions) - len(failed)} of {len(sessions)} sessions completed with a stored questionnaire")

    export_dir = os.path.join(out, "export")
    counts = export_study_data.export(data_dir, export_dir)
    print("Exported:", counts)
    report, results = analyze_study.analyze(export_dir)
    results["sessions"] = {"total": len(sessions), "failed": len(failed),
                           "director_activations": {st: sum(s["director_activations"] for s in sessions if s["study"] == st)
                                                    for st in ("search", "director")},
                           "retasked": {st: sum(s["retasked"] for s in sessions if s["study"] == st)
                                        for st in ("search", "director")}}
    with open(os.path.join(out, "report.txt"), "w") as f:
        f.write(report)
    with open(os.path.join(out, "results.json"), "w") as f:
        json.dump(results, f, indent=2)
    print(report)
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
