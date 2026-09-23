using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    internal sealed class LevelPlayHeaderView
    {
        const string DashboardUrl = "https://platform.ironsrc.com/partners/next/";
        const string AdUnitsUrl = "https://platform.ironsrc.com/partners/next/mediation/instances/";
        const string DocsUrl = "https://docs.unity.com/en-us/grow/levelplay/";

        public VisualElement Root { get; }

        public LevelPlayHeaderView(bool isInspectorMode = false, Action onRefresh = null)
        {
            Root = new VisualElement();
            Root.AddToClassList("lp-header");

            var row = new VisualElement();
            row.AddToClassList("lp-header-row");

            var title = new Label(isInspectorMode ? "LevelPlay Helper" : "LevelPlay Manager");
            title.AddToClassList("lp-header-title");
            row.Add(title);

            var version = GetHelperVersion();
            if (!string.IsNullOrEmpty(version))
            {
                var badge = new Label("v" + version);
                badge.AddToClassList("lp-header-version");
                row.Add(badge);
            }

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            var actions = new VisualElement();
            actions.AddToClassList("lp-toolbar-actions");

            if (isInspectorMode)
            {
                var openBtn = new Button(LevelPlaySetupWindow.Open) { text = "Open Manager Window" };
                openBtn.AddToClassList("lp-toolbar-btn");
                openBtn.AddToClassList("lp-action-btn--primary");
                actions.Add(openBtn);
            }

            actions.Add(CreateToolbarBtn("Dashboard", () => Application.OpenURL(DashboardUrl)));
            actions.Add(CreateToolbarBtn("Ad Units", () => Application.OpenURL(AdUnitsUrl)));
            actions.Add(CreateToolbarBtn("Docs", () => Application.OpenURL(DocsUrl)));

            if (onRefresh != null)
            {
                actions.Add(CreateToolbarBtn("Refresh", onRefresh));
            }

            row.Add(actions);
            Root.Add(row);

            var sdk = GetSdkVersion();
            var descText = (string.IsNullOrEmpty(sdk) ? "LevelPlay SDK not detected" : "LevelPlay SDK v" + sdk) +
                           " • Configure ad units, compliance, simulation and validate all project prerequisites.";

            var subtitle = new Label(descText);
            subtitle.AddToClassList("lp-header-subtitle");
            Root.Add(subtitle);
        }

        static Button CreateToolbarBtn(string text, Action onClick)
        {
            var btn = new Button(onClick) { text = text };
            btn.AddToClassList("lp-toolbar-btn");
            return btn;
        }

        static string GetHelperVersion()
        {
            try
            {
                var info = PackageInfo.FindForAssembly(typeof(LevelPlayHeaderView).Assembly);
                return info != null ? info.version : "";
            }
            catch
            {
                return "";
            }
        }

        static string GetSdkVersion()
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
    }
}

