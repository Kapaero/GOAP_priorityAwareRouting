import bisect
import csv
import datetime as dt
import os
import random
from collections import Counter


SOURCE = r"C:\Users\Глеб\Desktop\MIMIC -III (10000 patients)\ADMISSIONS\ADMISSIONS_sorted.csv"
OUT_DIR = r"C:\UnityProgects\GOAP_5Attempt\Assets\GOAP\Resources\MimicArrivals"
OUT_FILE = "mimic_heavy_day_2171_10_15_scaled.csv"

SEED = 20260603
TARGET_ARRIVALS = 300
EXPERIMENT_DURATION_SECONDS = 1200.0
SERVICE_MIN_SECONDS = 120.0
SERVICE_MAX_SECONDS = 300.0

CRITICAL_TERMS = (
    "CARDIAC ARREST",
    "ARREST",
    "SEPSIS",
    "SEPTIC",
    "SHOCK",
    "RESPIRATORY FAILURE",
    "HYPOXIA",
)


def parse_time(value):
    if not value:
        return None
    return dt.datetime.strptime(value, "%Y-%m-%d %H:%M:%S")


def time_of_day_seconds(value):
    return value.hour * 3600 + value.minute * 60 + value.second


def ed_los_minutes(row):
    start = parse_time(row.get("EDREGTIME"))
    end = parse_time(row.get("EDOUTTIME"))
    if start is None or end is None:
        return None

    minutes = (end - start).total_seconds() / 60.0
    if minutes <= 0:
        return None

    return minutes


def clean(value):
    return (
        str(value)
        .replace(",", ";")
        .replace("\r", " ")
        .replace("\n", " ")
        .replace('"', "'")
        .strip()
    )


def critical_reasons(row):
    diagnosis = (row.get("DIAGNOSIS") or "").upper()
    reasons = []

    if row.get("ADMISSION_TYPE") == "URGENT":
        reasons.append("urgent")
    if row.get("HOSPITAL_EXPIRE_FLAG") == "1":
        reasons.append("expired")

    for term in CRITICAL_TERMS:
        if term in diagnosis:
            reasons.append(term.lower().replace(" ", "_"))
            break

    return reasons


def load_rows():
    eligible = []
    arrivals_by_day = Counter()
    ed_arrivals_by_day = Counter()

    with open(SOURCE, newline="", encoding="utf-8-sig") as handle:
        for row in csv.DictReader(handle):
            arrival_time = parse_time(row.get("EDREGTIME")) or parse_time(row.get("ADMITTIME"))
            if arrival_time is not None:
                arrivals_by_day[arrival_time.date()] += 1
                if row.get("EDREGTIME"):
                    ed_arrivals_by_day[arrival_time.date()] += 1

            if row.get("ADMISSION_TYPE") not in ("EMERGENCY", "URGENT") or arrival_time is None:
                continue

            reasons = critical_reasons(row)
            eligible.append(
                {
                    "dt": arrival_time,
                    "critical": bool(reasons),
                    "reason": "+".join(reasons),
                    "hadm": row.get("HADM_ID", ""),
                    "atype": row.get("ADMISSION_TYPE", ""),
                    "diag": row.get("DIAGNOSIS", ""),
                    "ed_minutes": ed_los_minutes(row),
                }
            )

    return eligible, arrivals_by_day, ed_arrivals_by_day


def build_service_time_resolver(rows, rng):
    valid_minutes = sorted(row["ed_minutes"] for row in rows if row["ed_minutes"] is not None)
    if not valid_minutes:
        midpoint = (SERVICE_MIN_SECONDS + SERVICE_MAX_SECONDS) * 0.5
        return lambda row: (midpoint, "", "protocol_default")

    def service_from_minutes(minutes):
        if len(valid_minutes) == 1:
            rank = 0.5
        else:
            rank = bisect.bisect_left(valid_minutes, minutes) / float(len(valid_minutes) - 1)

        service_seconds = SERVICE_MIN_SECONDS + rank * (SERVICE_MAX_SECONDS - SERVICE_MIN_SECONDS)
        return max(SERVICE_MIN_SECONDS, min(SERVICE_MAX_SECONDS, service_seconds))

    fallback_pools = {False: [], True: []}
    for row in rows:
        minutes = row["ed_minutes"]
        if minutes is None:
            continue

        fallback_pools[row["critical"]].append(service_from_minutes(minutes))

    all_fallback = fallback_pools[False] + fallback_pools[True]

    def resolve(row):
        minutes = row["ed_minutes"]
        if minutes is not None:
            return service_from_minutes(minutes), f"{minutes:.1f}", "ed_los_percentile_2_to_5_min"

        pool = fallback_pools[row["critical"]] or all_fallback
        if pool:
            return rng.choice(pool), "", "ed_los_missing_sampled_2_to_5_min"

        midpoint = (SERVICE_MIN_SECONDS + SERVICE_MAX_SECONDS) * 0.5
        return midpoint, "", "protocol_default"

    return resolve


def generate_schedule(rows):
    rng = random.Random(SEED)
    resolve_service_time = build_service_time_resolver(rows, rng)

    if TARGET_ARRIVALS <= len(rows):
        sampled = rng.sample(rows, TARGET_ARRIVALS)
    else:
        sampled = [rng.choice(rows) for _ in range(TARGET_ARRIVALS)]

    sampled.sort(key=lambda item: (time_of_day_seconds(item["dt"]), rng.random()))

    generated = []
    last_second = -1.0
    for row in sampled:
        service_seconds, source_ed_minutes, service_source = resolve_service_time(row)
        generated_row = dict(row)
        generated_row["service_seconds"] = service_seconds
        generated_row["source_ed_minutes"] = source_ed_minutes
        generated_row["service_source"] = service_source

        scaled_second = (time_of_day_seconds(row["dt"]) / 86400.0) * EXPERIMENT_DURATION_SECONDS
        if scaled_second <= last_second:
            scaled_second = last_second + 0.05
        if scaled_second > EXPERIMENT_DURATION_SECONDS - 0.05:
            scaled_second = EXPERIMENT_DURATION_SECONDS - 0.05

        last_second = scaled_second
        generated.append((scaled_second, generated_row))

    return generated


def write_schedule(generated, arrivals_by_day, ed_arrivals_by_day):
    os.makedirs(OUT_DIR, exist_ok=True)
    out_path = os.path.join(OUT_DIR, OUT_FILE)

    busiest_day, busiest_count = arrivals_by_day.most_common(1)[0]
    busiest_ed_day, busiest_ed_count = ed_arrivals_by_day.most_common(1)[0]

    with open(out_path, "w", encoding="utf-8", newline="") as handle:
        handle.write("# source_dataset=MIMIC-III 10000 patients\n")
        handle.write("# source_table=ADMISSIONS_sorted.csv\n")
        handle.write(f"# calendar_busiest_day={busiest_day} original_arrivals={busiest_count}\n")
        handle.write(f"# calendar_busiest_ed_day={busiest_ed_day} original_ed_arrivals={busiest_ed_count}\n")
        handle.write(
            "# method=emergency_urgent_empirical_time_of_day_scaled "
            f"target_arrivals={TARGET_ARRIVALS} "
            f"duration_seconds={EXPERIMENT_DURATION_SECONDS:.0f} "
            f"seed={SEED}\n"
        )
        handle.write(
            "# service_time=ACEM_triage_window_2_to_5_minutes; "
            "derived_from_MIMIC_EDREGTIME_to_EDOUTTIME_percentile; "
            f"min_seconds={SERVICE_MIN_SECONDS:.0f} "
            f"max_seconds={SERVICE_MAX_SECONDS:.0f}\n"
        )
        handle.write(
            "# critical_rule=URGENT or HOSPITAL_EXPIRE_FLAG=1 or diagnosis contains "
            "arrest/sepsis/shock/respiratory failure/hypoxia\n"
        )
        handle.write(
            "time_seconds,is_critical,source_time,source_hadm_id,"
            "admission_type,critical_reason,diagnosis,service_seconds,"
            "source_ed_minutes,service_source\n"
        )

        for second, row in generated:
            handle.write(
                f"{second:.2f},"
                f"{str(row['critical']).lower()},"
                f"{row['dt'].strftime('%Y-%m-%d %H:%M:%S')},"
                f"{clean(row['hadm'])},"
                f"{clean(row['atype'])},"
                f"{clean(row['reason'])},"
                f"{clean(row['diag'])},"
                f"{row['service_seconds']:.2f},"
                f"{clean(row['source_ed_minutes'])},"
                f"{clean(row['service_source'])}\n"
            )

    return out_path, busiest_day, busiest_count, busiest_ed_day, busiest_ed_count


def main():
    rows, arrivals_by_day, ed_arrivals_by_day = load_rows()
    if not rows:
        raise RuntimeError("No emergency/urgent admissions found.")

    generated = generate_schedule(rows)
    out_path, busiest_day, busiest_count, busiest_ed_day, busiest_ed_count = write_schedule(
        generated,
        arrivals_by_day,
        ed_arrivals_by_day,
    )

    critical_count = sum(1 for _, row in generated if row["critical"])
    service_seconds = [row["service_seconds"] for _, row in generated]
    bins = [0] * 10
    for second, _ in generated:
        bins[min(9, int(second // 120))] += 1

    print(f"wrote={out_path}")
    print(f"eligible_rows={len(rows)}")
    print(f"target_arrivals={TARGET_ARRIVALS}")
    print(f"critical_arrivals={critical_count}")
    print(f"critical_rate={critical_count / TARGET_ARRIVALS:.3f}")
    print(f"calendar_busiest_day={busiest_day} original_arrivals={busiest_count}")
    print(f"calendar_busiest_ed_day={busiest_ed_day} original_ed_arrivals={busiest_ed_count}")
    print(f"first_arrival_second={generated[0][0]:.2f}")
    print(f"last_arrival_second={generated[-1][0]:.2f}")
    print(f"service_min_seconds={min(service_seconds):.2f}")
    print(f"service_avg_seconds={sum(service_seconds) / len(service_seconds):.2f}")
    print(f"service_max_seconds={max(service_seconds):.2f}")
    print(f"per_2min_bins={bins}")


if __name__ == "__main__":
    main()
