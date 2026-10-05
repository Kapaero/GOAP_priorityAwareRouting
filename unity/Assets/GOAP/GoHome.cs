using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;




public class GoHome : GAction
{
    GameObject resource;

    public override bool IsAchievable()
    {
        return true;
    }

    public override bool PrePerform()
    {
        target = gameObject;
        return true;
    }

    public override bool PostPerform()
    {
        TriageExperimentMetrics.RecordHome(gameObject);
        GWorld.Instance.RemovePatient(gameObject);
        target = null;
        Destroy(gameObject);
        return true;
    }
    public override bool EmergencyPerform()
    {
        target = null;
        return true;
    }
}
