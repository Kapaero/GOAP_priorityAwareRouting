"""Export the analysis-ready data of a finished campaign (shard directories written by the headless player) into two
CSV files: one row per patient per run, and one row per run.

Usage: python export_campaign_data.py --dirs <shard dirs> --out-dir <dir>
"""
import argparse
import sys
from pathlib import Path

import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_nhamcs_campaign import load_runs, read_csv_robust, offered_load  # noqa: E402

EVENTS = ["spawn", "waiting_room_entry", "wing_entry", "treatment_complete", "wing_exit", "home"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dirs", nargs="+", required=True)
    ap.add_argument("--out-dir", required=True, type=Path)
    args = ap.parse_args()
    args.out_dir.mkdir(parents=True, exist_ok=True)

    runs = load_runs(args.dirs)
    runs.to_csv(args.out_dir / "runs_summary.csv", index=False)

    cols = ["run_index", "architecture", "load_multiplier", "simulation_elapsed_seconds", "event", "agent",
            "is_critical", "detail"]
    frames = []
    for d in args.dirs:
        ev = read_csv_robust(Path(d) / "events.csv", usecols=cols)
        frames.append(ev[ev.event.isin(EVENTS)])
    ev = pd.concat(frames, ignore_index=True)
    ev["agent"] = ev.agent.astype(str)
    ev["is_critical"] = ev.is_critical.astype(str).str.strip().str.lower().eq("true")
    key = ["run_index", "agent"]
    times = ev.groupby(key + ["event"]).simulation_elapsed_seconds.min().unstack("event")
    meta = ev.groupby(key).agg(architecture=("architecture", "first"), load_multiplier=("load_multiplier", "first"),
                               is_critical=("is_critical", "max"))
    spawn = ev[ev.event == "spawn"].drop_duplicates(key).set_index(key).detail
    extra = pd.DataFrame({
        "seed": spawn.str.extract(r"service_source=\S*?_(\d+)\s*$")[0].astype(float),
        "service_seconds": spawn.str.extract(r"service_seconds=([\d.]+)")[0].astype(float),
        "acuity": spawn.str.extract(r"critical_reason=nhamcs_immedr_(\d)")[0],
        "arrival_mode": spawn.str.extract(r"admission_type=(\S+)")[0],
    })
    pat = meta.join(times).join(extra).reset_index()
    pat["u"] = offered_load(pat.load_multiplier).round(2)
    pat["time_to_assessment_seconds"] = pat.treatment_complete - pat.service_seconds - pat.waiting_room_entry
    order = ["run_index", "seed", "architecture", "load_multiplier", "u", "agent", "is_critical", "acuity",
             "arrival_mode", "service_seconds"] + EVENTS + ["time_to_assessment_seconds"]
    pat = pat[order].sort_values(["run_index", "spawn"])
    pat.to_csv(args.out_dir / "patient_records.csv", index=False, float_format="%.3f")
    print(f"runs {len(runs)}, patients {len(pat)} -> {args.out_dir}")


if __name__ == "__main__":
    main()
