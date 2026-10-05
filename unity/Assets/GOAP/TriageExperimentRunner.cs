using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class TriageExperimentRunner : MonoBehaviour
{
    static TriageExperimentRunner activeRunner;

    [SerializeField] bool autoStartOnPlay = false;
    [SerializeField] bool triggerStartExperiment;
    [SerializeField] bool triggerStopExperiment;
    [SerializeField] string experimentLabel = "triage_four_model_run";
    [SerializeField] string sceneName;
    [SerializeField] float runDurationSeconds = 1200f;
    [SerializeField] bool finishRunWhenAllScheduledPatientsServed = true;
    [SerializeField] float completionTimeoutSeconds = 0f;
    [SerializeField] float completionGraceSeconds = 1f;
    [SerializeField] bool removeScenePatientsBeforeRun = true;
    [SerializeField] bool runInBackgroundDuringExperiment = true;
    [SerializeField] bool showProgressOverlay = true;
    [SerializeField] float simulationTimeScale = 10f;
    // When > 0 the simulation advances in exact steps of this many simulated seconds per
    // frame (Time.captureDeltaTime, timeScale 1), independent of wall-clock frame time;
    // simulationTimeScale is then ignored. Together with the player flag
    // -job-worker-count 0 (NavMesh crowd update must not run on worker threads) this makes
    // headless runs bit-reproducible. 0 keeps the old variable-step behaviour.
    [SerializeField] float fixedSimulationStepSeconds = 0f;
    [SerializeField] bool configureSpawnerArrivalMode = true;
    [SerializeField] SpawnArrivalMode experimentSpawnMode = SpawnArrivalMode.MimicSchedule;
    [SerializeField] string mimicScheduleResourcePath = "MimicArrivals/mimic_heavy_day_2171_10_15_scaled";
    // When non-empty, the runner cycles through these schedule resource paths (one
    // independent replication "seed" per path) in addition to the load-multiplier
    // sweep, instead of using the single mimicScheduleResourcePath above for every run.
    [SerializeField] List<string> mimicScheduleResourcePaths = new List<string>();
    [SerializeField] float mimicScheduleTimeMultiplier = 2f;
    [SerializeField] List<float> mimicScheduleTimeMultipliers = new List<float>
    {
        2f,
        1.5f,
        1.25f,
        1f
    };
    [SerializeField] List<TriageExperimentArchitecture> architectures = new List<TriageExperimentArchitecture>
    {
        TriageExperimentArchitecture.FsmReactiveController,
        TriageExperimentArchitecture.PriorityQueueDispatcher,
        TriageExperimentArchitecture.DecisionTableController,
        TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner,
        TriageExperimentArchitecture.QLearningDispatcher
    };
    [SerializeField] bool running;
    [SerializeField] bool loadingScene;
    [SerializeField] int currentRunIndex = -1;
    [SerializeField] int currentLoadIndex = -1;
    [SerializeField] int currentScheduleIndex = -1;
    [SerializeField] float currentMimicScheduleTimeMultiplier = 2f;
    [SerializeField] TriageExperimentArchitecture currentArchitecture;
    [SerializeField] float currentRunElapsedSeconds;
    [SerializeField] float currentRunRealElapsedSeconds;
    [SerializeField] int totalRunCount;
    [SerializeField] int completedRunCount;
    [Range(0f, 1f)] [SerializeField] float currentRunProgress01;
    [Range(0f, 1f)] [SerializeField] float batchProgress01;
    [SerializeField] int expectedArrivalsThisRun;
    [SerializeField] string progressText;
    [SerializeField] string currentExperimentDirectory;

    float currentRunStartSimulationTime;
    float currentRunStartRealtime;
    float allPatientsServedSince = -1f;
    float originalTimeScale = 1f;
    float originalFixedDeltaTime = 0.02f;
    bool originalRunInBackground;
    bool timeScaleApplied;
    bool backgroundModeApplied;
    bool finished;
    // Offset added to the run index written to the metrics so that campaign shards started
    // with "-seedRange A B" (see ApplySeedRangeFromCommandLine) keep the global run index.
    int runIndexOffset;
    Spawn[] currentRunSpawners = new Spawn[0];

    public bool IsFinished
    {
        get { return finished; }
    }

    void Awake()
    {
        if (activeRunner != null && activeRunner != this)
        {
            Destroy(gameObject);
            return;
        }

        activeRunner = this;
        DontDestroyOnLoad(gameObject);

        if (string.IsNullOrEmpty(sceneName))
            sceneName = SceneManager.GetActiveScene().name;

        if (!ApplySingleRunFromCommandLine())
            ApplySeedRangeFromCommandLine();
    }

    // Player command line "-runIndex G" replays exactly the campaign run with global index G
    // (index = (seed * loadCount + loadIndex) * architectureCount + architectureIndex, the
    // numbering of StartNextRun) as a one-run batch, e.g. to repeat a single run with the GOAP
    // diagnostics log enabled ("-goapDiagnostics"). The run keeps its global index in the metrics.
    bool ApplySingleRunFromCommandLine()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] != "-runIndex")
                continue;

            if (!int.TryParse(args[i + 1], out int globalIndex) || globalIndex < 0)
                return false;

            int architectureCount = architectures != null ? architectures.Count : 0;
            int loadCount = GetLoadMultiplierCount();
            if (architectureCount == 0 || globalIndex >= GetScheduleCount() * loadCount * architectureCount)
                return false;

            int architectureIndex = globalIndex % architectureCount;
            int remaining = globalIndex / architectureCount;
            int loadIndex = remaining % loadCount;
            int scheduleIndex = remaining / loadCount;

            TriageExperimentArchitecture architecture = architectures[architectureIndex];
            float loadMultiplier = ResolveLoadMultiplier(loadIndex);
            string schedulePath = ResolveSchedulePath(scheduleIndex);

            architectures = new List<TriageExperimentArchitecture> { architecture };
            mimicScheduleTimeMultipliers = new List<float> { loadMultiplier };
            mimicScheduleResourcePaths = new List<string> { schedulePath };
            runIndexOffset = globalIndex;
            experimentLabel += "_run" + globalIndex;
            return true;
        }

        return false;
    }

    // Player command line "-seedRange A B" restricts the campaign to schedules A..B-1 so that
    // several player processes can run disjoint seed ranges in parallel. Global run indices
    // are preserved through runIndexOffset and the output directory label gets a suffix.
    void ApplySeedRangeFromCommandLine()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 2 < args.Length; i++)
        {
            if (args[i] != "-seedRange")
                continue;

            if (!int.TryParse(args[i + 1], out int first) || !int.TryParse(args[i + 2], out int endExclusive))
                return;

            if (mimicScheduleResourcePaths == null || mimicScheduleResourcePaths.Count == 0)
                return;

            first = Mathf.Clamp(first, 0, mimicScheduleResourcePaths.Count);
            endExclusive = Mathf.Clamp(endExclusive, first, mimicScheduleResourcePaths.Count);
            runIndexOffset = first * GetLoadMultiplierCount() * (architectures != null ? architectures.Count : 0);
            mimicScheduleResourcePaths = mimicScheduleResourcePaths.GetRange(first, endExclusive - first);
            experimentLabel += "_seeds" + first + "-" + (endExclusive - 1);
            return;
        }
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (!running)
            RestoreBackgroundMode();
    }

    void Start()
    {
        if (autoStartOnPlay)
            StartExperimentBatch();
    }

    void OnApplicationQuit()
    {
        if (running)
            StopExperimentBatch("application quit");
        else
            TriageExperimentMetrics.Flush();
    }

    void Update()
    {
        if (triggerStartExperiment)
        {
            triggerStartExperiment = false;
            StartExperimentBatch();
        }

        if (triggerStopExperiment)
        {
            triggerStopExperiment = false;
            StopExperimentBatch("manual stop");
        }

        if (!running || loadingScene)
        {
            UpdateProgressFields();
            return;
        }

        currentRunElapsedSeconds = Time.time - currentRunStartSimulationTime;
        currentRunRealElapsedSeconds = Time.realtimeSinceStartup - currentRunStartRealtime;
        TriageExperimentMetrics.SampleIfDue();
        UpdateProgressFields();

        if (finishRunWhenAllScheduledPatientsServed)
        {
            if (AllScheduledPatientsServed())
            {
                if (allPatientsServedSince < 0f)
                    allPatientsServedSince = Time.time;

                if (Time.time - allPatientsServedSince >= Mathf.Max(0f, completionGraceSeconds))
                {
                    StartNextRun("all scheduled patients served");
                    return;
                }
            }
            else
            {
                allPatientsServedSince = -1f;
            }

            if (completionTimeoutSeconds > 0f
                && currentRunElapsedSeconds >= Mathf.Max(1f, completionTimeoutSeconds))
            {
                StartNextRun("completion timeout elapsed");
            }

            return;
        }

        if (currentRunElapsedSeconds >= Mathf.Max(1f, runDurationSeconds))
            StartNextRun("duration elapsed");
    }

    [ContextMenu("Start Four Model Experiment")]
    public void StartExperimentBatch()
    {
        if (running)
            StopExperimentBatch("restart requested");

        if (architectures == null || architectures.Count == 0)
        {
            Debug.LogWarning("Triage experiment has no architectures configured.", this);
            return;
        }

        running = true;
        finished = false;
        currentRunIndex = -1;
        currentLoadIndex = -1;
        currentScheduleIndex = -1;
        totalRunCount = GetTotalRunCount();
        completedRunCount = 0;
        expectedArrivalsThisRun = 0;
        currentRunProgress01 = 0f;
        batchProgress01 = 0f;
        currentMimicScheduleTimeMultiplier = ResolveLoadMultiplier(0);
        currentRunElapsedSeconds = 0f;
        currentRunRealElapsedSeconds = 0f;
        ApplyBackgroundMode();
        ApplySimulationTimeScale();
        TriageExperimentMetrics.StartBatch(experimentLabel);
        currentExperimentDirectory = TriageExperimentMetrics.ExperimentDirectory;
        StartNextRun("batch start");
    }

    [ContextMenu("Stop Experiment")]
    public void StopExperiment()
    {
        StopExperimentBatch("manual stop");
    }

    void StopExperimentBatch(string reason)
    {
        if (!running)
            return;

        running = false;
        loadingScene = false;
        TriageExperimentMetrics.FinishRun(reason);
        TriageExperimentMetrics.FinishBatch();
        RestoreTimeScale();
        RestoreBackgroundMode();
        UpdateProgressFields();
        GoapDiagnostics.Log("Experiment", "batch stopped reason=" + reason);
    }

    void StartNextRun(string previousRunReason)
    {
        if (currentRunIndex >= 0)
        {
            TriageExperimentMetrics.FinishRun(previousRunReason);
            completedRunCount = Mathf.Min(completedRunCount + 1, Mathf.Max(0, totalRunCount));
        }

        currentRunIndex++;
        int loadCount = GetLoadMultiplierCount();
        int scheduleCount = GetScheduleCount();
        totalRunCount = scheduleCount * loadCount * architectures.Count;
        if (currentRunIndex >= totalRunCount)
        {
            running = false;
            TriageExperimentMetrics.FinishBatch();
            RestoreTimeScale();
            RestoreBackgroundMode();
            currentRunProgress01 = 1f;
            batchProgress01 = 1f;
            progressText = "Experiment complete: " + currentExperimentDirectory;
            finished = true;
            GoapDiagnostics.Log("Experiment", "all runs finished directory=" + currentExperimentDirectory);
            return;
        }

        int architectureIndex = currentRunIndex % architectures.Count;
        int remaining = currentRunIndex / architectures.Count;
        currentLoadIndex = remaining % loadCount;
        currentScheduleIndex = remaining / loadCount;
        currentMimicScheduleTimeMultiplier = ResolveLoadMultiplier(currentLoadIndex);
        currentArchitecture = architectures[architectureIndex];
        currentRunProgress01 = 0f;
        expectedArrivalsThisRun = 0;
        UpdateProgressFields();
        loadingScene = true;
        ResetStaticStateBeforeSceneLoad();
        SceneManager.LoadScene(sceneName);
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!running || !loadingScene)
            return;

        StartCoroutine(InitializeRunAfterSceneLoad());
    }

    IEnumerator InitializeRunAfterSceneLoad()
    {
        yield return null;

        WorldStates.ResetGlobalCounter();
        GWorld.Instance.ResetForLoadedScene();
        WorldStates.ResetGlobalCounter();
        RemoveScenePatientsBeforeRun();
        ConfigureSceneArchitecture();

        currentRunStartSimulationTime = Time.time;
        currentRunStartRealtime = Time.realtimeSinceStartup;
        currentRunElapsedSeconds = 0f;
        currentRunRealElapsedSeconds = 0f;
        allPatientsServedSince = -1f;
        loadingScene = false;
        TriageExperimentMetrics.StartRun(currentRunIndex + runIndexOffset, currentArchitecture, currentMimicScheduleTimeMultiplier);
        ResetSceneSpawnersForRun();
        UpdateProgressFields();
    }

    void ConfigureSceneArchitecture()
    {
        TriageExperimentMode[] modes = FindObjectsByType<TriageExperimentMode>(FindObjectsSortMode.None);
        foreach (TriageExperimentMode mode in modes)
        {
            if (mode == null)
                continue;

            mode.SetArchitecture(currentArchitecture, true);
            mode.SetApplyToExistingPatients(false);
        }

        Spawn[] spawners = FindObjectsByType<Spawn>(FindObjectsSortMode.None);
        currentRunSpawners = spawners;
        foreach (Spawn spawner in spawners)
        {
            if (spawner == null)
                continue;

            spawner.useSceneExperimentMode = true;
            spawner.lockSpawnedArchitecture = true;

            if (configureSpawnerArrivalMode)
            {
                spawner.arrivalMode = experimentSpawnMode;
                spawner.mimicArrivalResourcePath = ResolveSchedulePath(currentScheduleIndex);
                spawner.mimicScheduleTimeMultiplier = currentMimicScheduleTimeMultiplier;
                spawner.spawnOnStart = false;
                spawner.numPatients = 0;
                spawner.autoSpawn = true;
            }
        }

        GoapDiagnostics.Log(
            "Experiment",
            "scene configured runIndex="
            + currentRunIndex
            + " architecture="
            + currentArchitecture
            + " loadIndex="
            + currentLoadIndex
            + " loadMultiplier="
            + currentMimicScheduleTimeMultiplier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " scheduleIndex="
            + currentScheduleIndex
            + " spawners="
            + spawners.Length
            + " spawnMode="
            + experimentSpawnMode
            + " mimicSchedule="
            + ResolveSchedulePath(currentScheduleIndex));
    }

    int GetScheduleCount()
    {
        return mimicScheduleResourcePaths == null || mimicScheduleResourcePaths.Count == 0
            ? 1
            : mimicScheduleResourcePaths.Count;
    }

    string ResolveSchedulePath(int index)
    {
        if (mimicScheduleResourcePaths == null || mimicScheduleResourcePaths.Count == 0)
            return mimicScheduleResourcePath;

        int clampedIndex = Mathf.Clamp(index, 0, mimicScheduleResourcePaths.Count - 1);
        return mimicScheduleResourcePaths[clampedIndex];
    }

    int GetLoadMultiplierCount()
    {
        return mimicScheduleTimeMultipliers == null || mimicScheduleTimeMultipliers.Count == 0
            ? 1
            : mimicScheduleTimeMultipliers.Count;
    }

    int GetTotalRunCount()
    {
        if (architectures == null || architectures.Count == 0)
            return 0;

        return GetScheduleCount() * GetLoadMultiplierCount() * architectures.Count;
    }

    float ResolveLoadMultiplier(int index)
    {
        if (mimicScheduleTimeMultipliers == null || mimicScheduleTimeMultipliers.Count == 0)
            return Mathf.Max(0.01f, mimicScheduleTimeMultiplier);

        int clampedIndex = Mathf.Clamp(index, 0, mimicScheduleTimeMultipliers.Count - 1);
        return Mathf.Max(0.01f, mimicScheduleTimeMultipliers[clampedIndex]);
    }

    void ResetSceneSpawnersForRun()
    {
        if (currentRunSpawners == null || currentRunSpawners.Length == 0)
            currentRunSpawners = FindObjectsByType<Spawn>(FindObjectsSortMode.None);

        foreach (Spawn spawner in currentRunSpawners)
        {
            if (spawner == null)
                continue;

            spawner.ResetSpawnTimingForExperiment();
        }
    }

    void UpdateProgressFields()
    {
        if (!running)
            return;

        if (totalRunCount <= 0)
            totalRunCount = GetTotalRunCount();

        expectedArrivalsThisRun = GetExpectedArrivalsForCurrentRun();
        if (expectedArrivalsThisRun > 0)
        {
            float spawnedProgress = Mathf.Clamp01(TriageExperimentMetrics.SpawnedCount / (float)expectedArrivalsThisRun);
            float completedProgress = Mathf.Clamp01(TriageExperimentMetrics.CompletedHomeCount / (float)expectedArrivalsThisRun);
            currentRunProgress01 = (spawnedProgress + completedProgress) * 0.5f;
        }
        else
        {
            currentRunProgress01 = loadingScene ? 0f : currentRunProgress01;
        }

        batchProgress01 = totalRunCount > 0
            ? Mathf.Clamp01((completedRunCount + currentRunProgress01) / totalRunCount)
            : 0f;

        progressText =
            "Run "
            + Mathf.Clamp(currentRunIndex + 1, 0, Mathf.Max(1, totalRunCount))
            + "/"
            + Mathf.Max(1, totalRunCount)
            + " load x"
            + currentMimicScheduleTimeMultiplier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " "
            + currentArchitecture
            + " | spawned "
            + TriageExperimentMetrics.SpawnedCount
            + "/"
            + expectedArrivalsThisRun
            + " | home "
            + TriageExperimentMetrics.CompletedHomeCount
            + "/"
            + expectedArrivalsThisRun
            + " | elapsed "
            + currentRunElapsedSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
            + "s | batch "
            + (batchProgress01 * 100f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            + "%";
    }

    int GetExpectedArrivalsForCurrentRun()
    {
        if (currentRunSpawners == null || currentRunSpawners.Length == 0)
            currentRunSpawners = FindObjectsByType<Spawn>(FindObjectsSortMode.None);

        int expectedArrivals = 0;
        foreach (Spawn spawner in currentRunSpawners)
        {
            if (spawner == null || !spawner.HasFiniteSchedule)
                continue;

            expectedArrivals += spawner.ScheduledArrivalCount;
        }

        return expectedArrivals;
    }

    void RemoveScenePatientsBeforeRun()
    {
        if (!removeScenePatientsBeforeRun)
            return;

        Patient[] scenePatients = FindObjectsByType<Patient>(FindObjectsSortMode.None);
        int removedCount = 0;
        foreach (Patient patient in scenePatients)
        {
            if (patient == null)
                continue;

            GWorld.Instance.RemovePatient(patient.gameObject);
            Destroy(patient.gameObject);
            removedCount++;
        }

        if (removedCount > 0)
            GoapDiagnostics.Log("Experiment", "removed scene patients before run count=" + removedCount);
    }

    bool AllScheduledPatientsServed()
    {
        if (currentRunSpawners == null || currentRunSpawners.Length == 0)
            currentRunSpawners = FindObjectsByType<Spawn>(FindObjectsSortMode.None);

        int finiteScheduleSpawners = 0;
        int expectedArrivals = 0;
        foreach (Spawn spawner in currentRunSpawners)
        {
            if (spawner == null || !spawner.HasFiniteSchedule)
                continue;

            finiteScheduleSpawners++;
            expectedArrivals += spawner.ScheduledArrivalCount;

            if (!spawner.IsFiniteScheduleComplete)
                return false;
        }

        if (finiteScheduleSpawners == 0)
            return false;

        if (expectedArrivals <= 0)
            return false;

        if (TriageExperimentMetrics.SpawnedCount < expectedArrivals)
            return false;

        return TriageExperimentMetrics.CompletedHomeCount >= TriageExperimentMetrics.SpawnedCount;
    }

    static void ResetStaticStateBeforeSceneLoad()
    {
        WorldStates.ResetGlobalCounter();
        HospitalFlowController.ResetStaticStateForExperiment();
        GAgent.ResetStaticCountersForExperiment();
        TriageQLearningDispatcher.ResetForExperiment();
        TriageDqnDispatcher.ResetForExperiment();
    }

    void ApplyBackgroundMode()
    {
        if (!runInBackgroundDuringExperiment)
            return;

        if (!backgroundModeApplied)
        {
            originalRunInBackground = Application.runInBackground;
            backgroundModeApplied = true;
        }

        Application.runInBackground = true;
        GoapDiagnostics.Log("Experiment", "run in background enabled");
    }

    void RestoreBackgroundMode()
    {
        if (!backgroundModeApplied)
            return;

        Application.runInBackground = originalRunInBackground;
        backgroundModeApplied = false;
        GoapDiagnostics.Log("Experiment", "run in background restored=" + originalRunInBackground);
    }

    void ApplySimulationTimeScale()
    {
        if (!timeScaleApplied)
        {
            originalTimeScale = Time.timeScale;
            originalFixedDeltaTime = Time.fixedDeltaTime;
            timeScaleApplied = true;
        }

        if (fixedSimulationStepSeconds > 0f)
        {
            Time.captureDeltaTime = fixedSimulationStepSeconds;
            Time.timeScale = 1f;
            Time.fixedDeltaTime = fixedSimulationStepSeconds;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            GoapDiagnostics.Log("Experiment", "fixed simulation step applied=" + fixedSimulationStepSeconds);
            return;
        }

        float resolvedTimeScale = Mathf.Max(0.1f, simulationTimeScale);
        Time.timeScale = resolvedTimeScale;
        Time.fixedDeltaTime = originalFixedDeltaTime * resolvedTimeScale;
        GoapDiagnostics.Log("Experiment", "time scale applied=" + resolvedTimeScale);
    }

    void RestoreTimeScale()
    {
        if (!timeScaleApplied)
            return;

        Time.captureDeltaTime = 0f;
        Time.timeScale = originalTimeScale;
        Time.fixedDeltaTime = originalFixedDeltaTime;
        timeScaleApplied = false;
        GoapDiagnostics.Log("Experiment", "time scale restored=" + originalTimeScale);
    }

    void OnGUI()
    {
        if (!showProgressOverlay || string.IsNullOrEmpty(progressText))
            return;

        Rect panelRect = new Rect(12f, 12f, 560f, 92f);
        GUI.Box(panelRect, GUIContent.none);
        GUI.Label(new Rect(panelRect.x + 10f, panelRect.y + 8f, panelRect.width - 20f, 22f), progressText);
        DrawProgressBar(
            new Rect(panelRect.x + 10f, panelRect.y + 36f, panelRect.width - 20f, 18f),
            currentRunProgress01,
            "current run");
        DrawProgressBar(
            new Rect(panelRect.x + 10f, panelRect.y + 62f, panelRect.width - 20f, 18f),
            batchProgress01,
            "batch");
    }

    static void DrawProgressBar(Rect rect, float progress, string label)
    {
        float clampedProgress = Mathf.Clamp01(progress);
        GUI.Box(rect, GUIContent.none);

        Color previousColor = GUI.color;
        GUI.color = new Color(0.25f, 0.67f, 0.45f, 0.85f);
        Rect fillRect = rect;
        fillRect.width *= clampedProgress;
        GUI.DrawTexture(fillRect, Texture2D.whiteTexture);
        GUI.color = previousColor;

        GUI.Label(
            rect,
            label + " " + (clampedProgress * 100f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%");
    }
}
