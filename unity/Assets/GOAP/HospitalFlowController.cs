using System.Collections.Generic;
using UnityEngine;

public class HospitalFlowController : MonoBehaviour
{
    enum FlowMode
    {
        Admission,
        Release
    }

    static readonly HashSet<GameObject> patientsInsideWing = new HashSet<GameObject>();
    static readonly HashSet<GameObject> countedCuredPatients = new HashSet<GameObject>();
    static readonly List<HospitalFlowController> activeControllers = new List<HospitalFlowController>();
    const string CriticalPatientTag = "critical";
    const string IsCuredState = "isCured";

    [SerializeField] TargetAvailabilityManager availabilityManager;
    [SerializeField] bool automaticFlowEnabled = true;
    [SerializeField] int wingCapacityBeforeRelease = 15;
    [SerializeField] int resumeAdmissionAtOrBelow = 0;
    [SerializeField] bool releaseWhenNoCriticalQueued = true;
    [SerializeField] bool hardReleaseOverridesCriticalFlow = true;
    [SerializeField] int minimumAdmissionFrames = 120;
    [SerializeField] int minimumFramesBetweenSoftSwitches = 240;
    [SerializeField] int maxReleaseFrames = 0;
    [SerializeField] float maxReleaseSeconds = 30f;
    [SerializeField] bool releaseTimeoutForcesAdmission = true;
    [SerializeField] float releaseTimeoutAdmissionCooldownSeconds = 30f;
    [SerializeField] bool triggerForceAdmission;
    [SerializeField] bool triggerForceRelease;
    [SerializeField] bool logFlowChanges = false;
    [SerializeField] FlowMode currentMode = FlowMode.Admission;
    [SerializeField] int patientsInsideWingCount;
    [SerializeField] int curedPatientsInsideWingCount;
    [SerializeField] int uncuredPatientsInsideWingCount;
    [SerializeField] int criticalPatientsInsideWingCount;
    [SerializeField] int activeCriticalPatientsInsideWingCount;
    [SerializeField] int queuedPatientCount;
    [SerializeField] int queuedCriticalPatientCount;
    [SerializeField] int freeCubicleCount;
    [SerializeField] int freeCubicleLeftCount;
    [SerializeField] int releaseStartedFrame = -1;
    [SerializeField] float releaseStartedTime = -1f;
    [SerializeField] int admissionStartedFrame = -1;
    [SerializeField] bool releaseStartedByCapacity;
    [SerializeField] int patientsEnteredSinceLastExit;
    [SerializeField] int patientsCuredSinceLastExit;
    [SerializeField] int releaseTargetExitCount;
    [SerializeField] int releaseCuredExitCount;
    [SerializeField] int flowSwitchCount;
    [SerializeField] int admissionSwitchCount;
    [SerializeField] int releaseSwitchCount;
    [SerializeField] int suppressedSoftSwitchCount;
    [SerializeField] int releaseTimeoutFallbackCount;
    [SerializeField] float releaseCooldownRemainingSeconds;
    [SerializeField] float releaseTimeoutCooldownUntilTime = -1f;
    [SerializeField] int lastFlowSwitchFrame = -1;
    [SerializeField] int framesSinceLastFlowSwitch;
    [SerializeField] string lastFlowSwitchReason;

    public static bool AutomaticFlowIsActive
    {
        get
        {
            for (int i = activeControllers.Count - 1; i >= 0; i--)
            {
                HospitalFlowController controller = activeControllers[i];
                if (controller == null)
                {
                    activeControllers.RemoveAt(i);
                    continue;
                }

                if (controller.isActiveAndEnabled && controller.automaticFlowEnabled)
                    return true;
            }

            return false;
        }
    }

    public static void ResetStaticStateForExperiment()
    {
        patientsInsideWing.Clear();
        countedCuredPatients.Clear();
        activeControllers.Clear();
    }

    public static int GetPatientsInsideWingCount()
    {
        CleanupMissingPatients();
        return patientsInsideWing.Count;
    }

    public static int GetCuredPatientsInsideWingCount()
    {
        CleanupMissingPatients();
        return CountCuredPatientsInsideWing();
    }

    public static int GetUncuredPatientsInsideWingCount()
    {
        CleanupMissingPatients();
        return Mathf.Max(0, patientsInsideWing.Count - CountCuredPatientsInsideWing());
    }

    public static void RegisterWingEntry(GameObject patient)
    {
        if (patient == null)
            return;

        CleanupMissingPatients();

        if (patientsInsideWing.Add(patient))
        {
            NotifyWingEntry(patient);
            TriageExperimentMetrics.RecordWingEntry(patient);
            WorldStates.IncrementWorldStateChangeCounter(
                "wing entry patient=" + GetPatientName(patient) + " inside=" + patientsInsideWing.Count);
            GoapDiagnostics.Log(
                "FlowWing",
                "entry patient=" + GetPatientName(patient) + " inside=" + patientsInsideWing.Count);
        }
    }

    public static void RegisterWingExit(GameObject patient)
    {
        if (patient == null)
            return;

        bool patientWasCured = PatientHasBelief(patient, IsCuredState);
        CleanupMissingPatients();

        if (patientsInsideWing.Remove(patient))
        {
            countedCuredPatients.Remove(patient);
            NotifyWingExit(patient, patientWasCured);
            TriageExperimentMetrics.RecordWingExit(patient);
            WorldStates.IncrementWorldStateChangeCounter(
                "wing exit patient=" + GetPatientName(patient) + " inside=" + patientsInsideWing.Count);
            GoapDiagnostics.Log(
                "FlowWing",
                "exit patient=" + GetPatientName(patient) + " inside=" + patientsInsideWing.Count);
        }
    }

    public static void RegisterWingTreatmentComplete(GameObject patient)
    {
        if (patient == null)
            return;

        CleanupMissingPatients();

        if (!patientsInsideWing.Contains(patient))
        {
            GoapDiagnostics.Log(
                "FlowWing",
                "treatment complete outside wing patient=" + GetPatientName(patient));
            return;
        }

        if (!countedCuredPatients.Add(patient))
            return;

        TriageExperimentMetrics.RecordTreatmentComplete(patient);

        for (int i = activeControllers.Count - 1; i >= 0; i--)
        {
            HospitalFlowController controller = activeControllers[i];
            if (controller == null)
            {
                activeControllers.RemoveAt(i);
                continue;
            }

            controller.OnWingTreatmentComplete(patient);
        }
    }

    void Awake()
    {
        if (availabilityManager == null)
            availabilityManager = GetComponent<TargetAvailabilityManager>();
    }

    void OnEnable()
    {
        if (!activeControllers.Contains(this))
            activeControllers.Add(this);

        if (currentMode == FlowMode.Admission && admissionStartedFrame < 0)
            admissionStartedFrame = Time.frameCount;

        ApplyCurrentModeAvailability();
    }

    void OnDisable()
    {
        activeControllers.Remove(this);
    }

    void Update()
    {
        RefreshDebugValues();
        framesSinceLastFlowSwitch = lastFlowSwitchFrame < 0 ? -1 : Time.frameCount - lastFlowSwitchFrame;

        if (triggerForceAdmission)
        {
            triggerForceAdmission = false;
            ForceAdmissionMode();
        }

        if (triggerForceRelease)
        {
            triggerForceRelease = false;
            ForceReleaseMode();
        }

        if (!automaticFlowEnabled)
            return;

        UpdateAutomaticFlow();
    }

    [ContextMenu("Force Admission Mode")]
    public void ForceAdmissionMode()
    {
        SetAdmissionMode("manual", true);
    }

    [ContextMenu("Force Release Mode")]
    public void ForceReleaseMode()
    {
        SetReleaseMode("manual hard release", true, true);
    }

    void UpdateAutomaticFlow()
    {
        if (currentMode == FlowMode.Release)
        {
            bool targetExited = releaseTargetExitCount <= 0 || releaseCuredExitCount >= releaseTargetExitCount;
            bool noCuredPatientsRemain = curedPatientsInsideWingCount <= Mathf.Max(0, resumeAdmissionAtOrBelow);

            if (targetExited || noCuredPatientsRemain)
            {
                GoapDiagnostics.Log(
                    "Flow",
                    "release finished. targetExited="
                    + targetExited
                    + " noCuredPatientsRemain="
                    + noCuredPatientsRemain
                    + " releaseExited="
                    + releaseCuredExitCount
                    + "/"
                    + releaseTargetExitCount
                    + " curedInside="
                    + curedPatientsInsideWingCount
                    + " inside="
                    + patientsInsideWingCount);
                SetAdmissionMode("cured batch released", true);
                return;
            }
            else
            {
                bool overExpectedTime = ReleaseExceededExpectedTime();

                if (overExpectedTime && releaseTimeoutForcesAdmission)
                {
                    releaseTimeoutFallbackCount++;
                    releaseTimeoutCooldownUntilTime = Time.time
                        + Mathf.Max(0f, releaseTimeoutAdmissionCooldownSeconds);

                    GoapDiagnostics.Log(
                        "Flow",
                        "release timeout fallback to admission. releaseExited="
                        + releaseCuredExitCount
                        + "/"
                        + releaseTargetExitCount
                        + " curedInside="
                        + curedPatientsInsideWingCount
                        + " uncuredInside="
                        + uncuredPatientsInsideWingCount
                        + " criticalQueued="
                        + queuedCriticalPatientCount
                        + " cooldownUntil="
                        + releaseTimeoutCooldownUntilTime.ToString("F2"));
                    SetAdmissionMode("release timeout fallback", true);
                    return;
                }

                GoapDiagnostics.LogThrottled(
                    "hard-release-waiting",
                    overExpectedTime ? 30 : 120,
                    "Flow",
                    "release waiting. overExpectedTime="
                    + overExpectedTime
                    + " releaseExited="
                    + releaseCuredExitCount
                    + "/"
                    + releaseTargetExitCount
                    + " curedInside="
                    + curedPatientsInsideWingCount
                    + " uncuredInside="
                    + uncuredPatientsInsideWingCount
                    + " criticalQueued="
                    + queuedCriticalPatientCount);
            }

            return;
        }

        int releaseThreshold = Mathf.Max(1, wingCapacityBeforeRelease);
        bool overCapacity = patientsInsideWingCount >= releaseThreshold;
        bool hasPatientsReadyToExit = curedPatientsInsideWingCount > 0;
        bool noQueuedPatients = queuedPatientCount <= 0;
        bool countedBatchReady = patientsEnteredSinceLastExit >= releaseThreshold && patientsCuredSinceLastExit > 0;
        bool criticalFlowActive = CriticalFlowIsActive();
        bool hardReleaseNeeded = hardReleaseOverridesCriticalFlow
            && hasPatientsReadyToExit
            && (overCapacity || countedBatchReady);

        if (ReleaseTimeoutCooldownBlocksRelease())
        {
            GoapDiagnostics.LogThrottled(
                "release-timeout-cooldown",
                120,
                "Flow",
                "release cooldown holds admission. remaining="
                + releaseCooldownRemainingSeconds.ToString("F2")
                + " uncuredInside="
                + uncuredPatientsInsideWingCount
                + " curedInside="
                + curedPatientsInsideWingCount
                + " queued="
                + queuedPatientCount);
            return;
        }

        if (hardReleaseNeeded)
        {
            SetReleaseMode("hard release overrides critical flow", true, true);
            return;
        }

        if (criticalFlowActive)
            return;

        bool idleCuredBatchReady = releaseWhenNoCriticalQueued
            && patientsCuredSinceLastExit > 0
            && patientsEnteredSinceLastExit > 0
            && admissionStartedFrame >= 0
            && Time.frameCount - admissionStartedFrame >= Mathf.Max(0, minimumAdmissionFrames)
            && patientsEnteredSinceLastExit >= Mathf.Min(releaseThreshold, Mathf.Max(1, patientsCuredSinceLastExit));

        if (noQueuedPatients && hasPatientsReadyToExit)
        {
            SetReleaseMode("no queued patients", true, true);
            return;
        }

        if (overCapacity && hasPatientsReadyToExit)
        {
            SetReleaseMode("wing capacity cured batch", true, true);
            return;
        }

        if (countedBatchReady)
        {
            SetReleaseMode("entered/cured batch ready", true, true);
            return;
        }

        if (idleCuredBatchReady)
        {
            SetReleaseMode("idle cured batch ready", true, true);
            return;
        }

        if (overCapacity && !hasPatientsReadyToExit)
        {
            GoapDiagnostics.LogThrottled(
                "over-capacity-no-cured",
                30,
                "Flow",
                "over capacity but nobody can exit yet. inside="
                + patientsInsideWingCount
                + " uncuredInside="
                + uncuredPatientsInsideWingCount
                + " criticalQueued="
                + queuedCriticalPatientCount
                + " criticalFlowActive="
                + criticalFlowActive);
        }
    }

    void SetAdmissionMode(string reason)
    {
        SetAdmissionMode(reason, false);
    }

    void SetAdmissionMode(string reason, bool forceApply)
    {
        if (!forceApply && currentMode == FlowMode.Admission)
            return;

        if (!forceApply && !CanSoftSwitchNow())
        {
            suppressedSoftSwitchCount++;
            GoapDiagnostics.LogThrottled(
                "admission-soft-switch-suppressed",
                30,
                "Flow",
                "admission switch suppressed reason="
                + reason
                + " framesSinceLastSwitch="
                + framesSinceLastFlowSwitch);
            return;
        }

        currentMode = FlowMode.Admission;
        releaseStartedFrame = -1;
        releaseStartedTime = -1f;
        admissionStartedFrame = Time.frameCount;
        releaseStartedByCapacity = false;
        releaseTargetExitCount = 0;
        releaseCuredExitCount = 0;
        ApplyAdmissionAvailability();
        RegisterFlowSwitch(reason, FlowMode.Admission);
        LogModeChange(reason);
    }

    void SetReleaseMode(string reason, bool byCapacity)
    {
        SetReleaseMode(reason, byCapacity, false);
    }

    void SetReleaseMode(string reason, bool byCapacity, bool forceApply)
    {
        if (!forceApply && currentMode == FlowMode.Release && releaseStartedByCapacity == byCapacity)
            return;

        if (!forceApply && !CanSoftSwitchNow())
        {
            suppressedSoftSwitchCount++;
            GoapDiagnostics.LogThrottled(
                "release-soft-switch-suppressed",
                30,
                "Flow",
                "release switch suppressed reason="
                + reason
                + " framesSinceLastSwitch="
                + framesSinceLastFlowSwitch);
            return;
        }

        currentMode = FlowMode.Release;
        releaseStartedFrame = Time.frameCount;
        releaseStartedTime = Time.time;
        releaseStartedByCapacity = byCapacity;
        releaseTargetExitCount = Mathf.Max(1, curedPatientsInsideWingCount);
        releaseCuredExitCount = 0;
        ApplyReleaseAvailability();
        RegisterFlowSwitch(reason, FlowMode.Release);
        LogModeChange(reason);
    }

    void SetReleaseMode(string reason)
    {
        SetReleaseMode(reason, false);
    }

    void ApplyAdmissionAvailability()
    {
        if (availabilityManager == null)
            return;

        // Admission mode opens every entry node. Exit nodes become inactive through TargetAvailabilityManager pairs.
        availabilityManager.SetAllTargetsAvailable(true, "flow admission");
    }

    void ApplyReleaseAvailability()
    {
        if (availabilityManager == null)
            return;

        // Release mode closes every entry node. Exit nodes become active through TargetAvailabilityManager pairs.
        availabilityManager.SetAllTargetsAvailable(false, "flow release");
    }

    void ApplyCurrentModeAvailability()
    {
        if (currentMode == FlowMode.Release)
            ApplyReleaseAvailability();
        else
            ApplyAdmissionAvailability();
    }

    static void NotifyWingEntry(GameObject patient)
    {
        for (int i = activeControllers.Count - 1; i >= 0; i--)
        {
            HospitalFlowController controller = activeControllers[i];
            if (controller == null)
            {
                activeControllers.RemoveAt(i);
                continue;
            }

            controller.OnWingEntry(patient);
        }
    }

    static void NotifyWingExit(GameObject patient, bool patientWasCured)
    {
        for (int i = activeControllers.Count - 1; i >= 0; i--)
        {
            HospitalFlowController controller = activeControllers[i];
            if (controller == null)
            {
                activeControllers.RemoveAt(i);
                continue;
            }

            controller.OnWingExit(patient, patientWasCured);
        }
    }

    void OnWingEntry(GameObject patient)
    {
        patientsEnteredSinceLastExit++;
        GoapDiagnostics.Log(
            "FlowCounter",
            "wing entry counted patient="
            + GetPatientName(patient)
            + " enteredSinceLastExit="
            + patientsEnteredSinceLastExit
            + " curedSinceLastExit="
            + patientsCuredSinceLastExit);
    }

    void OnWingExit(GameObject patient, bool patientWasCured)
    {
        if (currentMode == FlowMode.Release && patientWasCured)
            releaseCuredExitCount++;

        GoapDiagnostics.Log(
            "FlowCounter",
            "wing exit counted patient="
            + GetPatientName(patient)
            + " cured="
            + patientWasCured
            + " releaseExited="
            + releaseCuredExitCount
            + "/"
            + releaseTargetExitCount
            + ". admission counters reset.");

        patientsEnteredSinceLastExit = 0;
        patientsCuredSinceLastExit = 0;
    }

    void OnWingTreatmentComplete(GameObject patient)
    {
        patientsCuredSinceLastExit++;

        if (currentMode == FlowMode.Release)
            releaseTargetExitCount++;

        GoapDiagnostics.Log(
            "FlowCounter",
            "treatment counted patient="
            + GetPatientName(patient)
            + " enteredSinceLastExit="
            + patientsEnteredSinceLastExit
            + " curedSinceLastExit="
            + patientsCuredSinceLastExit
            + " releaseTarget="
            + releaseTargetExitCount);
    }

    void RefreshDebugValues()
    {
        CleanupMissingPatients();

        patientsInsideWingCount = patientsInsideWing.Count;
        curedPatientsInsideWingCount = CountCuredPatientsInsideWing();
        uncuredPatientsInsideWingCount = Mathf.Max(0, patientsInsideWingCount - curedPatientsInsideWingCount);
        criticalPatientsInsideWingCount = CountCriticalPatientsInsideWing();
        activeCriticalPatientsInsideWingCount = CountActiveCriticalPatientsInsideWing();
        queuedPatientCount = GWorld.Instance.GetQueuedPatientCount();
        queuedCriticalPatientCount = GWorld.Instance.GetQueuedCriticalPatientCount();
        freeCubicleCount = GWorld.Instance.GetFreeCubicleCount();
        freeCubicleLeftCount = GWorld.Instance.GetFreeCubicleLeftCount();
        releaseCooldownRemainingSeconds = Mathf.Max(0f, releaseTimeoutCooldownUntilTime - Time.time);
    }

    bool CriticalFlowIsActive()
    {
        return queuedCriticalPatientCount > 0 || activeCriticalPatientsInsideWingCount > 0;
    }

    bool ReleaseExceededExpectedTime()
    {
        bool exceededSeconds = maxReleaseSeconds > 0f
            && releaseStartedTime >= 0f
            && Time.time - releaseStartedTime >= maxReleaseSeconds;

        bool exceededFrames = maxReleaseFrames > 0
            && releaseStartedFrame >= 0
            && Time.frameCount - releaseStartedFrame >= maxReleaseFrames;

        return exceededSeconds || exceededFrames;
    }

    bool ReleaseTimeoutCooldownBlocksRelease()
    {
        return releaseTimeoutCooldownUntilTime > Time.time
            && uncuredPatientsInsideWingCount > 0;
    }

    bool CanSoftSwitchNow()
    {
        return lastFlowSwitchFrame < 0
            || Time.frameCount - lastFlowSwitchFrame >= Mathf.Max(0, minimumFramesBetweenSoftSwitches);
    }

    void RegisterFlowSwitch(string reason, FlowMode mode)
    {
        flowSwitchCount++;
        lastFlowSwitchFrame = Time.frameCount;
        framesSinceLastFlowSwitch = 0;
        lastFlowSwitchReason = reason;

        if (mode == FlowMode.Release)
            releaseSwitchCount++;
        else
            admissionSwitchCount++;

        GoapDiagnostics.Log(
            "FlowSwitch",
            "mode="
            + mode
            + " reason="
            + reason
            + " inside="
            + patientsInsideWingCount
            + " curedInside="
            + curedPatientsInsideWingCount
            + " uncuredInside="
            + uncuredPatientsInsideWingCount
            + " queued="
            + queuedPatientCount
            + " criticalQueued="
            + queuedCriticalPatientCount
            + " activeCriticalInside="
            + activeCriticalPatientsInsideWingCount
            + " enteredSinceLastExit="
            + patientsEnteredSinceLastExit
            + " curedSinceLastExit="
            + patientsCuredSinceLastExit
            + " releaseExited="
            + releaseCuredExitCount
            + "/"
            + releaseTargetExitCount
            + " switchCount="
            + flowSwitchCount);
    }

    static int CountCriticalPatientsInsideWing()
    {
        int criticalCount = 0;
        foreach (GameObject patient in patientsInsideWing)
        {
            if (patient != null && patient.tag == CriticalPatientTag)
                criticalCount++;
        }

        return criticalCount;
    }

    static int CountActiveCriticalPatientsInsideWing()
    {
        int criticalCount = 0;
        foreach (GameObject patient in patientsInsideWing)
        {
            if (patient != null && patient.tag == CriticalPatientTag && !PatientHasBelief(patient, IsCuredState))
                criticalCount++;
        }

        return criticalCount;
    }

    static int CountCuredPatientsInsideWing()
    {
        int curedCount = 0;
        foreach (GameObject patient in patientsInsideWing)
        {
            if (patient != null && PatientHasBelief(patient, IsCuredState))
                curedCount++;
        }

        return curedCount;
    }

    static bool PatientHasBelief(GameObject patient, string state)
    {
        GAgent agent = patient.GetComponent<GAgent>();
        return agent != null && agent.beliefs.HasState(state);
    }

    static string GetPatientName(GameObject patient)
    {
        if (patient == null)
            return "<null>";

        return patient.name + "[" + patient.tag + "]";
    }

    void LogModeChange(string reason)
    {
        if (!logFlowChanges)
            return;

        Debug.Log(
            "Hospital flow mode: "
            + currentMode
            + " ("
            + reason
            + "), inside wing: "
            + patientsInsideWingCount
            + ", critical queued: "
            + queuedCriticalPatientCount
            + ", free cubicles: "
            + freeCubicleCount
            + "/"
            + freeCubicleLeftCount);
    }

    static void CleanupMissingPatients()
    {
        patientsInsideWing.RemoveWhere(patient => patient == null);
        countedCuredPatients.RemoveWhere(patient => patient == null);
    }
}
