"""
The planned analysis of the two human studies (docs/HUMAN_STUDY_PROTOCOL.md),
run on the CSV files written by tools/export_study_data.py.

    Backend/.venv/bin/python tools/analyze_study.py study_export/ [--json results.json]

Study 1, "search" (conditions MC = no surrogate, GP = epistemic gate):
  H1  efficiency: GP needs fewer playthroughs per archived design than MC
      (per participant; Wilcoxon signed-rank test, one-sided).
  H2  contamination: the replay pass rate of archived designs under GP is
      not lower than under MC by more than 10 percentage points
      (non-inferiority; paired bootstrap confidence interval).
Study 2, "director" (conditions DOFF = tactical director off, DON = on;
both sessions of a participant play the same design sequence, and the
comparison uses the trials both sessions reached):
  D1  the director changes how often players clear what they play
      (per-participant pass rate; Wilcoxon signed-rank, two-sided);
  D2  ... and how much damage they take per trial (same test).
Exploratory, for each study: played pass/fail by condition with trial
number as a covariate (logistic GEE, participants as clusters,
exchangeable correlation); questionnaire items by condition (Wilcoxon,
Holm-corrected); and, for study 1, the fit of the simulated player per
participant: kappa alone at reaction skill 0.5, and both factors (the
reaction skill q from per-archetype damage, then kappa given q).

Prints a plain-text report; --json also writes the numbers. Needs numpy,
scipy and statsmodels (Backend/requirements-analysis.txt).
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import sys
from collections import defaultdict
from typing import Any, Optional

import numpy as np
from scipy import stats

NON_INFERIORITY_MARGIN = 0.10
STUDY_CONDITIONS = {"search": ("MC", "GP"), "director": ("DOFF", "DON")}
NOT_ITEMS = {"participant_id", "session_number", "study", "condition", "comment"}


def read_csv(path: str) -> list[dict]:
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        return []
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    for row in rows:
        row.setdefault("study", "search")
        if not row["study"]:
            row["study"] = "search"
    return rows


def paired(by_participant: dict[str, dict[str, float]], ref: str, other: str) -> tuple[list[str], np.ndarray, np.ndarray]:
    ids = sorted(p for p, v in by_participant.items() if ref in v and other in v)
    return ids, np.array([by_participant[p][ref] for p in ids]), np.array([by_participant[p][other] for p in ids])


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


def wilcoxon(a: np.ndarray, b: np.ndarray, alternative: str = "two-sided") -> tuple[Optional[float], Optional[float]]:
    """W and p of a - b, or (None, None) when every difference is zero."""
    if not np.any(a - b != 0):
        return None, None
    w = stats.wilcoxon(a, b, alternative=alternative)
    return float(w.statistic), float(w.pvalue)


def describe_test(w: Optional[float], p: Optional[float]) -> str:
    return "no participant differs between the conditions (no test)" if w is None else f"W = {w:.1f}, p = {p:.4f}"


def per_participant(rows: list[dict], value) -> dict[str, dict[str, float]]:
    groups: dict[str, dict[str, list[float]]] = defaultdict(lambda: defaultdict(list))
    for r in rows:
        groups[r["participant_id"]][r["condition"]].append(value(r))
    return {p: {c: float(np.mean(v)) for c, v in conds.items()} for p, conds in groups.items()}


def gee(trials: list[dict], other: str, out: list[str], res: dict, label: str) -> None:
    participants = sorted({t["participant_id"] for t in trials})
    out.append(f"\nExploratory: played pass/fail ~ condition + trial (logistic GEE, exchangeable)")
    if len(participants) < 3 or len({t["condition"] for t in trials}) < 2:
        out.append("    needs at least three participants in both conditions")
        return
    rates = {c: np.mean([int(t["passed"]) for t in trials if t["condition"] == c]) for c in {t["condition"] for t in trials}}
    if any(rate in (0.0, 1.0) for rate in rates.values()):
        out.append("    not estimable: a condition passed every trial or failed every trial (perfect separation)")
        res["gee_estimable"] = False
        return
    import statsmodels.api as sm

    y = np.array([int(t["passed"]) for t in trials])
    x = np.column_stack([np.ones(len(trials)), [1.0 if t["condition"] == other else 0.0 for t in trials],
                         [float(t["trial"]) for t in trials]])
    groups = np.array([participants.index(t["participant_id"]) for t in trials])
    fit = sm.GEE(y, x, groups=groups, family=sm.families.Binomial(), cov_struct=sm.cov_struct.Exchangeable()).fit()
    coef, se, p = fit.params[1], fit.bse[1], fit.pvalues[1]
    res["gee_estimable"] = True
    res["gee_odds_ratio"] = math.exp(coef)
    res["gee_ci"] = [math.exp(coef - 1.96 * se), math.exp(coef + 1.96 * se)]
    res["gee_p"] = float(p)
    out.append(f"    odds ratio of passing, {label}: {math.exp(coef):.2f} "
               f"(95% CI {math.exp(coef - 1.96 * se):.2f}-{math.exp(coef + 1.96 * se):.2f}), p = {p:.4f}")


def questionnaire(answers: list[dict], ref: str, other: str, out: list[str], res: dict) -> None:
    out.append(f"\nExploratory: questionnaire items, {other} vs {ref} (Wilcoxon signed-rank, Holm-corrected)")
    items = [k for k in (answers[0].keys() if answers else []) if k not in NOT_ITEMS]
    rows = []
    for item in items:
        by_p: dict[str, dict[str, float]] = defaultdict(dict)
        for a in answers:
            if a.get(item, "") != "":
                by_p[a["participant_id"]][a["condition"]] = float(a[item])
        ids, a_ref, a_other = paired(by_p, ref, other)
        if len(ids) >= 2 and np.any(a_other - a_ref != 0):
            rows.append((item, np.median(a_ref), np.median(a_other), stats.wilcoxon(a_other, a_ref).pvalue, len(ids)))
    for (item, m_ref, m_other, _, n), p_adj in zip(rows, holm([r[3] for r in rows])):
        out.append(f"    {item:14s} median {ref} {m_ref:.1f}, {other} {m_other:.1f} (n = {n}), Holm p = {p_adj:.4f}")
    res["questionnaire_items_tested"] = len(rows)
    if not rows:
        out.append("    not enough paired answers that differ between conditions")


def _design_params(t: dict) -> dict:
    params = {k: float(t[k]) for k in ("enemy_density", "enemy_difficulty_scale", "enemy_tempo",
                                        "elite_rate", "pack_size")}
    params.update({"trap_density": 0.15, "chest_density": 0.15, "heal_density": 0.15,
                   "enemy_behavior_mix": {k[4:]: float(v) for k, v in t.items() if k.startswith("mix_")}})
    return params


def _kappa_ml(sp, params_list: list[dict], passed: np.ndarray, dodge: float) -> float:
    """Maximum-likelihood kappa (log grid) for a fixed reaction skill."""
    threats = [sp.raw_threat(p, dodge=dodge) for p in params_list]
    grid = np.exp(np.linspace(-4, 3, 400))

    def loglik(kappa: float) -> float:
        p = np.array([sp.SimulatedPlayer(kappa=kappa).p_pass_from_threat(th) for th in threats])
        p = np.clip(p, 1e-6, 1 - 1e-6)
        return float(np.sum(passed * np.log(p) + (1 - passed) * np.log(1 - p)))

    return float(max(grid, key=loglik))


def _reaction_fit(sp, rows: list[dict], min_enemies: int = 10) -> Optional[float]:
    """Reaction skill q from per-archetype damage: for each q, the damage
    each archetype is expected to deal per enemy met (the simulated player's
    measured duel table) is scaled by one common factor to the participant's
    observed damage, and q minimizes the squared log error over archetypes,
    weighted by the enemies met. Telegraphed archetypes cost less at higher
    q while the others do not, which is what identifies q."""
    behaviors = [k[2:] for k in rows[0] if k.startswith("n_")]
    if not behaviors:
        return None
    observed = {b: sum(float(r[f"dmg_{b}"] or 0) for r in rows) for b in behaviors}
    met = {b: sum(float(r[f"n_{b}"] or 0) for r in rows) for b in behaviors}
    used = [b for b in behaviors if met[b] >= min_enemies]
    if len(used) < 3 or sum(observed[b] for b in used) <= 0:
        return None
    params_list = [_design_params(r) for r in rows]
    best, best_loss = None, float("inf")
    for q in np.linspace(0, 1, 41):
        expected = {b: 0.0 for b in used}
        for prm, r in zip(params_list, rows):
            threat, enemies = sp.threat_breakdown(prm, dodge=float(q))
            for b in used:
                if enemies.get(b, 0) > 0:
                    expected[b] += float(r[f"n_{b}"] or 0) * threat[b] / enemies[b]
        scale = sum(observed[b] for b in used) / max(1e-9, sum(expected[b] for b in used))
        loss = sum(met[b] * (np.log(observed[b] + 1) - np.log(scale * expected[b] + 1)) ** 2 for b in used)
        if loss < best_loss:
            best, best_loss = float(q), loss
    return best


def kappa_fit(trials: list[dict], out: list[str], res: dict) -> None:
    out.append("\nExploratory: simulated-player fit per participant (maximum likelihood)")
    try:
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Backend"))
        import simulated_player as sp

        grid_edges = (float(np.exp(-4)), float(np.exp(3)))
        one, two = {}, {}
        for participant in sorted({t["participant_id"] for t in trials}):
            rows_p = [t for t in trials if t["participant_id"] == participant]
            params_list = [_design_params(t) for t in rows_p]
            y = np.array([int(t["passed"]) for t in rows_p])
            # One factor: kappa at the default reaction skill 0.5.
            one[participant] = _kappa_ml(sp, params_list, y, 0.5)
            # Two factors: q from per-archetype damage, then kappa given q.
            q = _reaction_fit(sp, rows_p)
            line = f"    {participant}: one factor kappa = {one[participant]:.3f}"
            if q is not None:
                k = _kappa_ml(sp, params_list, y, q)
                two[participant] = {"q": q, "kappa": k}
                line += f"; two factors q = {q:.2f}, kappa = {k:.3f}"
            edge = any(abs(v - e) < 1e-9 for v in [one[participant]] + ([two[participant]["kappa"]] if q is not None else [])
                       for e in grid_edges)
            out.append(line + f" over {len(rows_p)} trials" +
                       (" (kappa at the edge of the search grid: the trials do not bound it)" if edge else ""))
        res["kappa_fit"] = one
        res["skill_fit"] = two
    except Exception as exc:  # noqa: BLE001 - the fit is exploratory; report and carry on
        out.append(f"    skipped ({exc})")


def analyze_search(trials, auto, replays, answers, out: list[str]) -> dict[str, Any]:
    res: dict[str, Any] = {}
    played: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    archived: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    for t in trials:
        played[t["participant_id"]][t["condition"]] += 1
        archived[t["participant_id"]][t["condition"]] += int(t["passed"])
    for a in auto:
        archived[a["participant_id"]][a["condition"]] += int(a["accepted"])
    per_design = {p: {c: played[p][c] / max(1, archived[p][c]) for c in played[p]} for p in played}
    ids, mc, gp = paired(per_design, "MC", "GP")
    res["pairs"] = len(ids)
    res["trials_per_session"] = float(np.mean([v for p in played for v in played[p].values()])) if played else 0.0
    out.append("\nH1  playthroughs per archived design (lower is better)")
    if len(ids) >= 2:
        w, p = wilcoxon(gp, mc, "less")
        res.update(h1_mc_median=float(np.median(mc)), h1_gp_median=float(np.median(gp)), h1_w=w, h1_p=p)
        res["h1_archived_mc"] = float(np.mean([archived[i]["MC"] for i in ids]))
        res["h1_archived_gp"] = float(np.mean([archived[i]["GP"] for i in ids]))
        out.append(f"    MC median {np.median(mc):.2f}, GP median {np.median(gp):.2f}, n = {len(ids)} pairs; "
                   f"Wilcoxon signed-rank (GP < MC): {describe_test(w, p)}")
    else:
        out.append("    fewer than two complete participants")

    rates = per_participant(replays, lambda r: int(r["passed"]))
    ids, mc, gp = paired(rates, "MC", "GP")
    out.append(f"\nH2  replay pass rate of archived designs (non-inferiority margin {NON_INFERIORITY_MARGIN:.0%})")
    if len(ids) >= 2:
        diff = gp - mc
        lo, hi = bootstrap_mean_ci(diff)
        verdict = "non-inferior" if lo > -NON_INFERIORITY_MARGIN else "non-inferiority not shown"
        res.update(h2_mc=float(mc.mean()), h2_gp=float(gp.mean()), h2_diff=float(diff.mean()), h2_ci=[lo, hi],
                   h2_non_inferior=bool(lo > -NON_INFERIORITY_MARGIN), h2_pairs=len(ids))
        out.append(f"    MC {mc.mean():.1%}, GP {gp.mean():.1%}; GP - MC = {diff.mean():+.1%} "
                   f"(95% CI {lo:+.1%} to {hi:+.1%}) -> {verdict}")
    else:
        out.append("    fewer than two complete participants")

    pass_rate = per_participant(trials, lambda t: int(t["passed"]))
    ids, mc, gp = paired(pass_rate, "MC", "GP")
    if ids:
        res.update(pass_mc=float(mc.mean()), pass_gp=float(gp.mean()))
    gee(trials, "GP", out, res, "GP vs MC")
    questionnaire(answers, "MC", "GP", out, res)
    kappa_fit(trials, out, res)
    return res


def matched_trials(trials: list[dict], ref: str, other: str) -> list[dict]:
    """The trials whose index both sessions of the participant reached. In
    study 2 both sessions play the same design sequence, so trial k is the
    same design in both; sessions end on time, so their lengths differ."""
    reached: dict[tuple[str, str], set[int]] = defaultdict(set)
    for t in trials:
        reached[(t["participant_id"], t["condition"])].add(int(t["trial"]))
    common = {p: reached[(p, ref)] & reached[(p, other)] for p, _ in reached}
    return [t for t in trials if int(t["trial"]) in common[t["participant_id"]]]


def analyze_director(trials, replays, answers, out: list[str]) -> dict[str, Any]:
    res: dict[str, Any] = {"trials_all": len(trials)}
    trials = matched_trials(trials, "DOFF", "DON")
    res["trials_matched"] = len(trials)
    out.append(f"Compared on the {len(trials)} trials both sessions of a participant reached (same designs)")
    for key, label, value in (
        ("d1", "D1  played pass rate", lambda t: int(t["passed"])),
        ("d2", "D2  damage per trial", lambda t: float(t["damage"] or 0)),
        ("deaths", "    deaths per trial", lambda t: float(t["deaths"] or 0)),
        ("time", "    seconds per trial", lambda t: float(t["time_sec"] or 0)),
    ):
        ids, off, on = paired(per_participant(trials, value), "DOFF", "DON")
        out.append(f"\n{label} (director off vs on)")
        if len(ids) >= 2:
            w, p = wilcoxon(on, off)
            res.update({f"{key}_off": float(off.mean()), f"{key}_on": float(on.mean()), f"{key}_w": w, f"{key}_p": p})
            out.append(f"    off {off.mean():.3f}, on {on.mean():.3f}, n = {len(ids)} pairs; "
                       f"Wilcoxon signed-rank (two-sided): {describe_test(w, p)}")
        else:
            out.append("    fewer than two complete participants")
        res["pairs"] = len(ids)
    on_trials = [t for t in trials if t["condition"] == "DON"]
    total = sum(float(t["damage"] or 0) for t in on_trials)
    adapted = sum(float(t.get("adapted_damage") or 0) for t in on_trials)
    res["adapted_damage_share"] = adapted / total if total else 0.0
    res["trials_per_session"] = len(trials) / max(1, len({(t["participant_id"], t["condition"]) for t in trials}))
    out.append(f"\n    share of damage dealt by enemies carrying a tactical order (director on): "
               f"{res['adapted_damage_share']:.1%}")
    gee(trials, "DON", out, res, "director on vs off")
    questionnaire(answers, "DOFF", "DON", out, res)
    return res


def analyze(folder: str) -> tuple[str, dict[str, Any]]:
    out: list[str] = []
    results: dict[str, Any] = {}
    tables = {name: read_csv(os.path.join(folder, f"{name}.csv"))
              for name in ("trials", "auto_decisions", "replays", "questionnaires")}
    for study in ("search", "director"):
        part = {name: [r for r in rows if r["study"] == study] for name, rows in tables.items()}
        if not part["trials"]:
            continue
        participants = sorted({t["participant_id"] for t in part["trials"]})
        ref, other = STUDY_CONDITIONS[study]
        out.append(f"=== Study {'1 (search)' if study == 'search' else '2 (director)'}: {ref} vs {other} ===")
        out.append(f"Participants with trials: {len(participants)}; trials: {len(part['trials'])}; "
                   f"auto-decisions: {len(part['auto_decisions'])}; replays: {len(part['replays'])}; "
                   f"questionnaires: {len(part['questionnaires'])}")
        if study == "search":
            res = analyze_search(part["trials"], part["auto_decisions"], part["replays"], part["questionnaires"], out)
        else:
            res = analyze_director(part["trials"], part["replays"], part["questionnaires"], out)
        res["participants"] = len(participants)
        res["trials"] = len(part["trials"])
        results[study] = res
        out.append("")
    return "\n".join(out), results


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder", help="folder written by tools/export_study_data.py")
    parser.add_argument("--json", help="also write the numbers to this file")
    args = parser.parse_args()
    report, results = analyze(args.folder)
    print(report)
    if args.json:
        with open(args.json, "w") as f:
            json.dump(results, f, indent=2)


if __name__ == "__main__":
    main()
