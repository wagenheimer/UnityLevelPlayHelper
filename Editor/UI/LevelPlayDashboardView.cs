using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Unified UI Toolkit dashboard component for LevelPlayHelper.
    /// Shared directly between the Custom Inspector (LevelPlayHelperEditor)
    /// and the standalone window (LevelPlaySetupWindow).
    /// </summary>
    internal sealed class LevelPlayDashboardView
    {
        public enum Tab
        {
            Credentials,
            Rules,
            Testing,
            Diagnostics
        }

        public VisualElement Root { get; }

        readonly bool isInspector;
        readonly SerializedObject serializedObject;

        VisualElement tabBar;
        VisualElement contentHost;

        Button credentialsTabBtn;
        Button rulesTabBtn;
        Button testingTabBtn;
        Button diagnosticsTabBtn;

        Tab currentTab = Tab.Credentials;

        LevelPlayCredentialsPanel credentialsPanel;
        LevelPlayRulesPanel rulesPanel;
        LevelPlayTestingPanel testingPanel;
        LevelPlayDiagnosticsPanel diagnosticsPanel;

        public LevelPlayDashboardView(SerializedObject serialized = null, bool isInspector = false)
        {
            this.isInspector = isInspector;
            this.serializedObject = serialized;

            Root = new VisualElement();
            Root.AddToClassList("lp-root");
            LevelPlayUIStyle.Apply(Root);

            // Header
            var header = new LevelPlayHeaderView(isInspector, RefreshCurrent);
            Root.Add(header.Root);

            // Tab Bar
            tabBar = BuildTabBar();
            Root.Add(tabBar);

            // Content
            contentHost = new VisualElement();
            contentHost.style.flexGrow = 1;
            Root.Add(contentHost);

            ShowTab(Tab.Credentials);
        }

        VisualElement BuildTabBar()
        {
            var bar = new VisualElement();
            bar.AddToClassList("lp-tab-bar");

            credentialsTabBtn = CreateTabBtn("Credentials", Tab.Credentials);
            rulesTabBtn = CreateTabBtn("Rules & Privacy", Tab.Rules);
            testingTabBtn = CreateTabBtn("Testing", Tab.Testing);
            diagnosticsTabBtn = CreateTabBtn("Diagnostics", Tab.Diagnostics);

            bar.Add(credentialsTabBtn);
            bar.Add(rulesTabBtn);
            bar.Add(testingTabBtn);
            bar.Add(diagnosticsTabBtn);

            return bar;
        }

        Button CreateTabBtn(string title, Tab target)
        {
            var btn = new Button(() => ShowTab(target)) { text = title };
            btn.AddToClassList("lp-tab-button");
            return btn;
        }

        public void ShowTab(Tab target)
        {
            currentTab = target;
            contentHost.Clear();
            HighlightTabs();

            switch (currentTab)
            {
                case Tab.Credentials:
                    credentialsPanel ??= new LevelPlayCredentialsPanel(serializedObject);
                    credentialsPanel.Reload();
                    contentHost.Add(credentialsPanel.Root);
                    break;

                case Tab.Rules:
                    rulesPanel ??= new LevelPlayRulesPanel(serializedObject);
                    rulesPanel.Reload();
                    contentHost.Add(rulesPanel.Root);
                    break;

                case Tab.Testing:
                    testingPanel ??= new LevelPlayTestingPanel(serializedObject);
                    testingPanel.Reload();
                    contentHost.Add(testingPanel.Root);
                    break;

                case Tab.Diagnostics:
                    diagnosticsPanel ??= new LevelPlayDiagnosticsPanel();
                    diagnosticsPanel.Refresh();
                    contentHost.Add(diagnosticsPanel.Root);
                    break;
            }
        }

        void HighlightTabs()
        {
            StyleTab(credentialsTabBtn, currentTab == Tab.Credentials);
            StyleTab(rulesTabBtn, currentTab == Tab.Rules);
            StyleTab(testingTabBtn, currentTab == Tab.Testing);
            StyleTab(diagnosticsTabBtn, currentTab == Tab.Diagnostics);
        }

        void StyleTab(Button btn, bool active)
        {
            if (btn == null) return;
            if (active)
            {
                btn.AddToClassList("lp-tab-button--active");
            }
            else
            {
                btn.RemoveFromClassList("lp-tab-button--active");
            }
        }

        public void RefreshCurrent()
        {
            ShowTab(currentTab);
        }
    }
}

