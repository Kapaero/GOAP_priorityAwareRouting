using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Autonomous entry point for running the full research pipeline unattended:
// pretrain TriageQLearningDispatcher, then hand off to TriageExperimentRunner for
// the 5-architecture comparison batch, then quit the process (when run from the
// command line via -batchmode). Never saves the scene to disk; all configuration
// happens in-memory on the already-open editor scene.
public static class HeadlessPipelineRunner
{
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const double MaxWallClockSeconds = 3f * 60f * 60f;

    enum Phase
    {
        Idle,
        Training,
        Comparing
    }

    static Phase phase = Phase.Idle;
    static TriageQLearningTrainer trainerRef;
    static TriageExperimentRunner runnerRef;
    static double startTime;

    [MenuItem("GOAP/Run Full Headless Pipeline")]
    public static void RunFull()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[HeadlessPipelineRunner] Already in play mode, aborting.");
            return;
        }

        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.update -= OnUpdate;

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ConfigureRunnerInEditMode();

        phase = Phase.Idle;
        startTime = EditorApplication.timeSinceStartup;
        Debug.Log("[HeadlessPipelineRunner] Entering play mode to run pretraining + comparison batch.");
        EditorApplication.isPlaying = true;
    }

    static void ConfigureRunnerInEditMode()
    {
        TriageExperimentRunner runner = Object.FindFirstObjectByType<TriageExperimentRunner>();
        if (runner == null)
        {
            Debug.LogError("[HeadlessPipelineRunner] No TriageExperimentRunner found in " + ScenePath);
            return;
        }

        SerializedObject serializedRunner = new SerializedObject(runner);
        serializedRunner.FindProperty("autoStartOnPlay").boolValue = false;
        serializedRunner.FindProperty("experimentLabel").stringValue = "triage_five_model_comparison";

        SerializedProperty architecturesProp = serializedRunner.FindProperty("architectures");
        architecturesProp.arraySize = 5;
        for (int i = 0; i < 5; i++)
            architecturesProp.GetArrayElementAtIndex(i).intValue = i;

        serializedRunner.ApplyModifiedProperties();

        TriageQLearningTrainer trainer = Object.FindFirstObjectByType<TriageQLearningTrainer>();
        if (trainer == null)
        {
            GameObject trainerObject = new GameObject("QLearningTrainer (Headless)");
            trainer = trainerObject.AddComponent<TriageQLearningTrainer>();
        }

        SerializedObject serializedTrainer = new SerializedObject(trainer);
        serializedTrainer.FindProperty("autoStartOnPlay").boolValue = false;
        serializedTrainer.ApplyModifiedProperties();

        Debug.Log("[HeadlessPipelineRunner] Configured runner (5 architectures) and trainer in memory (not saved to scene).");
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode)
            return;

        trainerRef = Object.FindFirstObjectByType<TriageQLearningTrainer>();
        runnerRef = Object.FindFirstObjectByType<TriageExperimentRunner>();

        if (trainerRef == null || runnerRef == null)
        {
            Debug.LogError("[HeadlessPipelineRunner] Missing trainer or runner after entering play mode, aborting.");
            Finish(false);
            return;
        }

        Debug.Log("[HeadlessPipelineRunner] Play mode entered, starting Q-learning pretraining.");
        trainerRef.StartTrainingBatch();
        phase = Phase.Training;
        EditorApplication.update += OnUpdate;
    }

    static void OnUpdate()
    {
        if (EditorApplication.timeSinceStartup - startTime > MaxWallClockSeconds)
        {
            Debug.LogError("[HeadlessPipelineRunner] Exceeded max wall-clock budget, aborting.");
            Finish(false);
            return;
        }

        switch (phase)
        {
            case Phase.Training:
                if (trainerRef != null && trainerRef.IsFinished)
                {
                    Debug.Log("[HeadlessPipelineRunner] Pretraining complete, starting 5-architecture comparison batch.");
                    runnerRef.StartExperimentBatch();
                    phase = Phase.Comparing;
                }
                break;

            case Phase.Comparing:
                if (runnerRef != null && runnerRef.IsFinished)
                {
                    Debug.Log("[HeadlessPipelineRunner] Comparison batch complete.");
                    Finish(true);
                }
                break;
        }
    }

    static void Finish(bool success)
    {
        EditorApplication.update -= OnUpdate;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        phase = Phase.Idle;

        Debug.Log("[HeadlessPipelineRunner] Pipeline finished success=" + success);

        if (Application.isBatchMode)
        {
            EditorApplication.Exit(success ? 0 : 1);
            return;
        }

        if (EditorApplication.isPlaying)
            EditorApplication.isPlaying = false;
    }
}
