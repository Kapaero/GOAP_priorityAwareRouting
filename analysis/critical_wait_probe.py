"""How long do CRITICAL patients sit in the waiting queue while cubicles are free?

From the 1-second time series of every run: a row counts as 'critical wait with free cubicles' when at least one critical
patient is in the queue (critical_queue_count > 0) although at least one cubicle on either side is free.  With working
priority access this should be (almost) zero: a critical patient at the head of the queue is dispatched at once if a cubicle
is free.  Reported per load and controller: share of simulated time and critical-patient-seconds per 100 s of run time.

Usage: python critical_wait_probe.py --dirs <shard dirs>
"""
import argparse
import sys
from pathlib import Path

import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_nhamcs_campaign import ARCH_LABEL, ARCH_ORDER, offered_load, read_csv_robust  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dirs", nargs="+", required=True)
    args = ap.parse_args()

    cols = ["run_index", "architecture", "load_multiplier", "simulation_elapsed_seconds", "queue_count",
            "critical_queue_count", "free_cubicle", "free_cubicle_left"]
    frames = []
    for d in args.dirs:
        ts = read_csv_robust(Path(d) / "time_series.csv", usecols=cols)
        runs = read_csv_robust(Path(d) / "runs_summary.csv", usecols=["run_index"])
        ts = ts[ts.run_index.isin(runs.run_index)]  # finished runs only
        frames.append(ts)
    ts = pd.concat(frames, ignore_index=True)
    ts["u"] = offered_load(ts.load_multiplier).round(2)
    ts["free_any"] = (ts.free_cubicle + ts.free_cubicle_left) > 0
    ts["crit_wait_free"] = (ts.critical_queue_count > 0) & ts.free_any
    ts["crit_wait"] = ts.critical_queue_count > 0

    rows = []
    for (u, arch), g in ts.groupby(["u", "architecture"]):
        n_runs = g.run_index.nunique()
        rows.append({"u": u, "controller": ARCH_LABEL[arch], "runs": n_runs,
                     "rows_with_critical_in_queue_%": 100 * g.crit_wait.mean(),
                     "of_which_with_free_cubicles_%": 100 * g.crit_wait_free.mean(),
                     "crit_patient_s_waiting_with_free_cubicles_per_run": g.loc[g.crit_wait_free, "critical_queue_count"].sum() / n_runs})
    out = pd.DataFrame(rows)
    order = {ARCH_LABEL[a]: i for i, a in enumerate(ARCH_ORDER)}
    out = out.sort_values(["u", "controller"], key=lambda s: s.map(order) if s.name == "controller" else s)
    print(out.round(2).to_string(index=False))


if __name__ == "__main__":
    main()
