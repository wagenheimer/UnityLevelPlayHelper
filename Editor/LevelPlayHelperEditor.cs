using UnityEditor;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    [CustomEditor(typeof(LevelPlayHelper), true)]
    internal class LevelPlayHelperEditor : UnityEditor.Editor
    {
        const string DashboardUrl = "https://platform.ironsrc.com/";
        const string DocsUrl = "https://docs.unity.com/en-us/grow/levelplay/";
        const string RepoUrl = "https://github.com/wagenheimer/UnityLevelPlayHelper";

        public override void OnInspectorGUI()
        {
            DrawHeader();
            DrawStatus();

            EditorGUILayout.Space(6);
            DrawDefaultInspector();

            EditorGUILayout.Space(6);
            DrawFooterLinks();
        }

        new void DrawHeader()
        {
            EditorGUILayout.HelpBox(
                "Level Play Helper - reusable LevelPlay (Ads Mediation) manager.\n" +
                "Fill in the App Key and Ad Unit IDs per platform below. Leave an Ad Unit ID empty to disable that format on that platform.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Open Setup Checklist", GUILayout.Height(24)))
                SetupChecklistWindow.Open();

            if (GUILayout.Button("Dashboard", GUILayout.Height(24)))
                Application.OpenURL(DashboardUrl);

            if (GUILayout.Button("Docs", GUILayout.Height(24)))
                Application.OpenURL(DocsUrl);

            EditorGUILayout.EndHorizontal();
        }

        void DrawStatus()
        {
            if (!Application.isPlaying)
                return;

            var helper = (LevelPlayHelper)target;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("SDK Initialized", helper.IsSdkInitialized ? "Yes" : "No");
            EditorGUILayout.LabelField("Interstitial Ready", helper.IsInterstitialReady() ? "Yes" : "No");
            EditorGUILayout.LabelField("Rewarded Ready", helper.IsRewardedAdReady() ? "Yes" : "No");
        }

        void DrawFooterLinks()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Level Play Helper", EditorStyles.miniLabel);

            if (GUILayout.Button("README", EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                Application.OpenURL($"{RepoUrl}#readme");

            if (GUILayout.Button("Changelog", EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                Application.OpenURL($"{RepoUrl}/blob/master/CHANGELOG.md");

            if (GUILayout.Button("Report Issue", EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                Application.OpenURL($"{RepoUrl}/issues");

            EditorGUILayout.EndHorizontal();
        }
    }
}
