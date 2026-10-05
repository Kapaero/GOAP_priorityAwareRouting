using System.Collections;
using System.Collections.Generic;
using UnityEngine;




public class GetCubicleByPatient : GAction
{
    GameObject resource;

    public override bool IsAchievable()
    {
        if (beliefs != null && beliefs.HasState("atHospital"))
            return !GWorld.Instance.IsQueuedPatient(this.gameObject) || GWorld.Instance.IsNextPatient(this.gameObject);

        return true;
    }

    public override bool PrePerform()
    {
        if (!GWorld.Instance.IsNextPatient(this.gameObject))
            return false;

        if (!CubicleReservation.TryReserveForPatient(this.gameObject, inventory, out resource))
            return false;

        

        return true;
    }

    public override bool PostPerform()
    {
        HospitalFlowController.RegisterWingEntry(this.gameObject);
        resource = null;
        target = null;

        return true;
    }
    
    public override bool EmergencyPerform()
    {
        CubicleReservation.RollbackReservation(this.gameObject, inventory, resource);
        resource = null;
        target = null;

        return true;
    }
}
