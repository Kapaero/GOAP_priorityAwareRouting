# MIMIC-IV-ED Demo-Informed Synthetic Workload

This directory contains the supplementary workload package used by the GitHub Pages results summary.

The files are synthetic and should be described as **MIMIC-IV-ED demo-informed**, not as a full MIMIC-IV-ED cohort and not as clinical validation data. The package is intended to make the Unity transport-control experiment reproducible.

## Files

- `simulation_arrival_schedule.csv`  
  Canonical 300-patient simulation schedule for load multiplier `1.00`.

- `simulation_load_sweep_schedule.csv`  
  The same patients expanded across load multipliers `1.00`, `1.25`, `1.50`, and `2.00`.

- `unity_runtime_schedule.csv`  
  Runtime-compatible schedule with the `time_seconds,is_critical,...,service_seconds` column layout currently parsed by `Spawn.cs`.

- `mimic_iv_ed_demo_like_edstays.csv`  
  Synthetic demo-like ED stay metadata with MIMIC-IV-ED-style identifiers and time fields.

- `mimic_iv_ed_demo_like_triage.csv`  
  Synthetic demo-like triage metadata with acuity, complaint, and vital-sign columns.

- `manifest.json`  
  Dataset summary, generation seed, patient counts, and service-time bounds.

## Unity Fields

The Unity simulation conceptually uses the following fields:

- `arrival_seconds`
- `is_critical`
- `triage_contact_seconds`

For the current `Spawn.cs` parser, use `unity_runtime_schedule.csv`.

The MIMIC-like `edstays` and `triage` files are included for supplementary reporting and dataset-shape transparency.
