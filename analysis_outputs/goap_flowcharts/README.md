# GOAP Flowchart Figures

These figures describe the proposed transport-oriented GOAP system without mathematical notation.

1. `figure_1_architecture.svg` - Environment-mediated GOAP transport architecture.
2. `figure_2_agent_replanning_loop.svg` - Online execution, emergency rollback, and replanning loop.
3. `figure_3_transport_resource_flow.svg` - Priority-aware patient transport, queueing, cubicle reservation, and exit flow.
4. `figure_4_experiment_pipeline.svg` - Experimental setup from MIMIC-derived arrivals to protocol metrics and plots.

Suggested paper wording:

Figure 1 shows the system-level architecture. Agents plan over local beliefs and global world states, while the environment actively changes target availability and resource accessibility. Figure 2 details the online replanning loop used when an action target becomes unavailable. Figure 3 illustrates the transport and resource-flow model used in the hospital scenario. Figure 4 summarizes the experimental pipeline and outcome metrics.
