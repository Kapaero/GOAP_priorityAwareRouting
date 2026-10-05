using System.Collections;
using System.Collections.Generic;
using UnityEngine;




public class GoCorridorB : GAction
{
    GameObject resource;

    public override bool PrePerform()
    {
         if (beliefs.HasState("GoingToEmergencyRoomA"))
        {
            if (!CubicleReservation.TryReserveForPatient(this.gameObject, inventory, out resource))
                return false;
            beliefs.RemoveState("GoingToEmergencyRoomA");
            beliefs.RemoveState("WingPassed");

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
        beliefs.ModifyState("GoingToEmergencyRoomA", 1);
        beliefs.ModifyState("WingPassed", 1);
        return true;
    }
}
