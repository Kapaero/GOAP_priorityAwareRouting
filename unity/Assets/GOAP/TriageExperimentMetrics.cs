using System;
using System.Globalization;
using System.IO;
using UnityEngine;

public static class TriageExperimentMetrics
{
    const float SampleIntervalSeconds = 1f;

    static string experimentDirectory;
    static StreamWriter summaryWriter;
    static StreamWriter timeSeriesWriter;
    static StreamWriter eventsWriter;
    static bool batchActive;
    static bool runActive;
    static int runIndex;
    static TriageExperimentArchitecture architecture;
    static float loadMultiplier = 1f;
    static float runStartSimulationTime;
    static float runStartRealtime;
    static float nextSampleSimulationTime;

    static int spawned;
    static int criticalSpawned;
    static int normalSpawned;
    static int waitingEntered;
    static int wingEntered;
    static int treated;
    static int wingExited;
    static int completedHome;
    static int criticalCompletedHome;
    static int normalCompletedHome;

    public static bool IsRunActive
    {
        get { return runActive; }
    }

    public static string ExperimentDirectory
    {
        get { return experimentDirectory; }
    }

    public static int SpawnedCount
    {
        get { return spawned; }
    }

    public static int CompletedHomeCount
    {
        get { return completedHome; }
    }

    public static int CriticalSpawnedCount
    {
        get { return criticalSpawned; }
    }

    public static int CriticalCompletedHomeCount
    {
        get { return criticalCompletedHome; }
    }

    public static void StartBatch(string label)
    {
        CloseWriters();

        string root = ResolveExperimentRoot();
        string cleanLabel = SanitizeFileName(string.IsNullOrWhiteSpace(label) ? "triage_experiment" : label);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        // The process id keeps the directory unique when several player processes run
        // campaign shards in parallel and start within the same second.
        experimentDirectory = Path.Combine(
            root,
            cleanLabel + "_" + timestamp + "_p" + System.Diagnostics.Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(experimentDirectory);

        summaryWriter = new StreamWriter(Path.Combine(experimentDirectory, "runs_summary.csv"), false);
        timeSeriesWriter = new StreamWriter(Path.Combine(experimentDirectory, "time_series.csv"), false);
        eventsWriter = new StreamWriter(Path.Combine(experimentDirectory, "events.csv"), false);

        summaryWriter.WriteLine("run_index,architecture,load_multiplier,simulation_duration_seconds,real_duration_seconds,time_scale,spawned,critical_spawned,normal_spawned,waiting_entered,wing_entered,treated,wing_exited,completed_home,critical_completed_home,normal_completed_home,queue_count,critical_queue_count,inside_wing,free_cubicle,free_cubicle_left,world_counter,reason");
        timeSeriesWriter.WriteLine("run_index,architecture,load_multiplier,simulation_elapsed_seconds,real_elapsed_seconds,frame,spawned,critical_spawned,normal_spawned,waiting_entered,wing_entered,treated,wing_exited,completed_home,queue_count,critical_queue_count,inside_wing,cured_inside,uncured_inside,free_cubicle,free_cubicle_left,world_counter,plan_requests,plan_skips,set_destination_requests,path_skips,fps");
        eventsWriter.WriteLine("run_index,architecture,load_multiplier,simulation_elapsed_seconds,real_elapsed_seconds,frame,event,agent,is_critical,queue_count,critical_queue_count,inside_wing,free_cubicle,free_cubicle_left,world_counter,detail");

        batchActive = true;
        GoapDiagnostics.Log("Experiment", "batch started directory=" + experimentDirectory);
    }

    public static void StartRun(int index, TriageExperimentArchitecture runArchitecture)
    {
        StartRun(index, runArchitecture, 1f);
    }

    public static void StartRun(int index, TriageExperimentArchitecture runArchitecture, float runLoadMultiplier)
    {
        if (!batchActive)
            StartBatch("triage_experiment");

        runIndex = index;
        architecture = runArchitecture;
        loadMultiplier = Mathf.Max(0.01f, runLoadMultiplier);
        runStartSimulationTime = Time.time;
        runStartRealtime = Time.realtimeSinceStartup;
        nextSampleSimulationTime = runStartSimulationTime;
        runActive = true;
        ResetRunCounters();
        RecordEvent("run_start", null, "");
        GoapDiagnostics.Log(
            "Experiment",
            "run started index="
            + runIndex
            + " architecture="
            + architecture
            + " loadMultiplier="
            + loadMultiplier.ToString("F4", CultureInfo.InvariantCulture));
    }

    public static void FinishRun(string reason)
    {
        if (!runActive)
            return;

        SampleNow();
        RecordEvent("run_end", null, reason);

        float simulationElapsed = Mathf.Max(0.001f, Time.time - runStartSimulationTime);
        float realElapsed = Mathf.Max(0.001f, Time.realtimeSinceStartup - runStartRealtime);
        summaryWriter.WriteLine(
            runIndex
            + ","
            + architecture
            + ","
            + loadMultiplier.ToString("F4", CultureInfo.InvariantCulture)
            + ","
            + simulationElapsed.ToString("F3", CultureInfo.InvariantCulture)
            + ","
            + realElapsed.ToString("F3", CultureInfo.InvariantCulture)
            + ","
            + Time.timeScale.ToString("F2", CultureInfo.InvariantCulture)
            + ","
            + spawned
            + ","
            + criticalSpawned
            + ","
            + normalSpawned
            + ","
            + waitingEntered
            + ","
            + wingEntered
            + ","
            + treated
            + ","
            + wingExited
            + ","
            + completedHome
            + ","
            + criticalCompletedHome
            + ","
            + normalCompletedHome
            + ","
            + GWorld.Instance.GetQueuedPatientCount()
            + ","
            + GWorld.Instance.GetQueuedCriticalPatientCount()
            + ","
            + HospitalFlowController.GetPatientsInsideWingCount()
            + ","
            + GWorld.Instance.GetFreeCubicleCount()
            + ","
            + GWorld.Instance.GetFreeCubicleLeftCount()
            + ","
            + WorldStates.worldStateChangeCounter
            + ","
            + Csv(reason));

        Flush();
        GoapDiagnostics.Log("Experiment", "run finished index=" + runIndex + " architecture=" + architecture + " reason=" + reason);
        runActive = false;
    }

    public static void FinishBatch()
    {
        if (runActive)
            FinishRun("batch finished");

        GoapDiagnostics.Log("Experiment", "batch finished directory=" + experimentDirectory);
        Flush();
        CloseWriters();
        batchActive = false;
    }

    public static void SampleIfDue()
    {
        if (!runActive || Time.time < nextSampleSimulationTime)
            return;

        SampleNow();
        nextSampleSimulationTime = Time.time + SampleIntervalSeconds;
    }

    public static void RecordSpawn(GameObject patient, bool isCritical)
    {
        RecordSpawn(patient, isCritical, "");
    }

    public static void RecordSpawn(GameObject patient, bool isCritical, string detail)
    {
        if (!runActive)
            return;

        spawned++;
        if (isCritical)
            criticalSpawned++;
        else
            normalSpawned++;

        string resolvedDetail = "critical=" + isCritical;
        if (!string.IsNullOrWhiteSpace(detail))
            resolvedDetail += " " + detail;

        RecordEvent("spawn", patient, resolvedDetail);
    }

    public static void RecordWaitingRoomEntry(GameObject patient)
    {
        if (!runActive)
            return;

        waitingEntered++;
        RecordEvent("waiting_room_entry", patient, "");
    }

    public static void RecordWingEntry(GameObject patient)
    {
        if (!runActive)
            return;

        wingEntered++;
        RecordEvent("wing_entry", patient, "");
    }

    public static void RecordTreatmentComplete(GameObject patient)
    {
        if (!runActive)
            return;

        treated++;
        RecordEvent("treatment_complete", patient, "");
    }

    public static void RecordWingExit(GameObject patient)
    {
        if (!runActive)
            return;

        wingExited++;
        RecordEvent("wing_exit", patient, "");
    }

    public static void RecordHome(GameObject patient)
    {
        if (!runActive)
            return;

        completedHome++;
        if (IsCritical(patient))
            criticalCompletedHome++;
        else
            normalCompletedHome++;

        RecordEvent("home", patient, "");
    }

    public static void Flush()
    {
        if (summaryWriter != null)
            summaryWriter.Flush();
        if (timeSeriesWriter != null)
            timeSeriesWriter.Flush();
        if (eventsWriter != null)
            eventsWriter.Flush();
    }

    static void SampleNow()
    {
        if (timeSeriesWriter == null)
            return;

        float simulationElapsed = Time.time - runStartSimulationTime;
        float realElapsed = Time.realtimeSinceStartup - runStartRealtime;
        float fps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;

        timeSeriesWriter.WriteLine(
            runIndex
            + ","
            + architecture
            + ","
            + loadMultiplier.ToString("F4", CultureInfo.InvariantCulture)
            + ","
            + simulationElapsed.ToString("F3", CultureInfo.InvariantCulture)
            + ","
            + realElapsed.ToString("F3", CultureInfo.InvariantCulture)
            + ","
            + Time.frameCount
            + ","
            + spawned
            + ","
            + criticalSpawned
            + ","
            + normalSpawned
            + ","
            + waitingEntered
            + ","
            + wingEntered
            + ","
            + treated
            + ","
            + wingExited
            + ","
            + completedHome
            + ","
            + GWorld.Instance.GetQueuedPatientCount()
            + ","
            + GWorld.Instance.GetQueuedCriticalPatientCount()
            + ","
            + HospitalFlowController.GetPatientsInsideWingCount()
            + ","
            + HospitalFlowController.GetCuredPatientsInsideWingCount()
            + ","
            + HospitalFlowController.GetUncuredPatientsInsideWingCount()
            + ","
            + GWorld.Instance.GetFreeCubicleCount()
            + ","
            + GWorld.Instance.GetFreeCubicleLeftCount()
            + ","
            + WorldStates.worldStateChangeCounter
            + ","
            + GAgent.PlanRequestsThisFrame
            + ","
            + GAgent.PlanBudgetSkipsThisFrame
            + ","
            + GAgent.SetDestinationRequestsThisFrame
            + ","
            + GAgent.PathBudgetSkipsThisFrame
            + ","
            + fps.ToString("F2", CultureInfo.InvariantCulture));
    }

    static void RecordEvent(string eventName, GameObject patient, string detail)
    {
        if (eventsWriter == null)
            return;

        float simulationElapsed = Time.time - runStartSimulationTime;
        float realElapsed = Time.realtimeSinceStartup - runStartRealtime;

        eventsWriter.WriteLine(
            runIndex
            + ","
            + architecture
            + ","
            + loadMultiplier.ToString("F4", CultureInfo.InvariantCulture)
            + ","
            + simulationElapsed.ToString("F3", CultureInfo.InvariantCulture)
            + ","
            + realElapsed.ToString("F3", CultureInfo.InvariantCulture)
            + ","
            + Time.frameCount
            + ","
            + Csv(eventName)
            + ","
            + Csv(PatientLabel(patient))
            + ","
            + IsCritical(patient)
            + ","
            + GWorld.Instance.GetQueuedPatientCount()
            + ","
            + GWorld.Instance.GetQueuedCriticalPatientCount()
            + ","
            + HospitalFlowController.GetPatientsInsideWingCount()
            + ","
            + GWorld.Instance.GetFreeCubicleCount()
            + ","
            + GWorld.Instance.GetFreeCubicleLeftCount()
            + ","
            + WorldStates.worldStateChangeCounter
            + ","
            + Csv(detail));
    }

    static void ResetRunCounters()
    {
        spawned = 0;
        criticalSpawned = 0;
        normalSpawned = 0;
        waitingEntered = 0;
        wingEntered = 0;
        treated = 0;
        wingExited = 0;
        completedHome = 0;
        criticalCompletedHome = 0;
        normalCompletedHome = 0;
    }

    static void CloseWriters()
    {
        if (summaryWriter != null)
            summaryWriter.Close();
        if (timeSeriesWriter != null)
            timeSeriesWriter.Close();
        if (eventsWriter != null)
            eventsWriter.Close();

        summaryWriter = null;
        timeSeriesWriter = null;
        eventsWriter = null;
    }

    static string ResolveExperimentRoot()
    {
#if UNITY_EDITOR
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#else
        string projectRoot = Application.persistentDataPath;
#endif
        return Path.Combine(projectRoot, "GOAP_Diagnostics", "Experiments");
    }

    static bool IsCritical(GameObject patient)
    {
        return patient != null && patient.CompareTag("critical");
    }

    static string PatientLabel(GameObject patient)
    {
        if (patient == null)
            return "";

        return patient.name + "[" + patient.tag + "]";
    }

    static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    static string SanitizeFileName(string value)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value.Replace(' ', '_');
    }
}
