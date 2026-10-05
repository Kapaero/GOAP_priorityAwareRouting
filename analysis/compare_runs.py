"""Compare the event streams of the same campaign runs written by two builds / processes (wall-clock column
excluded). Used to show that a performance fix changes nothing in the simulation: every run that is finished in
both directories must reproduce row for row.

Usage: python compare_runs.py <dir A> <dir B> [--runs 0-11]
       <dir A> / <dir B>: one shard output directory each (events.csv + runs_summary.csv)
"""
import argparse
from pathlib import Path

import pandas as pd

COLS = ["run_index", "architecture", "load_multiplier", "simulation_elapsed_seconds", "frame", "event", "agent",
        "is_critical", "queue_count", "critical_queue_count", "inside_wing", "free_cubicle", "free_cubicle_left",
        "world_counter", "detail"]


def finished_runs(d):
    rs = pd.read_csv(Path(d) / "runs_summary.csv", on_bad_lines="skip", engine="python")
    return set(int(i) for i in rs.run_index)


def load_events(d, runs):
    ev = pd.read_csv(Path(d) / "events.csv", usecols=COLS, on_bad_lines="skip", engine="python")
    ev = ev[ev.run_index.isin(runs)]
    return ev.fillna("NA").astype(str)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir_a")
    ap.add_argument("dir_b")
    ap.add_argument("--runs", default=None, help="range of global run indices, e.g. 0-11")
    args = ap.parse_args()

    common = finished_runs(args.dir_a) & finished_runs(args.dir_b)
    if args.runs:
        lo, hi = (int(x) for x in args.runs.split("-"))
        common = {r for r in common if lo <= r <= hi}
    common = sorted(common)
    print(f"runs finished in both: {len(common)} {common[:3]}...{common[-3:] if common else ''}")
    if not common:
        return

    a, b = load_events(args.dir_a, common), load_events(args.dir_b, common)
    identical, different = [], []
    for run in common:
        ra = a[a.run_index == str(run)].reset_index(drop=True)
        rb = b[b.run_index == str(run)].reset_index(drop=True)
        if len(ra) != len(rb):
            different.append((run, f"row counts {len(ra)} vs {len(rb)}"))
            continue
        same = (ra == rb).all(axis=1)
        if same.all():
            identical.append(run)
        else:
            i = int(same.idxmin())
            different.append((run, f"first divergence at row {i}, t={ra.simulation_elapsed_seconds.iloc[i]} "
                                   f"A={ra.iloc[i].to_dict()} B={rb.iloc[i].to_dict()}"))
    print(f"identical runs: {len(identical)} / {len(common)}")
    for run, why in different[:10]:
        print(f"DIFFERENT run {run}: {why}")


if __name__ == "__main__":
    main()
