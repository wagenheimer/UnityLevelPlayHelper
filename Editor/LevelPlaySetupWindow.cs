using System.Linq;

using UnityEditor;
using UnityEditor.PackageManager;

using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Single entry point for everything LevelPlay: configure the credentials and run the full
    /// setup checklist. One menu item, one window - the old separate "Setup Checklist" and
    /// "Check for Updates" menus are gone.
    /// </summary>
    internal class LevelPlaySetupWindow : EditorWindow
    {
        internal const string MenuPath = "Tools/Wagenheimer/Level Play Helper/Setup & Config...";

        const string DashboardUrl = "https://platform.ironsrc.com/";
        const string AdUnitsUrl = "https://platform.ironsrc.com/partners/adUnits";
        const string DocsUrl = "https://docs.unity.com/en-us/grow/levelplay/";

        static readonly Color ColText = new Color(0.85f, 0.85f, 0.85f);
        static readonly Color ColDim = new Color(0.65f, 0.65f, 0.65f);
        static readonly Color ColAccent = new Color(0.290f, 0.565f, 0.851f);

        [MenuItem(MenuPath, priority = 100)]
        internal static void Open()
        {
            var window = GetWindow<LevelPlaySetupWindow>();
            window.titleContent = new GUIContent("LevelPlay Setup");
            window.minSize = new Vector2(760, 620);
            window.Show();
        }

        /// <summary>Version of this package (com.wagenheimer.levelplayhelper), or "" if unknown.</summary>
        static string HelperVersion()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(typeof(LevelPlaySetupWindow).Assembly);
                return info != null ? info.version : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Resolved version of the Ads Mediation package, or "" if not installed.</summary>
        static string SdkVersion()
        {
            try
            {
                var sdk = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == "com.unity.services.levelplay");
                return sdk != null ? sdk.version : "";
            }
            catch
            {
                return "";
            }
        }

        enum Tab
        {
            Credentials,
            Cloud,
            Checklist
        }

        VisualElement contentHost;
        Button credentialsTab;
        Button cloudTab;
        Button checklistTab;
        Tab tab = Tab.Credentials;

        LevelPlayCredentialsPanel credentialsPanel;
        LevelPlayCloudPanel cloudPanel;
        SetupChecklistView checklistView;

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1;
            root.style.paddingTop = 8;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            root.Add(BuildHeader());
            root.Add(BuildTabBar());

            contentHost = new VisualElement();
            contentHost.style.flexGrow = 1;
            root.Add(contentHost);

            ShowTab(Tab.Credentials);
        }

        VisualElement BuildHeader()
        {
            var header = new VisualElement();

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            header.Add(titleRow);

            var title = new Label("Level Play - Setup & Config");
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = ColText;
            titleRow.Add(title);

            var version = HelperVersion();
            if (!string.IsNullOrEmpty(version))
            {
                var badge = new Label("v" + version);
                badge.style.fontSize = 11;
                badge.style.color = ColDim;
                badge.style.marginLeft = 8;
                badge.style.marginTop = 3;
                titleRow.Add(badge);
            }

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            titleRow.Add(spacer);

            titleRow.Add(ToolbarButton("Dashboard", () => Application.OpenURL(DashboardUrl)));
            titleRow.Add(ToolbarButton("Ad Units", () => Application.OpenURL(AdUnitsUrl)));
            titleRow.Add(ToolbarButton("Docs", () => Application.OpenURL(DocsUrl)));
            titleRow.Add(ToolbarButton("Check for updates", () => UpdateChecker.CheckForUpdate(true)));
            titleRow.Add(ToolbarButton("Refresh", RefreshCurrent, ColAccent));

            var sdk = SdkVersion();
            var sub = new Label(
                (string.IsNullOrEmpty(sdk) ? "" : "LevelPlay SDK " + sdk + "  |  ") +
                "Configure the credentials (Credentials tab) and validate everything (Checklist tab). " +
                "Credentials live on the LevelPlayHelper prefab - not in Ads Mediation > Developer Settings.");
            sub.style.fontSize = 10;
            sub.style.color = ColDim;
            sub.style.whiteSpace = WhiteSpace.Normal;
            sub.style.marginTop = 2;
            sub.style.marginBottom = 6;
            header.Add(sub);

            return header;
        }

        VisualElement BuildTabBar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.marginBottom = 6;

            credentialsTab = TabButton("Credentials", Tab.Credentials);
            cloudTab = TabButton("Cloud (API)", Tab.Cloud);
            checklistTab = TabButton("Checklist", Tab.Checklist);
            bar.Add(credentialsTab);
            bar.Add(cloudTab);
            bar.Add(checklistTab);

            return bar;
        }

        Button TabButton(string text, Tab target)
        {
            var button = new Button(() => ShowTab(target)) { text = text };
            button.style.height = 24;
            button.style.marginRight = 4;
            button.style.paddingLeft = 14;
            button.style.paddingRight = 14;
            button.style.fontSize = 11.5f;
            return button;
        }

        Button ToolbarButton(string text, System.Action clicked, Color? accent = null)
        {
            var button = new Button(clicked) { text = text };
            button.style.height = 22;
            button.style.marginLeft = 4;
            button.style.paddingLeft = 10;
            button.style.paddingRight = 10;
            button.style.fontSize = 11;
            if (accent.HasValue)
            {
                button.style.backgroundColor = new Color(accent.Value.r, accent.Value.g, accent.Value.b, 0.35f);
                button.style.color = ColText;
            }
            return button;
        }

        void ShowTab(Tab target)
        {
            tab = target;

            if (contentHost == null)
                return;

            contentHost.Clear();
            HighlightTabs();

            switch (tab)
            {
                case Tab.Credentials:
                    credentialsPanel ??= new LevelPlayCredentialsPanel();
                    credentialsPanel.Reload();
                    contentHost.Add(credentialsPanel.Root);
                    break;

                case Tab.Cloud:
                    cloudPanel ??= new LevelPlayCloudPanel();
                    contentHost.Add(cloudPanel.Root);
                    break;

                case Tab.Checklist:
                    checklistView ??= new SetupChecklistView();
                    checklistView.Refresh();
                    contentHost.Add(checklistView.Root);
                    break;
            }
        }

        void HighlightTabs()
        {
            Style(credentialsTab, tab == Tab.Credentials);
            Style(cloudTab, tab == Tab.Cloud);
            Style(checklistTab, tab == Tab.Checklist);
        }

        void Style(Button button, bool active)
        {
            if (button == null) return;

            button.style.backgroundColor = active
                ? new Color(ColAccent.r, ColAccent.g, ColAccent.b, 0.35f)
                : new Color(0f, 0f, 0f, 0.18f);
            button.style.color = active ? ColText : ColDim;
        }

        void RefreshCurrent()
        {
            switch (tab)
            {
                case Tab.Credentials:
                    credentialsPanel?.Reload();
                    break;
                case Tab.Cloud:
                    // The cloud panel keeps its state (fetched apps/ad units) while the window is open.
                    break;
                case Tab.Checklist:
                    checklistView?.Refresh();
                    break;
            }
        }
    }
}
