using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

public class Node
{
    public Node parent;
    public float cost;
    public Dictionary<string, int> state;
    public GAction action;

    public Node(Node parent, float cost, Dictionary<string, int> allstates, GAction action)
    {
        this.parent = parent;
        this.cost = cost;
        this.state = new Dictionary<string, int>(allstates);
        this.action = action;
    }


        public Node(Node parent, float cost, Dictionary<string, int> allstates,Dictionary<string, int> beliefstates, GAction action)
    {
        this.parent = parent;
        this.cost = cost;
        this.state = new Dictionary<string, int>(allstates);
        foreach (KeyValuePair<string, int> b in beliefstates)
        {
            if (!this.state.ContainsKey(b.Key))
                this.state.Add(b.Key, b.Value);     
            }
        this.action = action;
    }
}

public class GPlanner
{
    static readonly bool LogPlans = false;
    const int MaxBuildGraphCalls = 20000;

    int buildGraphCalls;
    int actionChecks;
    int nodesCreated;
    float bestCost;
    bool searchBudgetExceeded;
    readonly Dictionary<string, float> bestCostBySearchState = new Dictionary<string, float>();

    public Queue<GAction> plan(List<GAction> actions, Dictionary<string, int> goal, WorldStates beliefstates)
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        buildGraphCalls = 0;
        actionChecks = 0;
        nodesCreated = 0;
        bestCost = Mathf.Infinity;
        searchBudgetExceeded = false;
        bestCostBySearchState.Clear();

        List<GAction> usableActions = new List<GAction>();
        foreach (GAction a in actions)
        {
            if (a.IsAchievable())
                usableActions.Add(a);
        }

        List<Node> leaves = new List<Node>();
        Node start = new Node(null, 0, GWorld.Instance.GetWorld().GetStates(), beliefstates.GetStates(),null);

        usableActions.Sort((left, right) => left.cost.CompareTo(right.cost));

        bool success = BuildGraph(start, leaves, usableActions, goal);
        stopwatch.Stop();

        if (!success)
        {
            //Debug.Log("NO PLAN");
            GoapDiagnostics.Log(
                "Planner",
                "failed elapsedMs="
                + stopwatch.Elapsed.TotalMilliseconds.ToString("F3")
                + " actions="
                + actions.Count
                + " usable="
                + usableActions.Count
                + " buildCalls="
                + buildGraphCalls
                + " actionChecks="
                + actionChecks
                + " nodes="
                + nodesCreated
                + " budgetExceeded="
                + searchBudgetExceeded
                + " goal="
                + StateDictionaryToString(goal)
                + " beliefs="
                + StateDictionaryToString(beliefstates.GetStates()));
            return null;
        }

        Node cheapest = null;
        foreach (Node leaf in leaves)
        {
            if (cheapest == null)
                cheapest = leaf;
            else
            {
                if (leaf.cost < cheapest.cost)
                    cheapest = leaf;
            }
        }

        List<GAction> result = new List<GAction>();
        Node n = cheapest;
        while (n != null)
        {
            if (n.action != null)
            {
                result.Insert(0, n.action);
            }
            n = n.parent;
        }

        Queue<GAction> queue = new Queue<GAction>();
        foreach (GAction a in result)
        {
            queue.Enqueue(a);
        }

        GoapDiagnostics.Log(
            "Planner",
            "success elapsedMs="
            + stopwatch.Elapsed.TotalMilliseconds.ToString("F3")
            + " actions="
            + actions.Count
            + " usable="
            + usableActions.Count
            + " buildCalls="
            + buildGraphCalls
            + " actionChecks="
            + actionChecks
            + " nodes="
            + nodesCreated
            + " budgetExceeded="
            + searchBudgetExceeded
            + " leaves="
            + leaves.Count
            + " cost="
            + cheapest.cost
            + " goal="
            + StateDictionaryToString(goal)
            + " plan="
            + ActionListToString(result));

        if (LogPlans)
        {
            Debug.Log("The Plan is: ");
            foreach (GAction a in queue)
            {
                Debug.Log("Q: " + a.actionName);
            }
        }

        return queue;
    }

    private bool BuildGraph(Node parent, List<Node> leaves, List<GAction> usuableActions, Dictionary<string, int> goal)
    {
        buildGraphCalls++;
        if (buildGraphCalls > MaxBuildGraphCalls)
        {
            searchBudgetExceeded = true;
            return false;
        }

        bool foundPath = false;
        foreach (GAction action in usuableActions)
        {
            float nextCost = parent.cost + action.cost;
            if (nextCost >= bestCost)
                continue;

            actionChecks++;
            if (action.IsAchievableGiven(parent.state))
            {
                Dictionary<string, int> currentState = new Dictionary<string, int>(parent.state);
                foreach (KeyValuePair<string, int> eff in action.effects)
                {
                    currentState[eff.Key] = eff.Value;
                }

                Node node = new Node(parent, nextCost, currentState, action);
                nodesCreated++;

                List<GAction> subset = ActionSubset(usuableActions, action);
                string searchStateKey = SearchStateKey(currentState, subset);
                if (bestCostBySearchState.TryGetValue(searchStateKey, out float knownCost) && knownCost <= node.cost)
                    continue;

                bestCostBySearchState[searchStateKey] = node.cost;
                
                if (GoalAchieved(goal, currentState))
                {
                    leaves.Add(node);
                    bestCost = node.cost;
                    foundPath = true;
                }
                else
                {
                    bool found = BuildGraph(node, leaves, subset, goal);
                    if (found)
                        foundPath = true;
                }
            }
        }
        return foundPath;
    }

    private bool GoalAchieved(Dictionary<string, int> goal, Dictionary<string, int> state)
    {
        foreach (KeyValuePair<string, int> g in goal)
        {
            if (!GAction.StateValueSatisfies(state, g.Key, g.Value))
                return false;
        }
        return true;
    }

    private List<GAction> ActionSubset(List<GAction> actions, GAction removeMe)
    {
        List<GAction> subset = new List<GAction>();
        foreach (GAction a in actions)
        {
            if (!a.Equals(removeMe))
                subset.Add(a);
        }
        return subset;
    }

    static string StateDictionaryToString(Dictionary<string, int> states)
    {
        if (states == null || states.Count == 0)
            return "<empty>";

        string result = "";
        foreach (KeyValuePair<string, int> state in states)
        {
            if (result.Length > 0)
                result += ",";

            result += state.Key + "=" + state.Value;
        }

        return result;
    }

    static string SearchStateKey(Dictionary<string, int> state, List<GAction> remainingActions)
    {
        string result = StateDictionaryToStringSorted(state);
        result += "|remaining=";

        for (int i = 0; i < remainingActions.Count; i++)
        {
            if (i > 0)
                result += ",";

            GAction action = remainingActions[i];
            result += action != null ? action.GetInstanceID().ToString() : "null";
        }

        return result;
    }

    static string StateDictionaryToStringSorted(Dictionary<string, int> states)
    {
        if (states == null || states.Count == 0)
            return "<empty>";

        List<string> keys = new List<string>(states.Keys);
        keys.Sort();

        string result = "";
        foreach (string key in keys)
        {
            if (result.Length > 0)
                result += ",";

            result += key + "=" + states[key];
        }

        return result;
    }

    static string ActionListToString(List<GAction> actions)
    {
        if (actions == null || actions.Count == 0)
            return "<empty>";

        string result = "";
        foreach (GAction action in actions)
        {
            if (result.Length > 0)
                result += " -> ";

            if (action == null)
                result += "<null>";
            else
                result += action.actionName + "(" + action.GetType().Name + ")";
        }

        return result;
    }

}
