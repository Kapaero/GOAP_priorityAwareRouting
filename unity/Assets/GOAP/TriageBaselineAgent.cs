using UnityEngine;
using UnityEngine.AI;

public class TriageBaselineAgent : MonoBehaviour
{
    public enum ReservedWing
    {
        None,
        Right,
        Left
    }

    public enum ExitRoute
    {
        None,
        CorridorA,
        CorridorB,
        CorridorCToA,
        CorridorCToB
    }

    enum BaselineState
    {
        ToEntrance,
        ToWaitingRoom,
        WaitingForQueue,
        ToWing,
        ToCorridorA,
        ToCorridorB,
        ToCorridorC,
        ToCubicle,
        Treating,
        ExitFirstCorridor,
        ExitSecondCorridor,
        ExitWing,
        GoHome,
        Done
    }

    const string CriticalPatientTag = "critical";
    const string EntranceTag = "Entrance";
    const string WaitingRoomTag = "WaitingAria";
    const string WingTag = "Wing";
    const string CorridorATag = "CorridorA";
    const string CorridorBTag = "CorridorB";
    const string CorridorCTag = "CorridorC";
    const string WingExitTag = "WingExit";
    const string CorridorAExitTag = "CorridorAExit";
    const string CorridorBExitTag = "CorridorBExit";
    const string CorridorCExitTag = "CorridorCExit";
    const string EmergencyAreaTag = "EmergencyArea";

    [SerializeField] float completionThreshold = 2f;
    [SerializeField] float entranceCompletionThreshold = 6f;
    [SerializeField] float waitingRoomCompletionThreshold = 6f;
    [SerializeField] float treatmentDuration = 0f;
    [SerializeField] TriageExperimentArchitecture controllerArchitecture = TriageExperimentArchitecture.FsmReactiveController;
    [SerializeField] bool criticalPrefersShortestPath = true;
    [SerializeField] bool preferWingWithMoreFreeCubicles = true;
    [SerializeField] bool logStateChanges = false;
    [SerializeField] BaselineState state = BaselineState.ToEntrance;
    [SerializeField] ReservedWing reservedWing = ReservedWing.None;
    [SerializeField] ExitRoute exitRoute = ExitRoute.None;
    [SerializeField] string currentTargetTag;
    [SerializeField] GameObject currentTarget;
    [SerializeField] bool waitingForTargetAvailability;

    NavMeshAgent navAgent;
    GAgent goapAgent;
    GInventory inventory;
    WorldStates beliefs;
    float treatmentCompleteTime;
    bool wingEntryRegistered;
    bool wingExitRegistered;
    bool initialized;
    int lastWaitingLogFrame = -1;
    int lastDestinationRefreshFrame = -1;
    Vector3 currentDestination;
    bool hasCurrentDestination;
    bool emergencyFallbackRequested;
    bool emergencyFallbackRunning;
    GameObject emergencyFallbackTarget;
    Vector3 emergencyFallbackDestination;
    bool hasEmergencyFallbackDestination;
    string emergencyFallbackReason;
    int lastEmergencyFallbackRefreshFrame = -1;

    public void ConfigureController(TriageExperimentArchitecture architecture)
    {
        controllerArchitecture = architecture;
    }

    void Awake()
    {
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();

        if (!initialized)
        {
            initialized = true;
            ChangeState(BaselineState.ToEntrance, "enabled");
        }
    }

    void Update()
    {
        ResolveReferences();

        if (navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh)
            return;

        if (emergencyFallbackRequested && !CanResumeFromEmergencyFallback())
        {
            ServiceEmergencyFallback();
            return;
        }

        if (emergencyFallbackRequested)
            ClearEmergencyFallback("route available");

        switch (state)
        {
            case BaselineState.ToEntrance:
                if (MoveToTagAndArrived(EntranceTag))
                {
                    SetBelief("hasArrived", 0);
                    ChangeState(BaselineState.ToWaitingRoom, "entrance reached");
                }
                break;

            case BaselineState.ToWaitingRoom:
                if (MoveToTagAndArrived(WaitingRoomTag))
                {
                    RegisterInWaitingQueue();
                    ChangeState(BaselineState.WaitingForQueue, "waiting room reached");
                }
                break;

            case BaselineState.WaitingForQueue:
                TryLeaveWaitingQueue();
                break;

            case BaselineState.ToWing:
                if (!TagIsAvailable(WingTag))
                {
                    if (IsPlainBaseline())
                    {
                        // A dispatched baseline patient keeps the reserved cubicle and stops where it is.
                        ClearCurrentTargetIfTag(WingTag);
                        LogWaiting("wing entry closed, waiting in place");
                        break;
                    }

                    RequestEmergencyFallbackIfRouteState("wing entry closed");
                    RollbackReservationBeforeWingEntry();
                    break;
                }

                if (MoveToTagAndArrived(WingTag))
                {
                    RegisterWingEntryIfNeeded();
                    ChooseCorridorAfterWing();
                }
                break;

            case BaselineState.ToCorridorA:
                if (MoveToTagAndArrived(CorridorATag))
                    ContinueAfterCorridorA();
                break;

            case BaselineState.ToCorridorB:
                if (MoveToTagAndArrived(CorridorBTag))
                    ContinueAfterCorridorB();
                break;

            case BaselineState.ToCorridorC:
                if (MoveToTagAndArrived(CorridorCTag))
                    ChangeState(BaselineState.ToCubicle, "corridor C reached");
                break;

            case BaselineState.ToCubicle:
                if (MoveToTargetAndArrived(GetReservedCubicle()))
                    StartTreatment();
                break;

            case BaselineState.Treating:
                if (Time.time >= treatmentCompleteTime)
                    CompleteTreatment();
                break;

            case BaselineState.ExitFirstCorridor:
                if (MoveToTagAndArrived(GetFirstExitCorridorTag()))
                    ContinueAfterFirstExitCorridor();
                break;

            case BaselineState.ExitSecondCorridor:
                if (MoveToTagAndArrived(GetSecondExitCorridorTag()))
                    ChangeState(BaselineState.ExitWing, "second exit corridor reached");
                break;

            case BaselineState.ExitWing:
                if (MoveToTagAndArrived(WingExitTag))
                {
                    if (!wingExitRegistered)
                    {
                        HospitalFlowController.RegisterWingExit(gameObject);
                        wingExitRegistered = true;
                    }

                    ChangeState(BaselineState.GoHome, "wing exit reached");
                }
                break;

            case BaselineState.GoHome:
                if (controllerArchitecture == TriageExperimentArchitecture.QLearningDispatcher)
                    TriageQLearningDispatcher.OnPatientCompleted(gameObject);
                if (controllerArchitecture == TriageExperimentArchitecture.DeepQLearningDispatcher)
                    TriageDqnDispatcher.OnPatientCompleted(gameObject);
                TriageExperimentMetrics.RecordHome(gameObject);
                GWorld.Instance.RemovePatient(gameObject);
                ChangeState(BaselineState.Done, "home");
                Destroy(gameObject);
                break;
        }
    }

    void ResolveReferences()
    {
        if (navAgent == null)
            navAgent = GetComponent<NavMeshAgent>();

        if (goapAgent == null)
            goapAgent = GetComponent<GAgent>();

        if (goapAgent != null)
        {
            inventory = goapAgent.inventory;
            beliefs = goapAgent.beliefs;
        }

        if (inventory == null)
            inventory = new GInventory();

        if (beliefs == null)
            beliefs = new WorldStates(false);
    }

    void RegisterInWaitingQueue()
    {
        if (GWorld.Instance.AddPatient(gameObject))
            GWorld.Instance.GetWorld().ModifyState("Waiting", 1);

        SetBelief("atHospital", 1);
        TriageExperimentMetrics.RecordWaitingRoomEntry(gameObject);
    }

    void TryLeaveWaitingQueue()
    {
        if (!GWorld.Instance.IsNextPatient(gameObject))
        {
            LogWaitingForQueue();
            return;
        }

        if (!TagIsAvailable(WingTag))
        {
            LogWaiting("wing entry is closed");
            return;
        }

        GameObject cubicle;
        if (!TryReserveCubicle(out cubicle))
        {
            LogWaiting("no baseline reservation available");
            return;
        }

        wingEntryRegistered = false;
        wingExitRegistered = false;
        currentTarget = null;
        currentTargetTag = null;
        ChangeState(BaselineState.ToWing, "cubicle reserved " + reservedWing);
    }

    bool TryReserveCubicle(out GameObject cubicle)
    {
        switch (controllerArchitecture)
        {
            case TriageExperimentArchitecture.PriorityQueueDispatcher:
                return TriagePriorityQueueDispatcher.TryDispatchPatient(
                    gameObject,
                    inventory,
                    out reservedWing,
                    out exitRoute,
                    out cubicle);

            case TriageExperimentArchitecture.DecisionTableController:
                return TryReserveCubicleByDecisionTable(out cubicle);

            case TriageExperimentArchitecture.QLearningDispatcher:
                return TriageQLearningDispatcher.TryDispatchPatient(
                    gameObject,
                    inventory,
                    out reservedWing,
                    out exitRoute,
                    out cubicle);

            case TriageExperimentArchitecture.DeepQLearningDispatcher:
                return TriageDqnDispatcher.TryDispatchPatient(
                    gameObject,
                    inventory,
                    out reservedWing,
                    out exitRoute,
                    out cubicle);

            case TriageExperimentArchitecture.FsmReactiveController:
            default:
                return TryReserveCubicleByReactiveController(out cubicle);
        }
    }

    bool TryReserveCubicleByReactiveController(out GameObject cubicle)
    {
        cubicle = null;
        exitRoute = ExitRoute.None;

        if (GWorld.Instance.GetFreeCubicleCount() > 0
            && CubicleReservation.TryReserveForPatient(gameObject, inventory, out cubicle))
        {
            reservedWing = ReservedWing.Right;
            return true;
        }

        if (GWorld.Instance.GetFreeCubicleLeftCount() > 0
            && CubicleReservation.TryReserveLeftForPatient(gameObject, inventory, out cubicle))
        {
            reservedWing = ReservedWing.Left;
            return true;
        }

        reservedWing = ReservedWing.None;
        return false;
    }

    bool TryReserveCubicleByDecisionTable(out GameObject cubicle)
    {
        cubicle = null;

        bool critical = IsCriticalPatient();
        bool rightFree = GWorld.Instance.GetFreeCubicleCount() > 0;
        bool leftFree = GWorld.Instance.GetFreeCubicleLeftCount() > 0;
        bool rightShortOpen = PathIsOpen(ExitRoute.CorridorA);
        bool leftShortOpen = PathIsOpen(ExitRoute.CorridorB);
        bool rightDetourOpen = PathIsOpen(ExitRoute.CorridorCToB);
        bool leftDetourOpen = PathIsOpen(ExitRoute.CorridorCToA);
        bool criticalQueued = GWorld.Instance.HasQueuedCriticalPatient();

        if (critical && criticalPrefersShortestPath)
        {
            if (rightFree && rightShortOpen && CubicleReservation.TryReserveForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Right;
                exitRoute = ExitRoute.CorridorA;
                return true;
            }

            if (leftFree && leftShortOpen && CubicleReservation.TryReserveLeftForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Left;
                exitRoute = ExitRoute.CorridorB;
                return true;
            }
        }

        bool preferRight = criticalQueued
            || !preferWingWithMoreFreeCubicles
            || GWorld.Instance.GetFreeCubicleCount() >= GWorld.Instance.GetFreeCubicleLeftCount();

        if (preferRight)
        {
            if (rightFree && rightShortOpen && CubicleReservation.TryReserveForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Right;
                exitRoute = ExitRoute.CorridorA;
                return true;
            }

            if (leftFree && leftShortOpen && CubicleReservation.TryReserveLeftForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Left;
                exitRoute = ExitRoute.CorridorB;
                return true;
            }

            if (rightFree && rightDetourOpen && CubicleReservation.TryReserveForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Right;
                exitRoute = ExitRoute.CorridorCToB;
                return true;
            }

            if (leftFree && leftDetourOpen && CubicleReservation.TryReserveLeftForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Left;
                exitRoute = ExitRoute.CorridorCToA;
                return true;
            }
        }
        else
        {
            if (leftFree && leftShortOpen && CubicleReservation.TryReserveLeftForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Left;
                exitRoute = ExitRoute.CorridorB;
                return true;
            }

            if (rightFree && rightShortOpen && CubicleReservation.TryReserveForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Right;
                exitRoute = ExitRoute.CorridorA;
                return true;
            }

            if (leftFree && leftDetourOpen && CubicleReservation.TryReserveLeftForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Left;
                exitRoute = ExitRoute.CorridorCToA;
                return true;
            }

            if (rightFree && rightDetourOpen && CubicleReservation.TryReserveForPatient(gameObject, inventory, out cubicle))
            {
                reservedWing = ReservedWing.Right;
                exitRoute = ExitRoute.CorridorCToB;
                return true;
            }
        }

        reservedWing = ReservedWing.None;
        exitRoute = ExitRoute.None;
        return false;
    }

    void ChooseCorridorAfterWing()
    {
        if (exitRoute != ExitRoute.None)
        {
            FollowAssignedRouteFromWing();
            return;
        }

        bool critical = IsCriticalPatient();

        if (reservedWing == ReservedWing.Right)
        {
            if ((!critical || criticalPrefersShortestPath) && TagIsAvailable(CorridorATag))
            {
                exitRoute = ExitRoute.CorridorA;
                ChangeState(BaselineState.ToCorridorA, "right short route");
                return;
            }

            if (TagIsAvailable(CorridorBTag))
            {
                exitRoute = ExitRoute.CorridorCToB;
                ChangeState(BaselineState.ToCorridorB, "right detour route");
                return;
            }
        }

        if (reservedWing == ReservedWing.Left)
        {
            if ((!critical || criticalPrefersShortestPath) && TagIsAvailable(CorridorBTag))
            {
                exitRoute = ExitRoute.CorridorB;
                ChangeState(BaselineState.ToCorridorB, "left short route");
                return;
            }

            if (TagIsAvailable(CorridorATag))
            {
                exitRoute = ExitRoute.CorridorCToA;
                ChangeState(BaselineState.ToCorridorA, "left detour route");
                return;
            }
        }

        RequestEmergencyFallbackIfRouteState("no open corridor after wing");
        LogWaiting("no open corridor after wing");
    }

    void FollowAssignedRouteFromWing()
    {
        switch (exitRoute)
        {
            case ExitRoute.CorridorA:
            case ExitRoute.CorridorCToA:
                if (TagIsAvailable(CorridorATag))
                    ChangeState(BaselineState.ToCorridorA, "assigned route " + exitRoute);
                else
                {
                    RequestEmergencyFallbackIfRouteState("assigned corridor A is closed");
                    LogWaiting("assigned corridor A is closed");
                }
                break;

            case ExitRoute.CorridorB:
            case ExitRoute.CorridorCToB:
                if (TagIsAvailable(CorridorBTag))
                    ChangeState(BaselineState.ToCorridorB, "assigned route " + exitRoute);
                else
                {
                    RequestEmergencyFallbackIfRouteState("assigned corridor B is closed");
                    LogWaiting("assigned corridor B is closed");
                }
                break;
        }
    }

    void RegisterWingEntryIfNeeded()
    {
        if (wingEntryRegistered)
            return;

        HospitalFlowController.RegisterWingEntry(gameObject);
        wingEntryRegistered = true;
    }

    void RollbackReservationBeforeWingEntry()
    {
        if (reservedWing == ReservedWing.None)
        {
            ChangeState(BaselineState.WaitingForQueue, "wing closed before reservation");
            return;
        }

        CubicleReservation.RollbackReservation(gameObject, inventory);
        reservedWing = ReservedWing.None;
        exitRoute = ExitRoute.None;
        wingEntryRegistered = false;
        wingExitRegistered = false;
        ChangeState(BaselineState.WaitingForQueue, "wing closed before entry");
    }

    void ContinueAfterCorridorA()
    {
        if (reservedWing == ReservedWing.Right)
        {
            ChangeState(BaselineState.ToCubicle, "right corridor A reached");
            return;
        }

        ChangeState(BaselineState.ToCorridorC, "left corridor A reached");
    }

    void ContinueAfterCorridorB()
    {
        if (reservedWing == ReservedWing.Left)
        {
            ChangeState(BaselineState.ToCubicle, "left corridor B reached");
            return;
        }

        ChangeState(BaselineState.ToCorridorC, "right corridor B reached");
    }

    void StartTreatment()
    {
        ResetPath();
        float resolvedTreatmentDuration = Patient.ResolveTreatmentDuration(gameObject, treatmentDuration);
        treatmentCompleteTime = Time.time + resolvedTreatmentDuration;
        GoapDiagnostics.Log(
            "BaselineAgent",
            "treatment started agent="
            + AgentLabel()
            + " duration="
            + resolvedTreatmentDuration.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        ChangeState(BaselineState.Treating, "cubicle reached");
    }

    void CompleteTreatment()
    {
        GWorld.Instance.GetWorld().ModifyState("Treeated", 1);

        if (reservedWing == ReservedWing.Left)
            CubicleReservation.ReleaseReservedCubicleLeft(inventory);
        else
            CubicleReservation.ReleaseReservedCubicle(inventory);

        SetBelief("isCured", 1);
        HospitalFlowController.RegisterWingTreatmentComplete(gameObject);
        ChangeState(BaselineState.ExitFirstCorridor, "treatment complete");
    }

    void ContinueAfterFirstExitCorridor()
    {
        if (exitRoute == ExitRoute.CorridorCToA || exitRoute == ExitRoute.CorridorCToB)
            ChangeState(BaselineState.ExitSecondCorridor, "exit corridor C reached");
        else
            ChangeState(BaselineState.ExitWing, "exit corridor reached");
    }

    string GetFirstExitCorridorTag()
    {
        switch (exitRoute)
        {
            case ExitRoute.CorridorA:
                return CorridorAExitTag;
            case ExitRoute.CorridorB:
                return CorridorBExitTag;
            case ExitRoute.CorridorCToA:
            case ExitRoute.CorridorCToB:
                return CorridorCExitTag;
            default:
                return string.Empty;
        }
    }

    string GetSecondExitCorridorTag()
    {
        switch (exitRoute)
        {
            case ExitRoute.CorridorCToA:
                return CorridorAExitTag;
            case ExitRoute.CorridorCToB:
                return CorridorBExitTag;
            default:
                return string.Empty;
        }
    }

    GameObject GetReservedCubicle()
    {
        if (reservedWing == ReservedWing.Left)
            return inventory.FindItemWithTag(CubicleReservation.CubicleLeftTag);

        return inventory.FindItemWithTag(CubicleReservation.CubicleTag);
    }

    bool MoveToTagAndArrived(string targetTag)
    {
        if (string.IsNullOrEmpty(targetTag))
        {
            LogWaiting("empty target tag");
            return false;
        }

        GameObject target = ResolveTaggedTargetForArchitecture(targetTag);
        if (target == null || !IsAvailableForArchitecture(target))
        {
            ClearCurrentTargetIfTag(targetTag);
            RequestEmergencyFallbackIfRouteState("target unavailable tag=" + targetTag);
            LogWaiting("target unavailable tag=" + targetTag);
            return false;
        }

        return MoveToTargetAndArrived(target, GetCompletionThresholdForTag(targetTag));
    }

    bool MoveToTargetAndArrived(GameObject target)
    {
        return MoveToTargetAndArrived(target, completionThreshold);
    }

    bool MoveToTargetAndArrived(GameObject target, float arrivalThreshold)
    {
        if (target == null || !IsAvailableForArchitecture(target))
        {
            ClearCurrentTargetIfTarget(target);
            RequestEmergencyFallbackIfRouteState("target unavailable object");
            LogWaiting("target unavailable object");
            return false;
        }

        if (currentTarget != target)
        {
            currentTarget = target;
            currentTargetTag = target.tag;
            waitingForTargetAvailability = false;

            if (!SetDestination(target))
            {
                RequestEmergencyFallbackIfRouteState("set destination failed target=" + TargetLabel(target));
                LogWaiting("set destination failed target=" + TargetLabel(target));
                currentTarget = null;
                currentTargetTag = null;
                return false;
            }

            GoapDiagnostics.Log(
                "BaselineAgent",
                "started agent="
                + AgentLabel()
                + " state="
                + state
                + " target="
                + TargetLabel(target));
        }
        else if (!navAgent.pathPending && !navAgent.hasPath && !ReachedTarget(target, arrivalThreshold))
        {
            if (Time.frameCount != lastDestinationRefreshFrame)
            {
                lastDestinationRefreshFrame = Time.frameCount;
                if (!SetDestination(target))
                {
                    RequestEmergencyFallbackIfRouteState("refresh destination failed target=" + TargetLabel(target));
                    LogWaiting("refresh destination failed target=" + TargetLabel(target));
                    return false;
                }

                GoapDiagnostics.LogThrottled(
                    "baseline-refresh-path-" + GetInstanceID(),
                    60,
                    "BaselineAgent",
                    "refreshed lost path agent="
                    + AgentLabel()
                    + " state="
                    + state
                    + " target="
                    + TargetLabel(target));
            }
        }

        return ReachedTarget(target, arrivalThreshold);
    }

    bool SetDestination(GameObject target)
    {
        if (target == null || navAgent == null)
            return false;

        ResetPath();
        hasCurrentDestination = false;
        Vector3 destination = GetSpreadDestination(target);
        if (!navAgent.SetDestination(destination))
            return false;

        currentDestination = destination;
        hasCurrentDestination = true;
        return true;
    }

    bool ReachedTarget(GameObject target)
    {
        return ReachedTarget(target, completionThreshold);
    }

    bool ReachedTarget(GameObject target, float arrivalThreshold)
    {
        if (target == null || navAgent == null)
            return false;

        if (navAgent.pathPending || navAgent.remainingDistance == Mathf.Infinity)
            return false;

        if (navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            return false;

        float arrivalDistance = Mathf.Max(1f, Mathf.Max(navAgent.stoppingDistance, arrivalThreshold));
        Vector3 agentPosition = navAgent.transform.position;
        Vector3 targetPosition = target.transform.position;
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

        return navAgent.hasPath && navAgent.remainingDistance <= arrivalDistance;
    }

    void RequestEmergencyFallbackIfRouteState(string reason)
    {
        // The baselines have no fallback rooms: a blocked patient stays in place and retries.
        if (IsPlainBaseline())
            return;

        if (!CurrentStateCanUseEmergencyFallback() && !emergencyFallbackRequested)
            return;

        emergencyFallbackRequested = true;
        emergencyFallbackReason = string.IsNullOrEmpty(reason) ? "fallback requested" : reason;

        GoapDiagnostics.LogThrottled(
            "baseline-emergency-fallback-request-" + GetInstanceID(),
            30,
            "EmergencyArea",
            "request agent="
            + AgentLabel()
            + " state="
            + state
            + " architecture="
            + controllerArchitecture
            + " reason="
            + emergencyFallbackReason);
    }

    bool CurrentStateCanUseEmergencyFallback()
    {
        switch (state)
        {
            case BaselineState.ToWing:
            case BaselineState.ToCorridorA:
            case BaselineState.ToCorridorB:
            case BaselineState.ToCorridorC:
            case BaselineState.ToCubicle:
            case BaselineState.ExitFirstCorridor:
            case BaselineState.ExitSecondCorridor:
            case BaselineState.ExitWing:
                return true;
            default:
                return false;
        }
    }

    bool CanResumeFromEmergencyFallback()
    {
        switch (state)
        {
            case BaselineState.WaitingForQueue:
                return GWorld.Instance.IsNextPatient(gameObject) && TagIsAvailable(WingTag);

            case BaselineState.ToWing:
                if (!wingEntryRegistered)
                    return TagIsAvailable(WingTag);

                if (exitRoute != ExitRoute.None)
                    return AssignedRouteStartIsAvailable();

                return AnyRouteFromWingIsAvailable();

            case BaselineState.ToCorridorA:
                return TagIsAvailable(CorridorATag);

            case BaselineState.ToCorridorB:
                return TagIsAvailable(CorridorBTag);

            case BaselineState.ToCorridorC:
                return TagIsAvailable(CorridorCTag);

            case BaselineState.ToCubicle:
                GameObject cubicle = GetReservedCubicle();
                return cubicle != null && cubicle.activeInHierarchy;

            case BaselineState.ExitFirstCorridor:
                return TagIsAvailable(GetFirstExitCorridorTag());

            case BaselineState.ExitSecondCorridor:
                return TagIsAvailable(GetSecondExitCorridorTag());

            case BaselineState.ExitWing:
                return TagIsAvailable(WingExitTag);

            default:
                return true;
        }
    }

    bool AssignedRouteStartIsAvailable()
    {
        switch (exitRoute)
        {
            case ExitRoute.CorridorA:
            case ExitRoute.CorridorCToA:
                return TagIsAvailable(CorridorATag);

            case ExitRoute.CorridorB:
            case ExitRoute.CorridorCToB:
                return TagIsAvailable(CorridorBTag);

            default:
                return false;
        }
    }

    bool AnyRouteFromWingIsAvailable()
    {
        if (reservedWing == ReservedWing.Right)
            return TagIsAvailable(CorridorATag) || TagIsAvailable(CorridorBTag);

        if (reservedWing == ReservedWing.Left)
            return TagIsAvailable(CorridorBTag) || TagIsAvailable(CorridorATag);

        return TagIsAvailable(CorridorATag) || TagIsAvailable(CorridorBTag);
    }

    void ClearEmergencyFallback(string reason)
    {
        if (!emergencyFallbackRequested && !emergencyFallbackRunning)
            return;

        GoapDiagnostics.Log(
            "EmergencyArea",
            "clear agent="
            + AgentLabel()
            + " state="
            + state
            + " architecture="
            + controllerArchitecture
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

        if (navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh)
            return false;

        if (!TargetIsAvailable(emergencyFallbackTarget) || !hasEmergencyFallbackDestination)
        {
            if (!TryFindNearestEmergencyArea(out emergencyFallbackTarget, out emergencyFallbackDestination))
            {
                GoapDiagnostics.LogThrottled(
                    "baseline-emergency-fallback-no-target-" + GetInstanceID(),
                    60,
                    "EmergencyArea",
                    "no active EmergencyArea found for agent="
                    + AgentLabel()
                    + " state="
                    + state
                    + " reason="
                    + emergencyFallbackReason);
                return false;
            }

            hasEmergencyFallbackDestination = true;
            emergencyFallbackRunning = false;
        }

        if (ReachedEmergencyFallbackTarget())
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

            lastEmergencyFallbackRefreshFrame = Time.frameCount;
            navAgent.ResetPath();
            if (navAgent.SetDestination(emergencyFallbackDestination))
            {
                emergencyFallbackRunning = true;
                GoapDiagnostics.Log(
                    "EmergencyArea",
                    "started agent="
                    + AgentLabel()
                    + " state="
                    + state
                    + " architecture="
                    + controllerArchitecture
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

    bool ReachedEmergencyFallbackTarget()
    {
        if (navAgent == null || !hasEmergencyFallbackDestination)
            return false;

        if (navAgent.pathPending || navAgent.remainingDistance == Mathf.Infinity)
            return false;

        if (navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            return false;

        float arrivalDistance = Mathf.Max(1f, Mathf.Max(navAgent.stoppingDistance, completionThreshold));
        Vector3 agentPosition = navAgent.transform.position;
        Vector2 agentXZ = new Vector2(agentPosition.x, agentPosition.z);
        Vector2 destinationXZ = new Vector2(emergencyFallbackDestination.x, emergencyFallbackDestination.z);

        if (Vector2.Distance(agentXZ, destinationXZ) <= arrivalDistance)
            return true;

        return navAgent.hasPath && navAgent.remainingDistance <= arrivalDistance;
    }

    bool TryFindNearestEmergencyArea(out GameObject nearestArea, out Vector3 destination)
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

            Vector3 candidateDestination = GetEmergencyAreaDestination(area);
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

    Vector3 GetEmergencyAreaDestination(GameObject area)
    {
        Vector3 targetPosition = area.transform.position;
        float spreadRadius = Mathf.Max(GetMinimumSpreadRadius(EmergencyAreaTag), navAgent != null ? navAgent.radius * 2f : 1f);

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

    float GetCompletionThresholdForTag(string targetTag)
    {
        if (targetTag == EntranceTag)
            return Mathf.Max(completionThreshold, entranceCompletionThreshold);

        if (targetTag == WaitingRoomTag)
            return Mathf.Max(completionThreshold, waitingRoomCompletionThreshold);

        return completionThreshold;
    }

    Vector3 GetSpreadDestination(GameObject target)
    {
        Vector3 targetPosition = target.transform.position;
        float spreadRadius = Mathf.Max(
            GetMinimumSpreadRadius(target.tag),
            navAgent != null ? navAgent.radius * 2f : 1f);

        Collider targetCollider = target.GetComponent<Collider>();
        if (targetCollider != null)
        {
            Vector3 extents = targetCollider.bounds.extents;
            float colliderRadius = Mathf.Min(extents.x, extents.z) * 0.65f;
            spreadRadius = Mathf.Max(spreadRadius, colliderRadius);
        }

        int seed = gameObject.GetInstanceID() ^ target.GetInstanceID();
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
            case EntranceTag:
            case WaitingRoomTag:
                return 6f;
            case WingTag:
            case WingExitTag:
            case EmergencyAreaTag:
                return 5f;
            case CorridorATag:
            case CorridorBTag:
            case CorridorCTag:
            case CorridorAExitTag:
            case CorridorBExitTag:
            case CorridorCExitTag:
                return 4f;
            default:
                return 1f;
        }
    }

    // The baselines (FSM, priority-queue dispatcher, decision table) do not replan: no
    // environment-mediated priority access (closed routes are closed for critical patients too), no
    // reservation rollback and no fallback rooms - once dispatched, a patient keeps its cubicle and
    // waits in place until its next target opens. Only the proposed GOAP replanner has
    // priority-aware access and local replanning.
    bool IsPlainBaseline()
    {
        return controllerArchitecture == TriageExperimentArchitecture.FsmReactiveController
            || controllerArchitecture == TriageExperimentArchitecture.PriorityQueueDispatcher
            || controllerArchitecture == TriageExperimentArchitecture.DecisionTableController;
    }

    GameObject ResolveTaggedTargetForArchitecture(string targetTag)
    {
        if (string.IsNullOrEmpty(targetTag))
            return null;

        return GameObject.FindWithTag(targetTag);
    }

    bool IsAvailableForArchitecture(GameObject target)
    {
        if (target == null)
            return false;

        return target.activeInHierarchy;
    }

    bool TagIsAvailable(string targetTag)
    {
        if (string.IsNullOrEmpty(targetTag))
            return false;

        GameObject target = ResolveTaggedTargetForArchitecture(targetTag);
        return target != null && IsAvailableForArchitecture(target);
    }

    bool TargetIsAvailable(GameObject target)
    {
        return target != null && IsAvailableForArchitecture(target);
    }

    bool PathIsOpen(ExitRoute route)
    {
        switch (route)
        {
            case ExitRoute.CorridorA:
                return TagIsAvailable(CorridorATag);
            case ExitRoute.CorridorB:
                return TagIsAvailable(CorridorBTag);
            case ExitRoute.CorridorCToA:
                return TagIsAvailable(CorridorATag) && TagIsAvailable(CorridorCTag);
            case ExitRoute.CorridorCToB:
                return TagIsAvailable(CorridorBTag) && TagIsAvailable(CorridorCTag);
            default:
                return false;
        }
    }

    bool IsCriticalPatient()
    {
        return gameObject.CompareTag(CriticalPatientTag);
    }

    void SetBelief(string key, int value)
    {
        if (beliefs == null)
            return;

        beliefs.SetState(key, value);
    }

    void ChangeState(BaselineState nextState, string reason)
    {
        if (state == nextState)
            return;

        BaselineState previousState = state;
        state = nextState;
        currentTarget = null;
        currentTargetTag = null;
        hasCurrentDestination = false;
        waitingForTargetAvailability = false;

        GoapDiagnostics.Log(
            "BaselineAgent",
            "state agent="
            + AgentLabel()
            + " "
            + previousState
            + "->"
            + nextState
            + " reason="
            + reason
            + " wing="
            + reservedWing
            + " exitRoute="
            + exitRoute);

        if (logStateChanges)
            Debug.Log("Baseline " + gameObject.name + ": " + previousState + " -> " + nextState + " (" + reason + ")");
    }

    void ClearCurrentTargetIfTag(string targetTag)
    {
        if (currentTargetTag != targetTag)
            return;

        ResetPath();
        currentTarget = null;
        currentTargetTag = null;
        hasCurrentDestination = false;
        waitingForTargetAvailability = true;
    }

    void ClearCurrentTargetIfTarget(GameObject target)
    {
        if (currentTarget != target)
            return;

        ResetPath();
        currentTarget = null;
        currentTargetTag = null;
        hasCurrentDestination = false;
        waitingForTargetAvailability = true;
    }

    void ResetPath()
    {
        if (navAgent != null && navAgent.enabled && navAgent.isOnNavMesh)
            navAgent.ResetPath();

        hasCurrentDestination = false;
    }

    void LogWaitingForQueue()
    {
        // Runs every frame for every waiting patient. Describing the whole queue is string building
        // of O(queue length) (O(queue^2) work per frame), so it is skipped when diagnostics are off;
        // LogWaiting keeps its state side effects and the simulation never reads the message.
        if (!GoapDiagnostics.IsActive)
        {
            LogWaiting(string.Empty);
            return;
        }

        LogWaiting(
            "not first in queue first="
            + TargetLabel(GWorld.Instance.PeekPatient())
            + " queue="
            + GWorld.Instance.GetPatientQueueDebugString());
    }

    void LogWaiting(string reason)
    {
        if (Time.frameCount == lastWaitingLogFrame)
            return;

        lastWaitingLogFrame = Time.frameCount;
        waitingForTargetAvailability = true;

        if (!GoapDiagnostics.IsActive)
            return;

        GoapDiagnostics.LogThrottled(
            "baseline-wait-" + GetInstanceID() + "-" + state,
            60,
            "BaselineAgent",
            "waiting agent="
            + AgentLabel()
            + " state="
            + state
            + " architecture="
            + controllerArchitecture
            + " waitingForTarget="
            + waitingForTargetAvailability
            + " reason="
            + reason);
    }

    string AgentLabel()
    {
        return gameObject.name + "[" + gameObject.tag + "]";
    }

    static string TargetLabel(GameObject target)
    {
        if (target == null)
            return "<null>";

        return target.name + "[" + target.tag + "] active=" + target.activeInHierarchy;
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
}
