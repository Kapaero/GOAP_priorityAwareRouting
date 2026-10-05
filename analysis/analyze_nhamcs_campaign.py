"""Analyse the NHAMCS-based campaign (one or more shard directories written by the headless player).

Load axis: u = offered load = mean arrival rate / reference wing capacity. Schedules are written on a base window
(300 patients in 1200 s = 15 patients/min) and the runner stretches the time axis by the multiplier m, so the
arrival rate is 15/m per minute and u = (15 / m) / CAPACITY_PER_MIN, with CAPACITY_PER_MIN ~ 6 (saturated wing
throughput measured in earlier runs: 5.7-6.8 patients/min depending on the controller).

Outcome: time-to-assessment = treatment_complete - service_seconds - waiting_room_entry (the logs store arrival,
treatment completion and the service duration). Per (controller, u, seed) means are compared across controllers
with an exact two-sided paired Wilcoxon signed-rank test (every seed is one cohort shared by all controllers),
Holm-adjusted over all reported tests.

Usage:
  python analyze_nhamcs_campaign.py --dirs <experiment dir> [<experiment dir> ...] --out-dir <output dir>
  python analyze_nhamcs_campaign.py --data-dir ../data --out-dir output      (from the bundled exported data)
"""
import argparse
import math
from pathlib import Path

import numpy as np
import pandas as pd

N_PATIENTS = 300
BASE_WINDOW_S = 1200.0
CAPACITY_PER_MIN = 6.0

ARCH_ORDER = [
    "FsmReactiveController",
    "PriorityQueueDispatcher",
    "DecisionTableController",
    "ProposedEnvironmentMediatedReplanner",
]
ARCH_LABEL = {
    "FsmReactiveController": "FSM",
    "PriorityQueueDispatcher": "Priority",
    "DecisionTableController": "DT",
    "ProposedEnvironmentMediatedReplanner": "Proposed",
}
PROPOSED = "ProposedEnvironmentMediatedReplanner"
COLORS = {
    "FsmReactiveController": "#e07b39",
    "PriorityQueueDispatcher": "#3f9142",
    "DecisionTableController": "#8e44ad",
    "ProposedEnvironmentMediatedReplanner": "#2b6cb0",
}


def read_csv_robust(path, **kwargs):
    try:
        return pd.read_csv(path, on_bad_lines="skip", **kwargs)
    except Exception:
        return pd.read_csv(path, on_bad_lines="skip", engine="python", **kwargs)


def offered_load(multiplier):
    return (N_PATIENTS / (BASE_WINDOW_S * multiplier / 60.0)) / CAPACITY_PER_MIN


def load_runs(dirs):
    runs = pd.concat([read_csv_robust(Path(d) / "runs_summary.csv") for d in dirs], ignore_index=True)
    runs["u"] = offered_load(runs.load_multiplier).round(2)
    return runs.drop_duplicates(["run_index"]).sort_values("run_index").reset_index(drop=True)


def reconstruct_patients(dirs):
    cols = ["run_index", "architecture", "load_multiplier", "simulation_elapsed_seconds",
            "event", "agent", "is_critical", "detail"]
    frames = []
    for d in dirs:
        ev = read_csv_robust(Path(d) / "events.csv", usecols=cols)
        ev = ev[ev.event.isin(["spawn", "waiting_room_entry", "treatment_complete"])].copy()
        ev["agent"] = ev.agent.astype(str)
        ev["is_critical"] = ev.is_critical.astype(str).str.strip().str.lower().eq("true")
        frames.append(ev)
    ev = pd.concat(frames, ignore_index=True)
    key = ["run_index", "agent"]

    times = ev.groupby(key + ["event"]).simulation_elapsed_seconds.min().unstack("event")
    meta = ev.groupby(key).agg(architecture=("architecture", "first"),
                               load_multiplier=("load_multiplier", "first"),
                               is_critical=("is_critical", "max"))
    spawn = ev[ev.event == "spawn"].drop_duplicates(key).set_index(key).detail
    service = spawn.str.extract(r"service_seconds=([\d.]+)")[0].astype(float).rename("service_seconds")
    seed = spawn.str.extract(r"service_source=\S*?_(\d+)\s*$")[0].astype(float).rename("seed")

    pat = meta.join(times).join(service).join(seed).reset_index()
    pat = pat.dropna(subset=["waiting_room_entry", "treatment_complete", "service_seconds", "seed"])
    pat["seed"] = pat.seed.astype(int)
    pat["tta_min"] = (pat.treatment_complete - pat.service_seconds - pat.waiting_room_entry) / 60.0
    pat = pat[pat.tta_min >= 0].copy()
    pat["u"] = offered_load(pat.load_multiplier).round(2)
    return pat


def t_crit(df_free):
    """Two-sided 95% Student-t critical value (Cornish-Fisher expansion around the normal quantile)."""
    z = 1.959964
    return z + (z**3 + z) / (4 * df_free) + (5 * z**5 + 16 * z**3 + 3 * z) / (96 * df_free**2)


def wilcoxon_exact(x, y):
    """Exact two-sided paired Wilcoxon signed-rank test. Returns (W_plus, p); zero differences dropped."""
    d = np.asarray(x, float) - np.asarray(y, float)
    d = d[d != 0]
    n = len(d)
    if n == 0:
        return 0.0, 1.0
    absd = np.abs(d)
    order = np.argsort(absd, kind="stable")
    sorted_abs = absd[order]
    ranks = np.empty(n)
    i = 0
    while i < n:
        j = i
        while j < n and sorted_abs[j] == sorted_abs[i]:
            j += 1
        ranks[order[i:j]] = (i + j + 1) / 2.0
        i = j
    r2 = np.rint(ranks * 2).astype(int)  # doubled ranks are integers even with ties
    total = int(r2.sum())
    counts = np.zeros(total + 1)
    counts[0] = 1.0
    for r in r2:
        new = counts.copy()
        new[r:] += counts[: total + 1 - r]
        counts = new
    probs = counts / counts.sum()
    w_obs2 = int(r2[d > 0].sum())
    centre = total / 2.0
    p = float(probs[np.abs(np.arange(total + 1) - centre) >= abs(w_obs2 - centre) - 1e-9].sum())
    return w_obs2 / 2.0, min(1.0, p)


def holm(pvals):
    order = np.argsort(pvals)
    m = len(pvals)
    adj = np.empty(m)
    running = 0.0
    for rank, idx in enumerate(order):
        running = max(running, (m - rank) * pvals[idx])
        adj[idx] = min(1.0, running)
    return adj


def seed_means(pat, u, arch, critical):
    sub = pat[(pat.u == u) & (pat.architecture == arch) & (pat.is_critical == critical)]
    return sub.groupby("seed").tta_min.mean()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dirs", nargs="+", help="shard output directories of the headless player")
    ap.add_argument("--data-dir", type=Path,
                    help="instead of --dirs: folder with patient_records.csv and runs_summary.csv "
                         "(written by export_campaign_data.py)")
    ap.add_argument("--out-dir", required=True, type=Path)
    ap.add_argument("--complete-only", action="store_true",
                    help="interim analysis of a running campaign: keep only the loads for which every controller "
                         "has all seeds finished")
    args = ap.parse_args()
    args.out_dir.mkdir(parents=True, exist_ok=True)

    if args.data_dir:
        runs = pd.read_csv(args.data_dir / "runs_summary.csv")
        pat = pd.read_csv(args.data_dir / "patient_records.csv")
        pat = pat.dropna(subset=["time_to_assessment_seconds", "seed"])
        pat["seed"] = pat.seed.astype(int)
        pat["tta_min"] = pat.time_to_assessment_seconds / 60.0
        pat = pat[pat.tta_min >= 0].copy()
    elif args.dirs:
        runs = load_runs(args.dirs)
        pat = reconstruct_patients(args.dirs)
    else:
        ap.error("give --dirs or --data-dir")
    if args.complete_only:
        counts = runs.groupby(["u", "architecture"]).run_index.nunique().unstack().reindex(columns=ARCH_ORDER).fillna(0)
        expected = int(counts.to_numpy().max())
        complete_u = [u for u in counts.index if (counts.loc[u] == expected).all()]
        print(f"complete-only: {expected} seeds expected, loads kept {complete_u}, dropped "
              f"{sorted(set(counts.index) - set(complete_u))}")
        runs = runs[runs.u.isin(complete_u)]
        pat = pat[pat.u.isin(complete_u)]
    loads = sorted(pat.u.unique())
    seeds = sorted(pat.seed.unique())
    print(f"runs: {len(runs)}  patients: {len(pat)}  seeds: {len(seeds)}  loads u: {loads}")

    timeouts = runs[runs.reason != "all scheduled patients served"]
    if len(timeouts):
        print("WARNING runs that hit the completion timeout:")
        print(timeouts[["run_index", "architecture", "u", "completed_home", "reason"]].to_string(index=False))

    # ---- table 1: per (load, controller) means across seeds ----
    rows = []
    for u in loads:
        for arch in ARCH_ORDER:
            row = {"u": u, "controller": ARCH_LABEL[arch]}
            for cls, crit in (("critical", True), ("normal", False)):
                sm = seed_means(pat, u, arch, crit)
                sub = pat[(pat.u == u) & (pat.architecture == arch) & (pat.is_critical == crit)]
                n = len(sm)
                half = t_crit(max(n - 1, 1)) * sm.std(ddof=1) / math.sqrt(n) if n > 1 else float("nan")
                row.update({f"{cls}_mean": sm.mean(), f"{cls}_ci95": half,
                            f"{cls}_p95": sub.tta_min.quantile(0.95) if len(sub) else float("nan")})
            row["seeds"] = n
            rr = runs[(runs.u == u) & (runs.architecture == arch)]
            row["completion_min"] = rr.simulation_duration_seconds.mean() / 60.0
            rows.append(row)
    table = pd.DataFrame(rows)
    table.to_csv(args.out_dir / "by_load_controller.csv", index=False)
    print("\n" + table.round(3).to_string(index=False))

    # ---- table 2: paired tests, Proposed vs each baseline ----
    trows = []
    for u in loads:
        for cls, crit in (("critical", True), ("normal", False)):
            prop = seed_means(pat, u, PROPOSED, crit)
            for arch in ARCH_ORDER:
                if arch == PROPOSED:
                    continue
                base = seed_means(pat, u, arch, crit)
                common = prop.index.intersection(base.index)
                diff = (prop[common] - base[common]).to_numpy()
                n = len(diff)
                w, p = wilcoxon_exact(prop[common].to_numpy(), base[common].to_numpy())
                half = t_crit(max(n - 1, 1)) * diff.std(ddof=1) / math.sqrt(n) if n > 1 else float("nan")
                trows.append({"u": u, "class": cls, "baseline": ARCH_LABEL[arch], "n_pairs": n,
                              "mean_diff_min": diff.mean(), "ci95": half, "W_plus": w, "p_exact": p,
                              "proposed_better_in": int((diff < 0).sum())})
    tests = pd.DataFrame(trows)
    tests["p_holm"] = holm(tests.p_exact.to_numpy())
    tests.to_csv(args.out_dir / "paired_tests.csv", index=False)
    print("\n" + tests.round(4).to_string(index=False))

    # ---- crossover: where the mean difference (Proposed - baseline) changes sign, by linear interpolation ----
    xrows = []
    for cls in ("critical", "normal"):
        for arch in ARCH_ORDER:
            if arch == PROPOSED:
                continue
            sub = tests[(tests["class"] == cls) & (tests.baseline == ARCH_LABEL[arch])].sort_values("u")
            u_arr, d_arr = sub.u.to_numpy(), sub.mean_diff_min.to_numpy()
            cross = [float(u_arr[i] - d_arr[i] * (u_arr[i + 1] - u_arr[i]) / (d_arr[i + 1] - d_arr[i]))
                     for i in range(len(u_arr) - 1) if d_arr[i] * d_arr[i + 1] < 0]
            xrows.append({"class": cls, "baseline": ARCH_LABEL[arch],
                          "crossover_u": ", ".join(f"{c:.2f}" for c in cross) if cross else "none in range"})
    cross_df = pd.DataFrame(xrows)
    cross_df.to_csv(args.out_dir / "crossover.csv", index=False)
    print("\nCrossover (Proposed - baseline changes sign):")
    print(cross_df.to_string(index=False))

    # ---- figures ----
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.ticker import FuncFormatter, LogLocator, NullFormatter

    plt.rcParams.update({"font.size": 11, "font.family": "sans-serif"})
    fig, axes = plt.subplots(1, 2, figsize=(9.6, 3.6))
    for ax, cls, title, logy in ((axes[0], "critical", "Critical patients", True), (axes[1], "normal", "Normal patients", False)):
        for arch in ARCH_ORDER:
            sub = table[table.controller == ARCH_LABEL[arch]].sort_values("u")
            lw = 2.4 if arch == PROPOSED else 1.5
            ax.plot(sub.u, sub[f"{cls}_mean"], marker="o", ms=4, lw=lw, color=COLORS[arch], label=ARCH_LABEL[arch])
            ax.fill_between(sub.u, sub[f"{cls}_mean"] - sub[f"{cls}_ci95"], sub[f"{cls}_mean"] + sub[f"{cls}_ci95"],
                            color=COLORS[arch], alpha=0.15, lw=0)
        ax.set_xlabel("Offered load u (arrival rate / wing capacity)")
        ax.set_ylabel("Mean time-to-assessment (min)")
        ax.set_title(title)
        if logy:
            ax.set_yscale("log")
            ax.yaxis.set_major_locator(LogLocator(base=10, subs=(1, 2, 3, 4, 5, 6, 8)))
            ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:g}"))
            ax.yaxis.set_minor_formatter(NullFormatter())
        ax.grid(linestyle=":", alpha=0.5)
    axes[0].legend(fontsize=9)
    fig.tight_layout()
    fig.savefig(args.out_dir / "fig_tta_vs_load.png", dpi=200)
    plt.close(fig)

    fig, axes = plt.subplots(1, 2, figsize=(9.6, 3.6))
    for ax, cls, title in ((axes[0], "critical", "Critical: Proposed minus baseline"), (axes[1], "normal", "Normal: Proposed minus baseline")):
        for arch in ARCH_ORDER:
            if arch == PROPOSED:
                continue
            sub = tests[(tests["class"] == cls) & (tests.baseline == ARCH_LABEL[arch])].sort_values("u")
            ax.errorbar(sub.u, sub.mean_diff_min, yerr=sub.ci95, marker="o", ms=4, lw=1.5, capsize=2,
                        color=COLORS[arch], label="vs " + ARCH_LABEL[arch])
        ax.axhline(0, color="black", lw=0.8)
        ax.set_xlabel("Offered load u (arrival rate / wing capacity)")
        ax.set_ylabel("Mean difference (min), < 0 favours Proposed")
        ax.set_title(title)
        ax.grid(linestyle=":", alpha=0.5)
    axes[0].legend(fontsize=9)
    fig.tight_layout()
    fig.savefig(args.out_dir / "fig_gap_vs_load.png", dpi=200)
    plt.close(fig)
    print("\nwritten tables and figures to", args.out_dir)


if __name__ == "__main__":
    main()
