using UnityEngine;

public class GoLeftCorridorB : GAction
{
    const string RetryFromWingState = "GoingToCubicleLeftFromWing";
    const string LeftWingPassedState = "LeftWingPassed";

    GameObject resource;

    public override bool PrePerform()
    {
        if (beliefs.HasState(RetryFromWingState))
        {
            if (!CubicleReservation.TryReserveLeftForPatient(this.gameObject, inventory, out resource))
                return false;

            beliefs.RemoveState(RetryFromWingState);
            beliefs.RemoveState(LeftWingPassedState);
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
        beliefs.ModifyState(RetryFromWingState, 1);
        beliefs.ModifyState(LeftWingPassedState, 1);
        return true;
    }
}
