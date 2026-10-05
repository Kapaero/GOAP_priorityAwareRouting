using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class WorldState
{
    public string key;
    public int value;
}

public class WorldStates
{
    public static int worldStateChangeCounter = 0;
    public Dictionary<string, int> states;
    readonly bool countGlobalChanges;

    public WorldStates()
        : this(true)
    {
    }

    public WorldStates(bool countGlobalChanges)
    {
        states = new Dictionary<string, int>();
        this.countGlobalChanges = countGlobalChanges;
    }

    public bool HasState(string key)
    {
        return states.ContainsKey(key);
    }

    void AddState(string key, int value)
    {
        states.Add(key, value);
        TrackChange("add " + key + " value=" + value);
    }

    public void ModifyState(string key, int value)
    {
        if (states.ContainsKey(key))
        {
            int oldValue = states[key];
            int newValue = states[key] + value;
            if (newValue <= 0)
            {
                states.Remove(key);
                TrackChange("remove " + key + " old=" + oldValue + " delta=" + value);
            }
            else if (oldValue != newValue)
            {
                states[key] = newValue;
                TrackChange("modify " + key + " old=" + oldValue + " new=" + newValue + " delta=" + value);
            }
        }
        else
            AddState(key, value);
    }

    public void RemoveState(string key)
    {
        if (states.Remove(key))
            TrackChange("remove " + key);
    }

    public void SetState(string key, int value)
    {
        if (states.ContainsKey(key))
        {
            if (states[key] != value)
            {
                int oldValue = states[key];
                states[key] = value;
                TrackChange("set " + key + " old=" + oldValue + " new=" + value);
            }
        }
        else
            AddState(key, value);
    }

    public Dictionary<string, int> GetStates()
    {
        return states;
    }

    public static void IncrementWorldStateChangeCounter(string reason)
    {
        worldStateChangeCounter++;
        GoapDiagnostics.Log(
            "WorldStateChange",
            reason + ", counter=" + worldStateChangeCounter);
    }

    public static void ResetGlobalCounter()
    {
        worldStateChangeCounter = 0;
    }

    void TrackChange(string reason)
    {
        if (countGlobalChanges)
            IncrementWorldStateChangeCounter(reason);
    }
}
