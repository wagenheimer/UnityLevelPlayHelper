using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEditor;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Editor window that inspects the project and reports whether each step of the
    /// LevelPlay integration checklist has been completed. Open via
    /// Tools/Wagenheimer/Level Play Helper/Setup Checklist... or from the LevelPlayHelper
    /// inspector.
    /// </summary>
    internal class SetupChecklistWindow : EditorWindow
    {
        enum CheckStatus { Pass, Warning, Fail, Manual }

        class CheckItem
        {
            public string Title;
            public CheckStatus Status;
            public string Detail;
            public string DocsUrl;
        }

        readonly List<CheckItem> items = new List<CheckItem>();
        Vector2 scroll;

        [MenuItem("Tools/Wagenheimer/Level Play Helper/Setup Checklist...", priority = 140)]
        internal static void Open()
        {
            var window = GetWindow<SetupChecklistWindow>(true, "LevelPlay Helper - Setup Checklist");
            window.minSize = new Vector2(520, 460);
            window.RunChecks();
        }

        void OnEnable() => RunChecks();

        void RunChecks()
        {
            items.Clear();

            CheckSdkInstalled();
            CheckDependencyResolver();

            var helperObjects = FindHelperSerializedObjects();
            CheckHelperInProject(helperObjects);
            CheckAppKeys(helperObjects);
            CheckAdUnitIds(helperObjects);
            CheckTestSuiteDisabled(helperObjects);
            CheckAndroidAdIdPermission();

            ManualItem(
                "iOS: App Tracking Transparency (ATT) implemented",
                "Required before LevelPlay.Init() on iOS 14.5+, and affects fill rate for personalized ads.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/regulation-advanced-settings");

            ManualItem(
                "iOS: SKAdNetwork IDs configured in Info.plist",
                "Required for iOS ad attribution across mediated networks.",
                "https://docs.unity.com/en-us/grow/levelplay/platform/ios-guide");

            ManualItem(
                "LevelPlay dashboard: App + Ad Units configured",
                "App must exist in the dashboard with active network instances per ad unit, or ads will fail to fill on device.",
                "https://platform.ironsrc.com/");

            ManualItem(
                "Test Suite validated on a real device build",
                "Mock ads in the Editor don't exercise error callbacks or real network behavior. Run the LevelPlay Test Suite on device before release.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite");

            Repaint();
        }

        #region Checks

        void CheckSdkInstalled()
        {
            var type = Type.GetType("Unity.Services.LevelPlay.LevelPlay, Unity.LevelPlay");

            if (type != null)
            {
                Add("Ads Mediation package installed", CheckStatus.Pass, "Unity.Services.LevelPlay assembly found.");
            }
            else
            {
                Add("Ads Mediation package installed", CheckStatus.Fail,
                    "Package not found. Install via Window > Package Manager > search 'Ads Mediation'.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
            }
        }

        void CheckDependencyResolver()
        {
            bool found = AssetDatabase.FindAssets("t:Script IOSResolver").Length > 0
                || AssetDatabase.FindAssets("t:Script AndroidResolver").Length > 0
                || Directory.Exists("Assets/ExternalDependencyManager")
                || Directory.Exists("Assets/MobileDependencyResolver")
                || Directory.Exists("Assets/Mobile Dependency Resolver")
                || Directory.Exists("Assets/Plugins/Android");

            Add("Native dependency resolver installed (EDM4U / MDR)", found ? CheckStatus.Pass : CheckStatus.Warning,
                found
                    ? "Dependency resolver detected in the project."
                    : "No EDM4U/MDR/gradle output detected yet. Required for Android/iOS builds - resolve dependencies via Assets > External Dependency Manager (or Mobile Dependency Resolver).",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
        }

        List<SerializedObject> FindHelperSerializedObjects()
        {
            var result = new List<SerializedObject>();
            var guids = AssetDatabase.FindAssets("t:Prefab");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                foreach (var helper in prefab.GetComponentsInChildren<LevelPlayHelper>(true))
                    result.Add(new SerializedObject(helper));
            }

            // Also check any instance currently in an open scene (not saved to a prefab).
            foreach (var helper in Resources.FindObjectsOfTypeAll<LevelPlayHelper>())
            {
                if (helper == null || EditorUtility.IsPersistent(helper.gameObject) == false && helper.gameObject.scene.IsValid() == false)
                    continue;

                result.Add(new SerializedObject(helper));
            }

            return result;
        }

        void CheckHelperInProject(List<SerializedObject> helpers)
        {
            if (helpers.Count > 0)
            {
                Add("LevelPlayHelper component present", CheckStatus.Pass,
                    $"Found {helpers.Count} instance(s) in project prefabs/scenes.");
            }
            else
            {
                Add("LevelPlayHelper component present", CheckStatus.Fail,
                    "No LevelPlayHelper (or subclass) found on any prefab or open scene. Add it to a persistent GameObject in your first scene.");
            }
        }

        void CheckAppKeys(List<SerializedObject> helpers)
        {
            if (helpers.Count == 0)
                return;

            foreach (var so in helpers)
            {
                var name = so.targetObject.name;
                var android = so.FindProperty("androidAppKey");
                var ios = so.FindProperty("iosAppKey");

                bool androidSet = android != null && !string.IsNullOrEmpty(android.stringValue);
                bool iosSet = ios != null && !string.IsNullOrEmpty(ios.stringValue);

                if (androidSet || iosSet)
                {
                    Add($"App Key set ({name})", CheckStatus.Pass,
                        $"Android: {(androidSet ? "set" : "empty")} | iOS: {(iosSet ? "set" : "empty")}");
                }
                else
                {
                    Add($"App Key set ({name})", CheckStatus.Fail,
                        "Both Android and iOS App Key fields are empty. Copy the App Key from the LevelPlay dashboard.");
                }
            }
        }

        void CheckAdUnitIds(List<SerializedObject> helpers)
        {
            if (helpers.Count == 0)
                return;

            string[] fields =
            {
                "androidInterstitialAdUnitId", "androidRewardedAdUnitId", "androidBannerAdUnitId",
                "iosInterstitialAdUnitId", "iosRewardedAdUnitId", "iosBannerAdUnitId"
            };

            foreach (var so in helpers)
            {
                var name = so.targetObject.name;
                int filled = fields.Count(f =>
                {
                    var prop = so.FindProperty(f);
                    return prop != null && !string.IsNullOrEmpty(prop.stringValue);
                });

                if (filled > 0)
                {
                    Add($"At least one Ad Unit ID set ({name})", CheckStatus.Pass,
                        $"{filled}/{fields.Length} ad unit ID fields filled.");
                }
                else
                {
                    Add($"At least one Ad Unit ID set ({name})", CheckStatus.Fail,
                        "No Ad Unit IDs configured for either platform - no ad format will load.");
                }
            }
        }

        void CheckTestSuiteDisabled(List<SerializedObject> helpers)
        {
            foreach (var so in helpers)
            {
                var name = so.targetObject.name;
                var prop = so.FindProperty("enableTestSuite");
                if (prop != null && prop.boolValue)
                {
                    Add($"Test Suite disabled ({name})", CheckStatus.Warning,
                        "Enable Test Suite is ON. Remember to disable it before shipping a release build.");
                }
                else
                {
                    Add($"Test Suite disabled ({name})", CheckStatus.Pass, "Test Suite flag is off.");
                }
            }
        }

        void CheckAndroidAdIdPermission()
        {
            const string manifestPath = "Assets/Plugins/Android/AndroidManifest.xml";

            int targetApi = (int)PlayerSettings.Android.targetSdkVersion;
            // Editor enum's Auto value resolves to whatever the installed SDK ships; treat as "recent enough" to warn.
            bool likelyApi33Plus = targetApi == 0 || targetApi >= 33;

            if (!likelyApi33Plus)
            {
                Add("Android AD_ID permission (API 33+)", CheckStatus.Pass, "Target API below 33 - AD_ID permission not required.");
                return;
            }

            if (!File.Exists(manifestPath))
            {
                Add("Android AD_ID permission (API 33+)", CheckStatus.Warning,
                    $"No custom AndroidManifest.xml found at {manifestPath}. If targeting API 33+, add com.google.android.gms.permission.AD_ID.");
                return;
            }

            var text = File.ReadAllText(manifestPath);
            bool hasPermission = text.Contains("com.google.android.gms.permission.AD_ID");

            Add("Android AD_ID permission (API 33+)", hasPermission ? CheckStatus.Pass : CheckStatus.Fail,
                hasPermission
                    ? "AD_ID permission declared in AndroidManifest.xml."
                    : $"AD_ID permission missing from {manifestPath}. Advertising ID access will fail on Android 13+.");
        }

        void Add(string title, CheckStatus status, string detail, string docsUrl = null) =>
            items.Add(new CheckItem { Title = title, Status = status, Detail = detail, DocsUrl = docsUrl });

        void ManualItem(string title, string detail, string docsUrl) =>
            Add(title, CheckStatus.Manual, detail, docsUrl);

        #endregion

        #region GUI

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("LevelPlay Helper - Setup Checklist", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                RunChecks();
            EditorGUILayout.EndHorizontal();

            int pass = items.Count(i => i.Status == CheckStatus.Pass);
            int total = items.Count(i => i.Status != CheckStatus.Manual);
            EditorGUILayout.LabelField($"{pass}/{total} automated checks passing", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            scroll = EditorGUILayout.BeginScrollView(scroll);

            foreach (var item in items)
                DrawItem(item);

            EditorGUILayout.EndScrollView();
        }

        void DrawItem(CheckItem item)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(IconFor(item.Status), GUILayout.Width(20));
            EditorGUILayout.LabelField(item.Title, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(item.DocsUrl) && GUILayout.Button("Docs", EditorStyles.miniButton, GUILayout.Width(45)))
                Application.OpenURL(item.DocsUrl);

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(item.Detail))
                EditorGUILayout.LabelField(item.Detail, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        static string IconFor(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Pass: return "✓";
                case CheckStatus.Warning: return "!";
                case CheckStatus.Fail: return "✕";
                default: return "•";
            }
        }

        #endregion
    }
}
