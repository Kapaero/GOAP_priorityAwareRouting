using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public enum TriageExperimentArchitecture
{
    FsmReactiveController = 0,
    PriorityQueueDispatcher = 1,
    DecisionTableController = 2,
    ProposedEnvironmentMediatedReplanner = 3,
    QLearningDispatcher = 4,
    DeepQLearningDispatcher = 5
}

public class TriageExperimentMode : MonoBehaviour
{
    static readonly List<TriageExperimentMode> activeControllers = new List<TriageExperimentMode>();

    [SerializeField] TriageExperimentArchitecture architecture = TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner;
    [SerializeField] bool applyToExistingPatients = true;
    [SerializeField] bool configureSpawnedPatients = true;
    [SerializeField] int refreshExistingPatientsEveryFrames = 30;
    [SerializeField] bool triggerApplyModeNow;
    [SerializeField] bool logModeChanges = false;
    [SerializeField] int configuredPatientCount;
    [SerializeField] int fsmReactivePatientCount;
    [SerializeField] int priorityDispatcherPatientCount;
    [SerializeField] int decisionTablePatientCount;
    [SerializeField] int goapPatientCount;
    [SerializeField] int qLearningPatientCount;

    int lastRefreshFrame = -1;

    public static TriageExperimentArchitecture ActiveArchitecture
    {
        get
        {
            TriageExperimentMode controller = GetActiveController();
            return controller != null
                ? controller.architecture
                : TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner;
        }
    }

    public void SetArchitecture(TriageExperimentArchitecture nextArchitecture, bool applyNow)
    {
        architecture = nextArchitecture;

        if (applyNow)
            ApplyModeToExistingPatients();
    }

    public void SetApplyToExistingPatients(bool enabled)
    {
        applyToExistingPatients = enabled;
    }

    void OnEnable()
    {
        if (!activeControllers.Contains(this))
            activeControllers.Add(this);

        ApplyModeToExistingPatients();
    }

    void OnDisable()
    {
        activeControllers.Remove(this);
    }

    void Update()
    {
        if (triggerApplyModeNow)
        {
            triggerApplyModeNow = false;
            ApplyModeToExistingPatients();
        }

        if (!applyToExistingPatients)
            return;

        int refreshFrames = Mathf.Max(1, refreshExistingPatientsEveryFrames);
        if (lastRefreshFrame >= 0 && Time.frameCount - lastRefreshFrame < refreshFrames)
            return;

        ApplyModeToExistingPatients();
    }

    public static void ConfigureSpawnedPatient(GameObject patient)
    {
        TriageExperimentMode controller = GetActiveController();
        if (controller == null || !controller.configureSpawnedPatients)
            return;

        ConfigurePatient(patient, controller.architecture);
    }

    public static void ConfigureSpawnedPatient(GameObject patient, TriageExperimentArchitecture architecture)
    {
        ConfigureSpawnedPatient(patient, architecture, false);
    }

    public static void ConfigureSpawnedPatient(
        GameObject patient,
        TriageExperimentArchitecture architecture,
        bool lockArchitecture)
    {
        ConfigurePatient(patient, architecture, lockArchitecture);
    }

    [ContextMenu("Apply Mode To Existing Patients")]
    public void ApplyModeToExistingPatients()
    {
        lastRefreshFrame = Time.frameCount;
        configuredPatientCount = 0;
        fsmReactivePatientCount = 0;
        priorityDispatcherPatientCount = 0;
        decisionTablePatientCount = 0;
        goapPatientCount = 0;
        qLearningPatientCount = 0;

        Patient[] patients = FindObjectsByType<Patient>(FindObjectsSortMode.None);
        foreach (Patient patient in patients)
        {
            if (patient == null)
                continue;

            TriageExperimentArchitecture patientArchitecture = ResolvePatientArchitecture(patient.gameObject);
            ConfigurePatient(patient.gameObject, patientArchitecture);
            CountConfiguredPatientArchitecture(patient.gameObject, patientArchitecture);
        }
    }

    public static void ConfigurePatient(GameObject patientObject, TriageExperimentArchitecture architecture)
    {
        ConfigurePatient(patientObject, architecture, false);
    }

    public static void ConfigurePatient(
        GameObject patientObject,
        TriageExperimentArchitecture architecture,
        bool lockArchitecture)
    {
        if (patientObject == null)
            return;

        Patient goapPatient = patientObject.GetComponent<Patient>();
        if (goapPatient == null)
            return;

        if (lockArchitecture)
            TriageAgentArchitecture.SetLockedArchitecture(patientObject, architecture);

        TriageBaselineAgent baselineAgent = patientObject.GetComponent<TriageBaselineAgent>();
        bool useGoap = architecture == TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner;
        if (!useGoap && baselineAgent == null)
            baselineAgent = patientObject.AddComponent<TriageBaselineAgent>();

        if (!useGoap)
        {
            if (goapPatient.enabled || goapPatient.currentAction != null)
                StopGoapAgent(goapPatient);

            goapPatient.enabled = false;

            if (baselineAgent != null)
            {
                baselineAgent.ConfigureController(architecture);
                baselineAgent.enabled = true;
            }
        }
        else
        {
            if (baselineAgent != null)
                baselineAgent.enabled = false;

            if (!goapPatient.enabled)
                goapPatient.enabled = true;
        }
    }

    static TriageExperimentArchitecture ResolvePatientArchitecture(GameObject patientObject)
    {
        TriageAgentArchitecture marker = patientObject != null
            ? patientObject.GetComponent<TriageAgentArchitecture>()
            : null;

        if (marker != null && marker.lockedBySpawner)
            return marker.architecture;

        TriageExperimentMode controller = GetActiveController();
        return controller != null
            ? controller.architecture
            : TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner;
    }

    void CountConfiguredPatientArchitecture(GameObject patientObject, TriageExperimentArchitecture patientArchitecture)
    {
        configuredPatientCount++;

        TriageBaselineAgent baselineAgent = patientObject != null
            ? patientObject.GetComponent<TriageBaselineAgent>()
            : null;
        Patient goapPatient = patientObject != null
            ? patientObject.GetComponent<Patient>()
            : null;

        switch (patientArchitecture)
        {
            case TriageExperimentArchitecture.FsmReactiveController:
                fsmReactivePatientCount++;
                break;
            case TriageExperimentArchitecture.PriorityQueueDispatcher:
                priorityDispatcherPatientCount++;
                break;
            case TriageExperimentArchitecture.DecisionTableController:
                decisionTablePatientCount++;
                break;
            case TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner:
                goapPatientCount++;
                break;
            case TriageExperimentArchitecture.QLearningDispatcher:
                qLearningPatientCount++;
                break;
        }

        if (logModeChanges)
        {
            GoapDiagnostics.Log(
                "ExperimentMode",
                "configured patient="
                + patientObject.name
                + " mode="
                + patientArchitecture
                + " baseline="
                + (baselineAgent != null && baselineAgent.enabled)
                + " goap="
                + (goapPatient != null && goapPatient.enabled));
        }
    }

    static void StopGoapAgent(Patient goapPatient)
    {
        if (goapPatient == null)
            return;

        goapPatient.CancelInvoke();

        if (goapPatient.currentAction != null)
        {
            goapPatient.currentAction.running = false;
            goapPatient.currentAction = null;
        }

        NavMeshAgent navAgent = goapPatient.GetComponent<NavMeshAgent>();
        if (navAgent != null && navAgent.enabled && navAgent.isOnNavMesh)
            navAgent.ResetPath();
    }

    static TriageExperimentMode GetActiveController()
    {
        for (int i = activeControllers.Count - 1; i >= 0; i--)
        {
            TriageExperimentMode controller = activeControllers[i];
            if (controller == null)
            {
                activeControllers.RemoveAt(i);
                continue;
            }

            if (controller.isActiveAndEnabled)
                return controller;
        }

        return null;
    }
}
