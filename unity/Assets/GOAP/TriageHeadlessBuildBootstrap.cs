using UnityEngine;

// Runtime (non-editor) chain orchestrator for a standalone headless build: runs
// TriageQLearningTrainer to completion, then TriageExperimentRunner's comparison
// batch, then quits the process. Only ever added to the dedicated headless build
// scene by HeadlessPlayerBuilder, never to the real SampleScene asset.
//
// Both the trainer and the runner reload the scene via SceneManager.LoadScene for
// every run, which destroys and recreates this GameObject too (it has no
// DontDestroyOnLoad of its own). Start() therefore fires once per scene reload;
// a static latch makes sure the batch is only ever launched once per process.
public class TriageHeadlessBuildBootstrap : MonoBehaviour
{
    enum Phase
    {
        Training,
        Comparing,
        Done
    }

    static bool launched;

    [SerializeField] TriageQLearningTrainer trainer;
    [SerializeField] TriageExperimentRunner runner;

    Phase phase = Phase.Training;

    void Start()
    {
        if (launched)
        {
            Destroy(gameObject);
            return;
        }

        launched = true;

        if (trainer == null)
            trainer = FindFirstObjectByType<TriageQLearningTrainer>();
        if (runner == null)
            runner = FindFirstObjectByType<TriageExperimentRunner>();

        if (trainer == null || runner == null)
        {
            Debug.LogError("[TriageHeadlessBuildBootstrap] Missing trainer or runner, quitting.");
            Application.Quit(1);
            return;
        }

        DontDestroyOnLoad(gameObject);
        Debug.Log("[TriageHeadlessBuildBootstrap] Starting Q-learning pretraining.");
        trainer.StartTrainingBatch();
    }

    void Update()
    {
        switch (phase)
        {
            case Phase.Training:
                if (trainer.IsFinished)
                {
                    Debug.Log("[TriageHeadlessBuildBootstrap] Pretraining complete, starting comparison batch.");
                    runner.StartExperimentBatch();
                    phase = Phase.Comparing;
                }
                break;

            case Phase.Comparing:
                if (runner.IsFinished)
                {
                    Debug.Log("[TriageHeadlessBuildBootstrap] Comparison batch complete, quitting.");
                    phase = Phase.Done;
                    Application.Quit(0);
                }
                break;
        }
    }
}
