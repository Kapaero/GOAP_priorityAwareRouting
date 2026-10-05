using UnityEngine;

public static class CubicleReservation
{
    public const string CubicleTag = "Cubicle";
    public const string CubicleLeftTag = "CubicleLeft";
    const string FreeCubicleState = "FreeCubicle";
    const string FreeCubicleLeftState = "FreeCubicleLeft";
    static int lastReservationFailureLogFrame = -1;

    public static bool TryReserveForPatient(GameObject patient, GInventory inventory, out GameObject cubicle)
    {
        return TryReserveForPatient(patient, inventory, out cubicle, false);
    }

    public static bool TryReserveLeftForPatient(GameObject patient, GInventory inventory, out GameObject cubicle)
    {
        return TryReserveForPatient(patient, inventory, out cubicle, true);
    }

    static bool TryReserveForPatient(GameObject patient, GInventory inventory, out GameObject cubicle, bool useLeftCubicle)
    {
        cubicle = null;

        if (patient == null || inventory == null)
            return false;

        GameObject firstPatient = GWorld.Instance.PeekPatient();
        if (firstPatient != patient)
        {
            LogReservationFailure(
                GetResourceName(useLeftCubicle)
                + " reservation failed. Patient is not first. Patient: "
                + patient.name
                + " ["
                + patient.tag
                + "], first: "
                + (firstPatient != null ? firstPatient.name + " [" + firstPatient.tag + "]" : "<none>")
                + ", queue: "
                + GWorld.Instance.GetPatientQueueDebugString());

            return false;
        }

        cubicle = useLeftCubicle ? GWorld.Instance.RemoveCubicleLeft() : GWorld.Instance.RemoveCubicle();
        if (cubicle == null)
        {
            LogReservationFailure(
                GetResourceName(useLeftCubicle)
                + " reservation failed. No free cubicles. Patient: "
                + patient.name
                + " ["
                + patient.tag
                + "], queue: "
                + GWorld.Instance.GetPatientQueueDebugString());

            return false;
        }

        if (!GWorld.Instance.RemovePatient(patient))
        {
            AddCubicle(cubicle, useLeftCubicle);
            cubicle = null;
            return false;
        }

        GWorld.Instance.GetWorld().ModifyState(GetFreeCubicleState(useLeftCubicle), -1);
        GWorld.Instance.GetWorld().ModifyState("Waiting", -1);

        inventory.AddItem(cubicle);
        GoapDiagnostics.Log(
            "CubicleReservation",
            GetResourceName(useLeftCubicle)
            + " reserved patient="
            + PatientLabel(patient)
            + " cubicle="
            + CubicleLabel(cubicle)
            + " queue="
            + GWorld.Instance.GetPatientQueueDebugString()
            + " freeRight="
            + GWorld.Instance.GetFreeCubicleCount()
            + " freeLeft="
            + GWorld.Instance.GetFreeCubicleLeftCount());
        return true;
    }

    public static GameObject ReleaseReservedCubicle(GInventory inventory)
    {
        return ReleaseReservedCubicle(inventory, CubicleTag, false);
    }

    public static GameObject ReleaseReservedCubicleLeft(GInventory inventory)
    {
        return ReleaseReservedCubicle(inventory, CubicleLeftTag, true);
    }

    static GameObject ReleaseReservedCubicle(GInventory inventory, string cubicleTag, bool useLeftCubicle)
    {
        if (inventory == null)
            return null;

        GameObject cubicle = inventory.FindItemWithTag(cubicleTag);
        if (cubicle == null)
            return null;

        inventory.RemoveItem(cubicle);
        AddCubicle(cubicle, useLeftCubicle);
        GWorld.Instance.GetWorld().ModifyState(GetFreeCubicleState(useLeftCubicle), 1);

        GoapDiagnostics.Log(
            "CubicleReservation",
            GetResourceName(useLeftCubicle)
            + " released cubicle="
            + CubicleLabel(cubicle)
            + " freeRight="
            + GWorld.Instance.GetFreeCubicleCount()
            + " freeLeft="
            + GWorld.Instance.GetFreeCubicleLeftCount());

        return cubicle;
    }

    public static void RollbackReservation(GameObject patient, GInventory inventory, GameObject cubicle)
    {
        if (cubicle == null)
            return;

        if (patient != null && GWorld.Instance.AddPatient(patient))
            GWorld.Instance.GetWorld().ModifyState("Waiting", 1);

        if (inventory != null)
            inventory.RemoveItem(cubicle);

        bool useLeftCubicle = cubicle.tag == CubicleLeftTag;
        AddCubicle(cubicle, useLeftCubicle);
        GWorld.Instance.GetWorld().ModifyState(GetFreeCubicleState(useLeftCubicle), 1);

        GoapDiagnostics.Log(
            "CubicleReservation",
            GetResourceName(useLeftCubicle)
            + " rollback patient="
            + PatientLabel(patient)
            + " cubicle="
            + CubicleLabel(cubicle)
            + " queue="
            + GWorld.Instance.GetPatientQueueDebugString()
            + " freeRight="
            + GWorld.Instance.GetFreeCubicleCount()
            + " freeLeft="
            + GWorld.Instance.GetFreeCubicleLeftCount());
    }

    public static void RollbackReservation(GameObject patient, GInventory inventory)
    {
        GameObject cubicle = inventory != null ? inventory.FindItemWithTag(CubicleTag) : null;
        if (cubicle == null && inventory != null)
            cubicle = inventory.FindItemWithTag(CubicleLeftTag);

        RollbackReservation(patient, inventory, cubicle);
    }

    public static void RollbackLeftReservation(GameObject patient, GInventory inventory)
    {
        GameObject cubicle = inventory != null ? inventory.FindItemWithTag(CubicleLeftTag) : null;
        RollbackReservation(patient, inventory, cubicle);
    }

    static void AddCubicle(GameObject cubicle, bool useLeftCubicle)
    {
        if (useLeftCubicle)
            GWorld.Instance.AddCubicleLeft(cubicle);
        else
            GWorld.Instance.AddCubicle(cubicle);
    }

    static string GetFreeCubicleState(bool useLeftCubicle)
    {
        return useLeftCubicle ? FreeCubicleLeftState : FreeCubicleState;
    }

    static string GetResourceName(bool useLeftCubicle)
    {
        return useLeftCubicle ? "CubicleLeft" : "Cubicle";
    }

    static void LogReservationFailure(string message)
    {
        if (Time.frameCount == lastReservationFailureLogFrame)
            return;

        lastReservationFailureLogFrame = Time.frameCount;
        GoapDiagnostics.Log("CubicleReservation", message);
    }

    static string PatientLabel(GameObject patient)
    {
        if (patient == null)
            return "<null>";

        return patient.name + "[" + patient.tag + "]";
    }

    static string CubicleLabel(GameObject cubicle)
    {
        if (cubicle == null)
            return "<null>";

        return cubicle.name + "[" + cubicle.tag + "]";
    }
}
