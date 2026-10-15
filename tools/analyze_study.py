"""
The planned analysis of the human study (docs/HUMAN_STUDY_PROTOCOL.md),
run on the CSV files written by tools/export_study_data.py.

    Backend/.venv/bin/python tools/analyze_study.py study_export/

Primary hypotheses (conditions MC = no surrogate, GP = epistemic gate):
  H1  efficiency: GP needs fewer playthroughs per archived design than MC
      (per participant; Wilcoxon signed-rank test, one-sided).
  H2  contamination: the replay pass rate of archived designs under GP is
      not lower than under MC by more than 10 percentage points
      (non-inferiority; paired bootstrap confidence interval).
Exploratory:
  - played pass/fail by condition, with trial number as a covariate, by a
    logistic GEE with participants as clusters (exchangeable correlation);
  - questionnaire items by condition (Wilcoxon signed-rank, Holm-corrected);
  - fit of the simulated player: per participant, kappa estimated from the
    played outcomes and the nominal threat of each design.

Prints a plain-text report. Needs numpy, scipy and statsmodels
(Backend/requirements-analysis.txt).
"""

from __future__ import annotations

import argparse
import csv
import math
import os
import sys
from collections import defaultdict

import numpy as np
from scipy import stats

NON_INFERIORITY_MARGIN = 0.10


def read_csv(path: str) -> list[dict]:
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        return []
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def paired(by_participant: dict[str, dict[str, float]]) -> tuple[list[str], np.ndarray, np.ndarray]:
    ids = sorted(p for p, v in by_participant.items() if "MC" in v and "GP" in v)
    return ids, np.array([by_participant[p]["MC"] for p in ids]), np.array([by_participant[p]["GP"] for p in ids])


def holm(pvalues: list[float]) -> list[float]:
    order = np.argsort(pvalues)
    adjusted = [0.0] * len(pvalues)
    running = 0.0
    for rank, i in enumerate(order):
        running = max(running, min(1.0, (len(pvalues) - rank) * pvalues[i]))
        adjusted[i] = running
    return adjusted


def bootstrap_mean_ci(x: np.ndarray, n: int = 10000, seed: int = 0) -> tuple[float, float]:
    rng = np.random.default_rng(seed)
    means = rng.choice(x, size=(n, x.size), replace=True).mean(axis=1)
    return float(np.percentile(means, 2.5)), float(np.percentile(means, 97.5))


def analyze(folder: str) -> str:
    out: list[str] = []
    trials = read_csv(os.path.join(folder, "trials.csv"))
    auto = read_csv(os.path.join(folder, "auto_decisions.csv"))
    replays = read_csv(os.path.join(folder, "replays.csv"))
    answers = read_csv(os.path.join(folder, "questionnaires.csv"))
    participants = sorted({t["participant_id"] for t in trials})
    out.append(f"Participants with trials: {len(participants)}; trials: {len(trials)}; "
               f"auto-decisions: {len(auto)}; replays: {len(replays)}; questionnaires: {len(answers)}")

    # H1: playthroughs per archived design.
    played: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    archived: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    for t in trials:
        played[t["participant_id"]][t["condition"]] += 1
        archived[t["participant_id"]][t["condition"]] += int(t["passed"])
    for a in auto:
        archived[a["participant_id"]][a["condition"]] += int(a["accepted"])
    per_design = {p: {c: played[p][c] / max(1, archived[p][c]) for c in played[p]} for p in played}
    ids, mc, gp = paired(per_design)
    out.append("\nH1  playthroughs per archived design (lower is better)")
    if len(ids) >= 2:
        w = stats.wilcoxon(gp, mc, alternative="less")
        out.append(f"    MC median {np.median(mc):.2f}, GP median {np.median(gp):.2f}, n = {len(ids)} pairs; "
                   f"Wilcoxon signed-rank (GP < MC): W = {w.statistic:.1f}, p = {w.pvalue:.4f}")
    else:
        out.append("    fewer than two complete participants")

    # H2: replay pass rate (1 - contamination).
    replay_rate: dict[str, dict[str, list[int]]] = defaultdict(lambda: defaultdict(list))
    for r in replays:
        replay_rate[r["participant_id"]][r["condition"]].append(int(r["passed"]))
    rates = {p: {c: float(np.mean(v)) for c, v in conds.items()} for p, conds in replay_rate.items()}
    ids, mc, gp = paired(rates)
    out.append(f"\nH2  replay pass rate of archived designs (non-inferiority margin {NON_INFERIORITY_MARGIN:.0%})")
    if len(ids) >= 2:
        diff = gp - mc
        lo, hi = bootstrap_mean_ci(diff)
        verdict = "non-inferior" if lo > -NON_INFERIORITY_MARGIN else "non-inferiority not shown"
        out.append(f"    MC {mc.mean():.1%}, GP {gp.mean():.1%}; GP - MC = {diff.mean():+.1%} "
                   f"(95% CI {lo:+.1%} to {hi:+.1%}) -> {verdict}")
    else:
        out.append("    fewer than two complete participants")

    # Exploratory: played outcomes, logistic GEE.
    out.append("\nExploratory: played pass/fail ~ condition + trial (logistic GEE, exchangeable)")
    if len(participants) >= 3 and len({t["condition"] for t in trials}) == 2:
        import statsmodels.api as sm

        y = np.array([int(t["passed"]) for t in trials])
        x = np.column_stack([np.ones(len(trials)), [1.0 if t["condition"] == "GP" else 0.0 for t in trials],
                             [float(t["trial"]) for t in trials]])
        groups = np.array([participants.index(t["participant_id"]) for t in trials])
        model = sm.GEE(y, x, groups=groups, family=sm.families.Binomial(), cov_struct=sm.cov_struct.Exchangeable())
        fit = model.fit()
        coef, se, p = fit.params[1], fit.bse[1], fit.pvalues[1]
        out.append(f"    odds ratio of passing, GP vs MC: {math.exp(coef):.2f} "
                   f"(95% CI {math.exp(coef - 1.96 * se):.2f}-{math.exp(coef + 1.96 * se):.2f}), p = {p:.4f}")
    else:
        out.append("    needs at least three participants in both conditions")

    # Exploratory: questionnaire items.
    out.append("\nExploratory: questionnaire items, GP vs MC (Wilcoxon signed-rank, Holm-corrected)")
    items = [k for k in (answers[0].keys() if answers else []) if k not in
             {"participant_id", "session_number", "condition", "comment"}]
    rows = []
    for item in items:
        by_p = {p: {} for p in {a["participant_id"] for a in answers}}
        for a in answers:
            if a.get(item, "") != "":
                by_p[a["participant_id"]][a["condition"]] = float(a[item])
        ids, mc, gp = paired(by_p)
        if len(ids) >= 2 and np.any(gp - mc != 0):
            rows.append((item, np.median(mc), np.median(gp), stats.wilcoxon(gp, mc).pvalue, len(ids)))
    for (item, m_mc, m_gp, _, n), p_adj in zip(rows, holm([r[3] for r in rows])):
        out.append(f"    {item:14s} median MC {m_mc:.1f}, GP {m_gp:.1f} (n = {n}), Holm p = {p_adj:.4f}")
    if not rows:
        out.append("    not enough paired answers")

    # Exploratory: fit of the simulated player.
    out.append("\nExploratory: simulated-player fit (kappa per participant, maximum likelihood)")
    try:
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Backend"))
        import simulated_player as sp

        for participant in participants:
            rows_p = [t for t in trials if t["participant_id"] == participant]
            threats = []
            for t in rows_p:
                params = {k: float(t[k]) for k in ("enemy_density", "enemy_difficulty_scale", "enemy_tempo",
                                                    "elite_rate", "pack_size")}
                params.update({"trap_density": 0.15, "chest_density": 0.15, "heal_density": 0.15,
                               "enemy_behavior_mix": {k[4:]: float(v) for k, v in t.items() if k.startswith("mix_")}})
                threats.append(sp.raw_threat(params, dodge=0.5))
            y = np.array([int(t["passed"]) for t in rows_p])
            grid = np.exp(np.linspace(-4, 3, 400))

            def loglik(kappa: float) -> float:
                p = np.array([sp.SimulatedPlayer(kappa=kappa).p_pass_from_threat(th) for th in threats])
                p = np.clip(p, 1e-6, 1 - 1e-6)
                return float(np.sum(y * np.log(p) + (1 - y) * np.log(1 - p)))

            best = max(grid, key=loglik)
            out.append(f"    {participant}: kappa = {best:.3f} over {len(rows_p)} trials")
    except Exception as exc:  # noqa: BLE001 - the fit is exploratory; report and carry on
        out.append(f"    skipped ({exc})")

    return "\n".join(out)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder", help="folder written by tools/export_study_data.py")
    print(analyze(parser.parse_args().folder))


if __name__ == "__main__":
    main()
