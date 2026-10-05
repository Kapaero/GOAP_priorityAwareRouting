using System.IO;
using UnityEngine;

// Runtime bootstrap for a standalone build that skips DQN pretraining and runs only
// the comparison batch, loading an already-trained network from disk (persistentDataPath,
// written by an earlier TriageDqnTrainer run) and freezing it before scoring. Used for
// out-of-distribution robustness probes (e.g. burst arrival schedules) where we want
// to evaluate the frozen policy learned under normal training conditions, not retrain.
public class TriageDqnHeadlessComparisonBootstrap : MonoBehaviour
{
    static bool launched;

    [SerializeField] TriageExperimentRunner runner;

    void Start()
    {
        if (launched)
        {
            Destroy(gameObject);
            return;
        }

        launched = true;

        if (runner == null)
            runner = FindFirstObjectByType<TriageExperimentRunner>();

        if (runner == null)
        {
            Debug.LogError("[TriageDqnHeadlessComparisonBootstrap] Missing runner, quitting.");
            Application.Quit(1);
            return;
        }

        DontDestroyOnLoad(gameObject);

        string networkPath = Path.Combine(Application.persistentDataPath, "GOAP_Diagnostics", "DQN", "dqn_network_trained.csv");
        if (TriageDqnDispatcher.LoadNetwork(networkPath))
            Debug.Log("[TriageDqnHeadlessComparisonBootstrap] Loaded trained network from " + networkPath);
        else
            Debug.LogWarning("[TriageDqnHeadlessComparisonBootstrap] No trained network found at " + networkPath);

        TriageDqnDispatcher.TrainingEnabled = false;

        Debug.Log("[TriageDqnHeadlessComparisonBootstrap] Starting comparison batch.");
        runner.StartExperimentBatch();
    }

    void Update()
    {
        if (runner != null && runner.IsFinished)
        {
            Debug.Log("[TriageDqnHeadlessComparisonBootstrap] Comparison batch complete, quitting.");
            enabled = false;
            Application.Quit(0);
        }
    }
}
