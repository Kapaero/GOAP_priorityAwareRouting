using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// Dedicated pretraining loop for TriageDqnDispatcher. Mirrors TriageQLearningTrainer's
// scene-reload loop but drives the neural-network dispatcher (reset/save network
// instead of reset/save table) and always exercises DeepQLearningDispatcher.
public class TriageDqnTrainer : MonoBehaviour
{
    static TriageDqnTrainer activeTrainer;

    [SerializeField] bool autoStartOnPlay = false;
    [SerializeField] bool triggerStartTraining;
    [SerializeField] bool triggerStopTraining;
    [SerializeField] string experimentLabel = "dqn_training";
    [SerializeField] string sceneName;
    [SerializeField] int trainingRunCount = 20;
    [SerializeField] bool finishRunWhenAllScheduledPatientsServed = true;
    [SerializeField] float completionTimeoutSeconds = 0f;
    [SerializeField] float completionGraceSeconds = 1f;
    [SerializeField] bool removeScenePatientsBeforeRun = true;
    [SerializeField] bool runInBackgroundDuringTraining = true;
    [SerializeField] float simulationTimeScale = 10f;
    [SerializeField] bool configureSpawnerArrivalMode = true;
    [SerializeField] SpawnArrivalMode trainingSpawnMode = SpawnArrivalMode.MimicSchedule;
    [SerializeField] string mimicScheduleResourcePath = "MimicArrivals/mimic_heavy_day_2171_10_15_scaled";
    [SerializeField] List<float> loadMultipliers = new List<float>
    {
        0.6f, 0.8f, 1f, 1.2f, 1.5f, 1.8f, 2.2f, 2.5f
    };
    [SerializeField] bool resetNetworkAtStart = true;
    [SerializeField] float startEpsilon = 0.3f;
    [SerializeField] int randomSeed = 20260719;
    [SerializeField] bool running;
    [SerializeField] bool loadingScene;
    [SerializeField] int currentRunIndex = -1;
    [SerializeField] float currentMimicScheduleTimeMultiplier = 1f;
    [SerializeField] float currentRunElapsedSeconds;
    [SerializeField] int expectedArrivalsThisRun;
    [SerializeField] string progressText;
    [SerializeField] string dqnDirectory;

    float currentRunStartSimulationTime;
    float allPatientsServedSince = -1f;
    float originalTimeScale = 1f;
    float originalFixedDeltaTime = 0.02f;
    bool originalRunInBackground;
    bool timeScaleApplied;
    bool backgroundModeApplied;
    bool finished;
    int lastProgressLogRunIndex = -1;
    Spawn[] currentRunSpawners = new Spawn[0];

    public bool IsFinished
    {
        get { return finished; }
    }

    public bool IsRunning
    {
        get { return running; }
    }

    void Awake()
    {
        if (activeTrainer != null && activeTrainer != this)
        {
            Destroy(gameObject);
            return;
        }

        activeTrainer = this;
        DontDestroyOnLoad(gameObject);

        if (string.IsNullOrEmpty(sceneName))
            sceneName = SceneManager.GetActiveScene().name;
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
            StartTrainingBatch();
    }

    void OnApplicationQuit()
    {
        if (running)
            StopTrainingBatch("application quit");
    }

    void Update()
    {
        if (triggerStartTraining)
        {
            triggerStartTraining = false;
            StartTrainingBatch();
        }

        if (triggerStopTraining)
        {
            triggerStopTraining = false;
            StopTrainingBatch("manual stop");
        }

        if (!running || loadingScene)
            return;

        currentRunElapsedSeconds = Time.time - currentRunStartSimulationTime;
        TriageExperimentMetrics.SampleIfDue();
        UpdateProgressText();

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

            if (completionTimeoutSeconds > 0f && currentRunElapsedSeconds >= Mathf.Max(1f, completionTimeoutSeconds))
                StartNextRun("completion timeout elapsed");

            return;
        }
    }

    [ContextMenu("Start DQN Training")]
    public void StartTrainingBatch()
    {
        if (running)
            StopTrainingBatch("restart requested");

        if (resetNetworkAtStart)
            TriageDqnDispatcher.ResetNetwork(startEpsilon, randomSeed);

        TriageDqnDispatcher.TrainingEnabled = true;

        dqnDirectory = ResolveDqnDirectory();
        TriageDqnDispatcher.BeginLogging(dqnDirectory);
        TriageExperimentMetrics.StartBatch(experimentLabel);

        running = true;
        finished = false;
        currentRunIndex = -1;
        currentRunElapsedSeconds = 0f;
        expectedArrivalsThisRun = 0;
        currentMimicScheduleTimeMultiplier = ResolveLoadMultiplier(0);
        ApplyBackgroundMode();
        ApplySimulationTimeScale();
        StartNextRun("training batch start");
    }

    [ContextMenu("Stop DQN Training")]
    public void StopTraining()
    {
        StopTrainingBatch("manual stop");
    }

    void StopTrainingBatch(string reason)
    {
        if (!running)
            return;

        running = false;
        loadingScene = false;
        TriageExperimentMetrics.FinishRun(reason);
        TriageExperimentMetrics.FinishBatch();
        TriageDqnDispatcher.EndLogging();
        RestoreTimeScale();
        RestoreBackgroundMode();
        GoapDiagnostics.Log("DqnTrainer", "training batch stopped reason=" + reason);
    }

    void StartNextRun(string previousRunReason)
    {
        if (currentRunIndex >= 0)
            TriageExperimentMetrics.FinishRun(previousRunReason);

        currentRunIndex++;
        if (currentRunIndex >= Mathf.Max(1, trainingRunCount))
        {
            FinishTrainingBatch();
            return;
        }

        currentMimicScheduleTimeMultiplier = ResolveLoadMultiplier(currentRunIndex);
        currentRunElapsedSeconds = 0f;
        expectedArrivalsThisRun = 0;
        loadingScene = true;
        ResetStaticStateBeforeSceneLoad();
        SceneManager.LoadScene(sceneName);
    }

    void FinishTrainingBatch()
    {
        running = false;
        TriageExperimentMetrics.FinishBatch();
        RestoreTimeScale();
        RestoreBackgroundMode();

        string networkPath = Path.Combine(ResolveDqnRoot(), "dqn_network_trained.csv");
        TriageDqnDispatcher.SaveNetwork(networkPath);
        TriageDqnDispatcher.EndLogging();
        TriageDqnDispatcher.TrainingEnabled = false;

        progressText = "DQN training complete: " + dqnDirectory;
        finished = true;
        GoapDiagnostics.Log(
            "DqnTrainer",
            "training complete runs=" + trainingRunCount
            + " episodes=" + TriageDqnDispatcher.EpisodeCount
            + " network=" + networkPath);
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
        currentRunElapsedSeconds = 0f;
        allPatientsServedSince = -1f;
        loadingScene = false;
        TriageExperimentMetrics.StartRun(currentRunIndex, TriageExperimentArchitecture.DeepQLearningDispatcher, currentMimicScheduleTimeMultiplier);
        ResetSceneSpawnersForRun();

        GoapDiagnostics.Log(
            "DqnTrainer",
            "run started index=" + currentRunIndex
            + "/" + trainingRunCount
            + " loadMultiplier=" + currentMimicScheduleTimeMultiplier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " epsilon=" + TriageDqnDispatcher.Epsilon.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + " episodesSoFar=" + TriageDqnDispatcher.EpisodeCount);
    }

    void ConfigureSceneArchitecture()
    {
        TriageExperimentMode[] modes = FindObjectsByType<TriageExperimentMode>(FindObjectsSortMode.None);
        foreach (TriageExperimentMode mode in modes)
        {
            if (mode == null)
                continue;

            mode.SetArchitecture(TriageExperimentArchitecture.DeepQLearningDispatcher, true);
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
                spawner.arrivalMode = trainingSpawnMode;
                spawner.mimicArrivalResourcePath = mimicScheduleResourcePath;
                spawner.mimicScheduleTimeMultiplier = currentMimicScheduleTimeMultiplier;
                spawner.spawnOnStart = false;
                spawner.numPatients = 0;
                spawner.autoSpawn = true;
            }
        }
    }

    float ResolveLoadMultiplier(int index)
    {
        if (loadMultipliers == null || loadMultipliers.Count == 0)
            return 1f;

        int wrappedIndex = index % loadMultipliers.Count;
        return Mathf.Max(0.01f, loadMultipliers[wrappedIndex]);
    }

    void ResetSceneSpawnersForRun()
    {
        if (currentRunSpawners == null || currentRunSpawners.Length == 0)
            currentRunSpawners = FindObjectsByType<Spawn>(FindObjectsSortMode.None);

        foreach (Spawn spawner in currentRunSpawners)
        {
            if (spawner != null)
                spawner.ResetSpawnTimingForExperiment();
        }
    }

    void RemoveScenePatientsBeforeRun()
    {
        if (!removeScenePatientsBeforeRun)
            return;

        Patient[] scenePatients = FindObjectsByType<Patient>(FindObjectsSortMode.None);
        foreach (Patient patient in scenePatients)
        {
            if (patient == null)
                continue;

            GWorld.Instance.RemovePatient(patient.gameObject);
            Destroy(patient.gameObject);
        }
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

        if (finiteScheduleSpawners == 0 || expectedArrivals <= 0)
            return false;

        expectedArrivalsThisRun = expectedArrivals;
        if (TriageExperimentMetrics.SpawnedCount < expectedArrivals)
            return false;

        return TriageExperimentMetrics.CompletedHomeCount >= TriageExperimentMetrics.SpawnedCount;
    }

    void UpdateProgressText()
    {
        if (Time.frameCount == lastProgressLogRunIndex)
            return;

        lastProgressLogRunIndex = Time.frameCount;
        progressText =
            "DQN training run " + (currentRunIndex + 1) + "/" + trainingRunCount
            + " load x" + currentMimicScheduleTimeMultiplier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " | spawned " + TriageExperimentMetrics.SpawnedCount + "/" + expectedArrivalsThisRun
            + " | home " + TriageExperimentMetrics.CompletedHomeCount
            + " | episodes " + TriageDqnDispatcher.EpisodeCount
            + " | epsilon " + TriageDqnDispatcher.Epsilon.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + " | elapsed " + currentRunElapsedSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    static void ResetStaticStateBeforeSceneLoad()
    {
        WorldStates.ResetGlobalCounter();
        HospitalFlowController.ResetStaticStateForExperiment();
        GAgent.ResetStaticCountersForExperiment();
        TriageDqnDispatcher.ResetForExperiment();
    }

    void ApplyBackgroundMode()
    {
        if (!runInBackgroundDuringTraining)
            return;

        if (!backgroundModeApplied)
        {
            originalRunInBackground = Application.runInBackground;
            backgroundModeApplied = true;
        }

        Application.runInBackground = true;
    }

    void RestoreBackgroundMode()
    {
        if (!backgroundModeApplied)
            return;

        Application.runInBackground = originalRunInBackground;
        backgroundModeApplied = false;
    }

    void ApplySimulationTimeScale()
    {
        if (!timeScaleApplied)
        {
            originalTimeScale = Time.timeScale;
            originalFixedDeltaTime = Time.fixedDeltaTime;
            timeScaleApplied = true;
        }

        float resolvedTimeScale = Mathf.Max(0.1f, simulationTimeScale);
        Time.timeScale = resolvedTimeScale;
        Time.fixedDeltaTime = originalFixedDeltaTime * resolvedTimeScale;
    }

    void RestoreTimeScale()
    {
        if (!timeScaleApplied)
            return;

        Time.timeScale = originalTimeScale;
        Time.fixedDeltaTime = originalFixedDeltaTime;
        timeScaleApplied = false;
    }

    static string ResolveDqnDirectory()
    {
        string root = ResolveDqnRoot();
        Directory.CreateDirectory(root);
        return root;
    }

    static string ResolveDqnRoot()
    {
#if UNITY_EDITOR
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#else
        string projectRoot = Application.persistentDataPath;
#endif
        return Path.Combine(projectRoot, "GOAP_Diagnostics", "DQN");
    }
}
