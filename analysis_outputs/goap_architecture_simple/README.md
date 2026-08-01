# Simple Architecture Figures

Use `architecture_overview_simple.svg` as the main paper figure. Use `architecture_component_view_compact.svg` only if the journal needs a smaller diagram.

Use `architecture_replanning_loop_explicit.svg` when the replanning mechanism needs to be visible without showing the full simulation pipeline. The matching Mermaid source is `architecture_replanning_loop_explicit.mmd`.

Use `architecture_replanning_loop_classic.svg` for a cleaner journal-style block diagram with the same logic and fewer annotations.

Use `architecture_personalized_world_view.svg` as the paper-oriented architecture figure when emphasizing the main novelty: the planner receives an agent-specific world-state view rather than the raw shared world state.

Use `architecture_conditional_access_filters.svg` when describing personalization as an emergent result of environment-managed node/resource filters rather than as a separate per-agent world-state object.

Use `architecture_conditional_access_filters_clean.svg` as the cleaner version with the same idea and less arrow overlap.

Use `architecture_conditional_access_filters_vertical.svg` as a portrait-oriented variant for narrow or two-column paper layouts.

Use `architecture_personalized_world_state.svg` as the preferred paper architecture figure when emphasizing the main contribution: transformation of shared world state into an agent-specific planning view.
