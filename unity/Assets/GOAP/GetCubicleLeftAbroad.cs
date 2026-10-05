using UnityEngine;

public class GetCubicleLeftAbroad : GAction
{
    public override bool IsAchievable()
    {
        return true;
    }

    public override bool PrePerform()
    {
        GameObject cubicle = inventory.FindItemWithTag(CubicleReservation.CubicleLeftTag);
        if (cubicle == null)
            return false;

        target = cubicle;
        duration = Patient.ResolveTreatmentDuration(gameObject, duration);
        return true;
    }

    public override bool PostPerform()
    {
        GWorld.Instance.GetWorld().ModifyState("Treeated", 1);
        CubicleReservation.ReleaseReservedCubicleLeft(inventory);
        target = null;
        beliefs.ModifyState("isCured", 1);
        HospitalFlowController.RegisterWingTreatmentComplete(this.gameObject);
        beliefs.ModifyState("ExitRouteCorridorCToA", 1);
        return true;
    }

    public override bool EmergencyPerform()
    {
        return true;
    }
}
