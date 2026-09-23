using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Testing & Simulation panel: configure device validation (Test Suite),
    /// in-game diagnostics overlay, editor mock test mode, and development build settings.
    /// </summary>
    internal sealed class LevelPlayTestingPanel
    {
        const string TestSuiteDocsUrl = "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite";

        public VisualElement Root { get; }

        readonly ScrollView scroll;
        readonly VisualElement body;
        SerializedObject serializedObject;
        LevelPlayHelper helper;

        Label testSuiteBadge;
        Label overlayBadge;
        Label editorMockBadge;

        Toggle testSuiteToggle;
        Toggle overlayToggle;
        Toggle editorMockToggle;
        Toggle devBuildToggle;

        bool isReloading;

        public LevelPlayTestingPanel(SerializedObject serialized = null)
        {
            Root = new VisualElement();
            Root.AddToClassList("lp-root");
            LevelPlayUIStyle.Apply(Root);

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);

            body = scroll.contentContainer;
            SetTarget(serialized);
        }

        public void SetTarget(SerializedObject serialized)
        {
            if (serialized != null && serialized.targetObject is LevelPlayHelper h)
            {
                serializedObject = serialized;
                helper = h;
            }
            else
            {
                helper = LevelPlayHelperLocator.FindPreferred();
                serializedObject = helper != null ? new SerializedObject(helper) : null;
            }

            Reload();
        }

        public void Reload()
        {
            body.Clear();

            if (helper == null || serializedObject == null)
            {
                body.Add(LevelPlayUIStyle.CreateCallout("No LevelPlayHelper found. Please configure on prefab.", "fail"));
                return;
            }

            isReloading = true;
            serializedObject.Update();

            body.Add(LevelPlayUIStyle.CreateCallout(
                "Use these testing utilities to validate ad formats, callbacks, and impression revenue before releasing to the store.",
                "info"));

            body.Add(BuildTestSuiteCard());
            body.Add(BuildDebugOverlayCard());
            body.Add(BuildEditorMockCard());
            body.Add(BuildBuildSettingsCard());
            body.Add(BuildFooterActions());

            isReloading = false;
        }

        VisualElement BuildTestSuiteCard()
        {
            bool isTestSuiteOn = serializedObject.FindProperty("enableTestSuite")?.boolValue ?? false;
            testSuiteBadge = LevelPlayUIStyle.CreateBadge(isTestSuiteOn ? "ENABLED" : "Disabled", isTestSuiteOn ? "warn" : "info");

            var card = LevelPlayUIStyle.CreateCard(
                "LevelPlay Test Suite (Device)",
                "Automatically launches the official ironSource/LevelPlay Test Suite UI on device startup. TURN OFF BEFORE RELEASE.",
                testSuiteBadge);

            testSuiteToggle = new Toggle("Enable Test Suite on Device")
            {
                value = isTestSuiteOn,
                tooltip = "Runs automatically right after LevelPlay.Init succeeds on physical Android/iOS devices."
            };

            testSuiteToggle.RegisterValueChangedCallback(evt =>
            {
                if (isReloading) return;
                SetHelperProperty("enableTestSuite", evt.newValue);
                LevelPlayUIStyle.SetBadge(testSuiteBadge, evt.newValue ? "ENABLED" : "Disabled", evt.newValue ? "warn" : "info");
            });

            card.Add(testSuiteToggle);
            return card;
        }

        VisualElement BuildDebugOverlayCard()
        {
            bool isOverlayOn = serializedObject.FindProperty("enableDebugOverlay")?.boolValue ?? true;
            overlayBadge = LevelPlayUIStyle.CreateBadge(isOverlayOn ? "Active" : "Disabled", isOverlayOn ? "ok" : "info");

            var card = LevelPlayUIStyle.CreateCard(
                "In-Game Diagnostics Overlay (ADS DBG)",
                "Attaches an in-game debug overlay HUD showing SDK init status, format readiness and live ad event log. Safe in release (auto-stripped).",
                overlayBadge);

            overlayToggle = new Toggle("Auto-attach Debug Overlay")
            {
                value = isOverlayOn,
                tooltip = "Press F8 or tap the 'ADS DBG' button on screen to toggle the overlay in Play mode or Development Builds."
            };

            overlayToggle.RegisterValueChangedCallback(evt =>
            {
                if (isReloading) return;
                SetHelperProperty("enableDebugOverlay", evt.newValue);
                LevelPlayUIStyle.SetBadge(overlayBadge, evt.newValue ? "Active" : "Disabled", evt.newValue ? "ok" : "info");
            });

            card.Add(overlayToggle);
            return card;
        }

        VisualElement BuildEditorMockCard()
        {
            bool isMockOn = LevelPlayEditorTestMode.Enabled;
            editorMockBadge = LevelPlayUIStyle.CreateBadge(isMockOn ? "Active" : "Off", isMockOn ? "ok" : "info");

            var card = LevelPlayUIStyle.CreateCard(
                "Editor Play Mode Mock Ads",
                "Temporarily adds project defines so monetization code runs in the Editor with mock ads and fake store without needing physical device.",
                editorMockBadge);

            editorMockToggle = new Toggle("Enable Mock Ads in Unity Editor")
            {
                value = isMockOn,
                tooltip = "Automatically handles scripting define symbols and guards against leaking into release builds."
            };

            editorMockToggle.RegisterValueChangedCallback(evt =>
            {
                if (isReloading) return;
                LevelPlayEditorTestMode.SetEnabled(evt.newValue);
                LevelPlayUIStyle.SetBadge(editorMockBadge, evt.newValue ? "Active" : "Off", evt.newValue ? "ok" : "info");
            });

            card.Add(editorMockToggle);

            var note = new Label("Build guard protects against accidentally building while Editor Test Mode is enabled.");
            note.AddToClassList("lp-card-subtitle");
            note.style.marginTop = 4;
            card.Add(note);

            return card;
        }

        VisualElement BuildBuildSettingsCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Unity Build Settings",
                "Quick toggle for Development Build so SDK logcat/console output remains unstripped on device.");

            devBuildToggle = new Toggle("Development Build")
            {
                value = EditorUserBuildSettings.development
            };

            devBuildToggle.RegisterValueChangedCallback(evt =>
            {
                if (isReloading) return;
                EditorUserBuildSettings.development = evt.newValue;
            });

            card.Add(devBuildToggle);
            return card;
        }

        VisualElement BuildFooterActions()
        {
            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var docsBtn = new Button(() => Application.OpenURL(TestSuiteDocsUrl)) { text = "Test Suite Documentation" };
            docsBtn.AddToClassList("lp-action-btn");
            actions.Add(docsBtn);

            var buildBtn = new Button(() => EditorApplication.ExecuteMenuItem("File/Build And Run")) { text = "Build & Run" };
            buildBtn.AddToClassList("lp-action-btn");
            actions.Add(buildBtn);

            return actions;
        }

        void SetHelperProperty(string propertyName, bool value)
        {
            if (helper == null || serializedObject == null) return;

            Undo.RegisterCompleteObjectUndo(helper, "Edit LevelPlay testing setting");
            serializedObject.Update();

            var prop = serializedObject.FindProperty(propertyName);
            if (prop != null)
            {
                prop.boolValue = value;
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(helper);
                AssetDatabase.SaveAssets();
            }
        }
    }
}
