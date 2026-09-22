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

            if (GUILayout.Button("Open Setup Checklist", GUILayout.Height(24)))
                SetupChecklistWindow.Open();

            if (GUILayout.Button("Dashboard", GUILayout.Height(24)))
                Application.OpenURL(DashboardUrl);

            if (GUILayout.Button("Docs", GUILayout.Height(24)))
                Application.OpenURL(DocsUrl);

            EditorGUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- credential checker

        /// <summary>
        /// Inline "is this configured?" panel. Red when the App Key / Ad Units for the active
        /// build target are missing, amber when partially set, and a per-platform summary matrix
        /// so the whole state is visible without opening the full checklist window.
        /// </summary>
        void DrawCredentialChecker()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Verificação de credenciais", EditorStyles.boldLabel);

            var platform = ActivePlatform();

            if (platform == null)
            {
                EditorGUILayout.HelpBox(
                    "Build target atual não é Android nem iOS. Selecione Android ou iOS em File > Build Settings para validar as credenciais daquela plataforma.\n" +
                    "No Editor os anúncios são mock, então funcionam mesmo com os campos vazios.",
                    MessageType.Info);
            }
            else
            {
                var prefix = platform == PlatformIos ? "ios" : "android";
                var appKey = GetString(prefix + "AppKey");
                var interstitial = GetString(prefix + "InterstitialAdUnitId");
                var rewarded = GetString(prefix + "RewardedAdUnitId");
                var banner = GetString(prefix + "BannerAdUnitId");

                var appKeyOk = IsSet(appKey) && !IsPlaceholder(appKey);
                var missingIds = new List<string>();
                if (!IsSet(interstitial)) missingIds.Add("interstitial");
                if (!IsSet(rewarded)) missingIds.Add("rewarded");
                if (!IsSet(banner)) missingIds.Add("banner");
                var placeholderId = IsPlaceholder(interstitial) || IsPlaceholder(rewarded) || IsPlaceholder(banner);

                if (!appKeyOk || missingIds.Count == 3)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"{platform}: credenciais NÃO configuradas — anúncios reais não vão preencher no device.");
                    sb.AppendLine();
                    if (!appKeyOk)
                        sb.AppendLine("• App Key " + (IsPlaceholder(appKey) ? "ainda é um placeholder" : "vazio") +
                                      " → LevelPlay Dashboard > Apps > seu app > App Key (uma por plataforma).");
                    if (missingIds.Count == 3)
                        sb.AppendLine("• Nenhum Ad Unit ID → Dashboard > Ad Units: crie Interstitial, Rewarded e Banner para " +
                                      platform + " e cole cada código no campo correspondente abaixo.");
                    sb.AppendLine();
                    sb.AppendLine($"Cole os valores nos campos \"{PlatformFieldLabel(platform)} ...\" logo abaixo.");
                    EditorGUILayout.HelpBox(sb.ToString().TrimEnd(), MessageType.Error);
                    DrawCheckerActions();
                }
                else if (missingIds.Count > 0 || placeholderId)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"{platform}: parcialmente configurado.");
                    if (missingIds.Count > 0)
                        sb.AppendLine("• Sem Ad Unit ID para: " + string.Join(", ", missingIds) +
                                      ". Esse formato fica DESABILITADO nesta plataforma (Dashboard > Ad Units).");
                    if (placeholderId)
                        sb.AppendLine("• Algum Ad Unit ID parece placeholder — troque pelo código real do dashboard.");
                    EditorGUILayout.HelpBox(sb.ToString().TrimEnd(), MessageType.Warning);
                    DrawCheckerActions();
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        $"{platform}: App Key e Ad Unit IDs configurados. Valide no device (Test Suite) antes de publicar.",
                        MessageType.Info);
                }
            }

            DrawCredentialMatrix();
            EditorGUILayout.Space(4);
        }

        void DrawCheckerActions()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Abrir Dashboard", GUILayout.Height(22)))
                Application.OpenURL(DashboardUrl);
            if (GUILayout.Button("Abrir Ad Units", GUILayout.Height(22)))
                Application.OpenURL(AdUnitsUrl);
            if (GUILayout.Button("Checklist completo", GUILayout.Height(22)))
                SetupChecklistWindow.Open();

            EditorGUILayout.EndHorizontal();
        }

        void DrawCredentialMatrix()
        {
            EditorGUILayout.LabelField("Resumo — App Key / Ad Units por plataforma", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            DrawPlatformRow("Android", "android");
            DrawPlatformRow("iOS", "ios");
            EditorGUI.indentLevel--;
        }

        void DrawPlatformRow(string label, string prefix)
        {
            var appKey = GetString(prefix + "AppKey");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(58));
            StatusDot(IsSet(appKey) && !IsPlaceholder(appKey), "App Key");
            StatusDot(IsSet(GetString(prefix + "InterstitialAdUnitId")), "Interstitial");
            StatusDot(IsSet(GetString(prefix + "RewardedAdUnitId")), "Rewarded");
            StatusDot(IsSet(GetString(prefix + "BannerAdUnitId")), "Banner");
            EditorGUILayout.EndHorizontal();
        }

        void StatusDot(bool ok, string text)
        {
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = ok ? OkColor : BadColor }
            };
            var content = new GUIContent((ok ? "\u25CF " : "\u25CB ") + text, ok ? "Configurado" : "Vazio / placeholder");
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

        static string PlatformFieldLabel(string platform) => platform == PlatformIos ? "iOS" : "Android";

        /// <summary>Reads a serialized string field (e.g. "androidAppKey") from the inspected helper.</summary>
        string GetString(string propertyName)
        {
            var prop = serializedObject.FindProperty(propertyName);
            return prop != null ? prop.stringValue : string.Empty;
        }

        static bool IsSet(string value) => !string.IsNullOrWhiteSpace(value);

        static bool IsPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            var lower = value.ToLowerInvariant();
            return lower.Contains("your_")
                   || lower.Contains("yourapp")
                   || lower.Contains("placeholder")
                   || lower == "test"
                   || lower == "editor"
                   || lower == "dummy";
        }
    }
}
