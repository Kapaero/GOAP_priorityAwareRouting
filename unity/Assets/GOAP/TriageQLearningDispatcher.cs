using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// Simple tabular Q-learning dispatcher for the wing/route decision every triage
// architecture makes when a patient leaves the waiting queue. Each dispatch is a
// single-step decision whose outcome (total time to go home) is only known much
// later, so this is one-step Q-learning with gamma=0 (Q(s,a) += a * (R - Q(s,a))),
// i.e. a contextual bandit formulation of Q-learning over the same 4 (wing, route)
// options TriagePriorityQueueDispatcher already enumerates.
public static class TriageQLearningDispatcher
{
    public enum DispatchAction
    {
        RightShort = 0,
        LeftShort = 1,
        RightDetour = 2,
        LeftDetour = 3
    }

    const int ActionCount = 4;
    const string CriticalPatientTag = "critical";
    const string CorridorATag = "CorridorA";
    const string CorridorBTag = "CorridorB";
    const string CorridorCTag = "CorridorC";

    struct PendingDecision
    {
        public int stateKey;
        public int action;
        public float dispatchTime;
        public bool critical;
    }

    public static bool TrainingEnabled = true;
    public static float LearningRate = 0.2f;
    public static float Epsilon = 0.3f;
    public static float MinEpsilon = 0.03f;
    public static float EpsilonDecay = 0.999f;
    public static float RewardClampSeconds = 900f;
    public static float CriticalRewardWeight = 1.5f;

    static readonly Dictionary<int, float[]> qTable = new Dictionary<int, float[]>();
    static readonly Dictionary<int, int[]> visitTable = new Dictionary<int, int[]>();
    static readonly Dictionary<int, PendingDecision> pending = new Dictionary<int, PendingDecision>();
    static int episodeCount;
    static StreamWriter decisionWriter;

    public static int EpisodeCount
    {
        get { return episodeCount; }
    }

    public static int StateCount
    {
        get { return qTable.Count; }
    }

    public static bool TryDispatchPatient(
        GameObject patient,
        GInventory inventory,
        out TriageBaselineAgent.ReservedWing reservedWing,
        out TriageBaselineAgent.ExitRoute exitRoute,
        out GameObject cubicle)
    {
        reservedWing = TriageBaselineAgent.ReservedWing.None;
        exitRoute = TriageBaselineAgent.ExitRoute.None;
        cubicle = null;

        if (patient == null || inventory == null)
            return false;

        if (!GWorld.Instance.IsNextPatient(patient))
            return false;

        bool critical = patient.CompareTag(CriticalPatientTag);
        int freeRight = GWorld.Instance.GetFreeCubicleCount();
        int freeLeft = GWorld.Instance.GetFreeCubicleLeftCount();
        bool corridorA = TagIsAvailable(CorridorATag);
        bool corridorB = TagIsAvailable(CorridorBTag);
        bool corridorC = TagIsAvailable(CorridorCTag);

        bool[] valid = new bool[ActionCount];
        valid[(int)DispatchAction.RightShort] = freeRight > 0 && corridorA;
        valid[(int)DispatchAction.LeftShort] = freeLeft > 0 && corridorB;
        valid[(int)DispatchAction.RightDetour] = freeRight > 0 && corridorB && corridorC;
        valid[(int)DispatchAction.LeftDetour] = freeLeft > 0 && corridorA && corridorC;

        if (!valid[0] && !valid[1] && !valid[2] && !valid[3])
            return false;

        int stateKey = EncodeState(critical, freeRight, freeLeft, corridorA, corridorB, corridorC);
        int action = SelectAction(stateKey, valid);

        bool reserved = ResolveWing(action) == TriageBaselineAgent.ReservedWing.Left
            ? CubicleReservation.TryReserveLeftForPatient(patient, inventory, out cubicle)
            : CubicleReservation.TryReserveForPatient(patient, inventory, out cubicle);

        if (!reserved)
            return false;

        reservedWing = ResolveWing(action);
        exitRoute = ResolveRoute(action);

        pending[patient.GetInstanceID()] = new PendingDecision
        {
            stateKey = stateKey,
            action = action,
            dispatchTime = Time.time,
            critical = critical
        };
        episodeCount++;

        GoapDiagnostics.Log(
            "QLearningDispatcher",
            "dispatch patient=" + patient.name
            + " critical=" + critical
            + " action=" + (DispatchAction)action
            + " wing=" + reservedWing
            + " route=" + exitRoute
            + " epsilon=" + Epsilon.ToString("F3", CultureInfo.InvariantCulture)
            + " training=" + TrainingEnabled);

        return true;
    }

    public static void OnPatientCompleted(GameObject patient)
    {
        if (patient == null)
            return;

        int instanceId = patient.GetInstanceID();
        if (!pending.TryGetValue(instanceId, out PendingDecision decision))
            return;

        pending.Remove(instanceId);

        float rawSeconds = Mathf.Clamp(Time.time - decision.dispatchTime, 0f, RewardClampSeconds);
        float weightedSeconds = decision.critical ? rawSeconds * CriticalRewardWeight : rawSeconds;
        float reward = -weightedSeconds;

        float[] qValues = GetOrCreateState(decision.stateKey);
        float qBefore = qValues[decision.action];
        float qAfter = qBefore;

        if (TrainingEnabled)
        {
            qAfter = qBefore + LearningRate * (reward - qBefore);
            qValues[decision.action] = qAfter;
            int[] visits = GetOrCreateVisits(decision.stateKey);
            visits[decision.action]++;
        }

        WriteDecisionRow(decision, reward, qBefore, qAfter);

        GoapDiagnostics.Log(
            "QLearningDispatcher",
            "reward patient=" + patient.name
            + " action=" + (DispatchAction)decision.action
            + " reward=" + reward.ToString("F1", CultureInfo.InvariantCulture)
            + " qBefore=" + qBefore.ToString("F2", CultureInfo.InvariantCulture)
            + " qAfter=" + qAfter.ToString("F2", CultureInfo.InvariantCulture));
    }

    public static void ResetForExperiment()
    {
        pending.Clear();
    }

    public static void ResetQTable(float startEpsilon)
    {
        qTable.Clear();
        visitTable.Clear();
        pending.Clear();
        episodeCount = 0;
        Epsilon = startEpsilon;
    }

    static int SelectAction(int stateKey, bool[] valid)
    {
        float[] qValues = GetOrCreateState(stateKey);

        if (TrainingEnabled && Random.value < Epsilon)
        {
            int validCount = 0;
            for (int i = 0; i < ActionCount; i++)
                if (valid[i])
                    validCount++;

            int pick = Random.Range(0, validCount);
            for (int i = 0; i < ActionCount; i++)
            {
                if (!valid[i])
                    continue;

                if (pick == 0)
                {
                    DecayEpsilon();
                    return i;
                }

                pick--;
            }
        }

        int bestAction = -1;
        float bestValue = float.NegativeInfinity;
        for (int i = 0; i < ActionCount; i++)
        {
            if (!valid[i])
                continue;

            if (bestAction < 0 || qValues[i] > bestValue)
            {
                bestAction = i;
                bestValue = qValues[i];
            }
        }

        DecayEpsilon();
        return bestAction;
    }

    static void DecayEpsilon()
    {
        if (!TrainingEnabled)
            return;

        Epsilon = Mathf.Max(MinEpsilon, Epsilon * EpsilonDecay);
    }

    static TriageBaselineAgent.ReservedWing ResolveWing(int action)
    {
        switch ((DispatchAction)action)
        {
            case DispatchAction.RightShort:
            case DispatchAction.RightDetour:
                return TriageBaselineAgent.ReservedWing.Right;
            case DispatchAction.LeftShort:
            case DispatchAction.LeftDetour:
                return TriageBaselineAgent.ReservedWing.Left;
            default:
                return TriageBaselineAgent.ReservedWing.None;
        }
    }

    static TriageBaselineAgent.ExitRoute ResolveRoute(int action)
    {
        switch ((DispatchAction)action)
        {
            case DispatchAction.RightShort:
                return TriageBaselineAgent.ExitRoute.CorridorA;
            case DispatchAction.LeftShort:
                return TriageBaselineAgent.ExitRoute.CorridorB;
            case DispatchAction.RightDetour:
                return TriageBaselineAgent.ExitRoute.CorridorCToB;
            case DispatchAction.LeftDetour:
                return TriageBaselineAgent.ExitRoute.CorridorCToA;
            default:
                return TriageBaselineAgent.ExitRoute.None;
        }
    }

    static int EncodeState(bool critical, int freeRight, int freeLeft, bool corridorA, bool corridorB, bool corridorC)
    {
        int criticalIndex = critical ? 1 : 0;
        int advantageIndex = CubicleAdvantageIndex(freeRight, freeLeft);
        int a = corridorA ? 1 : 0;
        int b = corridorB ? 1 : 0;
        int c = corridorC ? 1 : 0;

        return (((criticalIndex * 3 + advantageIndex) * 2 + a) * 2 + b) * 2 + c;
    }

    static void DecodeState(int stateKey, out bool critical, out int advantageIndex, out bool corridorA, out bool corridorB, out bool corridorC)
    {
        int c = stateKey % 2;
        stateKey /= 2;
        int b = stateKey % 2;
        stateKey /= 2;
        int a = stateKey % 2;
        stateKey /= 2;
        int advantage = stateKey % 3;
        stateKey /= 3;
        int criticalIndex = stateKey;

        critical = criticalIndex == 1;
        advantageIndex = advantage;
        corridorA = a == 1;
        corridorB = b == 1;
        corridorC = c == 1;
    }

    static int CubicleAdvantageIndex(int freeRight, int freeLeft)
    {
        int diff = freeRight - freeLeft;
        if (diff > 0)
            return 2;
        if (diff < 0)
            return 0;
        return 1;
    }

    static float[] GetOrCreateState(int stateKey)
    {
        if (!qTable.TryGetValue(stateKey, out float[] qValues))
        {
            qValues = new float[ActionCount];
            qTable[stateKey] = qValues;
        }

        return qValues;
    }

    static int[] GetOrCreateVisits(int stateKey)
    {
        if (!visitTable.TryGetValue(stateKey, out int[] visits))
        {
            visits = new int[ActionCount];
            visitTable[stateKey] = visits;
        }

        return visits;
    }

    static bool TagIsAvailable(string tag)
    {
        GameObject target = GameObject.FindWithTag(tag);
        return target != null && target.activeInHierarchy;
    }

    public static void BeginLogging(string directory)
    {
        EndLogging();

        Directory.CreateDirectory(directory);
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, "qlearning_decisions_" + timestamp + ".csv");
        decisionWriter = new StreamWriter(path, false);
        decisionWriter.WriteLine(
            "episode_index,simulation_time,critical,cubicle_advantage,corridor_a_open,corridor_b_open,corridor_c_open,state_key,action,action_name,epsilon,training_enabled,reward,q_before,q_after");
    }

    public static void EndLogging()
    {
        if (decisionWriter == null)
            return;

        decisionWriter.Flush();
        decisionWriter.Close();
        decisionWriter = null;
    }

    static void WriteDecisionRow(PendingDecision decision, float reward, float qBefore, float qAfter)
    {
        if (decisionWriter == null)
            return;

        DecodeState(decision.stateKey, out bool critical, out int advantageIndex, out bool corridorA, out bool corridorB, out bool corridorC);

        decisionWriter.WriteLine(
            episodeCount
            + "," + Time.time.ToString("F3", CultureInfo.InvariantCulture)
            + "," + critical
            + "," + (advantageIndex - 1)
            + "," + corridorA
            + "," + corridorB
            + "," + corridorC
            + "," + decision.stateKey
            + "," + decision.action
            + "," + (DispatchAction)decision.action
            + "," + Epsilon.ToString("F4", CultureInfo.InvariantCulture)
            + "," + TrainingEnabled
            + "," + reward.ToString("F2", CultureInfo.InvariantCulture)
            + "," + qBefore.ToString("F2", CultureInfo.InvariantCulture)
            + "," + qAfter.ToString("F2", CultureInfo.InvariantCulture));
    }

    public static void SaveQTable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (StreamWriter writer = new StreamWriter(path, false))
        {
            writer.WriteLine("state_key,q0,q1,q2,q3,visits0,visits1,visits2,visits3");
            foreach (KeyValuePair<int, float[]> entry in qTable)
            {
                int[] visits = GetOrCreateVisits(entry.Key);
                float[] q = entry.Value;
                writer.WriteLine(
                    entry.Key
                    + "," + q[0].ToString("F4", CultureInfo.InvariantCulture)
                    + "," + q[1].ToString("F4", CultureInfo.InvariantCulture)
                    + "," + q[2].ToString("F4", CultureInfo.InvariantCulture)
                    + "," + q[3].ToString("F4", CultureInfo.InvariantCulture)
                    + "," + visits[0]
                    + "," + visits[1]
                    + "," + visits[2]
                    + "," + visits[3]);
            }
        }

        GoapDiagnostics.Log("QLearningDispatcher", "saved q-table path=" + path + " states=" + qTable.Count);
    }

    public static bool LoadQTable(string path)
    {
        if (!File.Exists(path))
            return false;

        qTable.Clear();
        visitTable.Clear();

        string[] lines = File.ReadAllLines(path);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] parts = line.Split(',');
            if (parts.Length < 9)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stateKey))
                continue;

            float[] q = new float[ActionCount];
            int[] visits = new int[ActionCount];
            for (int a = 0; a < ActionCount; a++)
            {
                float.TryParse(parts[1 + a], NumberStyles.Float, CultureInfo.InvariantCulture, out q[a]);
                int.TryParse(parts[5 + a], NumberStyles.Integer, CultureInfo.InvariantCulture, out visits[a]);
            }

            qTable[stateKey] = q;
            visitTable[stateKey] = visits;
        }

        GoapDiagnostics.Log("QLearningDispatcher", "loaded q-table path=" + path + " states=" + qTable.Count);
        return true;
    }

    public static void WriteSnapshot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (StreamWriter writer = new StreamWriter(path, false))
        {
            writer.WriteLine(
                "state_key,critical,cubicle_advantage,corridor_a_open,corridor_b_open,corridor_c_open,q_right_short,q_left_short,q_right_detour,q_left_detour,visits_total,best_action");

            List<int> keys = new List<int>(qTable.Keys);
            keys.Sort();
            foreach (int stateKey in keys)
            {
                float[] q = qTable[stateKey];
                int[] visits = GetOrCreateVisits(stateKey);
                int totalVisits = visits[0] + visits[1] + visits[2] + visits[3];
                DecodeState(stateKey, out bool critical, out int advantageIndex, out bool corridorA, out bool corridorB, out bool corridorC);

                int bestAction = 0;
                for (int a = 1; a < ActionCount; a++)
                    if (q[a] > q[bestAction])
                        bestAction = a;

                writer.WriteLine(
                    stateKey
                    + "," + critical
                    + "," + (advantageIndex - 1)
                    + "," + corridorA
                    + "," + corridorB
                    + "," + corridorC
                    + "," + q[0].ToString("F3", CultureInfo.InvariantCulture)
                    + "," + q[1].ToString("F3", CultureInfo.InvariantCulture)
                    + "," + q[2].ToString("F3", CultureInfo.InvariantCulture)
                    + "," + q[3].ToString("F3", CultureInfo.InvariantCulture)
                    + "," + totalVisits
                    + "," + (DispatchAction)bestAction);
            }
        }

        GoapDiagnostics.Log("QLearningDispatcher", "wrote snapshot path=" + path + " states=" + qTable.Count);
    }
}
