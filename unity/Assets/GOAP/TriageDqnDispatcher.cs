using System.Globalization;
using System.IO;
using UnityEngine;

// Deep Q-learning version of TriageQLearningDispatcher: same wing/route decision,
// same one-step (gamma=0) Q-learning update rule, but the Q-function is a small
// hand-rolled MLP taking continuous congestion signals as input (queue depth,
// wing occupancy, free cubicles) instead of a lookup table over hand-bucketed
// discrete states. The goal is to let the network learn to recognize "the system
// is getting overloaded" from raw signals, rather than being blind to anything
// that wasn't hand-picked into a discrete state key.
public static class TriageDqnDispatcher
{
    public enum DispatchAction
    {
        RightShort = 0,
        LeftShort = 1,
        RightDetour = 2,
        LeftDetour = 3
    }

    const int ActionCount = 4;
    const int InputCount = 9;
    const int Hidden1Count = 16;
    const int Hidden2Count = 16;

    const string CriticalPatientTag = "critical";
    const string CorridorATag = "CorridorA";
    const string CorridorBTag = "CorridorB";
    const string CorridorCTag = "CorridorC";

    // Fixed normalization constants for this hospital layout (15 cubicles per wing,
    // 30-bed wing capacity, ~300 patients per experiment run). Only affects input
    // scaling, not correctness, if the scene's real capacity differs slightly.
    const float MaxCubiclesPerWing = 15f;
    const float MaxWingCapacity = 30f;
    const float MaxQueueNormalization = 300f;

    struct PendingDecision
    {
        public float[] features;
        public int action;
        public float dispatchTime;
        public bool critical;
    }

    public static bool TrainingEnabled = true;
    public static float LearningRate = 0.01f;
    public static float Epsilon = 0.3f;
    public static float MinEpsilon = 0.03f;
    public static float EpsilonDecay = 0.999f;
    public static float RewardClampSeconds = 900f;
    public static float CriticalRewardWeight = 1.5f;

    static float[,] weights1 = new float[InputCount, Hidden1Count];
    static float[] bias1 = new float[Hidden1Count];
    static float[,] weights2 = new float[Hidden1Count, Hidden2Count];
    static float[] bias2 = new float[Hidden2Count];
    static float[,] weights3 = new float[Hidden2Count, ActionCount];
    static float[] bias3 = new float[ActionCount];

    // Adam optimizer moment estimates, mirroring each weight/bias array above.
    static float[,] m1, v1, m2, v2, m3, v3;
    static float[] mb1, vb1, mb2, vb2, mb3, vb3;
    static int adamStep;
    const float Beta1 = 0.9f;
    const float Beta2 = 0.999f;
    const float AdamEpsilon = 1e-8f;

    static readonly System.Collections.Generic.Dictionary<int, PendingDecision> pending =
        new System.Collections.Generic.Dictionary<int, PendingDecision>();
    static int episodeCount;
    static StreamWriter decisionWriter;
    static bool initialized;

    public static int EpisodeCount
    {
        get { return episodeCount; }
    }

    static TriageDqnDispatcher()
    {
        InitializeNetwork(System.Environment.TickCount);
    }

    public static void InitializeNetwork(int seed)
    {
        System.Random rng = new System.Random(seed);
        InitLayer(weights1, bias1, rng);
        InitLayer(weights2, bias2, rng);
        InitLayer(weights3, bias3, rng);

        m1 = new float[InputCount, Hidden1Count]; v1 = new float[InputCount, Hidden1Count];
        m2 = new float[Hidden1Count, Hidden2Count]; v2 = new float[Hidden1Count, Hidden2Count];
        m3 = new float[Hidden2Count, ActionCount]; v3 = new float[Hidden2Count, ActionCount];
        mb1 = new float[Hidden1Count]; vb1 = new float[Hidden1Count];
        mb2 = new float[Hidden2Count]; vb2 = new float[Hidden2Count];
        mb3 = new float[ActionCount]; vb3 = new float[ActionCount];
        adamStep = 0;
        initialized = true;
    }

    static void InitLayer(float[,] weights, float[] bias, System.Random rng)
    {
        int fanIn = weights.GetLength(0);
        int fanOut = weights.GetLength(1);
        float scale = Mathf.Sqrt(2f / fanIn);
        for (int i = 0; i < fanIn; i++)
            for (int j = 0; j < fanOut; j++)
                weights[i, j] = (float)(rng.NextDouble() * 2 - 1) * scale;
        for (int j = 0; j < fanOut; j++)
            bias[j] = 0f;
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
        int queueCount = GWorld.Instance.GetQueuedPatientCount();
        int criticalQueueCount = GWorld.Instance.GetQueuedCriticalPatientCount();
        int insideWing = HospitalFlowController.GetPatientsInsideWingCount();

        bool[] valid = new bool[ActionCount];
        valid[(int)DispatchAction.RightShort] = freeRight > 0 && corridorA;
        valid[(int)DispatchAction.LeftShort] = freeLeft > 0 && corridorB;
        valid[(int)DispatchAction.RightDetour] = freeRight > 0 && corridorB && corridorC;
        valid[(int)DispatchAction.LeftDetour] = freeLeft > 0 && corridorA && corridorC;

        if (!valid[0] && !valid[1] && !valid[2] && !valid[3])
            return false;

        float[] features = BuildFeatures(critical, freeRight, freeLeft, corridorA, corridorB, corridorC, queueCount, criticalQueueCount, insideWing);
        float[] qValues = Forward(features, out _, out _);
        int action = SelectAction(qValues, valid);

        bool reserved = ResolveWing(action) == TriageBaselineAgent.ReservedWing.Left
            ? CubicleReservation.TryReserveLeftForPatient(patient, inventory, out cubicle)
            : CubicleReservation.TryReserveForPatient(patient, inventory, out cubicle);

        if (!reserved)
            return false;

        reservedWing = ResolveWing(action);
        exitRoute = ResolveRoute(action);

        pending[patient.GetInstanceID()] = new PendingDecision
        {
            features = features,
            action = action,
            dispatchTime = Time.time,
            critical = critical
        };
        episodeCount++;

        GoapDiagnostics.Log(
            "DqnDispatcher",
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

        float[] qValues = Forward(decision.features, out float[] hidden1, out float[] hidden2);
        float qBefore = qValues[decision.action];
        float qAfter = qBefore;

        if (TrainingEnabled)
        {
            Backward(decision.features, hidden1, hidden2, qValues, decision.action, reward);
            qAfter = Forward(decision.features, out _, out _)[decision.action];
        }

        WriteDecisionRow(decision, reward, qBefore, qAfter);

        GoapDiagnostics.Log(
            "DqnDispatcher",
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

    public static void ResetNetwork(float startEpsilon, int seed)
    {
        pending.Clear();
        episodeCount = 0;
        Epsilon = startEpsilon;
        InitializeNetwork(seed);
    }

    static float[] BuildFeatures(
        bool critical, int freeRight, int freeLeft, bool corridorA, bool corridorB, bool corridorC,
        int queueCount, int criticalQueueCount, int insideWing)
    {
        return new[]
        {
            critical ? 1f : 0f,
            Mathf.Clamp01(freeRight / MaxCubiclesPerWing),
            Mathf.Clamp01(freeLeft / MaxCubiclesPerWing),
            Mathf.Clamp01(queueCount / MaxQueueNormalization),
            Mathf.Clamp01(criticalQueueCount / MaxQueueNormalization),
            Mathf.Clamp01(insideWing / MaxWingCapacity),
            corridorA ? 1f : 0f,
            corridorB ? 1f : 0f,
            corridorC ? 1f : 0f
        };
    }

    static float[] Forward(float[] input, out float[] hidden1, out float[] hidden2)
    {
        hidden1 = DenseRelu(input, weights1, bias1, InputCount, Hidden1Count);
        hidden2 = DenseRelu(hidden1, weights2, bias2, Hidden1Count, Hidden2Count);
        return DenseLinear(hidden2, weights3, bias3, Hidden2Count, ActionCount);
    }

    static float[] DenseRelu(float[] input, float[,] weights, float[] bias, int fanIn, int fanOut)
    {
        float[] output = new float[fanOut];
        for (int j = 0; j < fanOut; j++)
        {
            float sum = bias[j];
            for (int i = 0; i < fanIn; i++)
                sum += input[i] * weights[i, j];
            output[j] = Mathf.Max(0f, sum);
        }
        return output;
    }

    static float[] DenseLinear(float[] input, float[,] weights, float[] bias, int fanIn, int fanOut)
    {
        float[] output = new float[fanOut];
        for (int j = 0; j < fanOut; j++)
        {
            float sum = bias[j];
            for (int i = 0; i < fanIn; i++)
                sum += input[i] * weights[i, j];
            output[j] = sum;
        }
        return output;
    }

    static void Backward(float[] input, float[] hidden1, float[] hidden2, float[] qValues, int action, float reward)
    {
        float[] dq = new float[ActionCount];
        dq[action] = qValues[action] - reward;

        float[] dHidden2 = new float[Hidden2Count];
        for (int i = 0; i < Hidden2Count; i++)
        {
            float grad = 0f;
            for (int j = 0; j < ActionCount; j++)
                grad += dq[j] * weights3[i, j];
            dHidden2[i] = hidden2[i] > 0f ? grad : 0f;
        }

        float[] dHidden1 = new float[Hidden1Count];
        for (int i = 0; i < Hidden1Count; i++)
        {
            float grad = 0f;
            for (int j = 0; j < Hidden2Count; j++)
                grad += dHidden2[j] * weights2[i, j];
            dHidden1[i] = hidden1[i] > 0f ? grad : 0f;
        }

        adamStep++;
        UpdateLayer(weights3, bias3, m3, v3, mb3, vb3, hidden2, dq, Hidden2Count, ActionCount);
        UpdateLayer(weights2, bias2, m2, v2, mb2, vb2, hidden1, dHidden2, Hidden1Count, Hidden2Count);
        UpdateLayer(weights1, bias1, m1, v1, mb1, vb1, input, dHidden1, InputCount, Hidden1Count);
    }

    static void UpdateLayer(
        float[,] weights, float[] bias, float[,] m, float[,] v, float[] mb, float[] vb,
        float[] layerInput, float[] outputGrad, int fanIn, int fanOut)
    {
        float biasCorrection1 = 1f - Mathf.Pow(Beta1, adamStep);
        float biasCorrection2 = 1f - Mathf.Pow(Beta2, adamStep);

        for (int i = 0; i < fanIn; i++)
        {
            for (int j = 0; j < fanOut; j++)
            {
                float grad = outputGrad[j] * layerInput[i];
                m[i, j] = Beta1 * m[i, j] + (1f - Beta1) * grad;
                v[i, j] = Beta2 * v[i, j] + (1f - Beta2) * grad * grad;
                float mHat = m[i, j] / biasCorrection1;
                float vHat = v[i, j] / biasCorrection2;
                weights[i, j] -= LearningRate * mHat / (Mathf.Sqrt(vHat) + AdamEpsilon);
            }
        }

        for (int j = 0; j < fanOut; j++)
        {
            float grad = outputGrad[j];
            mb[j] = Beta1 * mb[j] + (1f - Beta1) * grad;
            vb[j] = Beta2 * vb[j] + (1f - Beta2) * grad * grad;
            float mHat = mb[j] / biasCorrection1;
            float vHat = vb[j] / biasCorrection2;
            bias[j] -= LearningRate * mHat / (Mathf.Sqrt(vHat) + AdamEpsilon);
        }
    }

    static int SelectAction(float[] qValues, bool[] valid)
    {
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
        string path = Path.Combine(directory, "dqn_decisions_" + timestamp + ".csv");
        decisionWriter = new StreamWriter(path, false);
        decisionWriter.WriteLine(
            "episode_index,simulation_time,critical,free_right,free_left,queue_norm,critical_queue_norm,wing_occupancy_norm,corridor_a_open,corridor_b_open,corridor_c_open,action,action_name,epsilon,training_enabled,reward,q_before,q_after");
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

        float[] f = decision.features;
        decisionWriter.WriteLine(
            episodeCount
            + "," + Time.time.ToString("F3", CultureInfo.InvariantCulture)
            + "," + decision.critical
            + "," + f[1].ToString("F3", CultureInfo.InvariantCulture)
            + "," + f[2].ToString("F3", CultureInfo.InvariantCulture)
            + "," + f[3].ToString("F3", CultureInfo.InvariantCulture)
            + "," + f[4].ToString("F3", CultureInfo.InvariantCulture)
            + "," + f[5].ToString("F3", CultureInfo.InvariantCulture)
            + "," + (f[6] > 0.5f)
            + "," + (f[7] > 0.5f)
            + "," + (f[8] > 0.5f)
            + "," + decision.action
            + "," + (DispatchAction)decision.action
            + "," + Epsilon.ToString("F4", CultureInfo.InvariantCulture)
            + "," + TrainingEnabled
            + "," + reward.ToString("F2", CultureInfo.InvariantCulture)
            + "," + qBefore.ToString("F2", CultureInfo.InvariantCulture)
            + "," + qAfter.ToString("F2", CultureInfo.InvariantCulture));
    }

    public static void SaveNetwork(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (StreamWriter writer = new StreamWriter(path, false))
        {
            writer.WriteLine("layer,i,j,value");
            WriteLayer(writer, "w1", weights1, InputCount, Hidden1Count);
            WriteBias(writer, "b1", bias1, Hidden1Count);
            WriteLayer(writer, "w2", weights2, Hidden1Count, Hidden2Count);
            WriteBias(writer, "b2", bias2, Hidden2Count);
            WriteLayer(writer, "w3", weights3, Hidden2Count, ActionCount);
            WriteBias(writer, "b3", bias3, ActionCount);
        }

        GoapDiagnostics.Log("DqnDispatcher", "saved network path=" + path);
    }

    static void WriteLayer(StreamWriter writer, string name, float[,] weights, int fanIn, int fanOut)
    {
        for (int i = 0; i < fanIn; i++)
            for (int j = 0; j < fanOut; j++)
                writer.WriteLine(name + "," + i + "," + j + "," + weights[i, j].ToString("R", CultureInfo.InvariantCulture));
    }

    static void WriteBias(StreamWriter writer, string name, float[] bias, int fanOut)
    {
        for (int j = 0; j < fanOut; j++)
            writer.WriteLine(name + ",0," + j + "," + bias[j].ToString("R", CultureInfo.InvariantCulture));
    }

    public static bool LoadNetwork(string path)
    {
        if (!File.Exists(path))
            return false;

        string[] lines = File.ReadAllLines(path);
        for (int i = 1; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(',');
            if (parts.Length < 4)
                continue;

            string name = parts[0];
            int row = int.Parse(parts[1], CultureInfo.InvariantCulture);
            int col = int.Parse(parts[2], CultureInfo.InvariantCulture);
            float value = float.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture);

            switch (name)
            {
                case "w1": weights1[row, col] = value; break;
                case "b1": bias1[col] = value; break;
                case "w2": weights2[row, col] = value; break;
                case "b2": bias2[col] = value; break;
                case "w3": weights3[row, col] = value; break;
                case "b3": bias3[col] = value; break;
            }
        }

        GoapDiagnostics.Log("DqnDispatcher", "loaded network path=" + path);
        return true;
    }
}
