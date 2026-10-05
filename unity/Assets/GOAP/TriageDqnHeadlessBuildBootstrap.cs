using UnityEngine;

// Runtime chain orchestrator for a standalone headless build: runs TriageDqnTrainer
// to completion, then TriageExperimentRunner's comparison batch, then quits.
// Mirrors TriageHeadlessBuildBootstrap but for the neural-network dispatcher.
public class TriageDqnHeadlessBuildBootstrap : MonoBehaviour
{
    enum Phase
    {
        Training,
        Comparing,
        Done
    }

    static bool launched;

    [SerializeField] TriageDqnTrainer trainer;
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
            trainer = FindFirstObjectByType<TriageDqnTrainer>();
        if (runner == null)
            runner = FindFirstObjectByType<TriageExperimentRunner>();

        if (trainer == null || runner == null)
        {
            Debug.LogError("[TriageDqnHeadlessBuildBootstrap] Missing trainer or runner, quitting.");
            Application.Quit(1);
            return;
        }

        DontDestroyOnLoad(gameObject);
        Debug.Log("[TriageDqnHeadlessBuildBootstrap] Starting DQN pretraining.");
        trainer.StartTrainingBatch();
    }

    void Update()
    {
        switch (phase)
        {
            case Phase.Training:
                if (trainer.IsFinished)
                {
                    Debug.Log("[TriageDqnHeadlessBuildBootstrap] Pretraining complete, starting comparison batch.");
                    runner.StartExperimentBatch();
                    phase = Phase.Comparing;
                }
                break;

            case Phase.Comparing:
                if (runner.IsFinished)
                {
                    Debug.Log("[TriageDqnHeadlessBuildBootstrap] Comparison batch complete, quitting.");
                    phase = Phase.Done;
                    Application.Quit(0);
                }
                break;
        }
    }
}
