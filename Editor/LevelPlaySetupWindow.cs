using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Standalone EditorWindow for LevelPlayHelper management.
    /// Hosts the unified LevelPlayDashboardView for configuring credentials,
    /// ad pacing, testing, and full project setup checklist.
    /// </summary>
    internal class LevelPlaySetupWindow : EditorWindow
    {
        internal const string MenuPath = "Tools/Wagenheimer/Level Play Helper/LevelPlay Manager...";

        LevelPlayDashboardView dashboardView;

        [MenuItem(MenuPath, priority = 100)]
        internal static void Open()
        {
            var window = GetWindow<LevelPlaySetupWindow>();
            window.titleContent = new GUIContent("LevelPlay Manager");
            window.minSize = new Vector2(780, 620);
            window.Show();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1;

            dashboardView = new LevelPlayDashboardView(null, isInspector: false);
            root.Add(dashboardView.Root);
        }
    }
}
