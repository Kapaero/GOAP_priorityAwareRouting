"""Validate the generated NHAMCS-based cohorts against the NHAMCS ED pool they were drawn from.

Checks (cohorts pooled over all seeds vs. survey-weighted pool):
  * share of critical visits (IMMEDR 1-2);
  * distribution over the five triage levels (IMMEDR 1..5);
  * hourly arrival profile (24 bins) - maximum absolute difference, Pearson r, chi-square goodness of fit;
  * ambulance share among critical and among other visits.
Also reports the spread of the critical share across single 300-patient cohorts against the binomial expectation.

Usage:
  python validate_nhamcs_cohorts.py --nhamcs-dir <nhamcs dir> --seeds-dir <dir with nhamcs_seed_XX.csv>
        [--window-seconds 1200] [--out-csv validation.csv]
"""
import argparse
import math
import sys
from pathlib import Path

import numpy as np
import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_nhamcs_seeds import load_pool  # noqa: E402


def read_cohorts(seeds_dir):
    frames = []
    for path in sorted(seeds_dir.glob("nhamcs_seed_*.csv")):
        df = pd.read_csv(path, comment="#")
        df["seed"] = int(path.stem.split("_")[-1])
        frames.append(df)
    return pd.concat(frames, ignore_index=True)


def chi2_sf(x, k):
    """Survival function of chi-square(k) via the Wilson-Hilferty normal approximation (k >= 10)."""
    z = ((x / k) ** (1.0 / 3.0) - (1.0 - 2.0 / (9.0 * k))) / math.sqrt(2.0 / (9.0 * k))
    return 0.5 * math.erfc(z / math.sqrt(2.0))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--nhamcs-dir", required=True, type=Path)
    ap.add_argument("--seeds-dir", required=True, type=Path)
    ap.add_argument("--window-seconds", type=float, default=1200.0)
    ap.add_argument("--out-csv", type=Path, default=None)
    args = ap.parse_args()

    _, pool = load_pool(args.nhamcs_dir)
    w = pool.PATWT.to_numpy(float)
    wn = w / w.sum()
    coh = read_cohorts(args.seeds_dir)
    n = len(coh)
    rows = []

    # priority class
    pool_crit = float(wn[pool.IMMEDR.isin([1, 2]).to_numpy()].sum())
    coh_crit = float(coh.is_critical.astype(bool).mean())
    per_seed = coh.groupby("seed").is_critical.apply(lambda s: s.astype(bool).mean())
    n_per = int(coh.groupby("seed").size().iloc[0])
    binom_sd = math.sqrt(pool_crit * (1 - pool_crit) / n_per)
    rows.append(("critical share (IMMEDR 1-2)", round(pool_crit, 4), round(coh_crit, 4),
                 f"per-cohort sd {per_seed.std():.4f} vs binomial {binom_sd:.4f}"))

    # triage level distribution
    coh["immedr"] = coh.critical_reason.str.extract(r"(\d)$").astype(int)
    for lvl in range(1, 6):
        pw = float(wn[(pool.IMMEDR == lvl).to_numpy()].sum())
        pc = float((coh.immedr == lvl).mean())
        rows.append((f"IMMEDR {lvl} share", round(pw, 4), round(pc, 4), ""))

    # hourly arrival profile
    pool_hour = np.array([float(wn[(pool.minute_of_day // 60 == h).to_numpy()].sum()) for h in range(24)])
    coh_hour = np.floor(coh.time_seconds.to_numpy(float) / args.window_seconds * 24.0).clip(0, 23).astype(int)
    obs = np.bincount(coh_hour, minlength=24).astype(float)
    exp = pool_hour * n
    chi2 = float(((obs - exp) ** 2 / exp).sum())
    r = float(np.corrcoef(obs / n, pool_hour)[0, 1])
    max_diff = float(np.abs(obs / n - pool_hour).max())
    rows.append(("hourly arrival profile", "peak x%.2f @%02d:00" % (pool_hour.max() / pool_hour.mean(), pool_hour.argmax()),
                 "peak x%.2f @%02d:00" % ((obs / n).max() / (obs / n).mean(), (obs / n).argmax()),
                 f"max |diff| {100 * max_diff:.2f} pp, r={r:.4f}, chi2={chi2:.1f} (23 df) p~{chi2_sf(chi2, 23):.2f}"))

    # ambulance share by class
    for label, mask_pool, mask_coh in [
        ("ambulance share among critical", pool.IMMEDR.isin([1, 2]).to_numpy(), coh.is_critical.astype(bool).to_numpy()),
        ("ambulance share among other", (~pool.IMMEDR.isin([1, 2])).to_numpy(), (~coh.is_critical.astype(bool)).to_numpy()),
    ]:
        known_p = mask_pool & pool.ARREMS.isin([1, 2]).to_numpy()
        pool_amb = float(wn[known_p & (pool.ARREMS == 1).to_numpy()].sum() / wn[known_p].sum())
        known_c = mask_coh & coh.admission_type.isin(["AMBULANCE", "WALKIN"]).to_numpy()
        coh_amb = float((coh.admission_type[known_c] == "AMBULANCE").mean())
        rows.append((label, round(pool_amb, 4), round(coh_amb, 4), f"{int(known_c.sum())} cohort patients with known mode"))

    out = pd.DataFrame(rows, columns=["quantity", "NHAMCS (weighted pool)", f"cohorts (n={n})", "note"])
    print(out.to_string(index=False))
    if args.out_csv:
        out.to_csv(args.out_csv, index=False)
        print("written", args.out_csv)


if __name__ == "__main__":
    main()
