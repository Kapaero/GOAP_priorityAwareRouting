using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public abstract class GAction : MonoBehaviour
{
    public string actionName = "Action";
    public float cost = 1.0f;
    public GameObject target;
    public string targetTag;
    public float duration = 0;
    public float completionThreshold = 2f;
    public WorldState[] preConditions;
    public WorldState[] afterEffects;
    public NavMeshAgent agent;

    public Dictionary<string, int> preconditions;
    public Dictionary<string, int> effects;

    public WorldStates agentBeliefs;

    public GInventory inventory;
    public WorldStates beliefs;

    public bool running = false;

    public GAction()
    {
        preconditions = new Dictionary<string, int>();
        effects = new Dictionary<string, int>();

    }

    public void Awake()
    {
        agent = this.gameObject.GetComponent<NavMeshAgent>();

        if (preConditions != null)
            foreach (WorldState w in preConditions)
            {
                preconditions.Add(w.key, w.value);
            }

        if (afterEffects != null)
            foreach (WorldState w in afterEffects)
            {
                effects.Add(w.key, w.value);
            }

        inventory = this.GetComponent<GAgent>().inventory;
        beliefs = this.GetComponent<GAgent>().beliefs;
    }

    // Planning uses the same priority-aware availability as execution (GAgent.TargetIsAvailable):
    // a route closed by the mediator is closed for normal patients but stays plannable for a
    // critical one. With the physical activeInHierarchy check a critical patient could not even
    // build a plan while the wing was closed, so the priority access only worked for plans made
    // before the closure.
    public virtual bool IsAchievable()
    {
        bool critical = gameObject.CompareTag("critical");
        if (!string.IsNullOrEmpty(targetTag))
        {
            if (target == null || !TargetAvailabilityManager.IsAvailableForAgent(target, critical))
                target = TargetAvailabilityManager.ResolveTaggedTarget(targetTag);

            return target != null && TargetAvailabilityManager.IsAvailableForAgent(target, critical);
        }

        if (target != null)
            return TargetAvailabilityManager.IsAvailableForAgent(target, critical);

        return false;
    }

    public bool IsAchievableGiven(Dictionary<string, int> conditions)
    {
        foreach (KeyValuePair<string, int> p in preconditions)
        {
            if (!StateValueSatisfies(conditions, p.Key, p.Value))
                return false;
        }
        return true;
    }

    public static bool StateValueSatisfies(Dictionary<string, int> states, string key, int requiredValue)
    {
        if (states == null || !states.TryGetValue(key, out int currentValue))
            return false;

        if (requiredValue > 0)
            return currentValue >= requiredValue;

        return currentValue == requiredValue;
    }

    public abstract bool PrePerform();
    public abstract bool PostPerform();
    public abstract bool EmergencyPerform();
}

