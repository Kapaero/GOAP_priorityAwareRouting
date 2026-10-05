using System.IO;
using UnityEngine;

// Runtime (non-editor) bootstrap for a standalone build that skips pretraining and
// runs only the 5-architecture comparison batch, loading an already-trained
// Q-table from disk (persistentDataPath, written by an earlier TriageQLearningTrainer
// run) and freezing it before scoring. Quits the process when the batch finishes.
public class TriageHeadlessComparisonBootstrap : MonoBehaviour
{
    static bool launched;

    [SerializeField] TriageExperimentRunner runner;
    // The verbose per-frame GOAP log is only useful for debugging; it costs run time and
    // many GB per campaign. It does not influence the simulation.
    [SerializeField] bool disableGoapDiagnostics = false;

    void Start()
    {
        if (launched)
        {
            Destroy(gameObject);
            return;
        }

        launched = true;

        // "-goapDiagnostics" on the player command line keeps the log (single-run replays only:
        // the log file is shared by all processes of one machine).
        if (disableGoapDiagnostics && !HasCommandLineFlag("-goapDiagnostics"))
            GoapDiagnostics.ForcedOff = true;

        if (runner == null)
            runner = FindFirstObjectByType<TriageExperimentRunner>();

        if (runner == null)
        {
            Debug.LogError("[TriageHeadlessComparisonBootstrap] Missing runner, quitting.");
            Application.Quit(1);
            return;
        }

        DontDestroyOnLoad(gameObject);

        string qTablePath = Path.Combine(Application.persistentDataPath, "GOAP_Diagnostics", "QLearning", "qtable_trained.csv");
        if (TriageQLearningDispatcher.LoadQTable(qTablePath))
            Debug.Log("[TriageHeadlessComparisonBootstrap] Loaded trained Q-table from " + qTablePath);
        else
            Debug.LogWarning("[TriageHeadlessComparisonBootstrap] No trained Q-table found at " + qTablePath);

        TriageQLearningDispatcher.TrainingEnabled = false;

        Debug.Log("[TriageHeadlessComparisonBootstrap] Starting comparison batch.");
        runner.StartExperimentBatch();
    }

    static bool HasCommandLineFlag(string flag)
    {
        foreach (string argument in System.Environment.GetCommandLineArgs())
        {
            if (argument == flag)
                return true;
        }

        return false;
    }

    void Update()
    {
        if (runner != null && runner.IsFinished)
        {
            Debug.Log("[TriageHeadlessComparisonBootstrap] Comparison batch complete, quitting.");
            enabled = false;
            Application.Quit(0);
        }
    }
}
