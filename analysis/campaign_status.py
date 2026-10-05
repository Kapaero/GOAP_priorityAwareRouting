"""Progress of the running NHAMCS campaign shards: finished runs per shard, current run and simulated time.

Usage: python campaign_status.py [label]   (label = experiment label of the build, default triage_nhamcs_campaign)
"""
import os
import sys
from pathlib import Path

E = Path("C:/Users/Глеб/AppData/LocalLow/DefaultCompany/GOAP_5Attempt/GOAP_Diagnostics/Experiments")
LABEL = sys.argv[1] if len(sys.argv) > 1 else "triage_nhamcs_campaign"
PER_SHARD = 28  # 7 loads x 4 controllers per seed
dirs = sorted(E.glob(f"{LABEL}_seeds*"))
total = 0
for d in dirs:
    rs = d / "runs_summary.csv"
    n = 0
    if rs.exists():
        with open(rs, encoding="utf-8", errors="ignore") as f:
            n = max(0, sum(1 for _ in f) - 1)
    total += n
    cur = ""
    ts = d / "time_series.csv"
    if ts.exists() and ts.stat().st_size > 0:
        with open(ts, "rb") as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            f.seek(max(0, size - 4096))
            lines = f.read().decode("utf-8", "ignore").strip().splitlines()
        parts = lines[-1].split(",") if lines else []
        if len(parts) > 5:
            cur = f"run {parts[0]} {parts[1][:6]} m={parts[2]} t={parts[3]}s"
    print(f"{d.name[len(LABEL) + 1:].split('_20')[0]:>10}  {n:2d}/{PER_SHARD}  {cur}")
print(f"TOTAL finished runs: {total} / {PER_SHARD * 16}  (shards found: {len(dirs)})")
