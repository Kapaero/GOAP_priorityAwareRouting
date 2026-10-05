using System.Collections;
using System.Collections.Generic;
using UnityEngine;




public class GoCorridorC : GAction
{
    GameObject resource;

    public override bool PrePerform()
    {
        if (beliefs.HasState("GoingToEmergencyRoomB"))
        {
            if (!CubicleReservation.TryReserveForPatient(this.gameObject, inventory, out resource))
                return false;
            beliefs.RemoveState("GoingToEmergencyRoomB");
            beliefs.RemoveState("CorridorBPassed");

        }

        return true;
    }

    public override bool PostPerform()
    {

       

        return true;
    }
    public override bool EmergencyPerform()
    {
        CubicleReservation.RollbackReservation(this.gameObject, inventory);
        target = null;
        beliefs.ModifyState("GoingToEmergencyRoomB", 1);
        beliefs.ModifyState("CorridorBPassed", 1);
        return true;
    }
}
