"""Where does the time-to-assessment of each controller go?  TTA = treatment_complete - service - waiting_room_entry is
split at the moment the patient enters the wing:
    approach = wing_entry - waiting_room_entry          (queue wait + walk from the waiting room to the wing)
    inside   = treatment_complete - service - wing_entry (walk through the corridors to the cubicle + dwell times)
Means over patients, per load, controller and class, averaged first within a seed (every seed is one cohort).

Usage: python tta_decomposition.py --dirs <shard dir> [...] [--loads 0.5 0.75 1.0]
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
    ap.add_argument("--loads", nargs="*", type=float, default=None)
    ap.add_argument("--out-csv", type=Path, default=None)
    args = ap.parse_args()

    cols = ["run_index", "architecture", "load_multiplier", "simulation_elapsed_seconds", "event", "agent",
            "is_critical", "detail"]
    frames = []
    for d in args.dirs:
        ev = read_csv_robust(Path(d) / "events.csv", usecols=cols)
        ev = ev[ev.event.isin(["spawn", "waiting_room_entry", "wing_entry", "treatment_complete"])].copy()
        ev["agent"] = ev.agent.astype(str)
        ev["is_critical"] = ev.is_critical.astype(str).str.strip().str.lower().eq("true")
        frames.append(ev)
    ev = pd.concat(frames, ignore_index=True)
    ev["u"] = offered_load(ev.load_multiplier).round(2)
    if args.loads:
        ev = ev[ev.u.isin(args.loads)]

    key = ["run_index", "agent"]
    times = ev.groupby(key + ["event"]).simulation_elapsed_seconds.min().unstack("event")
    meta = ev.groupby(key).agg(architecture=("architecture", "first"), u=("u", "first"),
                               is_critical=("is_critical", "max"))
    spawn = ev[ev.event == "spawn"].drop_duplicates(key).set_index(key).detail
    service = spawn.str.extract(r"service_seconds=([\d.]+)")[0].astype(float).rename("service")
    seed = spawn.str.extract(r"service_source=\S*?_(\d+)\s*$")[0].astype(float).rename("seed")
    pat = meta.join(times).join(service).join(seed).reset_index()
    pat = pat.dropna(subset=["waiting_room_entry", "wing_entry", "treatment_complete", "service", "seed"])
    pat["approach"] = pat.wing_entry - pat.waiting_room_entry
    pat["inside"] = pat.treatment_complete - pat.service - pat.wing_entry
    pat["tta"] = pat.approach + pat.inside

    rows = []
    for (u, arch, crit), g in pat.groupby(["u", "architecture", "is_critical"]):
        per_seed = g.groupby("seed")[["approach", "inside", "tta"]].mean()
        rows.append({"u": u, "controller": ARCH_LABEL[arch], "class": "critical" if crit else "normal",
                     "approach_s": per_seed.approach.mean(), "inside_s": per_seed.inside.mean(),
                     "tta_s": per_seed.tta.mean(), "seeds": len(per_seed)})
    out = pd.DataFrame(rows)
    order = {ARCH_LABEL[a]: i for i, a in enumerate(ARCH_ORDER)}
    out = out.sort_values(["class", "u", "controller"], key=lambda s: s.map(order) if s.name == "controller" else s)
    print(out.round(1).to_string(index=False))
    if args.out_csv:
        out.to_csv(args.out_csv, index=False)
        print("written", args.out_csv)


if __name__ == "__main__":
    main()
