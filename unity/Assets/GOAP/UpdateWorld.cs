using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class UpdateWorld : MonoBehaviour
{
    public TMP_Text states;

    void LateUpdate()
    {
        Dictionary<string, int> worldstates = GWorld.Instance.GetWorld().GetStates();
        states.text = "";
        foreach (KeyValuePair<string, int> s in worldstates)
        {
            states.text += s.Key + ", " + s.Value + "\n";
        }
       // Debug.Log(states.text);
    }
}
