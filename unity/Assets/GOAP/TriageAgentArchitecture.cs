using UnityEngine;

public class TriageAgentArchitecture : MonoBehaviour
{
    public TriageExperimentArchitecture architecture = TriageExperimentArchitecture.ProposedEnvironmentMediatedReplanner;
    public bool lockedBySpawner;

    public static void SetLockedArchitecture(GameObject patient, TriageExperimentArchitecture architecture)
    {
        if (patient == null)
            return;

        TriageAgentArchitecture marker = patient.GetComponent<TriageAgentArchitecture>();
        if (marker == null)
            marker = patient.AddComponent<TriageAgentArchitecture>();

        marker.architecture = architecture;
        marker.lockedBySpawner = true;
    }
}
