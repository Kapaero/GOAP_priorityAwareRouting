# Results Narrative Draft

## Main Finding

All four controllers completed all 300 scheduled patients in every workload condition. The proposed environment-mediated GOAP controller showed a load-dependent effect. Under the densest workload (arrival multiplier 1.00), it achieved the lowest total completion time and the lowest critical-patient time to assessment. Under less dense workloads, the proposed controller introduced additional switching and replanning overhead, which increased completion time relative to the simpler baselines.

## Critical Patients

The strongest result appears in the densest workload. At arrival multiplier 1.00, the proposed controller reduced mean critical time to assessment to 1.35 min, compared with 1.55 min for the best baseline. Critical P95 time to assessment was also reduced to 2.61 min, compared with 3.24 min for the best baseline. No critical patient exceeded the 15 min time-to-assessment threshold in any controller or workload condition.

## Overall Throughput

At the densest workload, the proposed controller completed the full cohort in 50.81 min, slightly faster than the best baseline, Priority Queue Dispatcher, at 50.83 min. This difference is small, but it is important because the critical-patient benefit did not require a measurable loss of total throughput in the highest-density condition. In lower-density workloads, however, the proposed controller was slower, indicating that environment-mediated switching is most useful when contention is high enough to justify replanning overhead.

## Normal-Patient Delay

The prioritization mechanism delayed normal patients under moderate and low-density workloads. Compared with the average baseline normal-patient time to assessment, the proposed controller added approximately 1-2 min of mean normal delay at arrival multipliers 2.00, 1.50, and 1.25. At the densest workload, this penalty disappeared: normal-patient mean time to assessment was 0.66 min lower than the baseline average. This suggests that the proposed controller is not simply prioritizing critical patients at the expense of normal patients; at high contention, it can also reduce global blocking effects.

## Protocol Compliance

The service/contact duration was constrained to the 2-5 min triage contact window, and no run produced a contact-time violation. Time-to-assessment violations above 15 min occurred only among normal patients. At the densest workload, the proposed controller had the lowest overall 15 min breach rate (40.00%) among the four controllers.

## Interpretation

The results support a transport-control interpretation of the method. The main benefit of the proposed approach is not that it makes every route faster. Instead, it changes the environment so that critical agents can access short paths and resources when the system becomes congested. The same conditional access rules may force normal agents to wait or take detours, but this trade-off becomes beneficial under dense arrival conditions because it reduces conflicts around shared corridors, wing access, and cubicle assignment.

## Reporting Limitation

These results are based on a single trace-driven comparative batch rather than repeated independent seeds. For final statistical reporting, the same workload generation procedure should be repeated with multiple random seeds and reported as mean +/- standard deviation or confidence intervals. The workload should be described as MIMIC-demo-informed / trace-inspired synthetic stress testing, not as a direct replay of a representative real hospital day.
