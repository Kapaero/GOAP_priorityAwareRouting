using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(WorldStateChangeDebugger))]
public class WorldStateChangeDebuggerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        if (GUILayout.Button("Increment World State Change Counter"))
        {
            WorldStateChangeDebugger debugger = (WorldStateChangeDebugger)target;
            debugger.IncrementWorldStateChangeCounter();
            EditorUtility.SetDirty(debugger);
        }
    }
}
