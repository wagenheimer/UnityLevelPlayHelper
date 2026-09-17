using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using UnityEditor;
using UnityEditor.Build;

using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// UI Toolkit checklist that inspects the project and reports whether every step of the
    /// LevelPlay integration is done. Checks follow the official LevelPlay Unity integration
    /// workflow (package, native dependency resolution, credentials, initialization, privacy,
    /// Android/iOS build prerequisites and the release validation list).
    ///
    /// Open via Tools > Wagenheimer > Level Play Helper > Setup Checklist... or from the
    /// LevelPlayHelper inspector.
    /// </summary>
    internal class SetupChecklistWindow : EditorWindow
    {
        // ---------------------------------------------------------------- model

        internal enum CheckStatus
        {
            Pass,
            Warning,
            Fail,
            Manual,
            Info
        }

        internal sealed class CheckResult
        {
            public string Title = "";
            public CheckStatus Status = CheckStatus.Info;
            public string Detail = "";

            /// <summary>Extra, per-check evidence (paths, detected values, versions).</summary>
            public readonly List<string> Facts = new List<string>();

            /// <summary>Adds several evidence lines at once (List&lt;T&gt;.Add returns void, so chains must not be used).</summary>
            public CheckResult WithFacts(params string[] facts)
            {
                Facts.AddRange(facts);
                return this;
            }

            public string DocsUrl;
            public string ActionLabel;
            public Action Action;

            /// <summary>Optional custom control rendered under the row (used by the Editor test mode switch).</summary>
            public Func<VisualElement> CustomControl;
        }

        internal sealed class Section
        {
            public string Title = "";
            public string Subtitle = "";
            public readonly List<CheckResult> Items = new List<CheckResult>();

            public int AutomatedTotal => Items.Count(i => i.Status == CheckStatus.Pass
                                                        || i.Status == CheckStatus.Warning
                                                        || i.Status == CheckStatus.Fail);
            public int AutomatedPassed => Items.Count(i => i.Status == CheckStatus.Pass);
            public bool HasFail => Items.Any(i => i.Status == CheckStatus.Fail);
            public bool HasWarning => Items.Any(i => i.Status == CheckStatus.Warning);

            public CheckStatus WorstStatus
            {
                get
                {
                    if (HasFail) return CheckStatus.Fail;
                    if (HasWarning) return CheckStatus.Warning;
                    return CheckStatus.Pass;
                }
            }
        }

        // ---------------------------------------------------------------- palette

        static readonly Color ColPass = new Color(0.298f, 0.686f, 0.314f);
        static readonly Color ColWarn = new Color(1.000f, 0.690f, 0.125f);
        static readonly Color ColFail = new Color(0.898f, 0.282f, 0.302f);
        static readonly Color ColManual = new Color(0.620f, 0.620f, 0.620f);
        static readonly Color ColInfo = new Color(0.290f, 0.565f, 0.851f);
        static readonly Color ColText = new Color(0.85f, 0.85f, 0.85f);
        static readonly Color ColTextDim = new Color(0.65f, 0.65f, 0.65f);
        static readonly Color ColRow = new Color(1f, 1f, 1f, 0.035f);
        static readonly Color ColCard = new Color(1f, 1f, 1f, 0.055f);

        // ---------------------------------------------------------------- state

        readonly List<Section> sections = new List<Section>();
        Section active;
        DateTime lastRun;

        ScrollView scroll;
        VisualElement headerHost;
        VisualElement bodyHost;

        // Project-wide source scans, computed once per refresh and shared by several checks.
        Dictionary<string, List<string>> apiUsage;
        List<HelperInstance> helpers;
        string sdkVersion;
        string helperVersion;
        SerializedObject mediationSettings;
        SerializedObject networkSettings;

        sealed class HelperInstance
        {
            public LevelPlayHelper Component;
            public string Location;      // asset path (prefab) or scene path
            public bool IsPrefab;
        }

        // ---------------------------------------------------------------- entry points

        [MenuItem("Tools/Wagenheimer/Level Play Helper/Setup Checklist...", priority = 140)]
        internal static void Open()
        {
            var window = GetWindow<SetupChecklistWindow>();
            window.titleContent = new GUIContent("LevelPlay Setup Checklist");
            window.minSize = new Vector2(620, 520);
            window.Show();
            window.RunChecks();
        }

        void OnEnable() => RunChecks();

        // ---------------------------------------------------------------- UI

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 10;
            root.style.paddingBottom = 10;
            root.style.paddingLeft = 12;
            root.style.paddingRight = 12;

            headerHost = new VisualElement();
            root.Add(headerHost);

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            bodyHost = new VisualElement();
            scroll.Add(bodyHost);

            BuildChrome();
            Rebuild();
        }

        void BuildChrome()
        {
            headerHost.Clear();

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            headerHost.Add(titleRow);

            var title = new Label("LevelPlay - Setup Checklist");
            title.style.fontSize = 17;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = ColText;
            titleRow.Add(title);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            titleRow.Add(spacer);

            titleRow.Add(ToolbarButton("Dashboard", () => Application.OpenURL("https://platform.ironsrc.com/")));
            titleRow.Add(ToolbarButton("Docs", () => Application.OpenURL("https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation")));
            titleRow.Add(ToolbarButton("Refresh", RunChecks, ColInfo));

            var sub = new Label();
            sub.style.fontSize = 10;
            sub.style.color = ColTextDim;
            sub.style.marginTop = 2;
            sub.name = "subtitle";
            headerHost.Add(sub);
        }

        Button ToolbarButton(string text, Action clicked, Color? accent = null)
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

        void Rebuild()
        {
            if (bodyHost == null)
                return;

            Body();

            var sub = headerHost.Q<Label>("subtitle");
            if (sub != null)
            {
                var sdk = string.IsNullOrEmpty(sdkVersion) ? "SDK not detected" : "LevelPlay SDK " + sdkVersion;
                var helper = string.IsNullOrEmpty(helperVersion) ? "" : "  |  helper " + helperVersion;
                sub.text = sdk + helper + "  |  checked " + lastRun.ToString("HH:mm:ss");
            }
        }

        void Body()
        {
            bodyHost.Clear();

            int autoTotal = sections.Sum(s => s.AutomatedTotal);
            int autoPass = sections.Sum(s => s.AutomatedPassed);
            int fails = sections.SelectMany(s => s.Items).Count(i => i.Status == CheckStatus.Fail);
            int warns = sections.SelectMany(s => s.Items).Count(i => i.Status == CheckStatus.Warning);

            bodyHost.Add(SummaryCard(autoPass, autoTotal, warns, fails));

            foreach (var section in sections)
                bodyHost.Add(SectionCard(section));

            bodyHost.Add(Footer());
        }

        VisualElement SummaryCard(int pass, int total, int warnings, int failures)
        {
            var status = failures > 0 ? CheckStatus.Fail
                : warnings > 0 ? CheckStatus.Warning
                : CheckStatus.Pass;
            var accent = StatusColor(status);

            var card = Card(accent);
            card.style.marginBottom = 10;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            card.Add(row);

            var headline = new Label(failures > 0
                ? $"{total - pass} item(s) need attention"
                : warnings > 0
                    ? "Ready to build, with warnings"
                    : "All automated checks passed");
            headline.style.fontSize = 14;
            headline.style.unityFontStyleAndWeight = FontStyle.Bold;
            headline.style.color = accent;
            row.Add(headline);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            row.Add(Chip($"{pass}/{total} automated", accent));
            if (failures > 0) row.Add(Chip($"{failures} fail", ColFail));
            if (warnings > 0) row.Add(Chip($"{warnings} warning", ColWarn));

            var track = new VisualElement();
            track.style.height = 6;
            track.style.marginTop = 8;
            track.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            track.style.borderTopLeftRadius = 3;
            track.style.borderTopRightRadius = 3;
            track.style.borderBottomLeftRadius = 3;
            track.style.borderBottomRightRadius = 3;
            card.Add(track);

            var fill = new VisualElement();
            fill.style.height = 6;
            fill.style.width = Length.Percent(total == 0 ? 0 : Mathf.Round(100f * pass / total));
            fill.style.backgroundColor = accent;
            fill.style.borderTopLeftRadius = 3;
            fill.style.borderTopRightRadius = 3;
            fill.style.borderBottomLeftRadius = 3;
            fill.style.borderBottomRightRadius = 3;
            track.Add(fill);

            return card;
        }

        VisualElement SectionCard(Section section)
        {
            var accent = section.WorstStatus == CheckStatus.Pass && section.AutomatedTotal == 0
                ? ColManual
                : StatusColor(section.WorstStatus);

            var card = Card(accent);
            var expanded = true;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            card.Add(head);

            var chevron = new Label("v");
            chevron.style.width = 14;
            chevron.style.fontSize = 11;
            chevron.style.color = accent;
            head.Add(chevron);

            var name = new Label(section.Title);
            name.style.fontSize = 12.5f;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = ColText;
            head.Add(name);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            head.Add(spacer);

            var counter = $"{section.AutomatedPassed}/{section.AutomatedTotal}";
            if (section.AutomatedTotal == 0)
                counter = section.Items.Any(i => i.Status != CheckStatus.Manual) ? "info" : "manual";
            head.Add(Chip(counter, accent));

            var body = new VisualElement();
            body.style.marginTop = 6;
            card.Add(body);

            if (!string.IsNullOrEmpty(section.Subtitle))
            {
                var note = new Label(section.Subtitle);
                note.style.fontSize = 10;
                note.style.color = ColTextDim;
                note.style.whiteSpace = WhiteSpace.Normal;
                note.style.marginBottom = 4;
                body.Add(note);
            }

            foreach (var item in section.Items)
                body.Add(Row(item));

            head.RegisterCallback<ClickEvent>(_ =>
            {
                expanded = !expanded;
                body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                chevron.text = expanded ? "v" : ">";
            });

            return card;
        }

        VisualElement Row(CheckResult item)
        {
            var accent = StatusColor(item.Status);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.backgroundColor = ColRow;
            row.style.marginBottom = 3;
            row.style.paddingTop = 6;
            row.style.paddingBottom = 6;
            row.style.paddingLeft = 8;
            row.style.paddingRight = 8;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = accent;
            row.style.borderTopLeftRadius = 3;
            row.style.borderBottomLeftRadius = 3;

            var glyph = new Label(Glyph(item.Status));
            glyph.style.width = 18;
            glyph.style.fontSize = 12;
            glyph.style.unityFontStyleAndWeight = FontStyle.Bold;
            glyph.style.color = accent;
            row.Add(glyph);

            var column = new VisualElement();
            column.style.flexGrow = 1;
            column.style.flexShrink = 1;
            row.Add(column);

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            column.Add(head);

            var title = new Label(item.Title);
            title.style.fontSize = 11.5f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = ColText;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.flexShrink = 1;
            head.Add(title);

            if (!string.IsNullOrEmpty(item.Detail))
            {
                var detail = new Label(item.Detail);
                detail.style.fontSize = 10.5f;
                detail.style.color = ColTextDim;
                detail.style.whiteSpace = WhiteSpace.Normal;
                detail.style.marginTop = 2;
                column.Add(detail);
            }

            if (item.CustomControl != null)
            {
                var custom = item.CustomControl();
                if (custom != null)
                {
                    custom.style.marginTop = 6;
                    column.Add(custom);
                }
            }

            if (item.Facts.Count > 0)
            {
                var facts = new VisualElement();
                facts.style.marginTop = 4;
                facts.style.display = DisplayStyle.None;
                foreach (var fact in item.Facts)
                {
                    var line = new Label("- " + fact);
                    line.style.fontSize = 10;
                    line.style.color = ColTextDim;
                    line.style.whiteSpace = WhiteSpace.Normal;
                    facts.Add(line);
                }
                column.Add(facts);

                var visible = false;
                var toggle = new Button(() =>
                {
                    visible = !visible;
                    facts.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                })
                { text = $"details ({item.Facts.Count})" };
                toggle.style.fontSize = 9;
                toggle.style.height = 15;
                toggle.style.marginTop = 3;
                toggle.style.alignSelf = Align.FlexStart;
                column.Add(toggle);
            }

            if (!string.IsNullOrEmpty(item.DocsUrl))
            {
                var docs = new Button(() => Application.OpenURL(item.DocsUrl)) { text = "Docs" };
                docs.style.width = 44;
                docs.style.height = 18;
                docs.style.fontSize = 9.5f;
                docs.style.marginLeft = 6;
                docs.style.alignSelf = Align.FlexStart;
                row.Add(docs);
            }

            if (item.Action != null)
            {
                var action = new Button(item.Action) { text = item.ActionLabel };
                action.style.height = 18;
                action.style.fontSize = 9.5f;
                action.style.marginLeft = 6;
                action.style.alignSelf = Align.FlexStart;
                row.Add(action);
            }

            return row;
        }

        VisualElement Footer()
        {
            var footer = new VisualElement();
            footer.style.marginTop = 10;
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;

            footer.Add(Chip("PASS", ColPass));
            footer.Add(Legend("passed"));
            footer.Add(Chip("WARN", ColWarn));
            footer.Add(Legend("review before release"));
            footer.Add(Chip("FAIL", ColFail));
            footer.Add(Legend("blocks ads or the build"));
            footer.Add(Chip("MANUAL", ColManual));
            footer.Add(Legend("cannot be verified from the Editor"));

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            footer.Add(spacer);

            var hint = new Label("Mock ads work in the Editor with any credentials; real ads need a device build.");
            hint.style.fontSize = 9.5f;
            hint.style.color = ColTextDim;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.unityTextAlign = TextAnchor.MiddleRight;
            hint.style.maxWidth = 320;
            footer.Add(hint);

            return footer;
        }

        VisualElement Legend(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 9.5f;
            label.style.color = ColTextDim;
            label.style.marginLeft = 4;
            label.style.marginRight = 10;
            return label;
        }

        VisualElement Chip(string text, Color color)
        {
            var chip = new Label(text);
            chip.style.fontSize = 9.5f;
            chip.style.unityFontStyleAndWeight = FontStyle.Bold;
            chip.style.color = color;
            chip.style.backgroundColor = new Color(color.r, color.g, color.b, 0.16f);
            chip.style.paddingLeft = 6;
            chip.style.paddingRight = 6;
            chip.style.paddingTop = 2;
            chip.style.paddingBottom = 2;
            chip.style.marginLeft = 4;
            chip.style.borderTopLeftRadius = 8;
            chip.style.borderTopRightRadius = 8;
            chip.style.borderBottomLeftRadius = 8;
            chip.style.borderBottomRightRadius = 8;
            return chip;
        }

        VisualElement Card(Color accent)
        {
            var card = new VisualElement();
            card.style.backgroundColor = ColCard;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.marginBottom = 8;
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = accent;
            card.style.borderTopLeftRadius = 4;
            card.style.borderBottomLeftRadius = 4;
            return card;
        }

        static Color StatusColor(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Pass: return ColPass;
                case CheckStatus.Warning: return ColWarn;
                case CheckStatus.Fail: return ColFail;
                case CheckStatus.Manual: return ColManual;
                default: return ColInfo;
            }
        }

        static string Glyph(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Pass: return "\u2713";
                case CheckStatus.Warning: return "!";
                case CheckStatus.Fail: return "\u2715";
                case CheckStatus.Manual: return "\u2022";
                default: return "i";
            }
        }

        // ---------------------------------------------------------------- orchestration

        void RunChecks()
        {
            apiUsage = null;
            mediationSettings = null;
            networkSettings = null;

            sdkVersion = GetPackageVersion("com.unity.services.levelplay");
            helperVersion = GetHelperPackageVersion();
            helpers = FindHelperInstances();
            apiUsage = ScanProjectSources();

            sections.Clear();

            BeginSection("0 - Editor play mode", "Play mode can serve LevelPlay mock ads and the Unity IAP fake store: no device, no credentials. Enable it so your monetization assembly compiles in the Editor.");
            CheckEditorTestMode();
            CheckEditorMockAds();
            CheckIapFakeStore();

            BeginSection("1 - Package & SDK", "Distribution, version thresholds and developer flags of the Ads Mediation package.");
            CheckSdkInstalled();
            CheckSdkVersion();
            CheckNetworkAdapters();
            CheckLegacyFolders();
            CheckMavenCentral();
            CheckAutoInitConflict();
            CheckDeveloperFlags();
            CheckSdkDeveloperSettings();

            BeginSection("2 - Native dependencies", "LevelPlay needs native libraries resolved outside UPM, per platform.");
            CheckDependencyManager();
            CheckAndroidResolvedDependencies();
            CheckAdapterDescriptors();
            CheckAndroidGradleTemplate();
            CheckIosCocoaPods();
            CheckInternetPermission();

            BeginSection("3 - Helper component", "Where the LevelPlayHelper lives and whether the runtime will find it.");
            CheckHelperInstances();
            CheckHelperReachability();
            CheckHelperDuplicates();

            BeginSection("4 - Configuration", "Credentials, ad formats and consent written into the helper instance.");
            CheckAppKeys();
            CheckAdUnitMatrix();
            CheckFormatCoverage();
            CheckConsentConfiguration();
            CheckCadenceConfiguration();
            CheckTestSuiteFlag();
            CheckPlaceholders();

            BeginSection("5 - Android build", "Player settings that the LevelPlay Android SDK requires.");
            CheckAndroidBackend();
            CheckAndroidArchitectures();
            CheckAndroidAdIdPermission();
            CheckAndroidMinSdk();

            BeginSection("6 - iOS build", "Only required when shipping on iOS.");
            CheckIosAtt();
            CheckIosTrackingUsageDescription();
            CheckIosSkAdNetwork();
            CheckAdmobSettings();

            BeginSection("7 - Project code integration", "Scans the project sources for the LevelPlay/helper API usage patterns the SDK expects.");
            CheckRewardedUsage();
            CheckInterstitialUsage();
            CheckBannerUsage();
            CheckIlrdUsage();
            CheckPrivacyApiUsage();
            CheckDeprecatedApiUsage();
            CheckBidFloorUsage();

            BeginSection("8 - Release validation", "Cannot be verified from the Editor - tick these off before shipping.");
            ManualReleaseItems();

            lastRun = DateTime.Now;
            Rebuild();
        }

        void BeginSection(string title, string subtitle)
        {
            active = new Section { Title = title, Subtitle = subtitle };
            sections.Add(active);
        }

        CheckResult Add(CheckStatus status, string title, string detail = null, string docs = null, string actionLabel = null, Action action = null)
        {
            var item = new CheckResult
            {
                Title = title,
                Status = status,
                Detail = detail ?? "",
                DocsUrl = docs,
                ActionLabel = actionLabel,
                Action = action
            };
            active.Items.Add(item);
            return item;
        }

        CheckResult Manual(string title, string detail, string docs = null) => Add(CheckStatus.Manual, title, detail, docs);

        // ---------------------------------------------------------------- section 0: editor play mode

        void CheckEditorTestMode()
        {
            var wanted = LevelPlayEditorTestMode.EffectiveDefines;
            var enabled = LevelPlayEditorTestMode.Enabled;
            var consistent = LevelPlayEditorTestMode.IsConsistent(out var consistencyDetail);

            CheckStatus status;
            string detail;

            if (enabled && consistent)
            {
                status = CheckStatus.Pass;
                detail = "Active: your monetization assembly compiles in Play mode, so mock ads and the IAP fake store are available.";
            }
            else if (enabled)
            {
                status = CheckStatus.Warning;
                detail = consistencyDetail;
            }
            else if (!consistent)
            {
                status = CheckStatus.Warning;
                detail = consistencyDetail + " Use the switch below to make the state consistent again.";
            }
            else if (wanted.Count > 0)
            {
                status = CheckStatus.Info;
                detail = "Off: Play mode keeps the no-op monetization service until you enable it.";
            }
            else
            {
                status = CheckStatus.Info;
                detail = "Nothing to enable: no project define gates your monetization assembly. Type one below if your code is gated.";
            }

            var item = Add(status, "Enabled on Editor", detail, null);
            item.Facts.Add(LevelPlayEditorTestMode.StatusText);
            item.Facts.Add("active build target: " + LevelPlayEditorTestMode.ActiveTargetName);
            item.Facts.Add("block builds while enabled: " + LevelPlayEditorTestMode.BlockBuilds);

            var candidates = LevelPlayEditorTestMode.DetectCandidates();
            item.Facts.Add(candidates.Count > 0
                ? "asmdef defineConstraints detected: " + string.Join(", ", candidates)
                : "no defineConstraints found in project asmdefs");

            item.CustomControl = BuildEditorTestModeControls;
        }

        VisualElement BuildEditorTestModeControls()
        {
            var box = new VisualElement();

            var toggle = new Toggle("Enabled on Editor") { value = LevelPlayEditorTestMode.Enabled };
            toggle.style.unityFontStyleAndWeight = FontStyle.Bold;
            toggle.RegisterValueChangedCallback(evt =>
            {
                LevelPlayEditorTestMode.SetEnabled(evt.newValue);
                RunChecks();
            });
            box.Add(toggle);

            var defines = new TextField("Defines") { value = LevelPlayEditorTestMode.ConfiguredDefinesText };
            defines.tooltip = "Separate with ';'. Leave empty to auto-detect the project's asmdef defineConstraints.";
            defines.style.marginTop = 4;
            defines.RegisterValueChangedCallback(evt => LevelPlayEditorTestMode.ConfiguredDefinesText = evt.newValue);
            box.Add(defines);

            var block = new Toggle("Block builds while enabled") { value = LevelPlayEditorTestMode.BlockBuilds };
            block.style.marginTop = 2;
            block.RegisterValueChangedCallback(evt => LevelPlayEditorTestMode.BlockBuilds = evt.newValue);
            box.Add(block);

            var note = new Label("Mock ads accept any credential, so the App Key and Ad Unit IDs can stay empty while you iterate. Real mediation only happens on a device build.");
            note.style.fontSize = 10;
            note.style.color = ColTextDim;
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginTop = 4;
            box.Add(note);

            return box;
        }

        void CheckEditorMockAds()
        {
            Add(CheckStatus.Info, "Editor mock ads",
                "In Play mode the LevelPlay SDK serves mock ads and the helper falls back to mock credentials when the Inspector is empty, so the rewarded/interstitial flows can be exercised without a device. " +
                "OnAdLoadFailed, clicks and ILRD only happen on a device build.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite");
        }

        void CheckIapFakeStore()
        {
            var version = GetPackageVersion("com.unity.purchasing");
            if (string.IsNullOrEmpty(version))
            {
                Add(CheckStatus.Info, "In-app purchases (Editor fake store)",
                    "com.unity.purchasing is not installed, so there is no store to mock.");
                return;
            }

            Add(CheckStatus.Pass, "In-app purchases (Editor fake store)",
                $"Unity IAP {version} uses its built-in fake store in Play mode, so purchase, restore and entitlement flows run without Play Billing or the App Store. Real receipts require a device build.")
                .WithFacts("receipt validation is skipped in the Editor by design");
        }

        // ---------------------------------------------------------------- section 1: package & SDK

        void CheckSdkInstalled()
        {
            var type = Type.GetType("Unity.Services.LevelPlay.LevelPlay, Unity.LevelPlay");
            if (type == null)
            {
                Add(CheckStatus.Fail, "Ads Mediation package installed",
                    "Unity.Services.LevelPlay is not resolvable. Install it via Window > Package Manager > search 'Ads Mediation'. Without it every LevelPlay API fails with CS0246.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
                return;
            }

            var item = Add(CheckStatus.Pass, "Ads Mediation package installed",
                "Unity.Services.LevelPlay assembly resolved.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
            if (!string.IsNullOrEmpty(sdkVersion)) item.Facts.Add("com.unity.services.levelplay " + sdkVersion);
            item.Facts.Add("assembly: " + type.Assembly.GetName().Name);
        }

        void CheckSdkVersion()
        {
            if (string.IsNullOrEmpty(sdkVersion))
            {
                Add(CheckStatus.Warning, "SDK version meets the privacy / ILRD minimums",
                    "Could not read the resolved package version, so the 9.4.0 (privacy) and 9.5.0 (per-instance ILRD) thresholds could not be verified.");
                return;
            }

            var parsed = ParseVersion(sdkVersion);
            if (parsed == null)
            {
                Add(CheckStatus.Warning, "SDK version meets the privacy / ILRD minimums", "Unparsable version: " + sdkVersion);
                return;
            }

            var min = new Version(9, 4, 0);
            var ilrd = new Version(9, 5, 0);

            if (parsed < min)
            {
                Add(CheckStatus.Fail, "SDK version meets the privacy / ILRD minimums",
                    $"LevelPlay {sdkVersion} is below 9.4.0: neither SetGDPRConsent nor SetGDPRConsents compile. Upgrade through Ads Mediation > Network Manager.")
                    .Facts.Add("required: >= 9.4.0 for privacy APIs, >= 9.5.0 for per-instance OnAdImpressionDataReady");
                return;
            }

            if (parsed < ilrd)
            {
                Add(CheckStatus.Warning, "SDK version meets the privacy / ILRD minimums",
                    $"LevelPlay {sdkVersion}: privacy APIs are available, but ILRD still uses the global OnImpressionDataReady (deprecated on 9.5.0+).")
                    .Facts.Add("SetGDPRConsent(bool): available");
                return;
            }

            Add(CheckStatus.Pass, "SDK version meets the privacy / ILRD minimums",
                $"LevelPlay {sdkVersion}: boolean GDPR consent and per-ad-instance OnAdImpressionDataReady are both available.")
                .Facts.Add("SetGDPRConsent(bool) / SetCCPA / SetCOPPA: available");
        }

        void CheckNetworkAdapters()
        {
            var editorDir = "Assets/LevelPlay/Editor";
            if (!Directory.Exists(editorDir))
            {
                Add(CheckStatus.Warning, "Mediation adapters present",
                    "Assets/LevelPlay/Editor does not exist yet - it is created by the Ads Mediation package. Open Ads Mediation > Network Manager once so the adapter descriptors are generated.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
                return;
            }

            string[] files;
            try { files = Directory.GetFiles(editorDir, "*Dependencies.xml"); }
            catch (Exception e)
            {
                Add(CheckStatus.Warning, "Mediation adapters present", "Could not read " + editorDir + ": " + e.Message);
                return;
            }

            var adapters = files
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null && n.IndexOf("Adapter", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();

            var item = Add(adapters.Length > 0 ? CheckStatus.Pass : CheckStatus.Warning,
                "Mediation adapters present",
                adapters.Length > 0
                    ? $"{adapters.Length} adapter dependency descriptor(s) found. At least one network (Unity Ads ships with the package) must be active for ads to fill."
                    : "No adapter descriptors found. Add at least one network in Ads Mediation > Network Manager, otherwise no ad can fill on device.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
            foreach (var adapter in adapters)
                item.Facts.Add(adapter);
        }

        void CheckLegacyFolders()
        {
            if (Directory.Exists("Assets/IronSource"))
            {
                Add(CheckStatus.Warning, "No legacy SDK copy left in Assets",
                    "Assets/IronSource exists. With the UPM distribution the old folder must be deleted - it duplicates the SDK and breaks the Android/iOS build.",
                    "https://docs.unity.com/grow/levelplay/sdk/unity/migrate-to-9-0-0/")
                    .Facts.Add("found: Assets/IronSource");
                return;
            }

            Add(CheckStatus.Pass, "No legacy SDK copy left in Assets",
                "No Assets/IronSource folder. (Assets/LevelPlay is expected: the UPM package writes its adapter descriptors and settings there.)");
        }

        void CheckMavenCentral()
        {
            var editorDir = "Assets/LevelPlay/Editor";
            if (!Directory.Exists(editorDir))
            {
                Add(CheckStatus.Info, "Android dependencies served from Maven Central",
                    "Adapter descriptors not generated yet - nothing to scan.");
                return;
            }

            var hits = new List<string>();
            try
            {
                foreach (var file in Directory.GetFiles(editorDir, "*Dependencies.xml"))
                {
                    var text = File.ReadAllText(file);
                    if (text.Contains("android-sdk.is.com"))
                        hits.Add(Path.GetFileName(file));
                }
            }
            catch (Exception e)
            {
                Add(CheckStatus.Warning, "Android dependencies served from Maven Central", "Scan failed: " + e.Message);
                return;
            }

            if (hits.Count > 0)
            {
                var item = Add(CheckStatus.Fail, "Android dependencies served from Maven Central",
                    "Descriptor(s) still point at android-sdk.is.com, which was retired. Android builds fail on dependency resolution. Update the Ads Mediation package so the descriptors use com.unity3d.ads-mediation artifacts.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
                foreach (var hit in hits) item.Facts.Add(hit);
                return;
            }

            Add(CheckStatus.Pass, "Android dependencies served from Maven Central",
                "No android-sdk.is.com reference in the adapter descriptors.");
        }

        void CheckAutoInitConflict()
        {
            var settings = MediationSettingsAsset();
            if (settings == null)
            {
                Add(CheckStatus.Info, "SDK auto-init disabled",
                    "Assets/LevelPlay/Resources/LevelPlayMediationSettings.asset not found yet - it is created the first time the Ads Mediation settings are opened.");
                return;
            }

            var autoInit = GetBool(settings, "EnableIronsourceSDKInitAPI", false);
            var hasManualInit = apiUsage.ContainsKey("LevelPlay.Init") || helpers.Count > 0;

            if (autoInit && hasManualInit)
            {
                Add(CheckStatus.Warning, "SDK auto-init disabled",
                    "EnableIronsourceSDKInitAPI is ON while the project also initializes LevelPlay itself. Both paths run and the App Key can diverge - pick one (LevelPlayHelper needs its own Initialize()).",
                    "https://docs.unity.com/grow/levelplay/sdk/unity/developer-tools/")
                    .Facts.Add("EnableIronsourceSDKInitAPI = true");
                return;
            }

            Add(CheckStatus.Pass, "SDK auto-init disabled",
                autoInit
                    ? "Auto-init is ON and no manual initializer was detected - consistent."
                    : "Auto-init is OFF; initialization is driven by your own code (LevelPlayHelper).");
        }

        void CheckDeveloperFlags()
        {
            var settings = MediationSettingsAsset();
            if (settings == null)
            {
                Add(CheckStatus.Info, "Developer flags off for release", "Settings asset not present yet.");
                return;
            }

            var adapterDebug = GetBool(settings, "EnableAdapterDebug", false);
            var integrationHelper = GetBool(settings, "EnableIntegrationHelper", false);

            if (adapterDebug || integrationHelper)
            {
                var item = Add(CheckStatus.Warning, "Developer flags off for release",
                    "EnableAdapterDebug / EnableIntegrationHelper should be OFF for production. Turn them on only while diagnosing on device.");
                if (adapterDebug) item.Facts.Add("EnableAdapterDebug = true");
                if (integrationHelper) item.Facts.Add("EnableIntegrationHelper = true");
                return;
            }

            Add(CheckStatus.Pass, "Developer flags off for release",
                "EnableAdapterDebug and EnableIntegrationHelper are both off.");
        }

        void CheckSdkDeveloperSettings()
        {
            var settings = MediationSettingsAsset();
            if (settings == null)
            {
                Add(CheckStatus.Info, "Developer Settings App Keys filled",
                    "LevelPlay Mediation Settings asset not found yet.");
                return;
            }

            var android = GetString(settings, "AndroidAppKey");
            var ios = GetString(settings, "IOSAppKey");
            var bothEmpty = string.IsNullOrEmpty(android) && string.IsNullOrEmpty(ios);

            Add(bothEmpty ? CheckStatus.Info : CheckStatus.Pass, "Developer Settings App Keys filled",
                bothEmpty
                    ? "Ads Mediation > Developer Settings has no App Keys. That is fine while the helper supplies the App Key; the SDK only needs them for networks configured outside the helper (for example AdMob mediation)."
                    : "App Keys are present in Ads Mediation > Developer Settings.")
                .Facts.Add("Android: " + (string.IsNullOrEmpty(android) ? "empty" : Mask(android)));
        }

        // ---------------------------------------------------------------- section 2: native dependencies

        void CheckDependencyManager()
        {
            var edm4uPackage = GetPackageVersion("com.google.external-dependency-manager") != null;
            var jarResolver = Type.GetType("Google.JarResolver.PlayServicesSupport, Google.JarResolver") != null;

            var folders = new[]
            {
                "Assets/ExternalDependencyManager",
                "Assets/MobileDependencyResolver",
                "Assets/Mobile Dependency Resolver",
                "Assets/External Dependency Manager"
            }.Where(Directory.Exists).ToArray();

            var present = edm4uPackage || jarResolver || folders.Length > 0;
            var item = Add(present ? CheckStatus.Pass : CheckStatus.Warning, "Native dependency manager installed (EDM4U / UEDM / MDR)",
                present
                    ? "A dependency manager is available to resolve the LevelPlay native libraries."
                    : "No EDM4U / UEDM / MDR detected. Android/iOS builds fail without it (the code still compiles in the Editor). Restart Unity and accept the Mobile Dependency Resolver prompt, or install EDM4U.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
            if (edm4uPackage) item.Facts.Add("com.google.external-dependency-manager " + GetPackageVersion("com.google.external-dependency-manager"));
            if (jarResolver) item.Facts.Add("Google.JarResolver.PlayServicesSupport resolved");
            foreach (var folder in folders) item.Facts.Add(folder);
        }

        void CheckAndroidResolvedDependencies()
        {
            const string resolverXml = "ProjectSettings/AndroidResolverDependencies.xml";
            var hasGradleOutput = Directory.Exists("Assets/Plugins/Android")
                                  && Directory.GetFiles("Assets/Plugins/Android", "*", SearchOption.AllDirectories).Length > 0;

            if (!File.Exists(resolverXml))
            {
                Add(hasGradleOutput ? CheckStatus.Warning : CheckStatus.Warning, "Android native dependencies resolved",
                    "ProjectSettings/AndroidResolverDependencies.xml not found, so no Android resolve has been recorded. Run Assets > External Dependency Manager > Android Resolver > Resolve (newer MDR resolves automatically on build).");
                return;
            }

            var packages = ReadResolverPackages(resolverXml);
            var hasMediation = packages.Any(p => p.IndexOf("ads-mediation", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || p.IndexOf("levelplay", StringComparison.OrdinalIgnoreCase) >= 0);
            var hasUnityAds = packages.Any(p => p.IndexOf("unity-ads", StringComparison.OrdinalIgnoreCase) >= 0);
            var hasAdsIdentifier = packages.Any(p => p.IndexOf("ads-identifier", StringComparison.OrdinalIgnoreCase) >= 0);

            var status = hasMediation ? CheckStatus.Pass : CheckStatus.Warning;
            var item = Add(status, "Android native dependencies resolved",
                hasMediation
                    ? "The LevelPlay mediation SDK and at least one adapter are recorded as resolved."
                    : "No LevelPlay mediation artifact in the recorded Android dependencies. Re-run the Android Resolver so the mediation SDK and adapters are pulled.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
            item.Facts.Add("mediation artifacts: " + (hasMediation ? "yes" : "no"));
            item.Facts.Add("unity-ads adapter: " + (hasUnityAds ? "yes" : "no"));
            item.Facts.Add("play-services-ads-identifier: " + (hasAdsIdentifier ? "yes" : "no"));
            foreach (var package in packages)
                item.Facts.Add("package: " + package);
        }

        void CheckAdapterDescriptors()
        {
            var editorDir = "Assets/LevelPlay/Editor";
            var xml = Directory.Exists(editorDir) ? Directory.GetFiles(editorDir, "*Dependencies.xml") : Array.Empty<string>();

            Add(xml.Length > 0 ? CheckStatus.Pass : CheckStatus.Warning, "Adapter dependency descriptors committed",
                xml.Length > 0
                    ? "The EDM4U descriptors that pull the adapter AARs/Pods are present in the project."
                    : "No *Dependencies.xml under Assets/LevelPlay/Editor. Open Ads Mediation > Network Manager once to generate them.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation")
                .Facts.Add(xml.Length + " descriptor(s)");
        }

        void CheckAndroidGradleTemplate()
        {
            var hasMainTemplate = File.Exists("Assets/Plugins/Android/mainTemplate.gradle");
            var hasLauncher = File.Exists("Assets/Plugins/Android/launcherTemplate.gradle");
            var hasProperties = File.Exists("Assets/Plugins/Android/gradleTemplate.properties");

            Add(CheckStatus.Info, "Android Gradle templates",
                hasMainTemplate || hasLauncher
                    ? "Custom Gradle template(s) are enabled - required by older LevelPlay packages and harmless with the current one."
                    : "No custom Gradle template. Current LevelPlay packages do not need one; only enable it if a build error asks for it.")
                .WithFacts(
                    "mainTemplate.gradle: " + (hasMainTemplate ? "present" : "absent"),
                    "launcherTemplate.gradle: " + (hasLauncher ? "present" : "absent"),
                    "gradleTemplate.properties: " + (hasProperties ? "present" : "absent"));
        }

        void CheckIosCocoaPods()
        {
            var podfile = File.Exists("Assets/Plugins/iOS/Podfile");
            var iosPlugins = Directory.Exists("Assets/Plugins/iOS");

            Add(podfile ? CheckStatus.Pass : CheckStatus.Info, "iOS CocoaPods installed",
                podfile
                    ? "A Podfile was generated for the iOS resolver output."
                    : "No Podfile under Assets/Plugins/iOS. Required only for iOS builds - run Assets > External Dependency Manager > iOS Resolver > Install Cocoapods.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation")
                .Facts.Add("Assets/Plugins/iOS: " + (iosPlugins ? "present" : "absent"));
        }

        void CheckInternetPermission()
        {
            var forced = PlayerSettings.Android.forceInternetPermission;
            const string manifest = "Assets/Plugins/Android/AndroidManifest.xml";
            var declared = File.Exists(manifest) && File.ReadAllText(manifest).Contains("android.permission.INTERNET");

            Add(forced || declared ? CheckStatus.Pass : CheckStatus.Warning, "Android INTERNET permission",
                forced || declared
                    ? "Internet access is granted for the Android build (ads require it)."
                    : "Android INTERNET permission was not detected. Enable Player Settings > Android > Internet Access (Force internet permission) or declare android.permission.INTERNET in the manifest.",
                null)
                .WithFacts(
                    "forceInternetPermission: " + forced,
                    "declared in custom manifest: " + declared);
        }

        // ---------------------------------------------------------------- section 3: helper component

        void CheckHelperInstances()
        {
            if (helpers.Count > 0)
            {
                var item = Add(CheckStatus.Pass, "LevelPlayHelper component present",
                    $"Found {helpers.Count} instance(s) across prefabs and open scenes.");
                foreach (var helper in helpers)
                    item.Facts.Add(helper.Location + (helper.IsPrefab ? " (prefab)" : " (scene)"));
                return;
            }

            Add(CheckStatus.Fail, "LevelPlayHelper component present",
                "No LevelPlayHelper (or subclass) found on any prefab or open scene. Add it to a persistent GameObject that exists before the first ad call.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
        }

        void CheckHelperReachability()
        {
            if (helpers.Count == 0)
                return;

            var firstScene = FirstEnabledScene();
            var reachable = new List<string>();
            var unreachable = new List<string>();

            foreach (var helper in helpers)
            {
                if (helper.IsPrefab)
                {
                    if (helper.Location.Replace('\\', '/').Contains("/Resources/"))
                        reachable.Add(helper.Location + " (Resources - instantiable at runtime)");
                    else
                        unreachable.Add(helper.Location + " (prefab outside a Resources folder)");
                    continue;
                }

                if (!string.IsNullOrEmpty(firstScene) && helper.Location == firstScene)
                    reachable.Add(helper.Location + " (first enabled build scene)");
                else
                    unreachable.Add(helper.Location + (string.IsNullOrEmpty(firstScene) ? "" : " (scene is not the first in Build Settings)"));
            }

            var item = Add(reachable.Count > 0 ? CheckStatus.Pass : CheckStatus.Warning, "Helper reachable at runtime",
                reachable.Count > 0
                    ? "At least one helper instance will exist at runtime before the first ad call."
                    : "The helper only exists in places the runtime will not load it from. Put it in the first enabled build scene, or in a prefab under a Resources folder that you instantiate at boot.",
                null);
            foreach (var entry in reachable) item.Facts.Add("ok: " + entry);
            foreach (var entry in unreachable) item.Facts.Add("unreachable: " + entry);
            if (!string.IsNullOrEmpty(firstScene)) item.Facts.Add("first enabled build scene: " + firstScene);
        }

        void CheckHelperDuplicates()
        {
            if (helpers.Count <= 1)
            {
                if (helpers.Count == 1)
                    Add(CheckStatus.Pass, "Single helper instance", "Exactly one LevelPlayHelper instance, so the singleton is unambiguous.");
                return;
            }

            Add(CheckStatus.Warning, "Single helper instance",
                $"{helpers.Count} LevelPlayHelper instances found. The component is a singleton (the second one destroys itself), which hides configuration mistakes - keep one, and make sure the surviving one (the prefab you instantiate) holds the credentials.")
                .Facts.Add("instances: " + helpers.Count);
        }

        // ---------------------------------------------------------------- section 4: configuration

        void CheckAppKeys()
        {
            if (helpers.Count == 0)
                return;

            foreach (var helper in helpers.Select(h => h.Component).Distinct())
            {
                var so = new SerializedObject(helper);
                var android = GetString(so, "androidAppKey");
                var ios = GetString(so, "iosAppKey");

                var androidSet = !string.IsNullOrEmpty(android);
                var iosSet = !string.IsNullOrEmpty(ios);
                var androidPlaceholder = androidSet && IsPlaceholder(android);
                var iosPlaceholder = iosSet && IsPlaceholder(ios);

                CheckStatus status;
                string detail;
                if ((!androidSet && !iosSet) || androidPlaceholder || iosPlaceholder)
                {
                    status = CheckStatus.Fail;
                    detail = androidPlaceholder || iosPlaceholder
                        ? "The App Key still looks like a placeholder. Copy the real App Key from the LevelPlay dashboard (Apps > your app)."
                        : "Both App Key fields are empty. LevelPlay.Init cannot succeed without the App Key.";
                }
                else
                {
                    status = CheckStatus.Pass;
                    detail = "App Key is set per platform. Mock ads accept any value, but device builds need the real key from the dashboard.";
                }

                Add(status, "App Key set (" + helper.gameObject.name + ")", detail,
                    "https://docs.unity.com/en-us/grow/levelplay/platform/get-started/add-app")
                    .WithFacts(
                        "Android: " + Describe(android),
                        "iOS: " + Describe(ios));
            }
        }

        void CheckAdUnitMatrix()
        {
            if (helpers.Count == 0)
                return;

            foreach (var helper in helpers.Select(h => h.Component).Distinct())
            {
                var so = new SerializedObject(helper);
                var rows = new[]
                {
                    ("Android", "interstitial", GetString(so, "androidInterstitialAdUnitId")),
                    ("Android", "rewarded", GetString(so, "androidRewardedAdUnitId")),
                    ("Android", "banner", GetString(so, "androidBannerAdUnitId")),
                    ("iOS", "interstitial", GetString(so, "iosInterstitialAdUnitId")),
                    ("iOS", "rewarded", GetString(so, "iosRewardedAdUnitId")),
                    ("iOS", "banner", GetString(so, "iosBannerAdUnitId"))
                };

                var filled = rows.Count(r => !string.IsNullOrEmpty(r.Item3));
                var item = Add(filled > 0 ? CheckStatus.Pass : CheckStatus.Fail,
                    "Ad Unit IDs configured (" + helper.gameObject.name + ")",
                    filled > 0
                        ? "At least one ad format has an Ad Unit ID. An empty field disables that format on that platform."
                        : "No Ad Unit ID configured on either platform, so no ad format can load.",
                    "https://docs.unity.com/en-us/grow/levelplay/platform/get-started/ad-units");

                foreach (var (platform, format, value) in rows)
                    item.Facts.Add($"{platform} {format}: " + (string.IsNullOrEmpty(value) ? "empty (format disabled)" : Mask(value)));
            }
        }

        void CheckFormatCoverage()
        {
            if (helpers.Count == 0)
                return;

            var helper = helpers[0].Component;
            var so = new SerializedObject(helper);

            var usesRewarded = apiUsage.ContainsKey("ShowRewarded");
            var usesInterstitial = apiUsage.ContainsKey("ShowInterstitial");
            var usesBanner = apiUsage.ContainsKey("ShowBanner") || apiUsage.ContainsKey("CreateBanner");

            var missing = new List<string>();
            if (usesRewarded && string.IsNullOrEmpty(GetString(so, "androidRewardedAdUnitId")) && string.IsNullOrEmpty(GetString(so, "iosRewardedAdUnitId")))
                missing.Add("rewarded");
            if (usesInterstitial && string.IsNullOrEmpty(GetString(so, "androidInterstitialAdUnitId")) && string.IsNullOrEmpty(GetString(so, "iosInterstitialAdUnitId")))
                missing.Add("interstitial");
            if (usesBanner && string.IsNullOrEmpty(GetString(so, "androidBannerAdUnitId")) && string.IsNullOrEmpty(GetString(so, "iosBannerAdUnitId")))
                missing.Add("banner");

            if (missing.Count == 0)
            {
                Add(CheckStatus.Pass, "Every ad format used by the code has IDs",
                    $"Project code uses: {DescribeUsage(usesRewarded, usesInterstitial, usesBanner)}.")
                    .WithFacts(
                        "rewarded used: " + usesRewarded,
                        "interstitial used: " + usesInterstitial,
                        "banner used: " + usesBanner);
                return;
            }

            Add(CheckStatus.Warning, "Every ad format used by the code has IDs",
                "The project calls these formats but no Ad Unit ID is configured for either platform: " + string.Join(", ", missing) + ". Those calls will fail or fall back.",
                "https://docs.unity.com/en-us/grow/levelplay/platform/get-started/ad-units");
        }

        void CheckConsentConfiguration()
        {
            if (helpers.Count == 0)
                return;

            foreach (var helper in helpers.Select(h => h.Component).Distinct())
            {
                var so = new SerializedObject(helper);
                var consent = so.FindProperty("consentConfig");
                var gdpr = GetNestedBool(consent, "enableGDPRConsent", true);
                var ccpa = GetNestedBool(consent, "ccpaOptOut", false);
                var coppa = GetNestedBool(consent, "coppaChildDirected", false);

                Add(CheckStatus.Info, "Consent configuration (" + helper.gameObject.name + ")",
                    "Privacy flags are applied before LevelPlay.Init(). Whether GDPR/CCPA/COPPA applies to your app is a legal question - review it with counsel.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/regulation-advanced-settings")
                    .WithFacts(
                        "enableGDPRConsent: " + gdpr + " (reads PlayerPrefs 'UserConsent', 1 = consented)",
                        "ccpaOptOut: " + ccpa,
                        "coppaChildDirected: " + coppa);

                if (apiUsage.ContainsKey("SetUserConsent"))
                    Add(CheckStatus.Pass, "Consent collected then re-applied",
                        "SetUserConsent(...) is called from project code, so the consent answer is persisted and re-applied on the next launch.")
                        .Facts.Add("uses: SetUserConsent");
            }
        }

        void CheckCadenceConfiguration()
        {
            if (helpers.Count == 0)
                return;

            foreach (var helper in helpers.Select(h => h.Component).Distinct())
            {
                var so = new SerializedObject(helper);
                var ads = so.FindProperty("adsConfig");
                var min = GetNestedInt(ads, "minAdInterval", 2);
                var initial = GetNestedInt(ads, "initialAdInterval", 5);
                var reduce = GetNestedInt(ads, "adsNeededToReduceInterval", 3);

                var ok = min > 0 && initial > 0 && reduce > 0 && initial >= min;
                Add(ok ? CheckStatus.Pass : CheckStatus.Warning, "Ad cadence configuration sane",
                    ok
                        ? "Cadence values are positive and the initial interval is not shorter than the minimum interval."
                        : "Cadence values look inconsistent (zero/negative, or initialAdInterval below minAdInterval). Interstitial pacing is what protects retention.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation")
                    .WithFacts(
                        $"minAdInterval: {min}",
                        $"initialAdInterval: {initial}",
                        $"adsNeededToReduceInterval: {reduce}");
            }
        }

        void CheckTestSuiteFlag()
        {
            if (helpers.Count == 0)
                return;

            foreach (var helper in helpers.Select(h => h.Component).Distinct())
            {
                var so = new SerializedObject(helper);
                var enabled = GetBool(so, "enableTestSuite", false);

                Add(enabled ? CheckStatus.Warning : CheckStatus.Pass, "Test Suite disabled for release",
                    enabled
                        ? "Enable Test Suite is ON. That is what you want while validating on device - turn it OFF before shipping."
                        : "Test Suite is off. Turn it on temporarily to validate the integration on a physical device.",
                    "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite");
            }
        }

        void CheckPlaceholders()
        {
            if (helpers.Count == 0)
                return;

            var suspicious = new List<string>();
            foreach (var helper in helpers.Select(h => h.Component).Distinct())
            {
                var so = new SerializedObject(helper);
                foreach (var field in new[]
                         {
                             "androidAppKey", "iosAppKey", "androidInterstitialAdUnitId", "androidRewardedAdUnitId",
                             "androidBannerAdUnitId", "iosInterstitialAdUnitId", "iosRewardedAdUnitId", "iosBannerAdUnitId"
                         })
                {
                    var value = GetString(so, field);
                    if (!string.IsNullOrEmpty(value) && IsPlaceholder(value))
                        suspicious.Add(field + " = " + value);
                }
            }

            if (suspicious.Count > 0)
            {
                var item = Add(CheckStatus.Fail, "No placeholder credentials",
                    "Editor mock ads accept any value, so dummies are easy to ship by accident. Replace these with the real dashboard values.",
                    "https://platform.ironsrc.com/");
                foreach (var entry in suspicious) item.Facts.Add(entry);
                return;
            }

            Add(CheckStatus.Pass, "No placeholder credentials",
                "No test/editor/YOUR_* placeholder detected in the helper credentials.");
        }

        // ---------------------------------------------------------------- section 5: Android build

        void CheckAndroidBackend()
        {
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
            Add(backend == ScriptingImplementation.IL2CPP ? CheckStatus.Pass : CheckStatus.Warning, "Android scripting backend is IL2CPP",
                backend == ScriptingImplementation.IL2CPP
                    ? "IL2CPP is set, which the native LevelPlay libraries require."
                    : "Android is using Mono. Switch to IL2CPP (Player Settings > Android > Other Settings); the mediation SDK targets IL2CPP builds.")
                .Facts.Add("ScriptingBackend: " + backend);
        }

        void CheckAndroidArchitectures()
        {
            var architectures = PlayerSettings.Android.targetArchitectures;
            var hasArm64 = (architectures & AndroidArchitecture.ARM64) != 0;

            Add(hasArm64 ? CheckStatus.Pass : CheckStatus.Warning, "Android target includes ARM64",
                hasArm64
                    ? "ARM64 is included - required by Google Play and by the LevelPlay native libraries."
                    : "ARM64 is missing from Target Architectures. Google Play rejects new uploads without it, and the mediation AARs are arm64-only for some networks.")
                .Facts.Add("targetArchitectures: " + architectures);
        }

        void CheckAndroidAdIdPermission()
        {
            var target = (int)PlayerSettings.Android.targetSdkVersion;
            var api33Plus = target == 0 || target >= 33;

            const string customManifest = "Assets/Plugins/Android/AndroidManifest.xml";
            var declaredInManifest = File.Exists(customManifest)
                                     && File.ReadAllText(customManifest).Contains("com.google.android.gms.permission.AD_ID");

            var settings = MediationSettingsAsset();
            var sdkDeclares = settings != null && GetBool(settings, "DeclareAD_IDPermission", false);

            var resolverProvides = ReadResolverPackages("ProjectSettings/AndroidResolverDependencies.xml")
                .Any(p => p.IndexOf("ads-identifier", StringComparison.OrdinalIgnoreCase) >= 0
                          || p.IndexOf("play-services-ads", StringComparison.OrdinalIgnoreCase) >= 0);

            var item = Add(CheckStatus.Info, "Android AD_ID permission (API 33+)", "");

            if (!api33Plus)
            {
                item.Status = CheckStatus.Pass;
                item.Detail = $"Target API is {target}, below 33 - the AD_ID permission is not required.";
                return;
            }

            item.Facts.Add("targetSdk: " + (target == 0 ? "Automatic (33+)" : target.ToString()));
            item.Facts.Add("declared in custom AndroidManifest.xml: " + declaredInManifest);
            item.Facts.Add("LevelPlay DeclareAD_IDPermission flag: " + sdkDeclares);
            item.Facts.Add("play-services-ads-identifier resolved (AAR declares it): " + resolverProvides);

            if (declaredInManifest || sdkDeclares || resolverProvides)
            {
                item.Status = CheckStatus.Pass;
                item.Detail = declaredInManifest
                    ? "Declared in the project's AndroidManifest.xml."
                    : sdkDeclares
                        ? "The LevelPlay SDK declares it during the build (DeclareAD_IDPermission is ON)."
                        : "Provided by the resolved play-services-ads-identifier AAR, whose manifest is merged into the build - no custom manifest needed.";
                return;
            }

            item.Status = CheckStatus.Fail;
            item.Detail = "Targeting API 33+ without com.google.android.gms.permission.AD_ID. Advertising ID access fails on Android 13+, which hurts attribution and eCPM. Declare it in a custom AndroidManifest.xml or enable DeclareAD_IDPermission in Ads Mediation settings.";
            item.DocsUrl = "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation";
        }

        void CheckAndroidMinSdk()
        {
            var min = (int)PlayerSettings.Android.minSdkVersion;
            Add(CheckStatus.Info, "Android min SDK reported",
                "LevelPlay's native SDK declares its own minimum; make sure the project minimum is at or above it (see the Android SDK integration docs).")
                .Facts.Add("minSdkVersion: " + (min == 0 ? "Automatic" : min.ToString()));
        }

        // ---------------------------------------------------------------- section 6: iOS build

        void CheckIosAtt()
        {
            var usesAtt = apiUsage.ContainsKey("ATT");
            Add(usesAtt ? CheckStatus.Pass : CheckStatus.Warning, "App Tracking Transparency implemented",
                usesAtt
                    ? "ATT APIs are called from project code. Keep the request before LevelPlay.Init() so personalized ads can fill."
                    : "No ATT call detected (ATTrackingStatusBinding / ATTrackingManager). Required on iOS 14.5+: Apple demands authorization before accessing the advertising identifier, and fill rate depends on it.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/regulation-advanced-settings");
        }

        void CheckIosTrackingUsageDescription()
        {
            var hasKey = apiUsage.ContainsKey("NSUserTrackingUsageDescription");
            Add(hasKey ? CheckStatus.Pass : CheckStatus.Warning, "NSUserTrackingUsageDescription in Info.plist",
                hasKey
                    ? "A post-build step writes NSUserTrackingUsageDescription into Info.plist."
                    : "No post-build writer for NSUserTrackingUsageDescription found. Unity 6 has no Player Settings field for it, so add it via a [PostProcessBuild] step - Apple rejects vague or missing descriptions.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/regulation-advanced-settings");
        }

        void CheckIosSkAdNetwork()
        {
            var so = NetworkSettingsAsset();
            if (so == null)
            {
                Add(CheckStatus.Info, "iOS SKAdNetwork IDs automated",
                    "Network Manager settings asset not found - open Ads Mediation > Network Manager once to generate it.");
                return;
            }

            var enabled = GetBool(so, "AddNetworksSkadnetworkID", false);

            Add(enabled ? CheckStatus.Pass : CheckStatus.Warning, "iOS SKAdNetwork IDs automated",
                enabled
                    ? "The SDK injects the networks' SKAdNetwork IDs into Info.plist at build time."
                    : "SKAdNetwork automation is OFF in the Network Manager. Enable it so the mediated networks' SKAdNetwork IDs land in Info.plist.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation")
                .Facts.Add("asset: " + so.targetObject.name);
        }

        void CheckAdmobSettings()
        {
            const string knownPath = "Assets/LevelPlay/Resources/LevelPlayMediatedNetworkSettings.asset";
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(knownPath);
            var path = knownPath;

            if (asset == null)
            {
                var assets = AssetDatabase.FindAssets("t:LevelPlayMediationNetworkSettings");
                if (assets.Length > 0)
                {
                    path = AssetDatabase.GUIDToAssetPath(assets[0]);
                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                }
            }

            if (asset == null)
            {
                Add(CheckStatus.Info, "AdMob mediation settings (only if AdMob is used)",
                    "No LevelPlayMediatedNetworkSettings asset yet - created when AdMob is enabled in Ads Mediation settings.");
                return;
            }

            var so = new SerializedObject(asset);
            var enabled = GetBool(so, "EnableAdmob", false);
            var androidId = GetString(so, "AdmobAndroidAppId");
            var iosId = GetString(so, "AdmobIOSAppId");

            if (!enabled)
            {
                Add(CheckStatus.Pass, "AdMob mediation settings (only if AdMob is used)",
                    "AdMob mediation is disabled - nothing to configure.");
                return;
            }

            var missing = new List<string>();
            if (string.IsNullOrEmpty(androidId) || !androidId.StartsWith("ca-app-pub-")) missing.Add("AdmobAndroidAppId");
            if (string.IsNullOrEmpty(iosId) || !iosId.StartsWith("ca-app-pub-")) missing.Add("AdmobIOSAppId");

            var item = Add(missing.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, "AdMob mediation settings (only if AdMob is used)",
                missing.Count == 0
                    ? "AdMob is enabled and both app IDs look valid."
                    : "AdMob is enabled but these IDs are missing or malformed (they start with ca-app-pub-): " + string.Join(", ", missing) + ".");
            if (!string.IsNullOrEmpty(androidId)) item.Facts.Add("Android: " + Mask(androidId));
            if (!string.IsNullOrEmpty(iosId)) item.Facts.Add("iOS: " + Mask(iosId));
        }

        // ---------------------------------------------------------------- section 7: project code

        void CheckRewardedUsage()
        {
            var uses = apiUsage.ContainsKey("ShowRewarded") || apiUsage.ContainsKey("ShowRewardedAd");
            if (!uses)
            {
                Add(CheckStatus.Info, "Rewarded flow", "No rewarded call found in project code. LevelPlayHelper already handles load/reload, placement checks and the reward callback.");
                return;
            }

            var loadGuard = apiUsage.ContainsKey("IsRewardedAdReady") || apiUsage.ContainsKey("RewardedReady");
            Add(loadGuard ? CheckStatus.Pass : CheckStatus.Warning, "Rewarded flow",
                loadGuard
                    ? "The rewarded button path checks readiness before showing, so the UI can degrade gracefully when inventory is empty."
                    : "Rewarded ads are shown without any readiness check in project code. Gate the button on IsRewardedAdReady()/RewardedReady so the player never taps into a dead end.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
        }

        void CheckInterstitialUsage()
        {
            if (!apiUsage.ContainsKey("ShowInterstitial"))
            {
                Add(CheckStatus.Info, "Interstitial pacing", "No interstitial call found in project code.");
                return;
            }

            var pacing = apiUsage.ContainsKey("AdInterval") || apiUsage.ContainsKey("EventsToShowAd") || apiUsage.ContainsKey("lastAdTime");
            Add(pacing ? CheckStatus.Pass : CheckStatus.Warning, "Interstitial pacing",
                pacing
                    ? "Interstitial shows are gated by a pacing counter/interval, which is the recommended way to protect retention."
                    : "Interstitials are shown without a pacing counter in project code. Cap them (every N events / 3-7 minutes) - frequency capping is part of the production checklist.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
        }

        void CheckBannerUsage()
        {
            var uses = apiUsage.ContainsKey("ShowBanner") || apiUsage.ContainsKey("CreateBanner");
            if (!uses)
            {
                Add(CheckStatus.Info, "Banner usage", "No banner call found - the banner Ad Unit IDs can stay empty (levelplayhelper treats an empty ID as 'format disabled').");
                return;
            }

            Add(CheckStatus.Pass, "Banner usage",
                "Banner calls found. One banner per ad unit, destroyed when leaving the screen that shows it.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");
        }

        void CheckIlrdUsage()
        {
            var uses = apiUsage.ContainsKey("AdRevenuePaid") || apiUsage.ContainsKey("OnImpressionDataReady") || apiUsage.ContainsKey("OnAdImpressionDataReady");
            Add(uses ? CheckStatus.Pass : CheckStatus.Warning, "Impression-level revenue (ILRD) forwarded",
                uses
                    ? "Ad revenue callbacks are consumed by project code, so eCPM can be reconciled with the analytics platform."
                    : "No ILRD consumer found. Subscribe to LevelPlayHelper.OnAdRevenuePaid (or OnImpressionDataReady) and forward it to analytics - it only fires on a device build, never with mock ads.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation")
                .Facts.Add("OnImpressionDataReady fires on a background thread: marshal before touching Unity APIs.");
        }

        void CheckPrivacyApiUsage()
        {
            var uses = apiUsage.ContainsKey("SetGDPRConsent") || apiUsage.ContainsKey("SetGDPRConsents")
                       || apiUsage.ContainsKey("SetCCPA") || apiUsage.ContainsKey("SetCOPPA");
            if (helpers.Count > 0 && !uses)
            {
                Add(CheckStatus.Info, "Privacy APIs reachable",
                    "The helper applies GDPR/CCPA/COPPA from its own settings before Init, so project code does not need to call the privacy APIs directly. Only call them yourself if a CMP or your own dialog collects the answer.")
                    .Facts.Add("helper consentConfig is applied before LevelPlay.Init()");
                return;
            }

            Add(uses ? CheckStatus.Pass : CheckStatus.Info, "Privacy APIs reachable",
                uses ? "Project code calls the LevelPlay privacy APIs." : "No direct privacy API usage in project code.");
        }

        void CheckDeprecatedApiUsage()
        {
            var found = new List<string>();
            foreach (var key in new[]
                     {
                         "IronSource.Agent", "com.unity3d.mediation", "OnImpressionDataReadyEvent",
                         "SetConsent(", "do_not_sell", "is_child_directed"
                     })
                if (apiUsage.ContainsKey(key))
                    found.Add(key);

            if (found.Count > 0)
            {
                var item = Add(CheckStatus.Fail, "No deprecated / removed LevelPlay APIs",
                    "These symbols no longer exist in LevelPlay 9.x and either fail to compile or silently do nothing (IronSource.Agent also has no equivalent for onApplicationPause - delete that override). Migrate to the LevelPlay / LevelPlayPrivacySettings APIs.",
                    "https://docs.unity.com/grow/levelplay/sdk/unity/migrate-to-9-0-0/");
                foreach (var symbol in found)
                {
                    item.Facts.Add(symbol);
                    if (apiUsage.TryGetValue(symbol, out var files))
                        foreach (var file in files)
                            item.Facts.Add("   " + file);
                }
                return;
            }

            Add(CheckStatus.Pass, "No deprecated / removed LevelPlay APIs",
                "No IronSource.Agent, com.unity3d.mediation, OnImpressionDataReadyEvent, SetConsent, do_not_sell or onApplicationPause usage found.");
        }

        void CheckBidFloorUsage()
        {
            Add(apiUsage.ContainsKey("SetBidFloor") ? CheckStatus.Pass : CheckStatus.Info, "Bid floors (optional)",
                apiUsage.ContainsKey("SetBidFloor")
                    ? "Bid floors are configured, raising eCPM at the cost of fill rate - watch the fill reports after release."
                    : "No bid floors set. Safe to skip initially and add later once you have real dashboard data (starting ranges: rewarded $0.50-2.00, interstitial $0.20-1.00, banner $0.05-0.20).");
        }

        // ---------------------------------------------------------------- section 8: release validation

        void ManualReleaseItems()
        {
            Manual("Dashboard: app and ad units created",
                "Each store listing (bundle id) needs its own LevelPlay app, and every ad unit must have active network instances or it will not fill on device.",
                "https://docs.unity.com/en-us/grow/levelplay/platform/get-started/add-app");

            Manual("Credentials match the dashboard",
                "Confirm the App Key and Ad Unit IDs in the helper are the values from the dashboard for this exact app.",
                "https://platform.ironsrc.com/");

            Manual("Test Suite validated on a real device",
                "Mock ads in the Editor never exercise load/display failures. Enable Test Suite, build to a physical device, and run every implemented format.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite");

            Manual("Development build used while testing on device",
                "Without a Development Build the SDK console output is not visible, which makes ad failures very hard to diagnose.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite");

            Manual("Tested on multiple devices / OS versions",
                "Different screen sizes, notches and OS versions affect banner layout and ad availability.");

            Manual("Error handling verified in airplane mode",
                "With no network the game must keep playing: no crash, no blocked UI, and the ad-dependent feature must fall back gracefully.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/unity-sdk-installation");

            Manual("iOS: privacy manifest and store declarations",
                "Align the app's PrivacyInfo.xcprivacy and App Store privacy answers with the data the mediation SDK collects.",
                "https://developer.apple.com/app-store/user-privacy-and-data-use/");

            Manual("Mock ads vs real ads expectations understood",
                "Mock ads fire OnAdLoaded/OnAdDisplayed/OnAdRewarded/OnAdClosed only; failures, clicks and ILRD require a device build.",
                "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/test-suite");
        }

        // ---------------------------------------------------------------- scanners

        Dictionary<string, List<string>> ScanProjectSources()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            string[] guids;
            try { guids = AssetDatabase.FindAssets("t:MonoScript"); }
            catch { return map; }

            // Symbols worth reporting, and how they appear in source.
            var symbols = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "LevelPlay.Init", new[] { "LevelPlay.Init(" } },
                { "ShowRewarded", new[] { "ShowRewarded(" } },
                { "ShowRewardedAd", new[] { "ShowRewardedAd(" } },
                { "IsRewardedAdReady", new[] { "IsRewardedAdReady(" } },
                { "RewardedReady", new[] { "RewardedReady" } },
                { "ShowInterstitial", new[] { "ShowInterstitial(" } },
                { "ShowBanner", new[] { "ShowBanner(" } },
                { "CreateBanner", new[] { "CreateBanner(" } },
                { "SetBidFloor", new[] { "SetBidFloor(" } },
                { "AdInterval", new[] { "AdInterval" } },
                { "EventsToShowAd", new[] { "EventsToShowAd" } },
                { "lastAdTime", new[] { "lastAdTime" } },
                { "AdRevenuePaid", new[] { "AdRevenuePaid" } },
                { "OnImpressionDataReady", new[] { "OnImpressionDataReady" } },
                { "OnAdImpressionDataReady", new[] { "OnAdImpressionDataReady" } },
                { "SetGDPRConsent", new[] { "SetGDPRConsent(" } },
                { "SetGDPRConsents", new[] { "SetGDPRConsents(" } },
                { "SetCCPA", new[] { "SetCCPA(" } },
                { "SetCOPPA", new[] { "SetCOPPA(" } },
                { "SetUserConsent", new[] { "SetUserConsent(" } },
                { "ATT", new[] { "ATTrackingStatusBinding", "ATTrackingManager", "ATTRequester" } },
                { "NSUserTrackingUsageDescription", new[] { "NSUserTrackingUsageDescription" } },
                { "IronSource.Agent", new[] { "IronSource.Agent" } },
                { "com.unity3d.mediation", new[] { "com.unity3d.mediation" } },
                { "OnImpressionDataReadyEvent", new[] { "OnImpressionDataReadyEvent" } },
                { "SetConsent(", new[] { "LevelPlay.SetConsent(", "IronSource.SetConsent(" } },
                { "do_not_sell", new[] { "\"do_not_sell\"" } },
                { "is_child_directed", new[] { "\"is_child_directed\"" } }
            };

            var scanned = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                    continue;                      // never scan packages - only project code
                if (scanned++ > 4000)
                    break;

                string text;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length > 1_500_000)
                        continue;
                    text = File.ReadAllText(path);
                }
                catch { continue; }

                foreach (var pair in symbols)
                {
                    if (pair.Value.Any(token => text.IndexOf(token, StringComparison.Ordinal) >= 0))
                    {
                        if (!map.TryGetValue(pair.Key, out var list))
                        {
                            list = new List<string>();
                            map[pair.Key] = list;
                        }
                        if (list.Count < 6)
                            list.Add(path);
                    }
                }

                // The helper also supports a project-provided consent answer key; treat it as usage.
                if (text.Contains("UserConsent"))
                    map["SetUserConsent"] = map.TryGetValue("SetUserConsent", out var existing) ? existing : new List<string> { path };
            }

            return map;
        }

        List<HelperInstance> FindHelperInstances()
        {
            var result = new List<HelperInstance>();
            var seen = new HashSet<LevelPlayHelper>();

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                foreach (var helper in prefab.GetComponentsInChildren<LevelPlayHelper>(true))
                {
                    if (helper == null || !seen.Add(helper))
                        continue;
                    result.Add(new HelperInstance { Component = helper, Location = path, IsPrefab = true });
                }
            }

            foreach (var helper in Resources.FindObjectsOfTypeAll<LevelPlayHelper>())
            {
                if (helper == null || EditorUtility.IsPersistent(helper) || !helper.gameObject.scene.IsValid())
                    continue;
                if (!seen.Add(helper))
                    continue;

                result.Add(new HelperInstance
                {
                    Component = helper,
                    Location = helper.gameObject.scene.path,
                    IsPrefab = false
                });
            }

            return result;
        }

        SerializedObject MediationSettingsAsset()
        {
            if (mediationSettings != null)
                return mediationSettings;

            const string path = "Assets/LevelPlay/Resources/LevelPlayMediationSettings.asset";
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
                return null;

            return mediationSettings = new SerializedObject(asset);
        }

        SerializedObject NetworkSettingsAsset()
        {
            if (networkSettings != null)
                return networkSettings;

            var guids = AssetDatabase.FindAssets("t:NetworkManagerSettings");
            if (guids.Length == 0)
                return null;

            return networkSettings = new SerializedObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guids[0])));
        }

        static List<string> ReadResolverPackages(string path)
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(path))
                    return result;
                foreach (Match match in Regex.Matches(File.ReadAllText(path), @"<package>(.*?)</package>"))
                    result.Add(match.Groups[1].Value.Trim());
            }
            catch { }
            return result;
        }

        static string FirstEnabledScene()
        {
            var scene = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
            return scene?.path;
        }

        static string GetHelperPackageVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LevelPlayHelper).Assembly);
                return info?.version;
            }
            catch { return null; }
        }

        static string GetPackageVersion(string packageName)
        {
            try
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                return packages.FirstOrDefault(p => p.name == packageName)?.version;
            }
            catch { return null; }
        }

        static Version ParseVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
                return null;

            var match = Regex.Match(version, @"(\d+)\.(\d+)\.(\d+)");
            if (!match.Success)
                return null;

            return new Version(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value));
        }

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

        static string Describe(string value) => string.IsNullOrEmpty(value) ? "empty" : Mask(value);

        static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "empty";
            if (value.Length <= 4)
                return value;

            return value.Substring(0, 4) + new string('*', Math.Max(1, value.Length - 4));
        }

        static string DescribeUsage(bool rewarded, bool interstitial, bool banner)
        {
            var parts = new List<string>();
            if (rewarded) parts.Add("rewarded");
            if (interstitial) parts.Add("interstitial");
            if (banner) parts.Add("banner");
            return parts.Count == 0 ? "no ad format" : string.Join(", ", parts);
        }

        // ---- SerializedObject helpers (work for SDK types we cannot reference directly) ----

        static string GetString(SerializedObject so, string property)
        {
            var prop = so?.FindProperty(property);
            return prop?.stringValue ?? string.Empty;
        }

        static bool GetBool(SerializedObject so, string property, bool fallback)
        {
            var prop = so?.FindProperty(property);
            return prop != null ? prop.boolValue : fallback;
        }

        static bool GetNestedBool(SerializedProperty parent, string property, bool fallback)
        {
            var prop = parent?.FindPropertyRelative(property);
            return prop != null ? prop.boolValue : fallback;
        }

        static int GetNestedInt(SerializedProperty parent, string property, int fallback)
        {
            var prop = parent?.FindPropertyRelative(property);
            return prop != null ? prop.intValue : fallback;
        }
    }
}
