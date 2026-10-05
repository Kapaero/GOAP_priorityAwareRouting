using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public enum SpawnArrivalMode
{
    FixedInterval,
    MimicSchedule
}

public class Spawn : MonoBehaviour
{
    public GameObject patientPrefab;
    public Transform spawnPoint;
    public int numPatients = 1;
    public bool spawnOnStart = true;
    public bool autoSpawn = true;
    public SpawnArrivalMode arrivalMode = SpawnArrivalMode.FixedInterval;
    public bool useSceneExperimentMode = true;
    public TriageExperimentArchitecture architectureForSpawn = TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner;
    public bool lockSpawnedArchitecture = true;
    public float spawnInterval = 15f;
    public TextAsset mimicArrivalSchedule;
    public string mimicArrivalResourcePath = "MimicArrivals/mimic_heavy_day_2171_10_15_scaled";
    public float mimicScheduleTimeMultiplier = 1f;
    public float defaultTreatmentDurationSeconds = 120f;
    public float scheduleStartDelay;
    public bool loopSchedule;
    public int maxSpawnCatchUpPerFrame = 64;
    [Range(0f, 1f)] public float criticalChance = 0.15f;
    public string criticalTag = "critical";
    public string normalTag = "Untagged";
    public bool logSpawns = true;

    readonly List<ScheduledArrival> scheduledArrivals = new List<ScheduledArrival>();

    int spawnedCount;
    float nextSpawnTime;
    float scheduleStartTime;
    int nextScheduledArrivalIndex;
    bool scheduleLoadAttempted;

    public int SpawnedCount
    {
        get { return spawnedCount; }
    }

    public int ScheduledArrivalCount
    {
        get
        {
            LoadScheduledArrivals();
            return scheduledArrivals.Count;
        }
    }

    public int RemainingScheduledArrivals
    {
        get
        {
            LoadScheduledArrivals();
            return Mathf.Max(0, scheduledArrivals.Count - nextScheduledArrivalIndex);
        }
    }

    public bool HasFiniteSchedule
    {
        get { return arrivalMode == SpawnArrivalMode.MimicSchedule && !loopSchedule; }
    }

    public bool IsFiniteScheduleComplete
    {
        get
        {
            return HasFiniteSchedule
                && LoadScheduledArrivals()
                && nextScheduledArrivalIndex >= scheduledArrivals.Count;
        }
    }

    void Start()
    {
        ResetSpawnTimingForExperiment();

        if (spawnOnStart)
        {
            int initialCount = Mathf.Max(0, numPatients);
            for (int i = 0; i < initialCount; i++)
                SpawnPatient();
        }
    }

    void Update()
    {
        if (!autoSpawn || patientPrefab == null)
            return;

        if (arrivalMode == SpawnArrivalMode.MimicSchedule)
        {
            SpawnScheduledPatients();
            return;
        }

        SpawnFixedIntervalPatients();
    }

    [ContextMenu("Spawn Patient Now")]
    public void SpawnPatientNow()
    {
        SpawnPatient();
        ScheduleNextSpawn();
    }

    public void ResetSpawnTimingForExperiment()
    {
        spawnedCount = 0;
        nextScheduledArrivalIndex = 0;
        scheduleStartTime = Time.time + Mathf.Max(0f, scheduleStartDelay);
        nextSpawnTime = Time.time + Mathf.Max(0.1f, spawnInterval);
        scheduleLoadAttempted = false;
        scheduledArrivals.Clear();

        if (arrivalMode == SpawnArrivalMode.MimicSchedule)
            LoadScheduledArrivals();
    }

    void SpawnPatient()
    {
        bool isCritical = Random.value < Mathf.Clamp01(criticalChance);
        SpawnPatient(
            isCritical,
            Mathf.Max(0f, defaultTreatmentDurationSeconds),
            "default",
            "source=random chance=" + criticalChance.ToString("F3", CultureInfo.InvariantCulture));
    }

    void SpawnPatient(bool isCritical, float treatmentDurationSeconds, string treatmentDurationSource, string spawnDetail)
    {
        if (patientPrefab == null)
        {
            Debug.LogWarning("Spawn has no patient prefab assigned.", this);
            return;
        }

        Transform resolvedSpawnPoint = spawnPoint != null ? spawnPoint : transform;
        GameObject patient = Instantiate(
            patientPrefab,
            resolvedSpawnPoint.position,
            resolvedSpawnPoint.rotation);

        spawnedCount++;
        AssignTag(patient, isCritical ? criticalTag : normalTag);
        patient.name = patientPrefab.name + " Spawned " + spawnedCount + (isCritical ? " Critical" : "");
        TriageExperimentArchitecture architecture = GetSpawnArchitecture();
        ConfigurePatientTreatmentDuration(patient, treatmentDurationSeconds, treatmentDurationSource);

        if (logSpawns)
        {
            GoapDiagnostics.Log(
                "Spawner",
                "spawned patient="
                + patient.name
                + " tag="
                + patient.tag
                + " critical="
                + isCritical
                + " architecture="
                + architecture
                + " arrivalMode="
                + arrivalMode
                + " treatmentDurationSeconds="
                + treatmentDurationSeconds.ToString("F1", CultureInfo.InvariantCulture)
                + " detail="
                + spawnDetail);
        }

        TriageExperimentMode.ConfigureSpawnedPatient(patient, architecture, lockSpawnedArchitecture);
        TriageExperimentMetrics.RecordSpawn(patient, isCritical, spawnDetail);
    }

    void ConfigurePatientTreatmentDuration(GameObject patient, float durationSeconds, string source)
    {
        Patient patientComponent = patient != null ? patient.GetComponent<Patient>() : null;
        if (patientComponent == null)
            return;

        patientComponent.ConfigureTreatmentDuration(durationSeconds, source);
    }

    void ScheduleNextSpawn()
    {
        nextSpawnTime = Time.time + Mathf.Max(0.1f, spawnInterval);
    }

    TriageExperimentArchitecture GetSpawnArchitecture()
    {
        return useSceneExperimentMode
            ? TriageExperimentMode.ActiveArchitecture
            : architectureForSpawn;
    }

    void SpawnFixedIntervalPatients()
    {
        int spawnedThisFrame = 0;
        int maxCatchUp = Mathf.Max(1, maxSpawnCatchUpPerFrame);
        float interval = Mathf.Max(0.1f, spawnInterval);

        while (Time.time >= nextSpawnTime && spawnedThisFrame < maxCatchUp)
        {
            SpawnPatient();
            spawnedThisFrame++;
            nextSpawnTime += interval;
        }

        if (Time.time >= nextSpawnTime)
        {
            GoapDiagnostics.LogThrottled(
                "SpawnFixedIntervalCatchUpLimit" + GetInstanceID(),
                30,
                "Spawner",
                "fixed interval catch-up capped spawnedThisFrame="
                + spawnedThisFrame
                + " nextSpawnTime="
                + nextSpawnTime.ToString("F2", CultureInfo.InvariantCulture)
                + " time="
                + Time.time.ToString("F2", CultureInfo.InvariantCulture));
        }
    }

    void SpawnScheduledPatients()
    {
        if (!LoadScheduledArrivals() || scheduledArrivals.Count == 0)
            return;

        float elapsed = Time.time - scheduleStartTime;
        if (elapsed < 0f)
            return;

        int spawnedThisFrame = 0;
        int maxCatchUp = Mathf.Max(1, maxSpawnCatchUpPerFrame);

        while (nextScheduledArrivalIndex < scheduledArrivals.Count
            && elapsed >= scheduledArrivals[nextScheduledArrivalIndex].timeSeconds
            && spawnedThisFrame < maxCatchUp)
        {
            ScheduledArrival arrival = scheduledArrivals[nextScheduledArrivalIndex];
            SpawnPatient(
                arrival.isCritical,
                arrival.treatmentDurationSeconds,
                arrival.treatmentDurationSource,
                arrival.detail);
            nextScheduledArrivalIndex++;
            spawnedThisFrame++;
        }

        if (nextScheduledArrivalIndex >= scheduledArrivals.Count && loopSchedule)
            RestartScheduleLoop(elapsed);

        if (nextScheduledArrivalIndex < scheduledArrivals.Count
            && elapsed >= scheduledArrivals[nextScheduledArrivalIndex].timeSeconds)
        {
            GoapDiagnostics.LogThrottled(
                "SpawnScheduleCatchUpLimit" + GetInstanceID(),
                30,
                "Spawner",
                "schedule catch-up capped spawnedThisFrame="
                + spawnedThisFrame
                + " nextIndex="
                + nextScheduledArrivalIndex
                + " elapsed="
                + elapsed.ToString("F2", CultureInfo.InvariantCulture));
        }
    }

    bool LoadScheduledArrivals()
    {
        if (scheduleLoadAttempted)
            return scheduledArrivals.Count > 0;

        scheduleLoadAttempted = true;
        scheduledArrivals.Clear();

        TextAsset scheduleAsset = mimicArrivalSchedule;
        if (scheduleAsset == null && !string.IsNullOrWhiteSpace(mimicArrivalResourcePath))
            scheduleAsset = Resources.Load<TextAsset>(mimicArrivalResourcePath);

        if (scheduleAsset == null)
        {
            Debug.LogWarning("MIMIC arrival schedule was not found: " + mimicArrivalResourcePath, this);
            return false;
        }

        using (StringReader reader = new StringReader(scheduleAsset.text))
        {
            string line;
            int lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                ParseScheduleLine(line, lineNumber);
            }
        }

        scheduledArrivals.Sort((left, right) => left.timeSeconds.CompareTo(right.timeSeconds));
        GoapDiagnostics.Log(
            "Spawner",
            "loaded MIMIC schedule resource="
            + mimicArrivalResourcePath
            + " arrivals="
            + scheduledArrivals.Count
            + " timeMultiplier="
            + Mathf.Max(0.01f, mimicScheduleTimeMultiplier).ToString("F2", CultureInfo.InvariantCulture)
            + " lastArrival="
            + scheduledArrivals[scheduledArrivals.Count - 1].timeSeconds.ToString("F2", CultureInfo.InvariantCulture));

        return scheduledArrivals.Count > 0;
    }

    void ParseScheduleLine(string line, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string trimmedLine = line.Trim();
        if (trimmedLine.StartsWith("#") || trimmedLine.StartsWith("time_seconds"))
            return;

        string[] parts = trimmedLine.Split(',');
        if (parts.Length < 2)
            return;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float timeSeconds))
        {
            Debug.LogWarning("Invalid schedule time at line " + lineNumber + ": " + line, this);
            return;
        }

        bool isCritical = ParseCriticalFlag(parts[1]);
        float scaledTimeSeconds = Mathf.Max(0f, timeSeconds)
            * Mathf.Max(0.01f, mimicScheduleTimeMultiplier);
        float treatmentDurationSeconds = ParseTreatmentDurationSeconds(parts, lineNumber);
        string treatmentDurationSource = ParseTreatmentDurationSource(parts);

        scheduledArrivals.Add(
            new ScheduledArrival(
                scaledTimeSeconds,
                isCritical,
                treatmentDurationSeconds,
                treatmentDurationSource,
                BuildScheduleDetail(parts)));
    }

    static bool ParseCriticalFlag(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized == "true" || normalized == "1" || normalized == "critical";
    }

    static string BuildScheduleDetail(string[] parts)
    {
        if (parts.Length < 7)
            return "source=mimic_schedule";

        string detail = "source=mimic_schedule"
            + " source_time="
            + parts[2].Trim()
            + " hadm_id="
            + parts[3].Trim()
            + " admission_type="
            + parts[4].Trim()
            + " critical_reason="
            + parts[5].Trim()
            + " diagnosis="
            + parts[6].Trim();

        if (parts.Length >= 8)
            detail += " service_seconds=" + parts[7].Trim();
        if (parts.Length >= 9)
            detail += " source_ed_minutes=" + parts[8].Trim();
        if (parts.Length >= 10)
            detail += " service_source=" + parts[9].Trim();

        return detail;
    }

    float ParseTreatmentDurationSeconds(string[] parts, int lineNumber)
    {
        if (parts.Length >= 8
            && float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float serviceSeconds))
        {
            return Mathf.Max(0f, serviceSeconds);
        }

        if (parts.Length >= 8 && !string.IsNullOrWhiteSpace(parts[7]))
        {
            Debug.LogWarning("Invalid service_seconds at line " + lineNumber + ": " + parts[7], this);
        }

        return Mathf.Max(0f, defaultTreatmentDurationSeconds);
    }

    static string ParseTreatmentDurationSource(string[] parts)
    {
        if (parts.Length >= 10 && !string.IsNullOrWhiteSpace(parts[9]))
            return parts[9].Trim();

        if (parts.Length >= 8 && !string.IsNullOrWhiteSpace(parts[7]))
            return "mimic_schedule";

        return "default";
    }

    void RestartScheduleLoop(float elapsed)
    {
        if (scheduledArrivals.Count == 0)
            return;

        float scheduleLength = Mathf.Max(0.1f, scheduledArrivals[scheduledArrivals.Count - 1].timeSeconds);
        while (elapsed >= scheduleLength)
        {
            scheduleStartTime += scheduleLength;
            elapsed = Time.time - scheduleStartTime;
        }

        nextScheduledArrivalIndex = 0;
    }

    void AssignTag(GameObject patient, string tagName)
    {
        if (patient == null || string.IsNullOrEmpty(tagName) || patient.tag == tagName)
            return;

        try
        {
            patient.tag = tagName;
        }
        catch (UnityException)
        {
            Debug.LogError("Patient spawn tag is not defined: " + tagName, this);
        }
    }

    struct ScheduledArrival
    {
        public readonly float timeSeconds;
        public readonly bool isCritical;
        public readonly float treatmentDurationSeconds;
        public readonly string treatmentDurationSource;
        public readonly string detail;

        public ScheduledArrival(
            float timeSeconds,
            bool isCritical,
            float treatmentDurationSeconds,
            string treatmentDurationSource,
            string detail)
        {
            this.timeSeconds = timeSeconds;
            this.isCritical = isCritical;
            this.treatmentDurationSeconds = treatmentDurationSeconds;
            this.treatmentDurationSource = treatmentDurationSource;
            this.detail = detail;
        }
    }
}
