using System.Collections.Generic;
using UnityEngine;

public class TargetAvailabilityManager : MonoBehaviour
{
    static readonly List<TargetAvailabilityManager> activeManagers = new List<TargetAvailabilityManager>();

    [System.Serializable]
    public class TargetAvailability
    {
        public GameObject target;
        public GameObject exitTarget;
        public string exitTag;
        public bool available = true;

        [HideInInspector] public bool initialized;
        [HideInInspector] public bool lastAvailable;
        [HideInInspector] public bool lastTargetActiveSelf;
        [HideInInspector] public bool lastExitTargetActiveSelf;
    }

    public List<TargetAvailability> targets = new List<TargetAvailability>();

    [SerializeField] bool autoCreateExitTargets = true;
    [SerializeField] string exitTagSuffix = "Exit";
    [SerializeField] bool logChanges = false;
    [SerializeField] bool triggerInvertAvailabilityPulse;
    [SerializeField] int invertAvailabilityPulseFrames = 2;
    [SerializeField] bool invertAvailabilityPulseActive;
    [SerializeField] int currentWorldStateChangeCounter;
    [SerializeField] int availabilityChangeNotificationCount;
    [SerializeField] int batchedAvailabilityChangeNotificationCount;
    [SerializeField] int invertPulseStartCount;
    [SerializeField] int invertPulseRestoreCount;
    [SerializeField] int invertPulseExtendCount;

    readonly List<bool> availabilityBeforePulse = new List<bool>();
    int invertAvailabilityPulseRestoreFrame;
    bool batchAvailabilityChangeActive;
    bool batchAvailabilityChanged;

    void OnEnable()
    {
        if (!activeManagers.Contains(this))
            activeManagers.Add(this);

        InitializeTargets();
    }

    void OnDisable()
    {
        activeManagers.Remove(this);
    }

    void Update()
    {
        currentWorldStateChangeCounter = WorldStates.worldStateChangeCounter;

        if (triggerInvertAvailabilityPulse)
        {
            triggerInvertAvailabilityPulse = false;
            TriggerInvertAvailabilityPulse();
        }

        if (invertAvailabilityPulseActive && Time.frameCount >= invertAvailabilityPulseRestoreFrame)
            RestoreInvertAvailabilityPulse();

        foreach (TargetAvailability targetAvailability in targets)
            UpdateTargetAvailability(targetAvailability);
    }

    public void SetTargetAvailable(GameObject target, bool available)
    {
        TargetAvailability targetAvailability = FindTargetAvailability(target);
        if (targetAvailability == null)
        {
            targetAvailability = new TargetAvailability { target = target };
            targets.Add(targetAvailability);
            InitializeTarget(targetAvailability);
        }

        targetAvailability.available = available;
        ApplyAvailability(targetAvailability);
    }

    public void SetAllTargetsAvailable(bool available)
    {
        SetAllTargetsAvailable(available, "external");
    }

    public void SetAllTargetsAvailable(bool available, string reason)
    {
        GoapDiagnostics.Log(
            "TargetAvailability",
            "set all request available=" + available + " reason=" + reason + " targetCount=" + targets.Count);

        BeginBatchAvailabilityChange();
        try
        {
            foreach (TargetAvailability targetAvailability in targets)
            {
                if (targetAvailability == null || targetAvailability.target == null)
                    continue;

                if (!targetAvailability.initialized)
                    InitializeTarget(targetAvailability);

                bool targetAlreadyCorrect = targetAvailability.target.activeSelf == available;
                bool exitAlreadyCorrect = targetAvailability.exitTarget == null
                    || targetAvailability.exitTarget.activeSelf != available;
                if (targetAvailability.available == available && targetAlreadyCorrect && exitAlreadyCorrect)
                    continue;

                targetAvailability.available = available;
                ApplyAvailability(targetAvailability);
            }
        }
        finally
        {
            EndBatchAvailabilityChange();
        }
    }

    public static void TriggerInvertAvailabilityPulseOnAllManagers()
    {
        for (int i = activeManagers.Count - 1; i >= 0; i--)
        {
            TargetAvailabilityManager manager = activeManagers[i];
            if (manager == null)
            {
                activeManagers.RemoveAt(i);
                continue;
            }

            manager.TriggerInvertAvailabilityPulse();
        }
    }

    [ContextMenu("Sync From Targets")]
    public void InitializeTargets()
    {
        foreach (TargetAvailability targetAvailability in targets)
            InitializeTarget(targetAvailability);
    }

    [ContextMenu("Trigger Invert Availability Pulse")]
    public void TriggerInvertAvailabilityPulse()
    {
        if (invertAvailabilityPulseActive)
        {
            invertAvailabilityPulseRestoreFrame = Mathf.Max(
                invertAvailabilityPulseRestoreFrame,
                Time.frameCount + Mathf.Max(1, invertAvailabilityPulseFrames));
            invertPulseExtendCount++;
            GoapDiagnostics.Log(
                "TargetAvailability",
                "invert pulse extended restoreFrame="
                + invertAvailabilityPulseRestoreFrame
                + " extendCount="
                + invertPulseExtendCount);
            return;
        }

        if (logChanges)
            Debug.Log("Target availability invert pulse triggered");

        availabilityBeforePulse.Clear();

        BeginBatchAvailabilityChange();
        try
        {
            foreach (TargetAvailability targetAvailability in targets)
            {
                if (targetAvailability == null)
                {
                    availabilityBeforePulse.Add(false);
                    continue;
                }

                if (!targetAvailability.initialized)
                    InitializeTarget(targetAvailability);

                availabilityBeforePulse.Add(targetAvailability.available);
                targetAvailability.available = !targetAvailability.available;
                ApplyAvailability(targetAvailability);
            }
        }
        finally
        {
            EndBatchAvailabilityChange();
        }

        invertAvailabilityPulseActive = true;
        invertPulseStartCount++;
        invertAvailabilityPulseRestoreFrame = Time.frameCount + Mathf.Max(1, invertAvailabilityPulseFrames);

        GoapDiagnostics.Log(
            "TargetAvailability",
            "invert pulse started restoreFrame="
            + invertAvailabilityPulseRestoreFrame
            + " pulseFrames="
            + invertAvailabilityPulseFrames
            + " targetCount="
            + targets.Count);
    }

    void RestoreInvertAvailabilityPulse()
    {
        BeginBatchAvailabilityChange();
        try
        {
            int targetCount = Mathf.Min(targets.Count, availabilityBeforePulse.Count);
            for (int i = 0; i < targetCount; i++)
            {
                TargetAvailability targetAvailability = targets[i];
                if (targetAvailability == null)
                    continue;

                targetAvailability.available = availabilityBeforePulse[i];
                ApplyAvailability(targetAvailability);
            }
        }
        finally
        {
            EndBatchAvailabilityChange();
        }

        availabilityBeforePulse.Clear();
        invertAvailabilityPulseActive = false;
        invertPulseRestoreCount++;

        GoapDiagnostics.Log(
            "TargetAvailability",
            "invert pulse restored restoreCount=" + invertPulseRestoreCount);
    }

    void InitializeTarget(TargetAvailability targetAvailability)
    {
        if (targetAvailability == null || targetAvailability.target == null)
            return;

        GameObject exitTarget = EnsureExitTarget(targetAvailability);
        bool activeSelf = targetAvailability.target.activeSelf;
        if (exitTarget != null && exitTarget.activeSelf == activeSelf)
            exitTarget.SetActive(!activeSelf);

        targetAvailability.available = activeSelf;
        targetAvailability.lastAvailable = activeSelf;
        targetAvailability.lastTargetActiveSelf = activeSelf;
        targetAvailability.lastExitTargetActiveSelf = exitTarget != null && exitTarget.activeSelf;
        targetAvailability.initialized = true;
    }

    void UpdateTargetAvailability(TargetAvailability targetAvailability)
    {
        if (targetAvailability == null || targetAvailability.target == null)
            return;

        GameObject exitTarget = EnsureExitTarget(targetAvailability);

        if (!targetAvailability.initialized)
            InitializeTarget(targetAvailability);

        if (targetAvailability.available != targetAvailability.lastAvailable)
        {
            ApplyAvailability(targetAvailability);
            return;
        }

        bool activeSelf = targetAvailability.target.activeSelf;
        if (activeSelf != targetAvailability.lastTargetActiveSelf)
        {
            targetAvailability.available = activeSelf;
            ApplyAvailability(targetAvailability);
            return;
        }

        if (exitTarget != null && exitTarget.activeSelf != targetAvailability.lastExitTargetActiveSelf)
        {
            targetAvailability.available = !exitTarget.activeSelf;
            ApplyAvailability(targetAvailability);
        }
    }

    void ApplyAvailability(TargetAvailability targetAvailability)
    {
        if (targetAvailability == null || targetAvailability.target == null)
            return;

        GameObject exitTarget = EnsureExitTarget(targetAvailability);
        bool activeSelfBeforeApply = targetAvailability.target.activeSelf;
        bool exitActiveSelfBeforeApply = exitTarget != null && exitTarget.activeSelf;

        if (targetAvailability.target.activeSelf != targetAvailability.available)
            targetAvailability.target.SetActive(targetAvailability.available);

        if (exitTarget != null && exitTarget.activeSelf == targetAvailability.available)
            exitTarget.SetActive(!targetAvailability.available);

        bool exitTargetActiveSelf = exitTarget != null && exitTarget.activeSelf;
        bool changed =
            activeSelfBeforeApply != targetAvailability.target.activeSelf
            || exitActiveSelfBeforeApply != exitTargetActiveSelf
            || targetAvailability.lastAvailable != targetAvailability.available
            || targetAvailability.lastTargetActiveSelf != targetAvailability.target.activeSelf
            || targetAvailability.lastExitTargetActiveSelf != exitTargetActiveSelf;

        targetAvailability.lastAvailable = targetAvailability.available;
        targetAvailability.lastTargetActiveSelf = targetAvailability.target.activeSelf;
        targetAvailability.lastExitTargetActiveSelf = exitTargetActiveSelf;
        targetAvailability.initialized = true;

        if (changed)
        {
            GoapDiagnostics.Log(
                "TargetAvailability",
                "apply target="
                + targetAvailability.target.name
                + " entryActive="
                + targetAvailability.target.activeSelf
                + " exit="
                + (exitTarget != null ? exitTarget.name : "<none>")
                + " exitActive="
                + exitTargetActiveSelf
                + " available="
                + targetAvailability.available
                + " batch="
                + batchAvailabilityChangeActive);
            NotifyWorldChanged(targetAvailability);
        }
    }

    void BeginBatchAvailabilityChange()
    {
        batchAvailabilityChangeActive = true;
        batchAvailabilityChanged = false;
    }

    void EndBatchAvailabilityChange()
    {
        bool shouldNotify = batchAvailabilityChanged;
        batchAvailabilityChangeActive = false;
        batchAvailabilityChanged = false;

        if (shouldNotify)
        {
            batchedAvailabilityChangeNotificationCount++;
            GoapDiagnostics.Log(
                "TargetAvailability",
                "batch changed. batchedCount="
                + batchedAvailabilityChangeNotificationCount
                + " targetCount="
                + targets.Count);
            NotifyWorldChanged(null);
        }
    }

    GameObject EnsureExitTarget(TargetAvailability targetAvailability)
    {
        if (targetAvailability == null || targetAvailability.target == null)
            return null;

        string resolvedExitTag = ResolveExitTag(targetAvailability);
        if (targetAvailability.exitTarget != null)
        {
            AssignTagIfPossible(targetAvailability.exitTarget, resolvedExitTag);
            return targetAvailability.exitTarget;
        }

        if (!autoCreateExitTargets || string.IsNullOrEmpty(resolvedExitTag))
            return null;

        GameObject foundExitTarget = FindExitTargetInScene(targetAvailability.target, resolvedExitTag);
        if (foundExitTarget != null)
        {
            targetAvailability.exitTarget = foundExitTarget;
            return targetAvailability.exitTarget;
        }

        GameObject createdExitTarget = new GameObject(targetAvailability.target.name + " Exit");
        Transform createdTransform = createdExitTarget.transform;
        Transform sourceTransform = targetAvailability.target.transform;
        createdTransform.SetParent(sourceTransform.parent);
        createdTransform.localPosition = sourceTransform.localPosition;
        createdTransform.localRotation = sourceTransform.localRotation;
        createdTransform.localScale = sourceTransform.localScale;
        AssignTagIfPossible(createdExitTarget, resolvedExitTag);
        createdExitTarget.SetActive(false);

        targetAvailability.exitTarget = createdExitTarget;
        return targetAvailability.exitTarget;
    }

    string ResolveExitTag(TargetAvailability targetAvailability)
    {
        if (!string.IsNullOrEmpty(targetAvailability.exitTag))
            return targetAvailability.exitTag;

        if (targetAvailability.target == null)
            return string.Empty;

        string entryTag = targetAvailability.target.tag;
        if (string.IsNullOrEmpty(entryTag) || entryTag == "Untagged")
            return string.Empty;

        return entryTag + exitTagSuffix;
    }

    GameObject FindExitTargetInScene(GameObject entryTarget, string exitTag)
    {
        if (entryTarget == null || string.IsNullOrEmpty(exitTag))
            return null;

        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (GameObject candidate in allObjects)
        {
            if (candidate == null || candidate.scene != entryTarget.scene)
                continue;

            if (candidate.hideFlags != HideFlags.None)
                continue;

            if (candidate.tag == exitTag)
                return candidate;
        }

        return null;
    }

    void AssignTagIfPossible(GameObject targetObject, string tagName)
    {
        if (targetObject == null || string.IsNullOrEmpty(tagName) || targetObject.tag == tagName)
            return;

        try
        {
            targetObject.tag = tagName;
        }
        catch (UnityException)
        {
            Debug.LogError("Tag is not defined for exit target: " + tagName);
        }
    }

    TargetAvailability FindTargetAvailability(GameObject target)
    {
        foreach (TargetAvailability targetAvailability in targets)
        {
            if (targetAvailability == null)
                continue;

            if (targetAvailability.target == target || targetAvailability.exitTarget == target)
                return targetAvailability;
        }

        return null;
    }

    // --- Priority-aware access -------------------------------------------------
    // The paper's "environment-mediated" mechanism requires that a route/resource
    // remain feasible for a critical agent while becoming infeasible for a normal
    // agent AT THE SAME TIME. GameObject.SetActive cannot represent two different
    // states for two different observers, so genuine differentiation cannot be
    // expressed by simply toggling target.activeSelf (that is uniform for every
    // agent in the scene). These static helpers instead resolve/query availability
    // from the TargetAvailability *data* (available bool), independent of whether
    // the underlying GameObject has been deactivated, and grant a critical-tagged
    // requester a bypass on the specific tags that represent triage routing
    // (corridors and the wing entry/exit) even while that GameObject is inactive
    // for everyone else.
    static readonly HashSet<string> criticalPriorityBypassTags = new HashSet<string>
    {
        "CorridorA", "CorridorB", "CorridorC",
        "CorridorAExit", "CorridorBExit", "CorridorCExit",
        "Wing", "WingExit",
    };

    public static bool IsCriticalPriorityBypassTag(string tag)
    {
        return !string.IsNullOrEmpty(tag) && criticalPriorityBypassTags.Contains(tag);
    }

    // Resolves a tagged target the same way GameObject.FindWithTag would, except it
    // also finds targets that are currently deactivated (closed) by this manager,
    // by reading them straight out of the tracked TargetAvailability data. Falls
    // back to GameObject.FindWithTag for tags this manager does not track.
    public static GameObject ResolveTaggedTarget(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;

        for (int i = activeManagers.Count - 1; i >= 0; i--)
        {
            TargetAvailabilityManager manager = activeManagers[i];
            if (manager == null)
            {
                activeManagers.RemoveAt(i);
                continue;
            }

            foreach (TargetAvailability targetAvailability in manager.targets)
            {
                if (targetAvailability == null)
                    continue;

                if (targetAvailability.target != null && targetAvailability.target.tag == tag)
                    return targetAvailability.target;

                if (targetAvailability.exitTarget != null && targetAvailability.exitTarget.tag == tag)
                    return targetAvailability.exitTarget;
            }
        }

        return GameObject.FindWithTag(tag);
    }

    // True if `target` should be treated as reachable by this requester. For a
    // target this manager tracks, availability comes from the data flag (not
    // activeSelf) so a critical requester can be granted access to a tag-listed
    // corridor/wing route even while it is closed for everyone else. For an
    // untracked target (e.g. cubicles, entrance, emergency area) this falls back
    // to the legacy activeInHierarchy check, preserving existing behavior there.
    public static bool IsAvailableForAgent(GameObject target, bool requesterIsCritical)
    {
        if (target == null)
            return false;

        for (int i = activeManagers.Count - 1; i >= 0; i--)
        {
            TargetAvailabilityManager manager = activeManagers[i];
            if (manager == null)
            {
                activeManagers.RemoveAt(i);
                continue;
            }

            TargetAvailability targetAvailability = manager.FindTargetAvailability(target);
            if (targetAvailability == null)
                continue;

            bool isEntry = targetAvailability.target == target;
            bool baseAvailable = isEntry ? targetAvailability.available : !targetAvailability.available;
            if (baseAvailable)
                return true;

            return requesterIsCritical && IsCriticalPriorityBypassTag(target.tag);
        }

        return target.activeInHierarchy;
    }

    void NotifyWorldChanged(TargetAvailability targetAvailability)
    {
        if (batchAvailabilityChangeActive)
        {
            batchAvailabilityChanged = true;
            return;
        }

        string targetName = targetAvailability != null && targetAvailability.target != null
            ? targetAvailability.target.name
            : "<batch>";

        WorldStates.IncrementWorldStateChangeCounter("target availability changed target=" + targetName);
        availabilityChangeNotificationCount++;
        currentWorldStateChangeCounter = WorldStates.worldStateChangeCounter;

        if (logChanges && targetAvailability != null && targetAvailability.target != null)
        {
            Debug.Log(
                "Target availability changed: "
                + targetAvailability.target.name
                + " = "
                + targetAvailability.available
                + ", exit = "
                + (targetAvailability.exitTarget != null && targetAvailability.exitTarget.activeSelf)
                + ". World state change counter: "
                + currentWorldStateChangeCounter);
        }
    }
}
