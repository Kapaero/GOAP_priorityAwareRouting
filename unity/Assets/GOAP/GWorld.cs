using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class GWorld
{
    const string CriticalPatientTag = "critical";
    const string CubicleTag = "Cubicle";
    const string CubicleLeftTag = "CubicleLeft";
    const string FreeCubicleState = "FreeCubicle";
    const string FreeCubicleLeftState = "FreeCubicleLeft";

    private static readonly GWorld instance = new GWorld();
    private static WorldStates world;

    private static List<GameObject> patients;
    private static Dictionary<GameObject, int> patientOrders;
    private static int nextPatientOrder;
    private static Queue<GameObject> cubicles;
    private static Queue<GameObject> cubiclesLeft;

    // The next patient to treat (the first critical patient, else the oldest one) is asked for by
    // every waiting patient in every frame. It depends only on the queue contents, so it is
    // computed once per frame and queue change (queueVersion) instead of once per asking patient.
    private static int queueVersion;
    private static int cachedNextPatientVersion = -1;
    private static int cachedNextPatientFrame = -1;
    private static int cachedNextPatientIndex = -1;


    static GWorld()
    {
        world = new WorldStates();
        patients = new List<GameObject>();
        patientOrders = new Dictionary<GameObject, int>();
        nextPatientOrder = 0;
        cubicles = new Queue<GameObject>();
        cubiclesLeft = new Queue<GameObject>();


        GameObject[] cubes = GameObject.FindGameObjectsWithTag(CubicleTag);
        GameObject[] leftCubes = GameObject.FindGameObjectsWithTag(CubicleLeftTag);


        foreach (GameObject c in cubes)
            cubicles.Enqueue(c);
        if (cubes.Length > 0)
        {
            world.ModifyState(FreeCubicleState, cubes.Length);
        }

        foreach (GameObject c in leftCubes)
            cubiclesLeft.Enqueue(c);
        if (leftCubes.Length > 0)
        {
            world.ModifyState(FreeCubicleLeftState, leftCubes.Length);
        }
    }

    private GWorld()
    {
    }

    public bool AddPatient(GameObject p)
    {
        if (p == null || patients.Contains(p))
            return false;

        if (!patientOrders.ContainsKey(p))
            patientOrders.Add(p, nextPatientOrder++);

        patients.Add(p);
        patients.Sort(ComparePatientOrder);
        queueVersion++;
        GoapDiagnostics.Log(
            "Queue",
            "add patient=" + PatientLabel(p) + " queue=" + GetPatientQueueDebugString());
        return true;
    }

    public GameObject PeekPatient()
    {
        int patientIndex = GetNextPatientIndex();
        if (patientIndex < 0)
            return null;

        return patients[patientIndex];
    }


    public GameObject RemovePatient()
    {
        int patientIndex = GetNextPatientIndex();
        if (patientIndex < 0)
            return null;

        GameObject patient = patients[patientIndex];
        patients.RemoveAt(patientIndex);
        queueVersion++;
        GoapDiagnostics.Log(
            "Queue",
            "remove next patient=" + PatientLabel(patient) + " queue=" + GetPatientQueueDebugString());
        return patient;
    }

    public bool RemovePatient(GameObject patient)
    {
        if (patient == null)
            return false;

        bool removed = patients.Remove(patient);
        if (removed)
        {
            queueVersion++;
            GoapDiagnostics.Log(
                "Queue",
                "remove patient=" + PatientLabel(patient) + " queue=" + GetPatientQueueDebugString());
        }

        return removed;
    }

    public string GetPatientQueueDebugString()
    {
        RemoveMissingPatients();

        if (patients.Count == 0)
            return "<empty>";

        string queue = "";
        for (int i = 0; i < patients.Count; i++)
        {
            GameObject patient = patients[i];
            if (i > 0)
                queue += " -> ";

            queue += patient.name;
            queue += IsCriticalPatient(patient) ? "[critical]" : "[" + patient.tag + "]";
        }

        return queue;
    }

    public int GetQueuedCriticalPatientCount()
    {
        RemoveMissingPatients();

        int criticalCount = 0;
        for (int i = 0; i < patients.Count; i++)
        {
            if (IsCriticalPatient(patients[i]))
                criticalCount++;
        }

        return criticalCount;
    }

    public int GetQueuedPatientCount()
    {
        RemoveMissingPatients();
        return patients.Count;
    }

    public bool HasQueuedCriticalPatient()
    {
        return GetQueuedCriticalPatientCount() > 0;
    }

    public bool IsNextPatient(GameObject patient)
    {
        return patient != null && PeekPatient() == patient;
    }

    public bool IsQueuedPatient(GameObject patient)
    {
        RemoveMissingPatients();
        return patient != null && patients.Contains(patient);
    }

    public int GetFreeCubicleCount()
    {
        return cubicles.Count;
    }

    public int GetFreeCubicleLeftCount()
    {
        return cubiclesLeft.Count;
    }

    static int GetNextPatientIndex()
    {
        if (cachedNextPatientFrame == Time.frameCount && cachedNextPatientVersion == queueVersion)
            return cachedNextPatientIndex;

        RemoveMissingPatients();

        int nextIndex = patients.Count > 0 ? 0 : -1;
        for (int i = 0; i < patients.Count; i++)
        {
            if (IsCriticalPatient(patients[i]))
            {
                nextIndex = i;
                break;
            }
        }

        cachedNextPatientFrame = Time.frameCount;
        cachedNextPatientVersion = queueVersion;
        cachedNextPatientIndex = nextIndex;
        return nextIndex;
    }

    static bool IsCriticalPatient(GameObject patient)
    {
        return patient != null && patient.CompareTag(CriticalPatientTag);
    }

    static int ComparePatientOrder(GameObject left, GameObject right)
    {
        if (left == right)
            return 0;

        if (left == null)
            return 1;

        if (right == null)
            return -1;

        return GetPatientOrder(left).CompareTo(GetPatientOrder(right));
    }

    static int GetPatientOrder(GameObject patient)
    {
        if (patient == null)
            return int.MaxValue;

        if (!patientOrders.TryGetValue(patient, out int order))
        {
            order = nextPatientOrder++;
            patientOrders.Add(patient, order);
        }

        return order;
    }

    static void RemoveMissingPatients()
    {
        for (int i = patients.Count - 1; i >= 0; i--)
        {
            if (patients[i] == null)
            {
                patients.RemoveAt(i);
                queueVersion++;
            }
        }
    }

    public void AddCubicle(GameObject q)
    {
        if (q != null && !cubicles.Contains(q))
        {
            cubicles.Enqueue(q);
            GoapDiagnostics.Log(
                "CubicleQueue",
                "add cubicle=" + PatientLabel(q) + " freeRight=" + cubicles.Count);
        }
    }
    public GameObject RemoveCubicle()
    {
        if (cubicles.Count == 0) return null;
        GameObject cubicle = cubicles.Dequeue();
        GoapDiagnostics.Log(
            "CubicleQueue",
            "remove cubicle=" + PatientLabel(cubicle) + " freeRight=" + cubicles.Count);
        return cubicle;
    }

    public void AddCubicleLeft(GameObject q)
    {
        if (q != null && !cubiclesLeft.Contains(q))
        {
            cubiclesLeft.Enqueue(q);
            GoapDiagnostics.Log(
                "CubicleQueue",
                "add left cubicle=" + PatientLabel(q) + " freeLeft=" + cubiclesLeft.Count);
        }
    }

    public GameObject RemoveCubicleLeft()
    {
        if (cubiclesLeft.Count == 0) return null;
        GameObject cubicle = cubiclesLeft.Dequeue();
        GoapDiagnostics.Log(
            "CubicleQueue",
            "remove left cubicle=" + PatientLabel(cubicle) + " freeLeft=" + cubiclesLeft.Count);
        return cubicle;
    }


    public static GWorld Instance
    {
        get { return instance; }
    }

    public WorldStates GetWorld()
    {
        return world;
    }

    public void ResetForLoadedScene()
    {
        world = new WorldStates();
        patients.Clear();
        queueVersion++;
        cachedNextPatientFrame = -1;
        patientOrders.Clear();
        nextPatientOrder = 0;
        cubicles.Clear();
        cubiclesLeft.Clear();

        GameObject[] cubes = GameObject.FindGameObjectsWithTag(CubicleTag);
        GameObject[] leftCubes = GameObject.FindGameObjectsWithTag(CubicleLeftTag);

        foreach (GameObject c in cubes)
            cubicles.Enqueue(c);

        if (cubes.Length > 0)
            world.ModifyState(FreeCubicleState, cubes.Length);

        foreach (GameObject c in leftCubes)
            cubiclesLeft.Enqueue(c);

        if (leftCubes.Length > 0)
            world.ModifyState(FreeCubicleLeftState, leftCubes.Length);

        GoapDiagnostics.Log(
            "ExperimentReset",
            "GWorld reset freeRight=" + cubicles.Count + " freeLeft=" + cubiclesLeft.Count);
    }

    static string PatientLabel(GameObject patient)
    {
        if (patient == null)
            return "<null>";

        return patient.name + "[" + patient.tag + "]";
    }
}
