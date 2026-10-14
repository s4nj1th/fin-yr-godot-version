"""
Generates static charts from Backend/data/session_log.jsonl answering:
"how does each enemy AI archetype behave, in different situations, and how
is that changing across evolutionary generations?" See
docs/BEHAVIOR_ANALYTICS.md for the full picture; behavior_analytics.py holds
the (unit-tested) aggregation math this script only plots.

Run with:
    pip install -r requirements-analysis.txt
    python analyze_behavior_telemetry.py [--log PATH] [--output-dir DIR] [--evaluator all|human|bot]

Produces up to five PNGs in --output-dir (default reports/):
    genome_mix_evolution.png   - always, from every report_outcome trial ever logged
    archetype_profile.png      - per-archetype combat comparison
    archetype_evolution.png    - per-archetype combat metrics across generations
    situational_comparison.png - baseline vs. adapted, per archetype
    archetype_outliers.png     - per-trial spread per archetype, statistical outliers marked

Plus a console-only threshold report (no PNG — see print_threshold_report)
listing every trial that crossed a config.THRESHOLDS boundary (e.g. an
unusually crowded dungeon, an archetype dealing disproportionate damage).

The latter four need trials played with the EnemyBehaviorTelemetry-
instrumented client (Scripts/AI/EnemyBehaviorTelemetry.cs) — every trial
logged before that instrumentation existed has no per-archetype breakdown,
so this script says so and skips them rather than plotting fabricated zeros.

--evaluator defaults to "all" (human + bot trials combined). Pass "human"
or "bot" to see one evaluator in isolation — e.g. to sanity-check the
scripted bot evaluator (docs/EVOLUTIONARY_DESIGN.md "Scripted bot
evaluator") against human play before trusting its volume. Filtered runs
get a "_human"/"_bot" filename suffix so they don't overwrite the combined
charts or each other.

Color usage follows the categorical-palette method in the project's dataviz
skill: a fixed hue order assigned by identity (never re-cycled), a legend
whenever color encodes a series shared across x-axis categories, and no
dual-axis charts (each metric gets its own subplot instead of sharing a
scale with a different one).
"""

from __future__ import annotations

import argparse
import os

import behavior_analytics as ba
import config

# Fixed categorical order + hex values, taken from the project's dataviz
# skill reference palette (slots 1-4: blue, orange, aqua, yellow) — see
# that skill's references/palette.md. Assigned by archetype identity so the
# same archetype is always the same color across every chart in this
# script, never re-cycled based on which archetypes happen to have data.
ARCHETYPE_COLORS = {
    "melee_chaser": "#2a78d6",
    "melee_hit_run": "#eb6834",
    "ranged_kite": "#1baf7a",
    "ranged_strafe": "#eda100",
}
# Baseline vs. adapted is a two-way situational split, not an archetype
# identity, so it gets its own two slots (blue/orange) reused consistently
# across every archetype's subplot.
SITUATION_COLORS = {"baseline": "#2a78d6", "adapted": "#eb6834"}

CHART_SURFACE = "#fcfcfb"
PRIMARY_INK = "#0b0b0b"
SECONDARY_INK = "#52514e"
MUTED_INK = "#898781"
GRIDLINE = "#e1e0d9"
AXIS_LINE = "#c3c2b7"
# Reserved status color (dataviz skill's status palette, never reused for a
# regular series) — marks statistical outlier points on plot_archetype_outliers
# only. Never used to encode archetype/situation identity.
STATUS_CRITICAL = "#d03b3b"

METRIC_LABELS = {
    "damage_per_spawn": "Damage dealt to player / enemy spawned",
    "damage_taken_per_spawn": "Damage taken from player / enemy spawned",
    "kill_rate": "Killed-by-player rate",
    "rear_hit_rate": "Rear-hit rate (of hits taken)",
}


def _style_axis(ax, title):
    ax.set_facecolor(CHART_SURFACE)
    ax.set_title(title, color=PRIMARY_INK, fontsize=10, loc="left")
    ax.grid(axis="y", color=GRIDLINE, linewidth=0.8, zorder=0)
    ax.set_axisbelow(True)
    for spine_name, spine in ax.spines.items():
        if spine_name == "bottom":
            spine.set_color(AXIS_LINE)
        else:
            spine.set_visible(False)
    ax.tick_params(colors=MUTED_INK, labelsize=8)


def _archetype_order(archetypes) -> list[str]:
    """config.BEHAVIOR_TYPES order first (the canonical, documented order),
    then anything unexpected appended rather than silently dropped."""
    known = [a for a in config.BEHAVIOR_TYPES if a in archetypes]
    unknown = sorted(a for a in archetypes if a not in config.BEHAVIOR_TYPES)
    return known + unknown


def _output_path(output_dir: str, stem: str, suffix: str) -> str:
    """suffix is "" for --evaluator all (unchanged filenames, matching every
    prior run) or e.g. "human"/"bot" — so filtered runs don't silently
    overwrite the combined chart, or each other, in the same output dir."""
    filename = f"{stem}_{suffix}.png" if suffix else f"{stem}.png"
    return os.path.join(output_dir, filename)


def plot_genome_mix_evolution(events, output_dir: str, suffix: str = "") -> str | None:
    series = ba.genome_mix_series(events)
    if not series:
        print("[genome_mix_evolution] No report_outcome trials with genome params logged yet — skipping.")
        return None

    archetypes = _archetype_order({a for _, mix in series for a in mix})
    generations = [g for g, _ in series]

    fig, ax = plt.subplots(figsize=(7, 4.5), dpi=150)
    fig.patch.set_facecolor(CHART_SURFACE)
    for archetype in archetypes:
        values = [mix.get(archetype, 0.0) for _, mix in series]
        ax.plot(generations, values, color=ARCHETYPE_COLORS.get(archetype, MUTED_INK),
                 linewidth=2, marker="o", markersize=4, label=archetype)

    _style_axis(ax, "Evolutionary loop: proposed enemy_behavior_mix weight by generation")
    ax.set_xlabel("Genome generation", color=SECONDARY_INK, fontsize=9)
    ax.set_ylabel("Average mix weight", color=SECONDARY_INK, fontsize=9)
    ax.legend(frameon=False, fontsize=8, labelcolor=PRIMARY_INK)

    path = _output_path(output_dir, "genome_mix_evolution", suffix)
    fig.tight_layout()
    fig.savefig(path, facecolor=CHART_SURFACE)
    plt.close(fig)
    return path


def plot_archetype_profile(events, output_dir: str, suffix: str = "") -> str | None:
    events = ba.events_with_behavior_breakdown(events)
    if not events:
        print("[archetype_profile] No trials with per-archetype combat telemetry yet — "
              "play trials with the updated client to populate this chart.")
        return None

    profiles = ba.archetype_overall_profiles(events)
    archetypes = _archetype_order(profiles.keys())
    metrics = list(METRIC_LABELS.keys())

    fig, axes = plt.subplots(2, 2, figsize=(9, 7), dpi=150)
    fig.patch.set_facecolor(CHART_SURFACE)
    for ax, metric in zip(axes.flat, metrics):
        values = [profiles[a][metric] for a in archetypes]
        colors = [ARCHETYPE_COLORS.get(a, MUTED_INK) for a in archetypes]
        ax.bar(archetypes, values, color=colors, width=0.6, zorder=2)
        ax.set_xticks(range(len(archetypes)))
        ax.set_xticklabels(archetypes, rotation=20, ha="right")
        _style_axis(ax, METRIC_LABELS[metric])

    title_suffix = f" — evaluator: {suffix}" if suffix else ""
    fig.suptitle(f"Enemy archetype combat profile (baseline + adapted combined{title_suffix})",
                 color=PRIMARY_INK, fontsize=12, wrap=True)
    path = _output_path(output_dir, "archetype_profile", suffix)
    fig.tight_layout()
    fig.savefig(path, facecolor=CHART_SURFACE)
    plt.close(fig)
    return path


def plot_archetype_evolution(events, output_dir: str, suffix: str = "") -> str | None:
    events = ba.events_with_behavior_breakdown(events)
    if not events:
        print("[archetype_evolution] No trials with per-archetype combat telemetry yet — skipping.")
        return None

    series = ba.generation_series(events)
    archetypes = _archetype_order({a for _, profiles in series for a in profiles})
    generations = [g for g, _ in series]

    fig, axes = plt.subplots(1, 2, figsize=(11, 4.5), dpi=150)
    fig.patch.set_facecolor(CHART_SURFACE)
    for ax, metric in zip(axes, ["damage_per_spawn", "kill_rate"]):
        for archetype in archetypes:
            values = [profiles.get(archetype, {}).get(metric, 0.0) for _, profiles in series]
            ax.plot(generations, values, color=ARCHETYPE_COLORS.get(archetype, MUTED_INK),
                     linewidth=2, marker="o", markersize=4, label=archetype)
        _style_axis(ax, METRIC_LABELS[metric])
        ax.set_xlabel("Genome generation", color=SECONDARY_INK, fontsize=9)

    handles, labels = axes[0].get_legend_handles_labels()
    title_suffix = f" — evaluator: {suffix}" if suffix else ""
    fig.suptitle(f"Enemy archetype combat metrics across generations{title_suffix}",
                 color=PRIMARY_INK, fontsize=12, wrap=True)
    fig.tight_layout(rect=(0, 0.08, 1, 1))
    fig.legend(handles, labels, loc="lower center", ncol=len(archetypes),
               frameon=False, fontsize=8, labelcolor=PRIMARY_INK)
    path = _output_path(output_dir, "archetype_evolution", suffix)
    fig.savefig(path, facecolor=CHART_SURFACE)
    plt.close(fig)
    return path


def plot_situational_comparison(events, output_dir: str, suffix: str = "") -> str | None:
    events = ba.events_with_behavior_breakdown(events)
    if not events:
        print("[situational_comparison] No trials with per-archetype combat telemetry yet — skipping.")
        return None

    profiles = ba.archetype_situation_profiles(events)
    archetypes = _archetype_order(profiles.keys())
    metrics = list(METRIC_LABELS.keys())

    fig, axes = plt.subplots(2, 2, figsize=(9, 7), dpi=150)
    fig.patch.set_facecolor(CHART_SURFACE)
    bar_width = 0.35
    positions = list(range(len(archetypes)))
    for ax, metric in zip(axes.flat, metrics):
        for offset, situation in zip((-1, 1), ("baseline", "adapted")):
            values = [profiles[a][situation][metric] for a in archetypes]
            xs = [p + offset * bar_width / 2 for p in positions]
            ax.bar(xs, values, width=bar_width, color=SITUATION_COLORS[situation],
                    label=situation, zorder=2)
        ax.set_xticks(positions)
        ax.set_xticklabels(archetypes, rotation=20, ha="right")
        _style_axis(ax, METRIC_LABELS[metric])

    axes[0, 0].legend(frameon=False, fontsize=8, labelcolor=PRIMARY_INK)
    title_suffix = f" — evaluator: {suffix}" if suffix else ""
    fig.suptitle("Baseline vs. adapted enemies, per archetype "
                 f"(adapted = active AdaptationDirector order{title_suffix})",
                 color=PRIMARY_INK, fontsize=11, wrap=True)
    path = _output_path(output_dir, "situational_comparison", suffix)
    fig.tight_layout()
    fig.savefig(path, facecolor=CHART_SURFACE)
    plt.close(fig)
    return path


def plot_archetype_outliers(events, output_dir: str, suffix: str = "") -> str | None:
    """Box plots (per-archetype, per-trial distribution) with statistical
    outliers marked — see docs/BEHAVIOR_ANALYTICS.md "Outliers and
    thresholds". Distinct from the other three archetype charts: those
    show one aggregated number per archetype; this shows the *spread*
    across individual trials, so a single freak trial doesn't hide inside
    an average. matplotlib's boxplot places fliers using the same 1.5xIQR
    rule as behavior_analytics.find_iqr_outliers, so the marked points here
    are exactly what that function would flag."""
    events = ba.events_with_behavior_breakdown(events)
    if not events:
        print("[archetype_outliers] No trials with per-archetype combat telemetry yet — skipping.")
        return None

    metrics = list(METRIC_LABELS.keys())
    fig, axes = plt.subplots(2, 2, figsize=(9, 7), dpi=150)
    fig.patch.set_facecolor(CHART_SURFACE)

    any_outliers = False
    for ax, metric in zip(axes.flat, metrics):
        per_archetype = ba.per_trial_archetype_metric_values(events, metric)
        archetypes = _archetype_order(per_archetype.keys())
        data = [per_archetype[a] for a in archetypes]

        box = ax.boxplot(
            data, tick_labels=archetypes, patch_artist=True, widths=0.5,
            flierprops={"marker": "x", "markerfacecolor": STATUS_CRITICAL, "markeredgecolor": STATUS_CRITICAL,
                        "markersize": 7},
            medianprops={"color": PRIMARY_INK},
            whiskerprops={"color": AXIS_LINE}, capprops={"color": AXIS_LINE},
        )
        for patch, archetype in zip(box["boxes"], archetypes):
            patch.set_facecolor(ARCHETYPE_COLORS.get(archetype, MUTED_INK))
            patch.set_alpha(0.55)
            patch.set_edgecolor(ARCHETYPE_COLORS.get(archetype, MUTED_INK))

        if any(len(fl.get_xdata()) > 0 for fl in box["fliers"]):
            any_outliers = True

        ax.set_xticklabels(archetypes, rotation=20, ha="right")
        _style_axis(ax, METRIC_LABELS[metric])

    title_suffix = f" — evaluator: {suffix}" if suffix else ""
    outlier_note = "red x = statistical outlier (1.5x IQR)" if any_outliers else "no statistical outliers found"
    fig.suptitle(f"Per-trial spread by archetype ({outlier_note}{title_suffix})",
                 color=PRIMARY_INK, fontsize=11, wrap=True)
    path = _output_path(output_dir, "archetype_outliers", suffix)
    fig.tight_layout()
    fig.savefig(path, facecolor=CHART_SURFACE)
    plt.close(fig)
    return path


def print_threshold_report(events) -> None:
    """Console-only, not a chart: lists every trial that crossed a
    config.THRESHOLDS boundary (see docs/BEHAVIOR_ANALYTICS.md). Kept as
    text rather than a fifth PNG since the payload is "which specific
    genome_id and why," not a shape that benefits from a plot."""
    events = ba.events_with_behavior_breakdown(events)
    flagged = ba.flag_threshold_exceeding(events)
    if not flagged:
        print("[threshold_report] No trials crossed a config.THRESHOLDS boundary.")
    else:
        print(f"[threshold_report] {len(flagged)} trial(s) crossed a threshold:")
        for record in flagged:
            genome_id = record["genome_id"] or "(unknown)"
            print(f"  gen {record['generation']} — genome {genome_id}:")
            for reason in record["reasons"]:
                print(f"    - {reason}")

    print()
    _print_situational_shift_report(events)


def _print_situational_shift_report(events) -> None:
    """Console-only subsection of the threshold report: flags archetype/metric
    combinations where the aggregate behavior shifts substantially between
    baseline and adapted situations (see behavior_analytics.flag_situational_shift
    and docs/BEHAVIOR_ANALYTICS.md "Situational shift"). This is the answer to
    "how does behavior differ across situations" — not per-trial or per-archetype
    raw-value outliers, but a difference *between the two situations themselves*,
    collapsed across all trials so it speaks to the archetype-level pattern rather
    than any individual genome."""
    shifted = ba.flag_situational_shift(events)
    if not shifted:
        print("[situational_shift] No archetype/metric pairs crossed the situational-shift threshold.")
        return

    print(f"[situational_shift] {len(shifted)} archetype/metric pair(s) show a "
          f"meaningful shift between baseline and adapted situations "
          f"(threshold: |Δ| ≥ {config.THRESHOLDS.get('high_situational_shift_pct', 0.5):.0%}):")
    for record in shifted:
        pct = record["percent_change"]
        pct_str = f"{pct:+.0%}" if pct != float("inf") else "∞ (baseline was zero)"
        print(
            f"  {record['archetype']} — {record['metric']}: "
            f"baseline={record['baseline']:.3f}, adapted={record['adapted']:.3f}, "
            f"Δ={record['delta']:+.3f} ({pct_str})"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.strip().splitlines()[0])
    parser.add_argument("--log", default=config.SESSION_LOG_FILE, help="Path to session_log.jsonl")
    parser.add_argument("--output-dir", default=os.path.join(os.path.dirname(__file__), "reports"),
                         help="Directory to write PNG charts into")
    parser.add_argument("--player", default="all",
                        help="Only this player's trials (default: all players)")
    parser.add_argument("--evaluator", choices=["all", "human", "bot"], default="all",
                         help="Restrict to trials from one evaluator (see docs/EVOLUTIONARY_DESIGN.md "
                              "'Scripted bot evaluator'). Default 'all' mixes human and bot trials — "
                              "pass 'human' or 'bot' to compare them separately rather than blended.")
    args = parser.parse_args()

    os.makedirs(args.output_dir, exist_ok=True)
    events = ba.load_report_events(args.log)
    counts = ba.count_by_evaluator_type(events)
    print(f"Loaded {len(events)} report_outcome trial(s) from {args.log} "
          f"({counts['human']} human, {counts['bot']} bot)")

    events = ba.filter_by_evaluator_type(events, args.evaluator)
    events = ba.filter_by_player(events, args.player)
    suffix = "" if args.evaluator == "all" else args.evaluator
    if suffix:
        print(f"Filtered to evaluator_type={args.evaluator}: {len(events)} trial(s)")

    generated = [
        plot_genome_mix_evolution(events, args.output_dir, suffix),
        plot_archetype_profile(events, args.output_dir, suffix),
        plot_archetype_evolution(events, args.output_dir, suffix),
        plot_situational_comparison(events, args.output_dir, suffix),
        plot_archetype_outliers(events, args.output_dir, suffix),
    ]
    generated = [p for p in generated if p]

    print()
    print_threshold_report(events)

    if not generated:
        print("Nothing to plot yet.")
        return

    print(f"\nWrote {len(generated)} chart(s) to {args.output_dir}:")
    for path in generated:
        print(f"  {path}")


if __name__ == "__main__":
    try:
        import matplotlib.pyplot as plt
    except ImportError:
        raise SystemExit(
            "matplotlib is required for this script but isn't installed.\n"
            "Install it with: pip install -r requirements-analysis.txt"
        )
    main()
else:
    # Importing this module for its plotting functions (e.g. from a test or
    # a notebook) still needs matplotlib — fail the same way, just without
    # the __main__ guard's friendlier SystemExit framing, since callers here
    # are expected to handle ImportError themselves if they care to.
    import matplotlib.pyplot as plt
