using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Testing tab: turns the device-only validation helpers on and off with one click - the
    /// LevelPlay Test Suite, the in-game debug overlay and the Development Build flag - writing
    /// straight to the helper prefab / build settings, with the reminder to switch them back off
    /// before release.
    /// </summary>
    internal sealed class LevelPlayTestingPanel
    {
        static readonly Color ColOk = new Color(0.298f, 0.686f, 0.314f);
        static readonly Color ColWarn = new Color(1.000f, 0.690f, 0.125f);
        static readonly Color ColFail = new Color(0.898f, 0.282f, 0.302f);
        static readonly Color ColDim = new Color(0.650f, 0.650f, 0.650f);
        static readonly Color ColAccent = new Color(0.290f, 0.565f, 0.851f);
        static readonly Color ColText = new Color(0.850f, 0.850f, 0.850f);

        public VisualElement Root { get; }

        VisualElement body;
        Label testSuiteStatus;
        Toggle testSuiteToggle;
        Toggle overlayToggle;
        Toggle developmentBuildToggle;

        bool loading;

        public LevelPlayTestingPanel()
        {
            Root = new VisualElement();
            Root.style.flexGrow = 1;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);

            body = scroll.contentContainer;
            body.style.paddingTop = 10;
            body.style.paddingBottom = 10;
            body.style.paddingLeft = 2;
            body.style.paddingRight = 14;

            Reload();
        }

        public void Reload()
        {
            body.Clear();

            var helper = LevelPlayHelperLocator.FindPreferred();
            if (helper == null)
            {
                body.Add(Info("No LevelPlayHelper found",
                    "Create the helper prefab (Assets/Resources/Monetization/LevelPlayHelper.prefab) to enable the test helpers.",
                    ColFail));
                return;
            }

            var so = new SerializedObject(helper);

            loading = true;

            // ------------------------------------------------------------ Test Suite
            AddSection("Test Suite (device validation)",
                "The official LevelPlay Test Suite is the only way to confirm that real ads load and every callback fires. " +
                "It runs automatically right after initialization on a DEVICE build. Turn it off again before releasing.");

            testSuiteToggle = new Toggle("Enable Test Suite on device") { value = GetBool(so, "enableTestSuite") };
            testSuiteToggle.RegisterValueChangedCallback(e =>
            {
                if (loading) return;
                SetHelperBool(helper, "enableTestSuite", e.newValue);
                UpdateTestSuiteStatus(e.newValue);
            });
            body.Add(testSuiteToggle);

            testSuiteStatus = new Label();
            testSuiteStatus.style.fontSize = 10.5f;
            testSuiteStatus.style.whiteSpace = WhiteSpace.Normal;
            testSuiteStatus.style.marginTop = 2;
            testSuiteStatus.style.marginBottom = 6;
            body.Add(testSuiteStatus);

            body.Add(Steps(
                "How to use it:",
                "1. Turn the toggle above ON.",
                "2. Build a Development Build to a real device (Android or iOS).",
                "3. Run the app - the Test Suite opens by itself after the SDK initializes.",
                "4. Follow the on-screen tests for each ad format.",
                "5. Come back here and turn it OFF before you ship."));

            // ------------------------------------------------------------ Debug overlay
            AddSection("In-game debug overlay",
                "The ADS DBG panel shows SDK state, per-format readiness, load retries and a live ad event log. " +
                "It is compiled out of release builds, so leaving it on is safe.");

            overlayToggle = new Toggle("Auto-attach the debug overlay") { value = GetBool(so, "enableDebugOverlay", true) };
            overlayToggle.RegisterValueChangedCallback(e =>
            {
                if (loading) return;
                SetHelperBool(helper, "enableDebugOverlay", e.newValue);
            });
            body.Add(overlayToggle);

            var overlayHint = new Label("Active in the Editor and in Development Builds. Press F8 (or the ADS DBG button) to toggle the panel in game.");
            overlayHint.style.fontSize = 10;
            overlayHint.style.color = ColDim;
            overlayHint.style.whiteSpace = WhiteSpace.Normal;
            overlayHint.style.marginBottom = 6;
            body.Add(overlayHint);

            // ------------------------------------------------------------ Development Build
            AddSection("Development Build",
                "Without it the device build strips the SDK logs, so a failing integration is very hard to diagnose.");

            developmentBuildToggle = new Toggle("Development Build") { value = EditorUserBuildSettings.development };
            developmentBuildToggle.RegisterValueChangedCallback(e =>
            {
                if (loading) return;
                EditorUserBuildSettings.development = e.newValue;
                Log("Development Build set to " + e.newValue + ".");
            });
            body.Add(developmentBuildToggle);

            var buildHint = new Label("Remember to turn this off for the store build.");
            buildHint.style.fontSize = 10;
            buildHint.style.color = ColDim;
            buildHint.style.marginBottom = 6;
            body.Add(buildHint);

            loading = false;

            UpdateTestSuiteStatus(testSuiteToggle.value);
            body.Add(BuildActions(helper));
        }

        VisualElement BuildActions(LevelPlayHelper helper)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 8;

            row.Add(Action("Open Test Suite docs",
                () => Application.OpenURL("https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite")));
            row.Add(Action("Build & Run", BuildAndRun));
            row.Add(Action("Copy device checklist", () =>
            {
                EditorGUIUtility.systemCopyBuffer =
                    "LevelPlay device validation\n" +
                    "1. enableTestSuite is ON in the helper prefab\n" +
                    "2. Development Build is ON\n" +
                    "3. build to a physical device and run\n" +
                    "4. the Test Suite opens automatically - run every format\n" +
                    "5. back in the editor: enableTestSuite OFF, Development Build OFF";
                Log("Device checklist copied to the clipboard.");
            }));

            return row;
        }

        void BuildAndRun()
        {
            Log("Build & Run requested.");
            EditorApplication.ExecuteMenuItem("File/Build And Run");
        }

        void UpdateTestSuiteStatus(bool enabled)
        {
            if (testSuiteStatus == null) return;

            testSuiteStatus.text = enabled
                ? "\u25CF ENABLED - the Test Suite will open on the next device build. Turn it OFF before releasing."
                : "\u25CB Disabled - turn it on to validate real ads on a device.";
            testSuiteStatus.style.color = enabled ? ColWarn : ColDim;
        }

        void Log(string message) => Debug.Log("[LevelPlayHelper] " + message);

        // ------------------------------------------------------------ helpers

        static bool GetBool(SerializedObject so, string property, bool fallback = false)
        {
            var prop = so.FindProperty(property);
            return prop != null ? prop.boolValue : fallback;
        }

        static void SetHelperBool(LevelPlayHelper helper, string property, bool value)
        {
            Undo.RegisterCompleteObjectUndo(helper, "Edit LevelPlay testing flags");

            var so = new SerializedObject(helper);
            var prop = so.FindProperty(property);
            if (prop == null) return;

            prop.boolValue = value;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(helper);
            AssetDatabase.SaveAssets();

            Debug.Log($"[LevelPlayHelper] {property} = {value}");
        }

        void AddSection(string title, string subtitle)
        {
            var box = new VisualElement();
            box.style.marginTop = 14;
            box.style.marginBottom = 6;
            box.style.flexShrink = 0;

            var t = new Label(title);
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.fontSize = 12.5f;
            t.style.color = ColText;
            t.style.marginBottom = 2;
            box.Add(t);

            var s = new Label(subtitle);
            s.style.fontSize = 10.5f;
            s.style.color = ColDim;
            s.style.whiteSpace = WhiteSpace.Normal;
            box.Add(s);

            body.Add(box);
        }

        VisualElement Steps(string title, params string[] lines)
        {
            var box = new VisualElement();
            box.style.marginBottom = 6;

            var head = new Label(title);
            head.style.fontSize = 10.5f;
            head.style.color = ColText;
            head.style.marginBottom = 2;
            box.Add(head);

            foreach (var line in lines)
            {
                var label = new Label(line);
                label.style.fontSize = 10;
                label.style.color = ColDim;
                label.style.whiteSpace = WhiteSpace.Normal;
                box.Add(label);
            }

            return box;
        }

        static Button Action(string text, System.Action clicked)
        {
            var button = new Button(clicked) { text = text };
            button.style.height = 24;
            button.style.flexGrow = 0;
            button.style.alignSelf = Align.FlexStart;
            button.style.marginRight = 6;
            button.style.marginBottom = 4;
            button.style.paddingLeft = 12;
            button.style.paddingRight = 12;
            return button;
        }

        static VisualElement Info(string title, string bodyText, Color accent)
        {
            var box = new VisualElement();
            box.style.backgroundColor = new Color(accent.r, accent.g, accent.b, 0.12f);
            box.style.borderLeftWidth = 3;
            box.style.borderLeftColor = accent;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.marginBottom = 8;

            var t = new Label(title);
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.fontSize = 11.5f;
            t.style.color = ColText;
            box.Add(t);

            var b = new Label(bodyText);
            b.style.fontSize = 10.5f;
            b.style.color = ColDim;
            b.style.whiteSpace = WhiteSpace.Normal;
            b.style.marginTop = 2;
            box.Add(b);

            return box;
        }
    }
}
