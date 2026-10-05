using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Builds a standalone Windows player that runs the Q-learning pretraining and the
// 5-architecture comparison batch on its own (via TriageHeadlessBuildBootstrap),
// then quits. Building + running a standalone player headlessly is the officially
// supported way to run Unity content unattended (unlike entering Play Mode inside
// -batchmode, which relies on Editor-only systems such as the Shortcut Manager and
// can hang without a real interactive input device). Writes a *copy* of the scene
// under a new name so SampleScene.unity itself is never modified.
public static class HeadlessPlayerBuilder
{
    const string SourceScenePath = "Assets/Scenes/SampleScene.unity";
    const string BuildScenePath = "Assets/Scenes/HeadlessTriagePipeline.unity";
    const string ComparisonOnlyBuildScenePath = "Assets/Scenes/HeadlessTriageComparisonOnly.unity";
    const string StressTestBuildScenePath = "Assets/Scenes/HeadlessTriageStressTest.unity";
    const string WideRangeBuildScenePath = "Assets/Scenes/HeadlessTriageWideRangeTraining.unity";
    const string DqnBuildScenePath = "Assets/Scenes/HeadlessTriageDqn.unity";
    const string BurstRobustnessBuildScenePath = "Assets/Scenes/HeadlessTriageBurstRobustness.unity";
    const string ReplicationBuildScenePath = "Assets/Scenes/HeadlessTriageMimicIvEdReplication.unity";

    static readonly int[] FourBaselineArchitectures =
    {
        (int)TriageExperimentArchitecture.FsmReactiveController,
        (int)TriageExperimentArchitecture.PriorityQueueDispatcher,
        (int)TriageExperimentArchitecture.DecisionTableController,
        (int)TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner,
    };

    static readonly int[] AllFiveArchitectures = { 0, 1, 2, 3, 4 };
    static readonly int[] GoapVsQLearning =
    {
        (int)TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner,
        (int)TriageExperimentArchitecture.QLearningDispatcher
    };
    static readonly int[] GoapVsDqn =
    {
        (int)TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner,
        (int)TriageExperimentArchitecture.DeepQLearningDispatcher
    };

    [MenuItem("GOAP/Build Headless Pipeline Player (Train + Compare)")]
    public static void Build()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            BuildScenePath, "triage_five_model_comparison_headless_build", AllFiveArchitectures, null, 0f);
        if (runner == null)
            return;

        TriageQLearningTrainer trainer = Object.FindFirstObjectByType<TriageQLearningTrainer>();
        if (trainer == null)
        {
            GameObject trainerObject = new GameObject("QLearningTrainer (Headless Build)");
            trainer = trainerObject.AddComponent<TriageQLearningTrainer>();
        }

        SerializedObject serializedTrainer = new SerializedObject(trainer);
        serializedTrainer.FindProperty("autoStartOnPlay").boolValue = false;
        serializedTrainer.ApplyModifiedProperties();

        GameObject bootstrapObject = new GameObject("HeadlessBuildBootstrap");
        TriageHeadlessBuildBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessBuildBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("trainer").objectReferenceValue = trainer;
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, BuildScenePath);
    }

    [MenuItem("GOAP/Build Headless Pipeline Player (Compare Only, Reuse Trained Q-Table)")]
    public static void BuildComparisonOnly()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            ComparisonOnlyBuildScenePath, "triage_five_model_comparison_headless_build", AllFiveArchitectures, null, 0f);
        if (runner == null)
            return;

        GameObject bootstrapObject = new GameObject("HeadlessComparisonBootstrap");
        TriageHeadlessComparisonBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessComparisonBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, ComparisonOnlyBuildScenePath);
    }

    // High-load stress test: GOAP vs the already-trained/frozen Q-learning dispatcher
    // only, at schedule multipliers below 1.0 (i.e. busier than the historical MIMIC
    // arrival rate), to check whether GOAP's replanning advantage (if any) shows up
    // specifically under heavier-than-historical load. completionTimeoutSeconds caps
    // each run in case a load level makes the queue genuinely unstable (arrivals
    // outpacing service capacity forever).
    [MenuItem("GOAP/Build Headless Pipeline Player (Stress Test: GOAP vs Q-Learning, High Load)")]
    public static void BuildStressTestComparisonOnly()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            StressTestBuildScenePath,
            "triage_stress_test_goap_vs_qlearning",
            GoapVsQLearning,
            new[] { 0.75f, 0.5f, 0.3f },
            3600f);
        if (runner == null)
            return;

        GameObject bootstrapObject = new GameObject("HeadlessComparisonBootstrap");
        TriageHeadlessComparisonBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessComparisonBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, StressTestBuildScenePath);
    }

    // Retrains the Q-table from scratch across a load range that spans from extreme
    // (0.3x, well above historical MIMIC intensity) to comfortable (2.5x), so the
    // agent actually experiences the corridor/wing closures that only show up under
    // real congestion, then compares GOAP vs the newly trained policy across the
    // same spread to see whether training on extreme load fixes the generalization
    // gap found in the narrower-range-trained table (which only ever saw 6 of 48
    // possible states, all with every corridor open).
    [MenuItem("GOAP/Build Headless Pipeline Player (Train Wide Range Incl. Extreme Load)")]
    public static void BuildWideRangeTrainAndStressTest()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            WideRangeBuildScenePath,
            "triage_wide_range_trained_goap_vs_qlearning",
            GoapVsQLearning,
            new[] { 0.3f, 0.5f, 0.75f, 1.0f, 1.5f, 2.0f },
            3600f);
        if (runner == null)
            return;

        TriageQLearningTrainer trainer = Object.FindFirstObjectByType<TriageQLearningTrainer>();
        if (trainer == null)
        {
            GameObject trainerObject = new GameObject("QLearningTrainer (Headless Build)");
            trainer = trainerObject.AddComponent<TriageQLearningTrainer>();
        }

        SerializedObject serializedTrainer = new SerializedObject(trainer);
        serializedTrainer.FindProperty("autoStartOnPlay").boolValue = false;
        serializedTrainer.FindProperty("resetQTableAtStart").boolValue = true;
        serializedTrainer.FindProperty("trainingRunCount").intValue = 18;
        // Must match (or exceed) the evaluation timeout below: a shorter training
        // timeout was tried first and cut extreme-load training runs off before the
        // hospital's flow control ever escalated into closing corridors/wing entry,
        // so the agent never actually saw the states it needed to learn.
        serializedTrainer.FindProperty("completionTimeoutSeconds").floatValue = 3600f;

        SerializedProperty loadProp = serializedTrainer.FindProperty("loadMultipliers");
        float[] trainingLoads = { 0.3f, 0.4f, 0.5f, 0.6f, 0.75f, 0.9f, 1.0f, 1.2f, 1.5f, 1.8f, 2.0f, 2.5f };
        loadProp.arraySize = trainingLoads.Length;
        for (int i = 0; i < trainingLoads.Length; i++)
            loadProp.GetArrayElementAtIndex(i).floatValue = trainingLoads[i];
        serializedTrainer.ApplyModifiedProperties();

        GameObject bootstrapObject = new GameObject("HeadlessBuildBootstrap");
        TriageHeadlessBuildBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessBuildBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("trainer").objectReferenceValue = trainer;
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, WideRangeBuildScenePath);
    }

    // Trains the small hand-rolled neural-network dispatcher (TriageDqnDispatcher)
    // across the same wide load range (0.3x-2.5x) and compares it against GOAP
    // across 0.3x-2.0x, to test whether feeding continuous congestion signals
    // (queue depth, wing occupancy) instead of hand-bucketed discrete states lets
    // the learned policy generalize to extreme load the way the tabular Q-table
    // could not.
    [MenuItem("GOAP/Build Headless Pipeline Player (DQN: Train Wide Range + Compare vs GOAP)")]
    public static void BuildDqnWideRangeTrainAndCompare()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            DqnBuildScenePath,
            "triage_wide_range_trained_goap_vs_dqn",
            GoapVsDqn,
            new[] { 0.3f, 0.5f, 0.75f, 1.0f, 1.5f, 2.0f },
            3600f);
        if (runner == null)
            return;

        TriageDqnTrainer trainer = Object.FindFirstObjectByType<TriageDqnTrainer>();
        if (trainer == null)
        {
            GameObject trainerObject = new GameObject("DqnTrainer (Headless Build)");
            trainer = trainerObject.AddComponent<TriageDqnTrainer>();
        }

        SerializedObject serializedTrainer = new SerializedObject(trainer);
        serializedTrainer.FindProperty("autoStartOnPlay").boolValue = false;
        serializedTrainer.FindProperty("resetNetworkAtStart").boolValue = true;
        serializedTrainer.FindProperty("trainingRunCount").intValue = 18;
        serializedTrainer.FindProperty("completionTimeoutSeconds").floatValue = 3600f;

        SerializedProperty loadProp = serializedTrainer.FindProperty("loadMultipliers");
        float[] trainingLoads = { 0.3f, 0.4f, 0.5f, 0.6f, 0.75f, 0.9f, 1.0f, 1.2f, 1.5f, 1.8f, 2.0f, 2.5f };
        loadProp.arraySize = trainingLoads.Length;
        for (int i = 0; i < trainingLoads.Length; i++)
            loadProp.GetArrayElementAtIndex(i).floatValue = trainingLoads[i];
        serializedTrainer.ApplyModifiedProperties();

        GameObject bootstrapObject = new GameObject("DqnHeadlessBuildBootstrap");
        TriageDqnHeadlessBuildBootstrap bootstrap = bootstrapObject.AddComponent<TriageDqnHeadlessBuildBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("trainer").objectReferenceValue = trainer;
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, DqnBuildScenePath);
    }

    // Out-of-distribution robustness probe: reuses the already-trained/frozen DQN
    // network (no retraining) and GOAP (no training needed), but points the spawner
    // at a synthetic burst-arrival schedule (mass-casualty-style clusters + jittered
    // timing + elevated critical fraction inside bursts) that neither ever saw during
    // training on the smooth historical MIMIC schedule. Tests generalization vs.
    // overfitting to the specific training arrival pattern.
    [MenuItem("GOAP/Build Headless Pipeline Player (Burst Robustness: GOAP vs Trained DQN)")]
    public static void BuildDqnBurstRobustnessTest()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            BurstRobustnessBuildScenePath,
            "triage_burst_robustness_goap_vs_dqn",
            GoapVsDqn,
            new[] { 1.0f },
            3600f);
        if (runner == null)
            return;

        SerializedObject serializedRunner = new SerializedObject(runner);
        serializedRunner.FindProperty("mimicScheduleResourcePath").stringValue = "MimicArrivals/mimic_burst_robustness_test";
        serializedRunner.ApplyModifiedProperties();

        GameObject bootstrapObject = new GameObject("DqnHeadlessComparisonBootstrap");
        TriageDqnHeadlessComparisonBootstrap bootstrap = bootstrapObject.AddComponent<TriageDqnHeadlessComparisonBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, BurstRobustnessBuildScenePath);
    }

    // Honest-replication campaign: GOAP vs the three deterministic baselines (no
    // Q-learning/DQN, matching the ETASR paper's compared controllers), swept across
    // 20 independently bootstrap-resampled MIMIC-IV-ED demo cohorts (real PhysioNet
    // 222-stay pool, see Analysis/generate_mimic_iv_ed_seeds.py) x the paper's 4 load
    // multipliers, so significance can be computed at the seed level instead of the
    // single-run patient level.
    [MenuItem("GOAP/Build Headless Pipeline Player (MIMIC-IV-ED 20-Seed Replication)")]
    public static void BuildMimicIvEdReplication()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        string[] schedulePaths = new string[20];
        for (int i = 0; i < 20; i++)
            schedulePaths[i] = $"MimicArrivals/seeds/mimic_iv_ed_demo_seed_{i:00}";

        TriageExperimentRunner runner = ConfigureRunner(
            ReplicationBuildScenePath,
            "triage_mimic_iv_ed_replication_20seeds",
            FourBaselineArchitectures,
            new[] { 2.0f, 1.5f, 1.25f, 1.0f },
            6000f,
            schedulePaths,
            20f);
        if (runner == null)
            return;

        GameObject bootstrapObject = new GameObject("HeadlessComparisonBootstrap");
        TriageHeadlessComparisonBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessComparisonBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, ReplicationBuildScenePath);
    }

    // Quick isolated smoke test: load=2.00 only, 1 seed, all 4 architectures (4
    // runs total instead of 320). Builds into a SEPARATE output directory so it
    // can run alongside the full 20-seed replication build without file-lock
    // conflicts or touching its data. Used to check whether raising
    // GAgent.MaxPlanRequestsPerFrame/MaxSetDestinationRequestsPerFrame actually
    // fixes GOAP's normal-patient slowdown at load=2.00 before committing to a
    // full ~14h re-run.
    [MenuItem("GOAP/Build Headless Pipeline Player (MIMIC-IV-ED Load2 Quick Test)")]
    public static void BuildMimicIvEdReplicationQuickTest()
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        string[] schedulePaths = { "MimicArrivals/seeds/mimic_iv_ed_demo_seed_00" };

        TriageExperimentRunner runner = ConfigureRunner(
            ReplicationBuildScenePath,
            "triage_mimic_iv_ed_load2_quicktest",
            FourBaselineArchitectures,
            new[] { 2.0f },
            6000f,
            schedulePaths,
            20f);
        if (runner == null)
            return;

        GameObject bootstrapObject = new GameObject("HeadlessComparisonBootstrap");
        TriageHeadlessComparisonBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessComparisonBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, ReplicationBuildScenePath, "HeadlessTriageQuickTest");
    }

    // ---- NHAMCS-based replication: real ED visit cohorts, deterministic fixed-step runs ----
    const string NhamcsBuildScenePath = "Assets/Scenes/HeadlessTriageNhamcs.unity";

    // Offered load u = mean arrival rate relative to the wing's measured saturated throughput
    // (about 6 patients/min). Schedules are written on a 1200 s base window (15 patients per
    // minute, i.e. u = 2.5), so the runner's time multiplier is m = 2.5 / u.
    static float[] NhamcsLoadTimeMultipliers()
    {
        // Light loads first: they are the new, longest runs and the most valuable part of the sweep.
        float[] offeredLoads = { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 2.5f };
        float[] multipliers = new float[offeredLoads.Length];
        for (int i = 0; i < offeredLoads.Length; i++)
            multipliers[i] = 2.5f / offeredLoads[i];
        return multipliers;
    }

    static string[] NhamcsSchedulePaths(int count)
    {
        string[] paths = new string[count];
        for (int i = 0; i < count; i++)
            paths[i] = $"NhamcsArrivals/seeds/nhamcs_seed_{i:00}";
        return paths;
    }

    static void BuildNhamcsCampaign(string label, float[] loadMultipliers, int seedCount, string buildDirName)
    {
        Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);

        TriageExperimentRunner runner = ConfigureRunner(
            NhamcsBuildScenePath,
            label,
            FourBaselineArchitectures,
            loadMultipliers,
            15000f,
            NhamcsSchedulePaths(seedCount),
            -1f,
            1f / 60f);
        if (runner == null)
            return;

        GameObject bootstrapObject = new GameObject("HeadlessComparisonBootstrap");
        TriageHeadlessComparisonBootstrap bootstrap = bootstrapObject.AddComponent<TriageHeadlessComparisonBootstrap>();
        SerializedObject serializedBootstrap = new SerializedObject(bootstrap);
        serializedBootstrap.FindProperty("runner").objectReferenceValue = runner;
        serializedBootstrap.FindProperty("disableGoapDiagnostics").boolValue = true;
        serializedBootstrap.ApplyModifiedProperties();

        SaveCopyAndBuild(scene, NhamcsBuildScenePath, buildDirName);
    }

    // The player accepts "-seedRange A B" (campaign shard), "-runIndex G" (one run, e.g. a repeat
    // of a run with "-goapDiagnostics") and must be started with -job-worker-count 0.
    [MenuItem("GOAP/Build Headless Pipeline Player (NHAMCS 16-Seed Campaign)")]
    public static void BuildNhamcsReplication()
    {
        BuildNhamcsCampaign("triage_nhamcs_main", NhamcsLoadTimeMultipliers(), 16, "HeadlessTriageNhamcsMain");
    }

    // Small build for the determinism check and pilot: 2 seeds, 3 loads (u = 2.5, 1.0, 0.5).
    [MenuItem("GOAP/Build Headless Pipeline Player (NHAMCS Pilot)")]
    public static void BuildNhamcsPilot()
    {
        BuildNhamcsCampaign("triage_nhamcs_pilot", new[] { 1.0f, 2.5f, 5.0f }, 2, "HeadlessTriageNhamcsPilot");
    }

    static TriageExperimentRunner ConfigureRunner(
        string buildScenePath,
        string experimentLabel,
        int[] architectureValues,
        float[] loadMultipliers,
        float completionTimeoutSeconds,
        string[] schedulePaths = null,
        float simulationTimeScale = -1f,
        float fixedSimulationStepSeconds = 0f)
    {
        TriageExperimentRunner runner = Object.FindFirstObjectByType<TriageExperimentRunner>();
        if (runner == null)
        {
            Debug.LogError("[HeadlessPlayerBuilder] No TriageExperimentRunner found in " + SourceScenePath);
            return null;
        }

        SerializedObject serializedRunner = new SerializedObject(runner);
        serializedRunner.FindProperty("autoStartOnPlay").boolValue = false;
        serializedRunner.FindProperty("experimentLabel").stringValue = experimentLabel;
        serializedRunner.FindProperty("completionTimeoutSeconds").floatValue = completionTimeoutSeconds;
        // The runner's sceneName was previously hand-configured to "SampleScene" and is
        // serialized as non-empty, so its own Awake() auto-resolve (which only fires
        // when the field is empty) never kicks in. A standalone build only contains the
        // scene(s) explicitly passed to BuildPipeline.BuildPlayer, so it must be pointed
        // explicitly at this build's own scene name or SceneManager.LoadScene fails.
        serializedRunner.FindProperty("sceneName").stringValue = Path.GetFileNameWithoutExtension(buildScenePath);

        SerializedProperty architecturesProp = serializedRunner.FindProperty("architectures");
        architecturesProp.arraySize = architectureValues.Length;
        for (int i = 0; i < architectureValues.Length; i++)
            architecturesProp.GetArrayElementAtIndex(i).intValue = architectureValues[i];

        if (loadMultipliers != null)
        {
            SerializedProperty loadProp = serializedRunner.FindProperty("mimicScheduleTimeMultipliers");
            loadProp.arraySize = loadMultipliers.Length;
            for (int i = 0; i < loadMultipliers.Length; i++)
                loadProp.GetArrayElementAtIndex(i).floatValue = loadMultipliers[i];
        }

        if (schedulePaths != null)
        {
            SerializedProperty scheduleProp = serializedRunner.FindProperty("mimicScheduleResourcePaths");
            scheduleProp.arraySize = schedulePaths.Length;
            for (int i = 0; i < schedulePaths.Length; i++)
                scheduleProp.GetArrayElementAtIndex(i).stringValue = schedulePaths[i];
        }

        if (simulationTimeScale > 0f)
            serializedRunner.FindProperty("simulationTimeScale").floatValue = simulationTimeScale;

        serializedRunner.FindProperty("fixedSimulationStepSeconds").floatValue = fixedSimulationStepSeconds;

        serializedRunner.ApplyModifiedProperties();

        return runner;
    }

    static void SaveCopyAndBuild(Scene scene, string buildScenePath)
    {
        SaveCopyAndBuild(scene, buildScenePath, "HeadlessTriage");
    }

    static void SaveCopyAndBuild(Scene scene, string buildScenePath, string buildDirName)
    {
        bool saved = EditorSceneManager.SaveScene(scene, buildScenePath, saveAsCopy: true);
        if (!saved)
        {
            Debug.LogError("[HeadlessPlayerBuilder] Failed to save build scene copy to " + buildScenePath);
            return;
        }

        Debug.Log("[HeadlessPlayerBuilder] Wrote build scene copy to " + buildScenePath + " (source scene on disk untouched).");

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string buildDir = Path.Combine(projectRoot, "Builds", buildDirName);
        Directory.CreateDirectory(buildDir);
        string exePath = Path.Combine(buildDir, "HeadlessTriage.exe");

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { buildScenePath },
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log("[HeadlessPlayerBuilder] Build result=" + report.summary.result + " totalErrors=" + report.summary.totalErrors + " output=" + exePath);
    }
}
