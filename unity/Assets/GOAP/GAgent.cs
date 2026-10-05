using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using UnityEngine.AI;

public class SubGoal
{
    public Dictionary<string, int> sgoals;
    public bool remove;

    public SubGoal(string s, int i, bool r)
    {
        sgoals = new Dictionary<string, int>();
        sgoals.Add(s, i);
        remove = r;
    }
}

public class GAgent : MonoBehaviour
{
    const string CriticalPatientTag = "critical";
    const string EmergencyAreaTag = "EmergencyArea";
    // These were 6 (a global cap shared by every GOAP agent in the scene, not
    // per-agent). At 300 concurrent patients under heavy load, bursts of
    // simultaneous plan/destination requests (e.g. right after a queue release)
    // could very plausibly exceed 6/frame and back agents up for several frames
    // each time - the baseline controllers (TriageBaselineAgent) have no such cap
    // at all. Raised to effectively remove the cap as a bottleneck for testing.
    const int MaxPlanRequestsPerFrame = 300;
    const int MaxSetDestinationRequestsPerFrame = 300;

    public List<GAction> actions = new List<GAction>();
    public Dictionary<SubGoal, int> goals = new Dictionary<SubGoal, int>();
    public WorldStates beliefs = new WorldStates(false);

    public GInventory inventory = new GInventory();


    GPlanner planner;
    Queue<GAction> actionQueue;
    public GAction currentAction;
    SubGoal currentGoal;
    bool waitingForWorldStateChangeAfterFailedPlan = false;
    int failedPlanWorldStateChangeCounter = -1;
    int failedPlanFrame = -1;
    bool wasWaitingInPatientQueue;
    int consecutivePlanFailures;
    Vector3 currentDestination;
    bool hasCurrentDestination;
    bool emergencyFallbackRequested;
    bool emergencyFallbackRunning;
    GameObject emergencyFallbackTarget;
    Vector3 emergencyFallbackDestination;
    bool hasEmergencyFallbackDestination;
    string emergencyFallbackReason;
    int lastEmergencyFallbackRefreshFrame = -1;

    static int setDestinationFrame = -1;
    static int setDestinationRequestsThisFrame;
    static int pathBudgetSkipsThisFrame;
    static int maxSetDestinationRequestsInFrame;
    static int maxPathBudgetSkipsInFrame;
    static int planFrame = -1;
    static int planRequestsThisFrame;
    static int planBudgetSkipsThisFrame;
    static int maxPlanRequestsInFrame;
    static int maxPlanBudgetSkipsInFrame;

    public static int PlanRequestsThisFrame
    {
        get { return planRequestsThisFrame; }
    }

    public static int PlanBudgetSkipsThisFrame
    {
        get { return planBudgetSkipsThisFrame; }
    }

    public static int MaxPlanRequestsInFrame
    {
        get { return maxPlanRequestsInFrame; }
    }

    public static int MaxPlanBudgetSkipsInFrame
    {
        get { return maxPlanBudgetSkipsInFrame; }
    }

    public static int SetDestinationRequestsThisFrame
    {
        get { return setDestinationRequestsThisFrame; }
    }

    public static int PathBudgetSkipsThisFrame
    {
        get { return pathBudgetSkipsThisFrame; }
    }

    public static int MaxSetDestinationRequestsInFrame
    {
        get { return maxSetDestinationRequestsInFrame; }
    }

    public static int MaxPathBudgetSkipsInFrame
    {
        get { return maxPathBudgetSkipsInFrame; }
    }

    public static void ResetStaticCountersForExperiment()
    {
        setDestinationFrame = -1;
        setDestinationRequestsThisFrame = 0;
        pathBudgetSkipsThisFrame = 0;
        maxSetDestinationRequestsInFrame = 0;
        maxPathBudgetSkipsInFrame = 0;
        planFrame = -1;
        planRequestsThisFrame = 0;
        planBudgetSkipsThisFrame = 0;
        maxPlanRequestsInFrame = 0;
        maxPlanBudgetSkipsInFrame = 0;
    }

    public void Start()
    {
        GAction[] acts = this.GetComponents<GAction>();
        foreach (GAction a in acts)
            actions.Add(a);

        NavMeshAgent navAgent = GetComponent<NavMeshAgent>();
        if (navAgent != null)
        {
            navAgent.avoidancePriority = Mathf.Abs(GetInstanceID()) % 99;

            // Patients have no Collider/Rigidbody (confirmed in Patient.prefab) - the only
            // inter-agent "collision" is NavMeshAgent's own local avoidance (was
            // HighQualityObstacleAvoidance). Under heavy load (300 agents) this local
            // avoidance can jam an agent indefinitely with no logical failure ever raised
            // (ReachedCurrentTarget just never becomes true - no exception, no plan
            // invalidation, nothing to see in diagnostics), which was suspected as the
            // real driver of GOAP's outsized normal-patient slowdown at load=2.00. This
            // applies uniformly to every architecture (Patient : GAgent, Start() always
            // runs here regardless of controllerArchitecture), so it is not a GOAP-only
            // advantage.
            navAgent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
        }
    }


    bool invoked = false;
    void CompleteAction()
    {
        if (currentAction == null)
        {
            invoked = false;
            return;
        }

        GAction completedAction = currentAction;
        if (currentAction.agent != null && currentAction.agent.enabled && currentAction.agent.isOnNavMesh)
            currentAction.agent.ResetPath();

        hasCurrentDestination = false;
        currentAction.running = false;
        bool postPerformSucceeded = currentAction.PostPerform();
        GoapDiagnostics.Log(
            "AgentAction",
            "complete agent="
            + AgentLabel()
            + " action="
            + ActionLabel(completedAction)
            + " postSucceeded="
            + postPerformSucceeded
            + " worldCounter="
            + WorldStates.worldStateChangeCounter);

        if (
            postPerformSucceeded
            && IsCriticalPatient()
            && ShouldPulseAfterCriticalAction(currentAction)
            && !HospitalFlowController.AutomaticFlowIsActive)
        {
            TargetAvailabilityManager.TriggerInvertAvailabilityPulseOnAllManagers();
        }

        invoked = false;
    }

    bool IsCriticalPatient()
    {
        return gameObject.tag == CriticalPatientTag;
    }

    bool ShouldPulseAfterCriticalAction(GAction action)
    {
        return action != null && action.effects != null && action.effects.ContainsKey("IsThreated");
    }

    void InvalidateCurrentPlan(bool runEmergencyPerform)
    {
        CancelInvoke("CompleteAction");
        invoked = false;
        waitingForWorldStateChangeAfterFailedPlan = false;

        GAction interruptedAction = currentAction;
        bool shouldRequestEmergencyFallback = runEmergencyPerform && ShouldUseEmergencyFallbackForInterruptedAction(interruptedAction);
        if (interruptedAction != null)
        {
            GoapDiagnostics.Log(
                "AgentPlan",
                "invalidate agent="
                + AgentLabel()
                + " emergency="
                + runEmergencyPerform
                + " action="
                + ActionLabel(interruptedAction)
                + " target="
                + TargetLabel(interruptedAction.target));

            if (interruptedAction.agent != null && interruptedAction.agent.enabled && interruptedAction.agent.isOnNavMesh)
                interruptedAction.agent.ResetPath();

            hasCurrentDestination = false;
            interruptedAction.running = false;

            if (runEmergencyPerform)
                interruptedAction.EmergencyPerform();

        }

        currentAction = null;
        actionQueue = null;
        planner = null;
        currentGoal = null;

        if (shouldRequestEmergencyFallback)
            RequestEmergencyFallback("interrupted action " + ActionLabel(interruptedAction));
    }

    void RegisterPlanFailure()
    {
        RegisterPlanFailure(WorldStates.worldStateChangeCounter);
    }


    void RegisterPlanFailure(int failedWorldStateChangeCounter)
    {
        waitingForWorldStateChangeAfterFailedPlan = true;
        failedPlanWorldStateChangeCounter = failedWorldStateChangeCounter;
        failedPlanFrame = Time.frameCount;
        planner = null;
        actionQueue = null;
        currentGoal = null;
        consecutivePlanFailures++;

        GoapDiagnostics.Log(
            "AgentPlan",
            "plan failure agent="
            + AgentLabel()
            + " consecutiveFailures="
            + consecutivePlanFailures
            + " waitingForWorldChangeAt="
            + failedWorldStateChangeCounter
            + " beliefs="
            + StateDictionaryToString(beliefs.GetStates()));
    }

    void FailCurrentPlan(bool runEmergencyPerform)
    {
        int worldStateChangeCounterBeforeFailure = WorldStates.worldStateChangeCounter;

        InvalidateCurrentPlan(runEmergencyPerform);

        if (runEmergencyPerform)
        {
            waitingForWorldStateChangeAfterFailedPlan = false;
            failedPlanWorldStateChangeCounter = -1;
            failedPlanFrame = -1;
            return;
        }

        RegisterPlanFailure(worldStateChangeCounterBeforeFailure);
    }

    bool ShouldWaitForWorldStateChange()
    {
        if (!waitingForWorldStateChangeAfterFailedPlan)
            return false;

        if (Time.frameCount <= failedPlanFrame)
            return true;

        if (WorldStates.worldStateChangeCounter == failedPlanWorldStateChangeCounter)
        {
            GoapDiagnostics.LogThrottled(
                "wait-world-change-" + GetInstanceID(),
                60,
                "AgentPlan",
                "waiting for world change agent="
                + AgentLabel()
                + " failedCounter="
                + failedPlanWorldStateChangeCounter
                + " currentCounter="
                + WorldStates.worldStateChangeCounter);
            return true;
        }

        waitingForWorldStateChangeAfterFailedPlan = false;
        GoapDiagnostics.Log(
            "AgentPlan",
            "world change woke planner agent="
            + AgentLabel()
            + " failedCounter="
            + failedPlanWorldStateChangeCounter
            + " currentCounter="
            + WorldStates.worldStateChangeCounter);
        return false;
    }

    bool ReachedCurrentTarget()
    {
        if (currentAction == null || currentAction.agent == null || !TargetIsAvailable(currentAction.target))
            return false;

        NavMeshAgent navAgent = currentAction.agent;
        if (!navAgent.enabled || !navAgent.isOnNavMesh)
            return false;

        if (navAgent.pathPending || navAgent.remainingDistance == Mathf.Infinity)
            return false;

        if (navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            return false;

        float arrivalDistance = Mathf.Max(1f, Mathf.Max(navAgent.stoppingDistance, currentAction.completionThreshold));
        Vector3 agentPosition = navAgent.transform.position;
        Vector3 targetPosition = currentAction.target.transform.position;
        Vector2 agentXZ = new Vector2(agentPosition.x, agentPosition.z);
        Vector2 targetXZ = new Vector2(targetPosition.x, targetPosition.z);

        if (Vector2.Distance(agentXZ, targetXZ) <= arrivalDistance)
            return true;

        if (hasCurrentDestination)
        {
            Vector2 destinationXZ = new Vector2(currentDestination.x, currentDestination.z);
            if (Vector2.Distance(agentXZ, destinationXZ) <= arrivalDistance)
                return true;
        }

        if (navAgent.hasPath && navAgent.remainingDistance <= arrivalDistance)
            return true;

        return false;
    }

    bool TargetIsAvailable(GameObject target)
    {
        return target != null && TargetAvailabilityManager.IsAvailableForAgent(target, IsCriticalPatient());
    }

    bool ShouldUseEmergencyFallbackForInterruptedAction(GAction action)
    {
        if (action == null)
            return false;

        if (ActionTouchesRouteOrWing(action))
            return true;

        return AgentHasRouteProgressBelief();
    }

    bool ShouldUseEmergencyFallbackAfterPlanFailure()
    {
        if (ShouldWaitInPatientQueue())
            return false;

        return AgentHasRouteProgressBelief();
    }

    bool ActionTouchesRouteOrWing(GAction action)
    {
        if (action == null)
            return false;

        if (IsRouteTargetTag(action.targetTag))
            return true;

        if (ActionStatesTouchRoute(action.preconditions))
            return true;

        if (ActionStatesTouchRoute(action.effects))
            return true;

        return false;
    }

    static bool IsRouteTargetTag(string targetTag)
    {
        if (string.IsNullOrEmpty(targetTag))
            return false;

        return targetTag == "Wing"
            || targetTag == "WingExit"
            || targetTag.Contains("Corridor");
    }

    static bool ActionStatesTouchRoute(Dictionary<string, int> states)
    {
        if (states == null)
            return false;

        foreach (string key in states.Keys)
        {
            if (StateKeyTouchesRoute(key))
                return true;
        }

        return false;
    }

    bool AgentHasRouteProgressBelief()
    {
        Dictionary<string, int> states = beliefs.GetStates();
        foreach (string key in states.Keys)
        {
            if (StateKeyTouchesRoute(key))
                return true;
        }

        return false;
    }

    static bool StateKeyTouchesRoute(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        return key == "isCured"
            || key == "WingPassed"
            || key == "LeftWingPassed"
            || key == "ReadyToGoHome"
            || key.Contains("Corridor")
            || key.Contains("ExitRoute")
            || key.Contains("ExitWing")
            || key.Contains("GoingToEmergencyRoom")
            || key.Contains("GoingToCubicle");
    }

    void RequestEmergencyFallback(string reason)
    {
        emergencyFallbackRequested = true;
        emergencyFallbackReason = string.IsNullOrEmpty(reason) ? "fallback requested" : reason;

        GoapDiagnostics.LogThrottled(
            "emergency-fallback-request-" + GetInstanceID(),
            30,
            "EmergencyArea",
            "request agent="
            + AgentLabel()
            + " reason="
            + emergencyFallbackReason
            + " beliefs="
            + StateDictionaryToString(beliefs.GetStates()));
    }

    void ClearEmergencyFallback(string reason)
    {
        if (!emergencyFallbackRequested && !emergencyFallbackRunning)
            return;

        GoapDiagnostics.Log(
            "EmergencyArea",
            "clear agent="
            + AgentLabel()
            + " reason="
            + reason
            + " target="
            + TargetLabel(emergencyFallbackTarget));

        emergencyFallbackRequested = false;
        emergencyFallbackRunning = false;
        emergencyFallbackTarget = null;
        hasEmergencyFallbackDestination = false;
        emergencyFallbackReason = null;
    }

    bool ServiceEmergencyFallback()
    {
        if (!emergencyFallbackRequested)
            return false;

        NavMeshAgent navAgent = GetComponent<NavMeshAgent>();
        if (navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh)
            return false;

        if (!TargetIsAvailable(emergencyFallbackTarget) || !hasEmergencyFallbackDestination)
        {
            if (!TryFindNearestEmergencyArea(navAgent, out emergencyFallbackTarget, out emergencyFallbackDestination))
            {
                GoapDiagnostics.LogThrottled(
                    "emergency-fallback-no-target-" + GetInstanceID(),
                    60,
                    "EmergencyArea",
                    "no active EmergencyArea found for agent="
                    + AgentLabel()
                    + " reason="
                    + emergencyFallbackReason);
                return false;
            }

            hasEmergencyFallbackDestination = true;
            emergencyFallbackRunning = false;
        }

        if (ReachedEmergencyFallbackTarget(navAgent))
        {
            emergencyFallbackRunning = true;
            if (navAgent.hasPath)
                navAgent.ResetPath();
            return true;
        }

        if (!emergencyFallbackRunning || (!navAgent.pathPending && !navAgent.hasPath))
        {
            if (Time.frameCount == lastEmergencyFallbackRefreshFrame)
                return true;

            if (!TryConsumeSetDestinationBudget())
                return true;

            lastEmergencyFallbackRefreshFrame = Time.frameCount;
            navAgent.ResetPath();
            if (navAgent.SetDestination(emergencyFallbackDestination))
            {
                emergencyFallbackRunning = true;
                GoapDiagnostics.Log(
                    "EmergencyArea",
                    "started agent="
                    + AgentLabel()
                    + " target="
                    + TargetLabel(emergencyFallbackTarget)
                    + " destination="
                    + emergencyFallbackDestination
                    + " reason="
                    + emergencyFallbackReason);
            }
            else
            {
                GoapDiagnostics.Log(
                    "EmergencyArea",
                    "set destination failed agent="
                    + AgentLabel()
                    + " target="
                    + TargetLabel(emergencyFallbackTarget));
                emergencyFallbackTarget = null;
                hasEmergencyFallbackDestination = false;
                emergencyFallbackRunning = false;
                return false;
            }
        }

        return true;
    }

    bool ReachedEmergencyFallbackTarget(NavMeshAgent navAgent)
    {
        if (navAgent == null || !hasEmergencyFallbackDestination)
            return false;

        if (navAgent.pathPending || navAgent.remainingDistance == Mathf.Infinity)
            return false;

        if (navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            return false;

        float arrivalDistance = Mathf.Max(1f, Mathf.Max(navAgent.stoppingDistance, 2.5f));
        Vector3 agentPosition = navAgent.transform.position;
        Vector2 agentXZ = new Vector2(agentPosition.x, agentPosition.z);
        Vector2 destinationXZ = new Vector2(emergencyFallbackDestination.x, emergencyFallbackDestination.z);

        if (Vector2.Distance(agentXZ, destinationXZ) <= arrivalDistance)
            return true;

        return navAgent.hasPath && navAgent.remainingDistance <= arrivalDistance;
    }

    bool TryFindNearestEmergencyArea(NavMeshAgent navAgent, out GameObject nearestArea, out Vector3 destination)
    {
        nearestArea = null;
        destination = Vector3.zero;

        GameObject[] areas;
        try
        {
            areas = GameObject.FindGameObjectsWithTag(EmergencyAreaTag);
        }
        catch (UnityException)
        {
            return false;
        }

        if (areas == null || areas.Length == 0)
            return false;

        Vector3 agentPosition = navAgent.transform.position;
        float bestScore = Mathf.Infinity;
        bool foundPartial = false;
        NavMeshPath path = new NavMeshPath();

        foreach (GameObject area in areas)
        {
            if (!TargetIsAvailable(area))
                continue;

            Vector3 candidateDestination = GetEmergencyAreaDestination(area, navAgent);
            bool hasPath = NavMesh.CalculatePath(agentPosition, candidateDestination, NavMesh.AllAreas, path)
                && path.status != NavMeshPathStatus.PathInvalid;
            if (!hasPath)
                continue;

            bool complete = path.status == NavMeshPathStatus.PathComplete;
            float score = (agentPosition - candidateDestination).sqrMagnitude;
            if (!complete)
                score += 1000000f;

            if (nearestArea == null || score < bestScore || (complete && foundPartial))
            {
                nearestArea = area;
                destination = candidateDestination;
                bestScore = score;
                foundPartial = !complete;
            }
        }

        return nearestArea != null;
    }

    Vector3 GetEmergencyAreaDestination(GameObject area, NavMeshAgent navAgent)
    {
        Vector3 targetPosition = area.transform.position;
        float spreadRadius = Mathf.Max(GetMinimumSpreadRadius(EmergencyAreaTag), navAgent.radius * 2f);

        Collider targetCollider = area.GetComponent<Collider>();
        if (targetCollider != null)
        {
            Vector3 extents = targetCollider.bounds.extents;
            float colliderRadius = Mathf.Min(extents.x, extents.z) * 0.65f;
            spreadRadius = Mathf.Max(spreadRadius, colliderRadius);
        }

        int seed = gameObject.GetInstanceID() ^ area.GetInstanceID() ^ 7919;
        float angle = Hash01(seed) * Mathf.PI * 2f;
        float distance = spreadRadius * Mathf.Lerp(0.25f, 0.95f, Hash01(seed * 31 + 7));
        Vector3 candidate = targetPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;

        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, spreadRadius + 2f, NavMesh.AllAreas))
            return hit.position;

        if (NavMesh.SamplePosition(targetPosition, out hit, spreadRadius + 2f, NavMesh.AllAreas))
            return hit.position;

        return targetPosition;
    }

    bool ShouldWaitInPatientQueue()
    {
        if (!beliefs.HasState("atHospital") || beliefs.HasState("isCured"))
            return false;

        return GWorld.Instance.IsQueuedPatient(gameObject) && !GWorld.Instance.IsNextPatient(gameObject);
    }

    static bool TryConsumeSetDestinationBudget()
    {
        RefreshSetDestinationBudgetFrame();

        if (setDestinationRequestsThisFrame >= MaxSetDestinationRequestsPerFrame)
        {
            pathBudgetSkipsThisFrame++;
            maxPathBudgetSkipsInFrame = Mathf.Max(maxPathBudgetSkipsInFrame, pathBudgetSkipsThisFrame);
            GoapDiagnostics.LogOncePerFrame(
                "set-destination-budget-skip",
                "AgentBudget",
                "set destination budget exhausted. requests="
                + setDestinationRequestsThisFrame
                + " skips="
                + pathBudgetSkipsThisFrame);
            return false;
        }

        setDestinationRequestsThisFrame++;
        maxSetDestinationRequestsInFrame = Mathf.Max(maxSetDestinationRequestsInFrame, setDestinationRequestsThisFrame);
        return true;
    }

    static void RefreshSetDestinationBudgetFrame()
    {
        if (setDestinationFrame == Time.frameCount)
            return;

        setDestinationFrame = Time.frameCount;
        setDestinationRequestsThisFrame = 0;
        pathBudgetSkipsThisFrame = 0;
    }

    static bool TryConsumePlanBudget()
    {
        RefreshPlanBudgetFrame();

        if (planRequestsThisFrame >= MaxPlanRequestsPerFrame)
        {
            planBudgetSkipsThisFrame++;
            maxPlanBudgetSkipsInFrame = Mathf.Max(maxPlanBudgetSkipsInFrame, planBudgetSkipsThisFrame);
            GoapDiagnostics.LogOncePerFrame(
                "plan-budget-skip",
                "AgentBudget",
                "plan budget exhausted. requests="
                + planRequestsThisFrame
                + " skips="
                + planBudgetSkipsThisFrame);
            return false;
        }

        planRequestsThisFrame++;
        maxPlanRequestsInFrame = Mathf.Max(maxPlanRequestsInFrame, planRequestsThisFrame);
        return true;
    }

    static void RefreshPlanBudgetFrame()
    {
        if (planFrame == Time.frameCount)
            return;

        planFrame = Time.frameCount;
        planRequestsThisFrame = 0;
        planBudgetSkipsThisFrame = 0;
    }

    Vector3 GetSpreadDestination(GAction action, NavMeshAgent navAgent)
    {
        Vector3 targetPosition = action.target.transform.position;
        float spreadRadius = Mathf.Max(
            GetMinimumSpreadRadius(action.targetTag),
            Mathf.Max(navAgent.radius * 2f, action.completionThreshold));

        Collider targetCollider = action.target.GetComponent<Collider>();
        if (targetCollider != null)
        {
            Vector3 extents = targetCollider.bounds.extents;
            float colliderRadius = Mathf.Min(extents.x, extents.z) * 0.65f;
            spreadRadius = Mathf.Max(spreadRadius, colliderRadius);
        }

        int seed = gameObject.GetInstanceID() ^ action.GetInstanceID();
        float angle = Hash01(seed) * Mathf.PI * 2f;
        float distance = spreadRadius * Mathf.Lerp(0.25f, 0.95f, Hash01(seed * 31 + 7));
        Vector3 candidate = targetPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;

        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, spreadRadius + 2f, NavMesh.AllAreas))
            return hit.position;

        return targetPosition;
    }

    static float GetMinimumSpreadRadius(string targetTag)
    {
        switch (targetTag)
        {
            case "Entrance":
            case "WaitingAria":
                return 6f;
            case "Wing":
            case "WingExit":
            case EmergencyAreaTag:
                return 5f;
            case "CorridorA":
            case "CorridorB":
            case "CorridorC":
            case "CorridorAExit":
            case "CorridorBExit":
            case "CorridorCExit":
                return 4f;
            default:
                return 1f;
        }
    }

    static float Hash01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffff) / 16777215f;
        }
    }

    string AgentLabel()
    {
        return gameObject.name + "[" + gameObject.tag + "]";
    }

    static string ActionLabel(GAction action)
    {
        if (action == null)
            return "<none>";

        return action.actionName + "(" + action.GetType().Name + ")";
    }

    static string TargetLabel(GameObject target)
    {
        if (target == null)
            return "<null>";

        return target.name + "[" + target.tag + "] active=" + target.activeInHierarchy;
    }

    static string GoalToString(SubGoal goal)
    {
        if (goal == null || goal.sgoals == null)
            return "<none>";

        return StateDictionaryToString(goal.sgoals);
    }

    static string ActionQueueToString(Queue<GAction> queue)
    {
        if (queue == null || queue.Count == 0)
            return "<empty>";

        string result = "";
        foreach (GAction action in queue)
        {
            if (result.Length > 0)
                result += " -> ";

            result += ActionLabel(action);
        }

        return result;
    }

    static string StateDictionaryToString(Dictionary<string, int> states)
    {
        if (states == null || states.Count == 0)
            return "<empty>";

        string result = "";
        foreach (KeyValuePair<string, int> state in states)
        {
            if (result.Length > 0)
                result += ",";

            result += state.Key + "=" + state.Value;
        }

        return result;
    }

    void LateUpdate()
    {
        if (currentAction != null && currentAction.running)
        {
            if (!TargetIsAvailable(currentAction.target))
            {
                GoapDiagnostics.Log(
                    "AgentAction",
                    "running target unavailable agent="
                    + AgentLabel()
                    + " action="
                    + ActionLabel(currentAction)
                    + " target="
                    + TargetLabel(currentAction.target));
                FailCurrentPlan(true);
                return;
            }

            if (ReachedCurrentTarget())
            {
                if (!invoked)
                {
                    GoapDiagnostics.Log(
                        "AgentAction",
                        "arrived agent="
                        + AgentLabel()
                        + " action="
                        + ActionLabel(currentAction)
                        + " target="
                        + TargetLabel(currentAction.target)
                        + " duration="
                        + currentAction.duration);
                    Invoke("CompleteAction", currentAction.duration);
                    invoked = true;
                }
            }
            return;
        }

        if (emergencyFallbackRequested && ShouldWaitInPatientQueue())
        {
            wasWaitingInPatientQueue = true;
            ServiceEmergencyFallback();
            return;
        }

        if (emergencyFallbackRequested && waitingForWorldStateChangeAfterFailedPlan)
        {
            ServiceEmergencyFallback();
            if (ShouldWaitForWorldStateChange())
                return;
        }

        bool waitingInQueue = ShouldWaitInPatientQueue();
        if (waitingInQueue)
        {
            if (!wasWaitingInPatientQueue)
            {
                GoapDiagnostics.Log(
                    "AgentQueue",
                    "waiting in patient queue agent="
                    + AgentLabel()
                    + " first="
                    + TargetLabel(GWorld.Instance.PeekPatient())
                    + " queue="
                    + GWorld.Instance.GetPatientQueueDebugString());
            }

            wasWaitingInPatientQueue = true;
            return;
        }

        if (wasWaitingInPatientQueue)
        {
            GoapDiagnostics.Log(
                "AgentQueue",
                "queue wait released agent="
                + AgentLabel()
                + " first="
                + TargetLabel(GWorld.Instance.PeekPatient())
                + " queue="
                + GWorld.Instance.GetPatientQueueDebugString());
            wasWaitingInPatientQueue = false;
        }

        if (planner == null || actionQueue == null)
        {
            if (ShouldWaitForWorldStateChange())
                return;

            if (!TryConsumePlanBudget())
                return;

            planner = new GPlanner();

            var sortedGoals = from entry in goals orderby entry.Value descending select entry;

            foreach (KeyValuePair<SubGoal, int> sg in sortedGoals)
            {
                actionQueue = planner.plan(actions, sg.Key.sgoals, beliefs);
                if (actionQueue != null)
                {
                    currentGoal = sg.Key;
                    consecutivePlanFailures = 0;
                    ClearEmergencyFallback("plan success");
                    GoapDiagnostics.Log(
                        "AgentPlan",
                        "plan success agent="
                        + AgentLabel()
                        + " goal="
                        + GoalToString(currentGoal)
                        + " priority="
                        + sg.Value
                        + " actions="
                        + ActionQueueToString(actionQueue));
                    break;
                }
            }

            if (actionQueue == null)
            {
                RegisterPlanFailure();
                if (ShouldUseEmergencyFallbackAfterPlanFailure())
                {
                    RequestEmergencyFallback("no plan for " + AgentLabel());
                    ServiceEmergencyFallback();
                }
                return;
            }

            waitingForWorldStateChangeAfterFailedPlan = false;
        }

        if (actionQueue != null && actionQueue.Count == 0)
        {
            if (currentGoal.remove)
            {
                GoapDiagnostics.Log(
                    "AgentPlan",
                    "goal complete agent="
                    + AgentLabel()
                    + " goal="
                    + GoalToString(currentGoal));
                goals.Remove(currentGoal);
            }
            planner = null;
        }

        if (actionQueue != null && actionQueue.Count > 0)
        {
            if (!TryConsumeSetDestinationBudget())
                return;

            currentAction = actionQueue.Dequeue();
            GoapDiagnostics.Log(
                "AgentAction",
                "preperform agent="
                + AgentLabel()
                + " action="
                + ActionLabel(currentAction)
                + " target="
                + TargetLabel(currentAction.target)
                + " targetTag="
                + currentAction.targetTag
                + " remainingActions="
                + actionQueue.Count);

            if (currentAction.PrePerform())
            {
                if (currentAction.target == null && currentAction.targetTag != "")
                    currentAction.target = TargetAvailabilityManager.ResolveTaggedTarget(currentAction.targetTag);

                if (TargetIsAvailable(currentAction.target))
                {
                    NavMeshAgent navAgent = currentAction.agent;
                    if (navAgent != null && navAgent.enabled && navAgent.isOnNavMesh)
                    {
                        CancelInvoke("CompleteAction");
                        invoked = false;
                        navAgent.ResetPath();
                        hasCurrentDestination = false;
                        Vector3 destination = GetSpreadDestination(currentAction, navAgent);
                        if (navAgent.SetDestination(destination))
                        {
                            currentDestination = destination;
                            hasCurrentDestination = true;
                            currentAction.running = true;
                            GoapDiagnostics.Log(
                                "AgentAction",
                                "started agent="
                                + AgentLabel()
                                + " action="
                                + ActionLabel(currentAction)
                                + " target="
                                + TargetLabel(currentAction.target)
                                + " destination="
                                + destination
                                + " remainingActions="
                                + actionQueue.Count);
                        }
                        else
                        {
                            GoapDiagnostics.Log(
                                "AgentPath",
                                "set destination failed agent="
                                + AgentLabel()
                                + " action="
                                + ActionLabel(currentAction)
                                + " target="
                                + TargetLabel(currentAction.target));
                            FailCurrentPlan(true);
                        }
                    }
                    else
                    {
                        GoapDiagnostics.Log(
                            "AgentPath",
                            "nav agent unavailable agent="
                            + AgentLabel()
                            + " action="
                            + ActionLabel(currentAction)
                            + " target="
                            + TargetLabel(currentAction.target));
                        FailCurrentPlan(true);
                    }
                }
                else
                {
                    GoapDiagnostics.Log(
                        "AgentAction",
                        "target unavailable after preperform agent="
                        + AgentLabel()
                        + " action="
                        + ActionLabel(currentAction)
                        + " target="
                        + TargetLabel(currentAction.target)
                        + " targetTag="
                        + currentAction.targetTag);
                    FailCurrentPlan(true);
                }
            }
            else
            {
                GoapDiagnostics.Log(
                    "AgentAction",
                    "preperform failed agent="
                    + AgentLabel()
                    + " action="
                    + ActionLabel(currentAction)
                    + " target="
                    + TargetLabel(currentAction.target)
                    + " queue="
                    + GWorld.Instance.GetPatientQueueDebugString());
                RegisterPlanFailure();
                if (ShouldUseEmergencyFallbackAfterPlanFailure())
                {
                    RequestEmergencyFallback("preperform failed " + ActionLabel(currentAction));
                    ServiceEmergencyFallback();
                }
            }

        }

    }
}
