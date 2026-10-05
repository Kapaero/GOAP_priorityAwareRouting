using UnityEngine;

public class WorldStateChangeDebugger : MonoBehaviour
{
    [SerializeField] bool triggerIncrement;
    [SerializeField] HospitalFlowController flowController;
    [SerializeField] bool fileDiagnosticsEnabled = true;
    [SerializeField] bool triggerFlushDiagnostics;
    [SerializeField] float longFrameLogThresholdMs = 50f;
    [SerializeField] string diagnosticsLogPath;
    [SerializeField] bool triggerForceAdmissionMode;
    [SerializeField] bool triggerForceReleaseMode;
    [SerializeField] int currentWorldStateChangeCounter;
    [SerializeField] int worldStateChangesThisFrame;
    [SerializeField] int maxWorldStateChangesInFrame;
    [SerializeField] int setDestinationRequestsThisFrame;
    [SerializeField] int maxSetDestinationRequestsInFrame;
    [SerializeField] int pathBudgetSkipsThisFrame;
    [SerializeField] int maxPathBudgetSkipsInFrame;
    [SerializeField] int planRequestsThisFrame;
    [SerializeField] int maxPlanRequestsInFrame;
    [SerializeField] int planBudgetSkipsThisFrame;
    [SerializeField] int maxPlanBudgetSkipsInFrame;
    [SerializeField] int manualIncrementCount;

    int lastWorldStateChangeCounter;

    void Awake()
    {
        if (flowController == null)
            flowController = GetComponent<HospitalFlowController>();
    }

    void Update()
    {
        GoapDiagnostics.Enabled = fileDiagnosticsEnabled;
        diagnosticsLogPath = GoapDiagnostics.LogPath;

        currentWorldStateChangeCounter = WorldStates.worldStateChangeCounter;
        worldStateChangesThisFrame = Mathf.Max(0, currentWorldStateChangeCounter - lastWorldStateChangeCounter);
        maxWorldStateChangesInFrame = Mathf.Max(maxWorldStateChangesInFrame, worldStateChangesThisFrame);
        lastWorldStateChangeCounter = currentWorldStateChangeCounter;
        setDestinationRequestsThisFrame = GAgent.SetDestinationRequestsThisFrame;
        maxSetDestinationRequestsInFrame = GAgent.MaxSetDestinationRequestsInFrame;
        pathBudgetSkipsThisFrame = GAgent.PathBudgetSkipsThisFrame;
        maxPathBudgetSkipsInFrame = GAgent.MaxPathBudgetSkipsInFrame;
        planRequestsThisFrame = GAgent.PlanRequestsThisFrame;
        maxPlanRequestsInFrame = GAgent.MaxPlanRequestsInFrame;
        planBudgetSkipsThisFrame = GAgent.PlanBudgetSkipsThisFrame;
        maxPlanBudgetSkipsInFrame = GAgent.MaxPlanBudgetSkipsInFrame;

        LogFrameSummaryIfNeeded();

        if (triggerIncrement)
        {
            triggerIncrement = false;
            IncrementWorldStateChangeCounter();
        }

        if (triggerFlushDiagnostics)
        {
            triggerFlushDiagnostics = false;
            GoapDiagnostics.Flush();
        }

        if (triggerForceAdmissionMode)
        {
            triggerForceAdmissionMode = false;
            if (flowController != null)
                flowController.ForceAdmissionMode();
        }

        if (triggerForceReleaseMode)
        {
            triggerForceReleaseMode = false;
            if (flowController != null)
                flowController.ForceReleaseMode();
        }
    }

    [ContextMenu("Increment World State Change Counter")]
    public void IncrementWorldStateChangeCounter()
    {
        WorldStates.IncrementWorldStateChangeCounter("manual debugger increment");
        manualIncrementCount++;
        currentWorldStateChangeCounter = WorldStates.worldStateChangeCounter;

        Debug.Log("Manual world state change counter increment: " + currentWorldStateChangeCounter);
    }

    void LogFrameSummaryIfNeeded()
    {
        float frameMs = Time.unscaledDeltaTime * 1000f;
        bool longFrame = frameMs >= longFrameLogThresholdMs;
        bool manyWorldChanges = worldStateChangesThisFrame > 2;
        bool budgetPressure = planBudgetSkipsThisFrame > 0 || pathBudgetSkipsThisFrame > 0;
        bool heavyWork = planRequestsThisFrame >= 4 || setDestinationRequestsThisFrame >= 4;

        if (!longFrame && !manyWorldChanges && !budgetPressure && !heavyWork)
            return;

        GoapDiagnostics.LogOncePerFrame(
            "frame-summary",
            "FrameSummary",
            "frameMs="
            + frameMs.ToString("F1")
            + " worldChanges="
            + worldStateChangesThisFrame
            + " planRequests="
            + planRequestsThisFrame
            + " planSkips="
            + planBudgetSkipsThisFrame
            + " setDestinationRequests="
            + setDestinationRequestsThisFrame
            + " pathSkips="
            + pathBudgetSkipsThisFrame
            + " worldCounter="
            + currentWorldStateChangeCounter);
    }
}
