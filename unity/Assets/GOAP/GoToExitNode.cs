using UnityEngine;

public class GoToExitNode : GAction
{
    public override bool PrePerform()
    {
        return true;
    }

    public override bool PostPerform()
    {
        if (effects.ContainsKey("ExitWingPassed"))
            HospitalFlowController.RegisterWingExit(this.gameObject);

        target = null;
        return true;
    }

    public override bool EmergencyPerform()
    {
        target = null;
        return true;
    }
}
