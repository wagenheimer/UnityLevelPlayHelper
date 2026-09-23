using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Custom UI Toolkit inspector for LevelPlayHelper.
    /// Provides direct in-inspector access to credentials, rules, simulation/testing,
    /// and project verification with two-way data binding and instant Undo/Redo.
    /// </summary>
    [CustomEditor(typeof(LevelPlayHelper), true)]
    internal class LevelPlayHelperEditor : UnityEditor.Editor
    {
        LevelPlayDashboardView dashboardView;

        public override VisualElement CreateInspectorGUI()
        {
            dashboardView = new LevelPlayDashboardView(serializedObject, isInspector: true);
            return dashboardView.Root;
        }
    }
}
