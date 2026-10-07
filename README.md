# Environment-Mediated GOAP for Priority-Aware Triage Routing

Simulation code, patient cohorts, campaign data, and analysis scripts for the paper
**"Environment-Mediated Dynamic Route Planning for Priority-Aware Triage Routing"**
(Bondarenko, G.O., Syryh, A.S., Popova, A.R.), submitted to the *Journal of Information
Processing* (IPSJ). The paper itself is not included here while the submission is under
review, pending confirmation of IPSJ's self-archiving/copyright terms.

## What this is

A deterministic Unity NavMesh simulation of a hospital triage/initial-assessment wing,
comparing a proposed **environment-mediated GOAP (Goal-Oriented Action Planning)** routing
controller against three deterministic baselines (a finite-state reactive controller, a
priority-queue dispatcher, and a decision-table controller). The workload consists of
cohorts of real emergency-department visits resampled from the **NHAMCS 2016-2019** public-use
files, run at seven offered loads.

The core idea: instead of hard-coding priority handling into per-agent rules, an
*environment mediator* changes which routes and resources are feasible for an agent,
conditional on that agent's priority and the current state of the wing. A critical agent and
a normal agent can see a *different* feasibility for the same corridor at the same moment.
The GOAP agent uses this priority-conditioned feasibility both when it builds a plan and when
it executes each action, and replans whenever its plan is invalidated.

### How the baselines differ

All four controllers run in the same mediated environment and are subject to the same closures
of the wing entrance and corridors: the mediator switches the wing between admission and
release phases for every controller alike.

The baselines differ from the proposed controller in two ways:

1. **No priority-conditioned access.** A route closed by the mediator is closed for every
   patient the baseline dispatches, critical or not.
2. **No reaction to changes during an action.** A baseline patient cannot react to a change of
   the environment while an action is in progress. It keeps its reserved cubicle and completes
   the current action first; only then does it observe the new state of the route, and if its
   next target is closed it waits in place until the target opens. There is no plan
   invalidation, no reservation rollback, and no fallback room.

The proposed controller, in contrast, compares the world-state version on every update. When
the version changes and its next action is no longer feasible for that agent, it interrupts the
action, releases its temporary reservations, clears the outdated plan, and replans. A critical
agent can therefore plan and walk through a route that is closed for normal-priority patients
instead of waiting for the closure to end.

## Main result (16 paired cohorts per load, 448 runs)

Mean time-to-assessment of critical patients, minutes (`data/` -> `analysis/`):

| offered load u | FSM | Priority | DT | Proposed |
|---|---|---|---|---|
| 0.5 | 0.55 | 0.56 | 0.55 | **0.45** |
| 1.0 | 0.90 | 0.93 | 0.90 | **0.48** |
| 1.5 | 0.96 | 0.95 | 0.95 | **0.49** |
| 2.5 | 1.04 | 1.02 | 1.10 | **0.50** |

The proposed controller is faster than every baseline at every load in all 16 cohorts
(exact paired Wilcoxon, Holm-adjusted p = 0.0013). Normal-priority patients are not
measurably delayed (at most 0.12 min up to capacity; 0.2-0.7 min faster under overload).

## Repository layout

```
unity/Assets/GOAP/     Unity C# source of the simulation (agents, GOAP planner, environment
                       mediator, baseline controllers, experiment runner, headless builder),
                       the patient prefab, the hospital-wing model, and the 16 NHAMCS cohorts
                       (Resources/NhamcsArrivals/seeds/nhamcs_seed_00..15.csv)
unity/Assets/Scenes/   HeadlessTriageNhamcs.unity, the scene of the campaign build
data/                  Analysis-ready data of the campaign reported in the paper
analysis/              Cohort generation and validation, campaign launch and analysis scripts
```

## Reproducing the tables, tests, and figures from the bundled data

```bash
cd analysis
pip install -r requirements.txt
python analyze_nhamcs_campaign.py --data-dir ../data --out-dir output
```

This writes `by_load_controller.csv` (Tables 1-2: mean, 95% CI, P95 per load and
controller), `paired_tests.csv` (all 42 exact paired Wilcoxon tests with Holm adjustment),
`crossover.csv`, and the two figures of the paper. The exact Wilcoxon test is implemented in
the script itself (no SciPy).

### Data files

- **`data/patient_records.csv`** (134,400 rows): one row per patient per run: `run_index`,
  `seed`, `architecture`, `load_multiplier`, `u` (offered load), `agent`, `is_critical`,
  `acuity` (NHAMCS immediacy level 1-5), `arrival_mode`, `service_seconds`, the event times
  `spawn`, `waiting_room_entry`, `wing_entry`, `treatment_complete`, `wing_exit`, `home`
  (simulated seconds), and `time_to_assessment_seconds` =
  `treatment_complete - service_seconds - waiting_room_entry`.
- **`data/runs_summary.csv`** (448 rows): one row per run, as written by the simulation.
- **`data/cohort_validation.csv`**: the 16 cohorts compared with the survey-weighted NHAMCS
  pool (critical share, acuity mix, hourly arrival profile, arrival mode).

## Patient cohorts (NHAMCS)

`analysis/generate_nhamcs_seeds.py` draws 16 survey-weighted bootstrap cohorts of 300 visits
from the 52,833 NHAMCS 2016-2019 ED visits that report both an arrival time and a triage
immediacy level. Critical = immediacy 1-2 (14.6%). Each patient keeps the arrival time of day,
the acuity level and the arrival mode of its source visit; the contact (service) time is drawn
from 120-300 s. Arrival times are compressed onto a 1,200 s base window, which the runner
stretches by the load multiplier m (offered load u = 2.5 / m, with a measured wing capacity of
about 6 patients/min).

```bash
python generate_nhamcs_seeds.py --nhamcs-dir <folder with the NHAMCS ED zip files and .dct dictionaries> \
       --out-dir ../unity/Assets/GOAP/Resources/NhamcsArrivals/seeds
python validate_nhamcs_cohorts.py --nhamcs-dir <same folder> \
       --seeds-dir ../unity/Assets/GOAP/Resources/NhamcsArrivals/seeds --out-csv ../data/cohort_validation.csv
```

The generated cohorts used in the paper are already included.

## Running the simulation

`unity/Assets/` is taken from a Unity 6000.3.2f1 project; place it in a Unity project's
`Assets/` folder (project settings and `.meta` files are not included). Build the headless
campaign player with the menu item `GOAP/Build Headless Pipeline Player (NHAMCS 16-Seed
Campaign)` (`Editor/HeadlessPlayerBuilder.cs`), then run the 16 shards:

```powershell
powershell -File analysis/launch_nhamcs_campaign.ps1 -BuildDir HeadlessTriageNhamcsMain
```

Each shard runs one cohort at 7 loads x 4 controllers (28 runs). The player must be started
with `-job-worker-count 0`; together with the fixed simulation step of 1/60 s this makes runs
bit-for-bit reproducible (`analysis/compare_runs.py` compares two event logs). Other player
options: `-seedRange A B` (cohorts A..B-1), `-runIndex G` (a single run) and
`-goapDiagnostics` (verbose GOAP log). Output goes to
`<persistentDataPath>/GOAP_Diagnostics/Experiments/`; `analysis/export_campaign_data.py`
turns the shard directories into the files in `data/`.

Key source files:

- `TargetAvailabilityManager.cs` - the environment mediator; `IsAvailableForAgent` grants a
  critical agent access to a route that is closed for normal agents.
- `HospitalFlowController.cs` - switches the wing between admission and release phases.
- `GAgent.cs`, `GPlanner.cs`, `GAction.cs` - the GOAP agent, planner and action base class
  (priority-conditioned feasibility in `GAction.IsAchievable`).
- `TriageBaselineAgent.cs`, `TriagePriorityQueueDispatcher.cs` - the three baselines.
- `TriageExperimentRunner.cs`, `TriageExperimentMetrics.cs` - campaign orchestration and logs.

The tree also contains Q-learning/DQN dispatchers from an earlier, separate line of work; they
are not part of this paper and can be ignored.

## Data provenance

National Center for Health Statistics, National Hospital Ambulatory Medical Care Survey,
Emergency Department public-use data files 2016-2019:
<https://www.cdc.gov/nchs/ahcd/datasets_documentation_related.htm>. The files are public-use,
de-identified survey data; no patient identifiers are contained in this repository.

## Citation

## License

Code and analysis scripts are released under the MIT License (see [`LICENSE`](LICENSE)). The
hospital-wing 3D model (`unity/Assets/GOAP/Больница.fbx`) was custom-built for this project.

## Contact

Gleb O. Bondarenko - gobondarenko@itmo.ru
