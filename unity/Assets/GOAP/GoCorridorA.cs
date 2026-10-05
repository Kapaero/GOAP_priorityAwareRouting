using System.Collections;
using System.Collections.Generic;
using UnityEngine;




public class GoCorridorA : GAction
{
    GameObject resource;

    public override bool IsAchievable()
    {
        return true;
    }

    public override bool PrePerform()
    {
        GameObject cubicle = inventory.FindItemWithTag("Cubicle");
        if (cubicle == null)
            return false;
        target = cubicle;
        duration = Patient.ResolveTreatmentDuration(gameObject, duration);
        return true;
    }

    public override bool PostPerform()
    {
        GWorld.Instance.GetWorld().ModifyState("Treeated", 1);
        CubicleReservation.ReleaseReservedCubicle(inventory);
        target = null;
        beliefs.ModifyState("isCured", 1);
        HospitalFlowController.RegisterWingTreatmentComplete(this.gameObject);
        beliefs.ModifyState("ExitRouteCorridorA", 1);
        return true;
    }
    public override bool EmergencyPerform()
    {
        CubicleReservation.RollbackReservation(this.gameObject, inventory);
        target = null;

        return true;
    }
}
