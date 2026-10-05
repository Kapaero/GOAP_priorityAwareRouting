using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Patient : GAgent
{
    [SerializeField] float treatmentDurationSeconds = 120f;
    [SerializeField] string treatmentDurationSource = "default";

    public float TreatmentDurationSeconds
    {
        get { return Mathf.Max(0f, treatmentDurationSeconds); }
    }

    public string TreatmentDurationSource
    {
        get { return treatmentDurationSource; }
    }

    public void ConfigureTreatmentDuration(float durationSeconds, string source)
    {
        treatmentDurationSeconds = Mathf.Max(0f, durationSeconds);
        treatmentDurationSource = string.IsNullOrWhiteSpace(source) ? "runtime" : source;
    }

    public static float ResolveTreatmentDuration(GameObject patientObject, float fallbackDuration)
    {
        if (patientObject == null)
            return Mathf.Max(0f, fallbackDuration);

        Patient patient = patientObject.GetComponent<Patient>();
        if (patient == null)
            return Mathf.Max(0f, fallbackDuration);

        return patient.TreatmentDurationSeconds;
    }

    // Start is called before the first frame update
    new void Start()
    {




        base.Start();
        SubGoal s1 = new SubGoal("IsWaiting", 1, true);
        SubGoal s2 = new SubGoal("IsThreated", 1, true);
        SubGoal s3 = new SubGoal("gotHome", 1, true);

        goals.Add(s1, 3);
        goals.Add(s2, 4); 
        goals.Add(s3, 5); 
     
     
    }

}
