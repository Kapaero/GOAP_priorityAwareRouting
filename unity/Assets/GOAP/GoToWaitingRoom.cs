using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GoToWaitingRoom : GAction
{
    public override bool PrePerform()
    {
        return true;
    }

    public override bool PostPerform()
    {

        if (GWorld.Instance.AddPatient(this.gameObject))
            GWorld.Instance.GetWorld().ModifyState("Waiting", 1);

        beliefs.ModifyState("atHospital", 1);
        TriageExperimentMetrics.RecordWaitingRoomEntry(this.gameObject);

        return true;
    }
    
    public override bool EmergencyPerform()
    {
        GoapDiagnostics.Log("Emergency", "GoToWaitingRoom emergency agent=" + gameObject.name);

        return true;
    }
}
