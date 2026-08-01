import csv
import json
import random
import re
from datetime import datetime, timedelta
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "analysis_outputs" / "triage_mimic_heavy_day_service_2_5min_load_sweep_until_clear_20260604_140639" / "patient_protocol_records.csv"
OUT_DIR = ROOT / "docs" / "dataset"

ARCHITECTURE = "ProposedEnvironmentMediatedReplanner"
LOAD_MULTIPLIER = "1.0"
SEED = 20260603
LOAD_SWEEP = [1.0, 1.25, 1.5, 2.0]
BASE_DATETIME = datetime(2180, 1, 1, 8, 0, 0)

CRITICAL_COMPLAINTS = [
    "Chest pain",
    "Shortness of breath",
    "Altered mental status",
    "Sepsis evaluation",
    "Hypotension",
    "Respiratory distress",
]

NORMAL_COMPLAINTS = [
    "Abdominal pain",
    "Fever",
    "Headache",
    "Minor trauma",
    "Nausea",
    "Back pain",
    "Wound check",
    "Dizziness",
]

RACES = [
    "WHITE",
    "BLACK/AFRICAN AMERICAN",
    "HISPANIC/LATINO",
    "ASIAN",
    "OTHER",
]

ARRIVAL_TRANSPORT = [
    "WALK IN",
    "AMBULANCE",
    "TRANSFER",
]


def parse_bool(value):
    return str(value).strip().lower() == "true"


def patient_index(agent_name, fallback):
    match = re.search(r"Spawned\s+(\d+)", agent_name or "")
    return int(match.group(1)) if match else fallback


def clean_float(value):
    return float(str(value).strip())


def fmt_dt(value):
    return value.strftime("%Y-%m-%d %H:%M:%S")


def read_canonical_rows():
    rows = []
    with SOURCE.open(newline="", encoding="utf-8-sig") as handle:
        for raw in csv.DictReader(handle):
            if raw["architecture"] != ARCHITECTURE or raw["load_multiplier"] != LOAD_MULTIPLIER:
                continue

            order = patient_index(raw["agent"], len(rows) + 1)
            rows.append(
                {
                    "patient_index": order,
                    "source_agent_name": raw["agent"],
                    "is_critical": parse_bool(raw["is_critical"]),
                    "arrival_seconds": clean_float(raw["spawn_time"]),
                    "triage_contact_seconds": clean_float(raw["service_seconds"]),
                }
            )

    rows.sort(key=lambda item: item["patient_index"])
    if len(rows) != 300:
        raise RuntimeError(f"Expected 300 canonical rows, found {len(rows)}")

    return rows


def synthetic_metadata(row, rng):
    critical = row["is_critical"]
    subject_id = 4_000_000 + row["patient_index"]
    stay_id = 30_000_000 + row["patient_index"]
    hadm_id = 20_000_000 + row["patient_index"]

    acuity = rng.choice([1, 2]) if critical else rng.choices([3, 4, 5], weights=[0.56, 0.34, 0.10], k=1)[0]
    chiefcomplaint = rng.choice(CRITICAL_COMPLAINTS if critical else NORMAL_COMPLAINTS)

    if critical:
        heartrate = rng.randint(102, 142)
        resprate = rng.randint(22, 34)
        o2sat = rng.randint(86, 96)
        sbp = rng.randint(82, 122)
        dbp = rng.randint(45, 78)
        pain = rng.choice(["7", "8", "9", "10", "unable"])
        transport = rng.choices(ARRIVAL_TRANSPORT, weights=[0.24, 0.68, 0.08], k=1)[0]
    else:
        heartrate = rng.randint(68, 108)
        resprate = rng.randint(14, 22)
        o2sat = rng.randint(95, 100)
        sbp = rng.randint(105, 150)
        dbp = rng.randint(62, 92)
        pain = str(rng.randint(0, 7))
        transport = rng.choices(ARRIVAL_TRANSPORT, weights=[0.78, 0.16, 0.06], k=1)[0]

    temperature = round(rng.uniform(36.1, 39.4 if critical else 38.8), 1)
    demo_los_minutes = rng.randint(45, 240) if critical else rng.randint(25, 180)

    return {
        "subject_id": subject_id,
        "hadm_id": hadm_id,
        "stay_id": stay_id,
        "gender": rng.choice(["M", "F"]),
        "race": rng.choice(RACES),
        "arrival_transport": transport,
        "disposition": "ADMITTED" if critical and rng.random() < 0.58 else rng.choice(["DISCHARGE", "ADMITTED", "OBSERVATION"]),
        "acuity": acuity,
        "chiefcomplaint": chiefcomplaint,
        "temperature": temperature,
        "heartrate": heartrate,
        "resprate": resprate,
        "o2sat": o2sat,
        "sbp": sbp,
        "dbp": dbp,
        "pain": pain,
        "demo_los_minutes": demo_los_minutes,
    }


def enrich_rows(rows):
    rng = random.Random(SEED)
    enriched = []
    for row in rows:
        meta = synthetic_metadata(row, rng)
        arrival_dt = BASE_DATETIME + timedelta(seconds=row["arrival_seconds"])
        enriched.append({**row, **meta, "intime": arrival_dt, "outtime": arrival_dt + timedelta(minutes=meta["demo_los_minutes"])})

    return enriched


def write_csv(path, fieldnames, rows):
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow({key: row.get(key, "") for key in fieldnames})


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    rows = enrich_rows(read_canonical_rows())

    schedule_rows = []
    for row in rows:
        schedule_rows.append(
            {
                "patient_id": f"P{row['patient_index']:03d}",
                "subject_id": row["subject_id"],
                "stay_id": row["stay_id"],
                "arrival_seconds": f"{row['arrival_seconds']:.3f}",
                "arrival_minutes": f"{row['arrival_seconds'] / 60.0:.3f}",
                "is_critical": str(row["is_critical"]).lower(),
                "priority_label": "critical" if row["is_critical"] else "normal",
                "acuity": row["acuity"],
                "triage_contact_seconds": f"{row['triage_contact_seconds']:.2f}",
                "triage_contact_minutes": f"{row['triage_contact_seconds'] / 60.0:.3f}",
                "source_label": "MIMIC-IV-ED-demo-informed-synthetic",
                "source_agent_name": row["source_agent_name"],
            }
        )

    write_csv(
        OUT_DIR / "simulation_arrival_schedule.csv",
        [
            "patient_id",
            "subject_id",
            "stay_id",
            "arrival_seconds",
            "arrival_minutes",
            "is_critical",
            "priority_label",
            "acuity",
            "triage_contact_seconds",
            "triage_contact_minutes",
            "source_label",
            "source_agent_name",
        ],
        schedule_rows,
    )

    sweep_rows = []
    for multiplier in LOAD_SWEEP:
        for row in rows:
            arrival_seconds = row["arrival_seconds"] * multiplier
            sweep_rows.append(
                {
                    "load_multiplier": f"{multiplier:.2f}",
                    "patient_id": f"P{row['patient_index']:03d}",
                    "arrival_seconds": f"{arrival_seconds:.3f}",
                    "arrival_minutes": f"{arrival_seconds / 60.0:.3f}",
                    "is_critical": str(row["is_critical"]).lower(),
                    "priority_label": "critical" if row["is_critical"] else "normal",
                    "acuity": row["acuity"],
                    "triage_contact_seconds": f"{row['triage_contact_seconds']:.2f}",
                }
            )

    write_csv(
        OUT_DIR / "simulation_load_sweep_schedule.csv",
        [
            "load_multiplier",
            "patient_id",
            "arrival_seconds",
            "arrival_minutes",
            "is_critical",
            "priority_label",
            "acuity",
            "triage_contact_seconds",
        ],
        sweep_rows,
    )

    runtime_rows = []
    for row in rows:
        runtime_rows.append(
            {
                "time_seconds": f"{row['arrival_seconds']:.3f}",
                "is_critical": str(row["is_critical"]).lower(),
                "source_time": fmt_dt(row["intime"]),
                "source_hadm_id": row["hadm_id"],
                "admission_type": "URGENT" if row["is_critical"] else "EMERGENCY",
                "critical_reason": "synthetic_priority" if row["is_critical"] else "",
                "diagnosis": row["chiefcomplaint"],
                "service_seconds": f"{row['triage_contact_seconds']:.2f}",
                "source_ed_minutes": row["demo_los_minutes"],
                "service_source": "mimic_iv_ed_demo_informed_protocol_2_to_5_min",
            }
        )

    write_csv(
        OUT_DIR / "unity_runtime_schedule.csv",
        [
            "time_seconds",
            "is_critical",
            "source_time",
            "source_hadm_id",
            "admission_type",
            "critical_reason",
            "diagnosis",
            "service_seconds",
            "source_ed_minutes",
            "service_source",
        ],
        runtime_rows,
    )

    write_csv(
        OUT_DIR / "mimic_iv_ed_demo_like_edstays.csv",
        [
            "subject_id",
            "hadm_id",
            "stay_id",
            "intime",
            "outtime",
            "gender",
            "race",
            "arrival_transport",
            "disposition",
            "synthetic_priority_label",
            "simulation_arrival_seconds",
            "simulation_triage_contact_seconds",
        ],
        [
            {
                **row,
                "intime": fmt_dt(row["intime"]),
                "outtime": fmt_dt(row["outtime"]),
                "synthetic_priority_label": "critical" if row["is_critical"] else "normal",
                "simulation_arrival_seconds": f"{row['arrival_seconds']:.3f}",
                "simulation_triage_contact_seconds": f"{row['triage_contact_seconds']:.2f}",
            }
            for row in rows
        ],
    )

    write_csv(
        OUT_DIR / "mimic_iv_ed_demo_like_triage.csv",
        [
            "subject_id",
            "stay_id",
            "temperature",
            "heartrate",
            "resprate",
            "o2sat",
            "sbp",
            "dbp",
            "pain",
            "acuity",
            "chiefcomplaint",
            "synthetic_priority_label",
        ],
        [
            {
                **row,
                "synthetic_priority_label": "critical" if row["is_critical"] else "normal",
            }
            for row in rows
        ],
    )

    critical_count = sum(1 for row in rows if row["is_critical"])
    services = [row["triage_contact_seconds"] for row in rows]
    arrivals = [row["arrival_seconds"] for row in rows]
    manifest = {
        "name": "MIMIC-IV-ED demo-informed synthetic triage transport workload",
        "version": "2026-06-06",
        "seed": SEED,
        "patient_count": len(rows),
        "critical_count": critical_count,
        "normal_count": len(rows) - critical_count,
        "critical_fraction": round(critical_count / len(rows), 4),
        "base_arrival_window_seconds": round(max(arrivals) - min(arrivals), 3),
        "load_multipliers": LOAD_SWEEP,
        "triage_contact_seconds_min": round(min(services), 2),
        "triage_contact_seconds_max": round(max(services), 2),
        "triage_contact_seconds_mean": round(sum(services) / len(services), 2),
        "source": "Derived from the experiment log slice ProposedEnvironmentMediatedReplanner/load_multiplier=1.0 and exported as a MIMIC-IV-ED demo-like synthetic supplementary dataset.",
        "clinical_validation": False,
        "synthetic": True,
        "unity_fields": ["arrival_seconds", "is_critical", "triage_contact_seconds"],
        "files": [
            "simulation_arrival_schedule.csv",
            "simulation_load_sweep_schedule.csv",
            "unity_runtime_schedule.csv",
            "mimic_iv_ed_demo_like_edstays.csv",
            "mimic_iv_ed_demo_like_triage.csv",
        ],
    }

    with (OUT_DIR / "manifest.json").open("w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)
        handle.write("\n")

    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
