"""Generate independent triage-arrival cohorts from the real NHAMCS ED public-use files (CDC/NCHS, 2016-2019)
in the schedule CSV format read by Assets/GOAP/Spawn.cs (same 10 columns as the earlier MIMIC-based seeds).

Method (no parametric arrival model):
  * pool  = all ED visits with a valid arrival clock time (ARRTIME) and a triage level (IMMEDR 1-5);
  * cohort = N visits drawn WITH replacement with probability proportional to the survey weight PATWT, so every
    cohort is a nationally representative sample of visits (a weighted bootstrap);
  * priority   : critical = IMMEDR 1 (immediate) or 2 (emergent); everything else normal (weighted share ~14.6%);
  * arrival time: each visit keeps its REAL time of day; the 24 h day is mapped onto a replay window
    (--window-seconds; 1200 s = compressed surge, 86400 s = real-time replay) with a uniform jitter inside the
    clock minute. The empirical hourly profile (peak 1.46x at 11:00) therefore comes straight from the data;
  * service/contact time: uniform on the protocol window 120-300 s (2-5 min), as in the earlier experiments;
  * the experiment sweeps the arrival rate by stretching the time axis in the Unity runner (time multiplier m),
    so the schedules are written once at the base window.

Usage:
  python generate_nhamcs_seeds.py --nhamcs-dir <dir with edYYYY.zip + eddictYYYY.dct> --out-dir <seeds dir>
        [--n-seeds 16] [--n-patients 300] [--window-seconds 1200] [--base-seed 20261005]
Get the files from https://www.cdc.gov/nchs/nhamcs/ (NHAMCS public-use data files, ED 2016-2019 + Stata dictionaries).
"""
import argparse
import io
import json
import re
import zipfile
from pathlib import Path

import numpy as np
import pandas as pd

VARS = ["ARRTIME", "IMMEDR", "ARREMS", "PATWT"]
YEARS = (2016, 2017, 2018, 2019)


def column_positions(dct_path):
    pos = {}
    for line in dct_path.read_text(encoding="latin-1").splitlines():
        m = re.match(r"\s*\w+\s+(\w+)\s+(\d+)(?:-(\d+))?", line)
        if m and m.group(1) in VARS:
            first = int(m.group(2))
            last = int(m.group(3) or first)
            pos[m.group(1)] = (first - 1, last)
    return pos


def load_pool(nhamcs_dir):
    frames = []
    for year in YEARS:
        zip_path = next(p for p in nhamcs_dir.iterdir() if p.suffix.lower() == ".zip" and str(year) in p.name)
        pos = column_positions(nhamcs_dir / f"eddict{year}.dct")
        with zipfile.ZipFile(zip_path) as z:
            raw = z.read(z.namelist()[0]).decode("latin-1")
        df = pd.read_fwf(io.StringIO(raw), colspecs=[pos[v] for v in VARS], names=VARS, dtype=str)
        for col in VARS:
            df[col] = pd.to_numeric(df[col], errors="coerce")
        df["year"] = year
        df["row"] = np.arange(len(df))
        frames.append(df)
    d = pd.concat(frames, ignore_index=True)
    hh = d.ARRTIME // 100
    mm = d.ARRTIME % 100
    valid_time = d.ARRTIME.between(0, 2359) & (mm < 60) & (hh < 24)
    triaged = d.IMMEDR.isin([1, 2, 3, 4, 5])
    pool = d[valid_time & triaged & (d.PATWT > 0)].copy()
    pool["minute_of_day"] = (pool.ARRTIME // 100) * 60 + (pool.ARRTIME % 100)
    return d, pool.reset_index(drop=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--nhamcs-dir", required=True, type=Path)
    ap.add_argument("--out-dir", required=True, type=Path)
    ap.add_argument("--n-seeds", type=int, default=16)
    ap.add_argument("--n-patients", type=int, default=300)
    ap.add_argument("--window-seconds", type=float, default=1200.0)
    ap.add_argument("--base-seed", type=int, default=20261005)
    ap.add_argument("--service-min", type=float, default=120.0)
    ap.add_argument("--service-max", type=float, default=300.0)
    args = ap.parse_args()

    everything, pool = load_pool(args.nhamcs_dir)
    w = pool.PATWT.to_numpy(float)
    p = w / w.sum()
    crit_pool = pool.IMMEDR.isin([1, 2]).to_numpy()
    print(f"NHAMCS records {len(everything)}; usable pool {len(pool)} "
          f"(valid ARRTIME and IMMEDR 1-5); weighted critical share {float((p * crit_pool).sum()):.4f}")

    args.out_dir.mkdir(parents=True, exist_ok=True)
    header = (
        "# source_dataset=NHAMCS ED public-use files 2016-2019 (CDC/NCHS), weighted bootstrap of visits (PATWT)\n"
        "# priority=critical if IMMEDR in {{1,2}} else normal; arrival=real clock time of the visit mapped onto the window\n"
        "# window_seconds={win} n_patients={n} service_time=uniform_{smin:.0f}_{smax:.0f}s\n"
    )
    manifest = []
    for seed in range(args.n_seeds):
        rng = np.random.default_rng(args.base_seed + seed)
        idx = rng.choice(len(pool), size=args.n_patients, replace=True, p=p)
        s = pool.iloc[idx].reset_index(drop=True)
        t = (s.minute_of_day.to_numpy(float) + rng.uniform(0.0, 1.0, len(s))) / 1440.0 * args.window_seconds
        order = np.argsort(t, kind="stable")
        s, t = s.iloc[order].reset_index(drop=True), t[order]
        service = rng.uniform(args.service_min, args.service_max, len(s))
        crit = s.IMMEDR.isin([1, 2]).to_numpy()
        amb = s.ARREMS.map({1: "AMBULANCE", 2: "WALKIN"}).fillna("UNKNOWN").to_numpy()

        name = f"nhamcs_seed_{seed:02d}"
        out = args.out_dir / f"{name}.csv"
        with open(out, "w", encoding="utf-8", newline="\n") as f:
            f.write(header.format(win=args.window_seconds, n=args.n_patients, smin=args.service_min, smax=args.service_max))
            f.write("time_seconds,is_critical,source_time,source_hadm_id,admission_type,critical_reason,diagnosis,"
                    "service_seconds,source_ed_minutes,service_source\n")
            for i in range(len(s)):
                f.write(
                    f"{t[i]:.3f},{'true' if crit[i] else 'false'},{int(s.ARRTIME[i]):04d},"
                    f"{int(s.year[i])}-{int(s.row[i]):05d},{amb[i]},nhamcs_immedr_{int(s.IMMEDR[i])},NHAMCS,"
                    f"{service[i]:.2f},,{name}\n"
                )
        manifest.append({
            "seed": seed, "file": out.name, "n": int(len(s)), "critical": int(crit.sum()),
            "critical_share": round(float(crit.mean()), 4),
            "ambulance_share": round(float((amb == "AMBULANCE").mean()), 4),
            "first_arrival_s": round(float(t[0]), 2), "last_arrival_s": round(float(t[-1]), 2),
        })
        print(f"seed {seed:02d}: critical {int(crit.sum()):3d} ({crit.mean():.3f}) "
              f"ambulance {float((amb == 'AMBULANCE').mean()):.3f} last arrival {t[-1]:.1f} s -> {out.name}")

    (args.out_dir / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    shares = np.array([m["critical_share"] for m in manifest])
    print(f"critical share over seeds: mean {shares.mean():.3f}, range {shares.min():.3f}-{shares.max():.3f}")


if __name__ == "__main__":
    main()
