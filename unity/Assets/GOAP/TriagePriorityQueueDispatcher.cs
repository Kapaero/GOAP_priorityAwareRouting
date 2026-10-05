using UnityEngine;

public static class TriagePriorityQueueDispatcher
{
    const string CriticalPatientTag = "critical";
    const string CorridorATag = "CorridorA";
    const string CorridorBTag = "CorridorB";
    const string CorridorCTag = "CorridorC";

    public static bool TryDispatchPatient(
        GameObject patient,
        GInventory inventory,
        out TriageBaselineAgent.ReservedWing reservedWing,
        out TriageBaselineAgent.ExitRoute exitRoute,
        out GameObject cubicle)
    {
        reservedWing = TriageBaselineAgent.ReservedWing.None;
        exitRoute = TriageBaselineAgent.ExitRoute.None;
        cubicle = null;

        if (patient == null || inventory == null)
            return false;

        if (!GWorld.Instance.IsNextPatient(patient))
        {
            GoapDiagnostics.LogThrottled(
                "dispatcher-not-first-" + patient.GetInstanceID(),
                60,
                "PriorityDispatcher",
                "patient is not first patient="
                + PatientLabel(patient)
                + " first="
                + PatientLabel(GWorld.Instance.PeekPatient())
                + " queue="
                + GWorld.Instance.GetPatientQueueDebugString());
            return false;
        }

        bool critical = patient.CompareTag(CriticalPatientTag);
        DispatchOption option = ChooseOption(critical);
        if (!option.valid)
            return false;

        bool reserved = option.wing == TriageBaselineAgent.ReservedWing.Left
            ? CubicleReservation.TryReserveLeftForPatient(patient, inventory, out cubicle)
            : CubicleReservation.TryReserveForPatient(patient, inventory, out cubicle);

        if (!reserved)
            return false;

        reservedWing = option.wing;
        exitRoute = option.route;

        GoapDiagnostics.Log(
            "PriorityDispatcher",
            "dispatch patient="
            + PatientLabel(patient)
            + " critical="
            + critical
            + " wing="
            + reservedWing
            + " route="
            + exitRoute
            + " cubicle="
            + PatientLabel(cubicle)
            + " freeRight="
            + GWorld.Instance.GetFreeCubicleCount()
            + " freeLeft="
            + GWorld.Instance.GetFreeCubicleLeftCount());

        return true;
    }

    static DispatchOption ChooseOption(bool critical)
    {
        DispatchOption rightShort = new DispatchOption(
            TriageBaselineAgent.ReservedWing.Right,
            TriageBaselineAgent.ExitRoute.CorridorA,
            GWorld.Instance.GetFreeCubicleCount() > 0 && PathIsOpen(TriageBaselineAgent.ExitRoute.CorridorA));

        DispatchOption leftShort = new DispatchOption(
            TriageBaselineAgent.ReservedWing.Left,
            TriageBaselineAgent.ExitRoute.CorridorB,
            GWorld.Instance.GetFreeCubicleLeftCount() > 0 && PathIsOpen(TriageBaselineAgent.ExitRoute.CorridorB));

        DispatchOption rightDetour = new DispatchOption(
            TriageBaselineAgent.ReservedWing.Right,
            TriageBaselineAgent.ExitRoute.CorridorCToB,
            GWorld.Instance.GetFreeCubicleCount() > 0 && PathIsOpen(TriageBaselineAgent.ExitRoute.CorridorCToB));

        DispatchOption leftDetour = new DispatchOption(
            TriageBaselineAgent.ReservedWing.Left,
            TriageBaselineAgent.ExitRoute.CorridorCToA,
            GWorld.Instance.GetFreeCubicleLeftCount() > 0 && PathIsOpen(TriageBaselineAgent.ExitRoute.CorridorCToA));

        if (critical)
        {
            if (rightShort.valid)
                return rightShort;
            if (leftShort.valid)
                return leftShort;
            if (rightDetour.valid)
                return rightDetour;
            if (leftDetour.valid)
                return leftDetour;
        }

        bool preferRight = GWorld.Instance.GetFreeCubicleCount() >= GWorld.Instance.GetFreeCubicleLeftCount();
        if (preferRight)
        {
            if (rightShort.valid)
                return rightShort;
            if (rightDetour.valid)
                return rightDetour;
            if (leftShort.valid)
                return leftShort;
            if (leftDetour.valid)
                return leftDetour;
        }
        else
        {
            if (leftShort.valid)
                return leftShort;
            if (leftDetour.valid)
                return leftDetour;
            if (rightShort.valid)
                return rightShort;
            if (rightDetour.valid)
                return rightDetour;
        }

        return DispatchOption.Invalid;
    }

    static bool PathIsOpen(TriageBaselineAgent.ExitRoute route)
    {
        switch (route)
        {
            case TriageBaselineAgent.ExitRoute.CorridorA:
                return TagIsAvailable(CorridorATag);
            case TriageBaselineAgent.ExitRoute.CorridorB:
                return TagIsAvailable(CorridorBTag);
            case TriageBaselineAgent.ExitRoute.CorridorCToA:
                return TagIsAvailable(CorridorATag) && TagIsAvailable(CorridorCTag);
            case TriageBaselineAgent.ExitRoute.CorridorCToB:
                return TagIsAvailable(CorridorBTag) && TagIsAvailable(CorridorCTag);
            default:
                return false;
        }
    }

    static bool TagIsAvailable(string tag)
    {
        GameObject target = GameObject.FindWithTag(tag);
        return target != null && target.activeInHierarchy;
    }

    static string PatientLabel(GameObject patient)
    {
        if (patient == null)
            return "<null>";

        return patient.name + "[" + patient.tag + "]";
    }

    struct DispatchOption
    {
        public static readonly DispatchOption Invalid = new DispatchOption(
            TriageBaselineAgent.ReservedWing.None,
            TriageBaselineAgent.ExitRoute.None,
            false);

        public readonly TriageBaselineAgent.ReservedWing wing;
        public readonly TriageBaselineAgent.ExitRoute route;
        public readonly bool valid;

        public DispatchOption(
            TriageBaselineAgent.ReservedWing wing,
            TriageBaselineAgent.ExitRoute route,
            bool valid)
        {
            this.wing = wing;
            this.route = route;
            this.valid = valid;
        }
    }
}
