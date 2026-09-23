using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Diagnostics Hub: integrates project-wide prerequisites checklist
    /// and ironSource / LevelPlay Cloud REST API verification in a clean unified view.
    /// </summary>
    internal sealed class LevelPlayDiagnosticsPanel
    {
        public VisualElement Root { get; }

        readonly VisualElement body;
        readonly VisualElement subTabBar;
        readonly VisualElement contentHost;

        Button checklistSubTabBtn;
        Button cloudSubTabBtn;

        SetupChecklistView checklistView;
        LevelPlayCloudPanel cloudPanel;

        enum SubTab { Checklist, Cloud }
        SubTab currentSubTab = SubTab.Checklist;

        public LevelPlayDiagnosticsPanel()
        {
            Root = new VisualElement();
            Root.AddToClassList("lp-root");
            LevelPlayUIStyle.Apply(Root);

            subTabBar = BuildSubTabBar();
            Root.Add(subTabBar);

            contentHost = new VisualElement();
            contentHost.style.flexGrow = 1;
            Root.Add(contentHost);

            ShowSubTab(SubTab.Checklist);
        }

        VisualElement BuildSubTabBar()
        {
            var bar = new VisualElement();
            bar.AddToClassList("lp-platform-tabs");
            bar.style.marginBottom = 10;

            checklistSubTabBtn = new Button(() => ShowSubTab(SubTab.Checklist)) { text = "Project Integration Checklist" };
            checklistSubTabBtn.AddToClassList("lp-platform-tab");

            cloudSubTabBtn = new Button(() => ShowSubTab(SubTab.Cloud)) { text = "Cloud API (ironSource Verification)" };
            cloudSubTabBtn.AddToClassList("lp-platform-tab");

            bar.Add(checklistSubTabBtn);
            bar.Add(cloudSubTabBtn);

            return bar;
        }

        public void Refresh()
        {
            if (currentSubTab == SubTab.Checklist)
            {
                checklistView?.Refresh();
            }
        }

        void ShowSubTab(SubTab tab)
        {
            currentSubTab = tab;
            contentHost.Clear();

            if (tab == SubTab.Checklist)
            {
                checklistSubTabBtn.AddToClassList("lp-platform-tab--active");
                cloudSubTabBtn.RemoveFromClassList("lp-platform-tab--active");

                checklistView ??= new SetupChecklistView();
                checklistView.Refresh();
                contentHost.Add(checklistView.Root);
            }
            else
            {
                cloudSubTabBtn.AddToClassList("lp-platform-tab--active");
                checklistSubTabBtn.RemoveFromClassList("lp-platform-tab--active");

                cloudPanel ??= new LevelPlayCloudPanel();
                contentHost.Add(cloudPanel.Root);
            }
        }
    }
}

