using System.Collections.Generic;
using System.Text;

using UnityEditor;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    [CustomEditor(typeof(LevelPlayHelper), true)]
    internal class LevelPlayHelperEditor : UnityEditor.Editor
    {
        const string DashboardUrl = "https://platform.ironsrc.com/";
        const string AdUnitsUrl = "https://platform.ironsrc.com/partners/adUnits";
        const string DocsUrl = "https://docs.unity.com/en-us/grow/levelplay/";
        const string RepoUrl = "https://github.com/wagenheimer/UnityLevelPlayHelper";

        static readonly Color OkColor = new Color(0.24f, 0.62f, 0.31f);
        static readonly Color BadColor = new Color(0.85f, 0.24f, 0.26f);

        public override void OnInspectorGUI()
        {
            DrawHeader();
            DrawCredentialChecker();
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

            if (GUILayout.Button("Open Setup & Config", GUILayout.Height(24)))
                LevelPlaySetupWindow.Open();

            if (GUILayout.Button("Dashboard", GUILayout.Height(24)))
                Application.OpenURL(DashboardUrl);

            if (GUILayout.Button("Docs", GUILayout.Height(24)))
                Application.OpenURL(DocsUrl);

            EditorGUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- credential checker

        /// <summary>
        /// Inline "is this configured?" panel. Red when a platform has no App Key (or none of its
        /// Ad Unit IDs) or a credential is malformed, amber when a single format/platform is
        /// incomplete. Both platforms are always evaluated - previously only the active build
        /// target was, so on a desktop target the panel stayed informational and a missing
        /// credential was never flagged in red.
        /// </summary>
        void DrawCredentialChecker()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("LevelPlay credentials", EditorStyles.boldLabel);

            var active = ActivePlatform();

            var errors = new List<string>();
            var warnings = new List<string>();

            EvaluatePlatform("Android", "android", active, errors, warnings);
            EvaluatePlatform("iOS", "ios", active, errors, warnings);

            if (errors.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Missing or invalid credentials - real ads will NOT fill on device.");
                foreach (var e in errors) sb.AppendLine("- " + e);
                sb.AppendLine();
                sb.AppendLine("Fill in the App Key / Ad Unit ID fields below (Dashboard > Apps / Ad Units).");
                EditorGUILayout.HelpBox(sb.ToString().TrimEnd(), MessageType.Error);
                DrawCheckerActions();
            }
            else if (warnings.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Partially configured.");
                foreach (var w in warnings) sb.AppendLine("- " + w);
                EditorGUILayout.HelpBox(sb.ToString().TrimEnd(), MessageType.Warning);
                DrawCheckerActions();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Android and iOS: App Key and Ad Unit IDs are set. Validate on device (Test Suite) before releasing.",
                    MessageType.Info);
            }

            DrawCredentialMatrix();
            EditorGUILayout.Space(4);
        }

        void EvaluatePlatform(string platform, string prefix, string active, List<string> errors, List<string> warnings)
        {
            var tag = platform == active ? platform + " (current build target)" : platform;

            var appKey = GetString(prefix + "AppKey");
            var interstitial = GetString(prefix + "InterstitialAdUnitId");
            var rewarded = GetString(prefix + "RewardedAdUnitId");
            var banner = GetString(prefix + "BannerAdUnitId");

            // App Key: empty, placeholder or malformed are all hard errors.
            if (!CredentialValidation.IsSet(appKey))
                errors.Add($"{tag}: App Key is empty (Dashboard > Apps).");
            else if (CredentialValidation.IsPlaceholder(appKey))
                errors.Add($"{tag}: App Key is still a placeholder (Dashboard > Apps).");
            else
            {
                var reason = CredentialValidation.DescribeProblem(appKey, true);
                if (CredentialValidation.IsHardProblem(reason))
                    errors.Add($"{tag}: invalid App Key - {reason}.");
            }

            // Ad Unit IDs: none at all is a hard error; a single empty one is a warning.
            var missing = new List<string>();
            if (!CredentialValidation.IsSet(interstitial)) missing.Add("interstitial");
            if (!CredentialValidation.IsSet(rewarded)) missing.Add("rewarded");
            if (!CredentialValidation.IsSet(banner)) missing.Add("banner");

            if (missing.Count == 3)
                errors.Add($"{tag}: no Ad Unit IDs (Dashboard > Ad Units).");
            else if (missing.Count > 0)
                warnings.Add($"{tag}: no Ad Unit ID for {string.Join(", ", missing)} - that format is disabled on this platform.");

            CheckId(interstitial, "interstitial", tag, errors);
            CheckId(rewarded, "rewarded", tag, errors);
            CheckId(banner, "banner", tag, errors);
        }

        static void CheckId(string value, string label, string tag, List<string> errors)
        {
            if (!CredentialValidation.IsSet(value))
                return;

            var reason = CredentialValidation.DescribeProblem(value, false);
            if (CredentialValidation.IsHardProblem(reason))
                errors.Add($"{tag}: invalid {label} Ad Unit ID - {reason}.");
        }

        void DrawCheckerActions()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Open Dashboard", GUILayout.Height(22)))
                Application.OpenURL(DashboardUrl);
            if (GUILayout.Button("Open Ad Units", GUILayout.Height(22)))
                Application.OpenURL(AdUnitsUrl);
            if (GUILayout.Button("Open Setup & Config", GUILayout.Height(22)))
                LevelPlaySetupWindow.Open();

            EditorGUILayout.EndHorizontal();
        }

        void DrawCredentialMatrix()
        {
            EditorGUILayout.LabelField("Summary - App Key / Ad Units per platform", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            DrawPlatformRow("Android", "android");
            DrawPlatformRow("iOS", "ios");
            EditorGUI.indentLevel--;
        }

        void DrawPlatformRow(string label, string prefix)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(58));
            StatusDot(IsCredentialOk(GetString(prefix + "AppKey"), true), "App Key");
            StatusDot(IsCredentialOk(GetString(prefix + "InterstitialAdUnitId"), false), "Interstitial");
            StatusDot(IsCredentialOk(GetString(prefix + "RewardedAdUnitId"), false), "Rewarded");
            StatusDot(IsCredentialOk(GetString(prefix + "BannerAdUnitId"), false), "Banner");
            EditorGUILayout.EndHorizontal();
        }

        static bool IsCredentialOk(string value, bool isAppKey) =>
            CredentialValidation.IsSet(value)
            && !CredentialValidation.IsPlaceholder(value)
            && !CredentialValidation.IsHardProblem(CredentialValidation.DescribeProblem(value, isAppKey));

        void StatusDot(bool ok, string text)
        {
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = ok ? OkColor : BadColor }
            };
            var content = new GUIContent((ok ? "\u25CF " : "\u25CB ") + text, ok ? "Configured" : "Empty / placeholder / invalid");
            EditorGUILayout.LabelField(content, style, GUILayout.Width(104));
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

            var overlay = UnityEngine.Object.FindObjectOfType<UI.LevelPlayDebugOverlay>();
            EditorGUILayout.LabelField("Debug Overlay",
                overlay != null ? "Active (press F8 in game)"
                    : helper.enableDebugOverlay ? "Auto in Editor / Dev builds" : "Disabled");
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

        // ---------------------------------------------------------------- helpers

        const string PlatformIos = "iOS";

        /// <summary>"Android" / "iOS" for the active build target, or null when it is neither.</summary>
        static string ActivePlatform()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target == BuildTarget.Android) return "Android";
            if (target == BuildTarget.iOS) return PlatformIos;
            return null;
        }

        /// <summary>Reads a serialized string field (e.g. "androidAppKey") from the inspected helper.</summary>
        string GetString(string propertyName)
        {
            var prop = serializedObject.FindProperty(propertyName);
            return prop != null ? prop.stringValue : string.Empty;
        }
    }
}
