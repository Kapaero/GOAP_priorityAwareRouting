using UnityEngine;

public class GoLeftCorridorC : GAction
{
    const string RetryFromAState = "GoingToCubicleLeftFromA";
    const string LeftCorridorAPassedState = "LeftCorridorAPassed";

    GameObject resource;

    public override bool PrePerform()
    {
        if (beliefs.HasState(RetryFromAState))
        {
            if (!CubicleReservation.TryReserveLeftForPatient(this.gameObject, inventory, out resource))
                return false;

            beliefs.RemoveState(RetryFromAState);
            beliefs.RemoveState(LeftCorridorAPassedState);
        }

        return true;
    }

    public override bool PostPerform()
    {
        return true;
    }

    public override bool EmergencyPerform()
    {
        CubicleReservation.RollbackLeftReservation(this.gameObject, inventory);
        target = null;
        beliefs.ModifyState(RetryFromAState, 1);
        beliefs.ModifyState(LeftCorridorAPassedState, 1);
        return true;
    }
}
