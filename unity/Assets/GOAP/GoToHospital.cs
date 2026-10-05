using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GoToHospital : GAction
{
    public override bool PrePerform()
    {
        return true;
    }

    public override bool PostPerform()
    {
        return true;
    }

    public override bool EmergencyPerform()
    {
        GoapDiagnostics.Log("Emergency", "GoToHospital emergency agent=" + gameObject.name);
        return true;
    }
}
