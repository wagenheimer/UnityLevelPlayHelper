using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using UnityEditor;
using UnityEditor.Build;

using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Cloud tab: talks to the ironSource / LevelPlay publisher API to fetch the App Keys and
    /// Ad Unit IDs, push them into the helper prefab, create apps / ad units and turn on the
    /// default networks. Read + write, always with a preview and an explicit confirmation.
    /// </summary>
    internal sealed class LevelPlayCloudPanel
    {
        static readonly Color ColOk = new Color(0.298f, 0.686f, 0.314f);
        static readonly Color ColWarn = new Color(1.000f, 0.690f, 0.125f);
        static readonly Color ColFail = new Color(0.898f, 0.282f, 0.302f);
        static readonly Color ColDim = new Color(0.650f, 0.650f, 0.650f);
        static readonly Color ColAccent = new Color(0.290f, 0.565f, 0.851f);
        static readonly Color ColText = new Color(0.850f, 0.850f, 0.850f);

        static readonly string[] DefaultNetworks = { "ironSource", "unityAds" };
        static readonly string[] Formats = { "rewarded", "interstitial", "banner" };

        public VisualElement Root { get; }
        VisualElement body;

        readonly List<LevelPlayApiClient.AppDto> apps = new List<LevelPlayApiClient.AppDto>();
        LevelPlayApiClient.AppDto androidApp;
        LevelPlayApiClient.AppDto iosApp;
        readonly Dictionary<string, List<LevelPlayApiClient.AdUnitDto>> unitsByApp = new Dictionary<string, List<LevelPlayApiClient.AdUnitDto>>();

        Label statusLabel;
        Label storedLabel;
        VisualElement appsHost;
        VisualElement unitsHost;
        VisualElement createHost;
        VisualElement createSection;
        VisualElement networksHost;
        bool appsFetched;
        bool networksJustEnabled;
        TextField secretField;
        TextField refreshField;

        bool createLiveApp;
        string newAppName = "";
        string newAppPlatform = "Android";
        string newStoreUrl = "";
        string newTaxonomy = "puzzle";
        bool newCoppa;

        public LevelPlayCloudPanel()
        {
            Root = new VisualElement();
            Root.style.flexGrow = 1;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);
            body = scroll.contentContainer;
            body.style.paddingLeft = 2;
            body.style.paddingRight = 14;

            Build();
        }

        // ------------------------------------------------------------ layout

        void Build()
        {
            body.Clear();

            BuildCredentials();
            BuildApplications();
            BuildCreateApp();
            BuildAdUnits();
            BuildNetworks();
        }

        void BuildCredentials()
        {
            AddSection("API credentials",
                "Account-level secrets from LevelPlay > My Account > API. Stored in EditorPrefs on this machine only - never written to the project or logged.");

            secretField = CredentialField("Secret Key");
            refreshField = CredentialField("Refresh Token");
            body.Add(secretField);
            body.Add(refreshField);

            storedLabel = new Label();
            storedLabel.style.fontSize = 10;
            storedLabel.style.color = ColDim;
            storedLabel.style.marginTop = 2;
            body.Add(storedLabel);

            var actions = ButtonRow(
                Action("Connect", ConnectAsync, true),
                Action("Clear", ClearCredentials));
            body.Add(actions);

            statusLabel = new Label();
            statusLabel.style.fontSize = 10.5f;
            statusLabel.style.color = ColDim;
            statusLabel.style.marginTop = 6;
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            body.Add(statusLabel);

            UpdateStoredLabel();
        }

        void BuildApplications()
        {
            AddSection("Applications",
                "Fetch your apps, then apply the per-platform App Key to the helper prefab.");

            body.Add(ButtonRow(Action("Fetch applications", FetchApplicationsAsync, true)));

            appsHost = new VisualElement();
            appsHost.style.marginTop = 6;
            body.Add(appsHost);

            RenderApps();
        }

        void BuildCreateApp()
        {
            // Only meaningful when the account has no app yet, so it is hidden as soon as
            // "Fetch applications" returns something.
            createSection = new VisualElement();
            createSection.style.display = DisplayStyle.None;

            createSection.Add(SectionHeader("Create application",
                "No app found on the account: create it here - either not published yet (name + platform) or already on the store (store URL + taxonomy)."));

            var mode = new Toggle("Already published on the store") { value = createLiveApp };
            mode.RegisterValueChangedCallback(e =>
            {
                createLiveApp = e.newValue;
                RenderCreateFields();
            });
            createSection.Add(mode);

            createHost = new VisualElement();
            createSection.Add(createHost);

            RenderCreateFields();
            body.Add(createSection);

            UpdateCreateVisibility();
        }

        void UpdateCreateVisibility()
        {
            if (createSection == null) return;
            createSection.style.display = appsFetched && apps.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void BuildAdUnits()
        {
            AddSection("Ad units",
                "Fetch the ad units of each app and apply the IDs to the helper prefab. Create the missing formats in one click.");

            body.Add(ButtonRow(
                Action("Fetch ad units", FetchAdUnitsAsync, true),
                Action("Apply Ad Unit IDs", ApplyAdUnitIds),
                Action("Create missing ad units", CreateMissingAdUnitsAsync)));

            unitsHost = new VisualElement();
            unitsHost.style.marginTop = 6;
            body.Add(unitsHost);

            RenderUnits();
        }

        void BuildNetworks()
        {
            AddSection("Networks (mediation instances)",
                "Do you need this? Every ad unit must have at least one ACTIVE network, otherwise it has no demand and never fills. " +
                "Check the list below: if a format already shows active networks, you can skip this. " +
                "Only formats showing \"no active networks\" need the button.");

            var note = new Label(
                "What the button does: it creates/activates the default pair (ironSource + UnityAds) for every ad unit of the selected apps, " +
                "on the LevelPlay dashboard.\n" +
                "Apps not live in the store: the platform creates the instances as inactive; they activate once the app is published.");
            note.style.fontSize = 10;
            note.style.color = ColDim;
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 4;
            body.Add(note);

            body.Add(ButtonRow(Action("Enable default networks", EnableDefaultNetworks, true)));

            networksHost = new VisualElement();
            networksHost.style.marginTop = 6;
            body.Add(networksHost);

            RenderNetworks();
        }

        void RenderNetworks()
        {
            if (networksHost == null) return;
            networksHost.Clear();

            foreach (var prefix in new[] { "android", "ios" })
            {
                var app = prefix == "android" ? androidApp : iosApp;
                if (app == null) continue;

                var platform = prefix == "android" ? "Android" : "iOS";

                var title = new Label($"{platform} - {app.appName}");
                title.style.fontSize = 11;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = ColText;
                title.style.marginTop = 6;
                networksHost.Add(title);

                var anyMissing = false;

                foreach (var format in Formats)
                {
                    var networks = LevelPlayCloudCache.ActiveNetworks(app, format);
                    var active = networks != null && networks.Any(n => !string.IsNullOrEmpty(n));
                    if (!active) anyMissing = true;

                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.marginTop = 1;
                    row.style.flexShrink = 0;

                    var dot = new Label(active ? "\u25CF" : "\u25CB");
                    dot.style.width = 14;
                    dot.style.color = active ? ColOk : ColFail;
                    row.Add(dot);

                    var text = active
                        ? $"{format}: {string.Join(", ", networks.Where(n => !string.IsNullOrEmpty(n)))}"
                        : $"{format}: no active networks - this format will NOT fill";
                    var label = new Label(text);
                    label.style.fontSize = 10.5f;
                    label.style.color = active ? ColText : ColFail;
                    row.Add(label);

                    networksHost.Add(row);
                }

                if (anyMissing)
                {
                    var fix = new Label(networksJustEnabled
                        ? "Still no active network above. A non-default instance only activates after the default one is active, and some networks (e.g. Unity Ads) need their app/instance config in the dashboard - the sourceID / zoneID. Check Dashboard > Ad Units > the ad unit > Instances."
                        : "Click \"Enable default networks\" above to add ironSource + UnityAds to the formats marked above.");
                    fix.style.fontSize = 10;
                    fix.style.color = ColWarn;
                    fix.style.whiteSpace = WhiteSpace.Normal;
                    fix.style.marginTop = 2;
                    networksHost.Add(fix);
                }
            }
        }

        void AddSection(string title, string subtitle) => body.Add(SectionHeader(title, subtitle));

        VisualElement SectionHeader(string title, string subtitle)
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

            return box;
        }

        // ------------------------------------------------------------ widget helpers

        static TextField CredentialField(string label)
        {
            var field = new TextField(label);
            field.tooltip = "Paste the value. Leave blank to keep the value already stored on this machine.";
            field.style.marginBottom = 2;
            field.style.flexShrink = 0;
            return field;
        }

        static VisualElement ButtonRow(params VisualElement[] buttons)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 4;
            row.style.flexShrink = 0;

            foreach (var button in buttons) row.Add(button);

            return row;
        }

        static Button Action(string text, Action clicked, bool primary = false)
        {
            var button = new Button(clicked) { text = text };
            button.style.height = 24;
            button.style.flexGrow = 0;                 // default Button style grows to fill
            button.style.alignSelf = Align.FlexStart;
            button.style.marginRight = 6;
            button.style.marginBottom = 4;
            button.style.paddingLeft = 12;
            button.style.paddingRight = 12;
            if (primary)
            {
                button.style.backgroundColor = new Color(ColAccent.r, ColAccent.g, ColAccent.b, 0.35f);
                button.style.color = ColText;
            }
            return button;
        }

        static Label Hint(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 10;
            label.style.color = ColDim;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 2;
            label.style.marginBottom = 4;
            return label;
        }

        void SetStatus(string text, Color color)
        {
            if (statusLabel == null) return;
            statusLabel.text = text;
            statusLabel.style.color = color;
        }

        void UpdateStoredLabel()
        {
            if (storedLabel == null) return;

            storedLabel.text = LevelPlayApiCredentials.HasCredentials
                ? "Credentials stored on this machine (EditorPrefs). Leave the fields blank to keep them."
                : "No credentials stored yet.";
            storedLabel.style.color = LevelPlayApiCredentials.HasCredentials ? ColOk : ColDim;
        }

        // ------------------------------------------------------------ credentials / auth

        void ClearCredentials()
        {
            LevelPlayApiCredentials.Clear();
            LevelPlayApiClient.InvalidateToken();
            secretField.value = "";
            refreshField.value = "";
            UpdateStoredLabel();
            SetStatus("Credentials cleared.", ColDim);
        }

        async void ConnectAsync()
        {
            var secret = secretField.value.Trim();
            var refresh = refreshField.value.Trim();

            if (!string.IsNullOrEmpty(secret) || !string.IsNullOrEmpty(refresh))
            {
                LevelPlayApiCredentials.Save(
                    string.IsNullOrEmpty(secret) ? LevelPlayApiCredentials.SecretKey : secret,
                    string.IsNullOrEmpty(refresh) ? LevelPlayApiCredentials.RefreshToken : refresh);

                secretField.value = "";
                refreshField.value = "";
                UpdateStoredLabel();
            }

            if (!LevelPlayApiCredentials.HasCredentials)
            {
                SetStatus("Paste the Secret Key and Refresh Token, then press Connect.", ColFail);
                return;
            }

            SetStatus("Connecting...", ColAccent);
            var result = await LevelPlayApiClient.AuthenticateAsync(LevelPlayApiCredentials.SecretKey, LevelPlayApiCredentials.RefreshToken);
            SetStatus(result.Ok ? "Connected. Bearer token acquired (valid 24h)." : "Auth failed: " + result.Error,
                result.Ok ? ColOk : ColFail);
        }

        // ------------------------------------------------------------ applications

        async void FetchApplicationsAsync()
        {
            SetStatus("Fetching applications...", ColAccent);

            var (result, list) = await LevelPlayApiClient.GetApplicationsAsync();
            if (!result.Ok)
            {
                SetStatus("Could not list applications: " + result.Error, ColFail);
                return;
            }

            apps.Clear();
            apps.AddRange(list);
            LevelPlayCloudCache.SetApps(list);
            appsFetched = true;
            networksJustEnabled = false;
            androidApp = MatchApp("Android");
            iosApp = MatchApp("iOS");

            RenderApps();
            RenderNetworks();
            UpdateCreateVisibility();
            SetStatus($"Fetched {apps.Count} application(s).", ColOk);
        }

        LevelPlayApiClient.AppDto MatchApp(string platform)
        {
            var expected = BundleIdFor(platform);
            var candidates = apps.Where(a => string.Equals(a.platform, platform, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrEmpty(expected))
            {
                var exact = candidates.FirstOrDefault(a => string.Equals(a.bundleId, expected, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            return candidates.FirstOrDefault();
        }

        static string BundleIdFor(string platform)
        {
            try
            {
                return platform == "iOS"
                    ? PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS)
                    : PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            }
            catch
            {
                return "";
            }
        }

        void RenderApps()
        {
            if (appsHost == null) return;
            appsHost.Clear();

            if (apps.Count == 0)
            {
                appsHost.Add(Hint("No applications loaded yet."));
                return;
            }

            appsHost.Add(AppPicker("Android", "android", () => androidApp, a => androidApp = a));
            appsHost.Add(AppPicker("iOS", "ios", () => iosApp, a => iosApp = a));
            appsHost.Add(ButtonRow(Action("Apply App Keys to helper prefab", ApplyAppKeys)));
        }

        VisualElement AppPicker(string platform, string prefix, Func<LevelPlayApiClient.AppDto> get, Action<LevelPlayApiClient.AppDto> set)
        {
            var candidates = apps.Where(a => string.Equals(a.platform, platform, StringComparison.OrdinalIgnoreCase)).ToList();
            var current = get();

            var box = new VisualElement();
            box.style.marginTop = 8;
            box.style.flexShrink = 0;

            var bundle = BundleIdFor(platform);
            var label = new Label($"{platform}  (project bundle id: {(string.IsNullOrEmpty(bundle) ? "?" : bundle)})");
            label.style.fontSize = 10.5f;
            label.style.color = ColDim;
            box.Add(label);

            if (candidates.Count == 0)
            {
                box.Add(Hint($"No {platform} application found on the account."));
                return box;
            }

            var names = candidates.Select(a => $"{a.appName}  [{a.appKey}]  {a.bundleId}").ToList();
            var dropdown = new DropdownField(names, Mathf.Max(0, candidates.IndexOf(current)));
            dropdown.style.marginTop = 2;
            dropdown.style.flexShrink = 0;
            dropdown.RegisterValueChangedCallback(_ => set(candidates[dropdown.index]));
            box.Add(dropdown);

            if (current != null && !string.IsNullOrEmpty(bundle) && !string.Equals(current.bundleId, bundle, StringComparison.OrdinalIgnoreCase))
            {
                var warn = new Label($"Bundle id does not match the project ({current.bundleId} != {bundle}) - make sure this is the right app.");
                warn.style.fontSize = 10;
                warn.style.color = ColWarn;
                warn.style.whiteSpace = WhiteSpace.Normal;
                box.Add(warn);
            }

            return box;
        }

        void ApplyAppKeys()
        {
            var writes = 0;

            if (androidApp != null) { SetHelperString("androidAppKey", androidApp.appKey); writes++; }
            if (iosApp != null) { SetHelperString("iosAppKey", iosApp.appKey); writes++; }

            SetStatus(writes > 0
                ? $"Applied {writes} App Key(s) to the helper prefab."
                : "No application selected.", writes > 0 ? ColOk : ColWarn);
        }

        // ------------------------------------------------------------ create application

        void RenderCreateFields()
        {
            if (createHost == null) return;
            createHost.Clear();

            if (createLiveApp)
            {
                AddText(createHost, "Store URL", newStoreUrl, v => newStoreUrl = v);
                AddText(createHost, "Taxonomy (sub-genre)", newTaxonomy, v => newTaxonomy = v);
                createHost.Add(Hint("The app name and platform come from the store listing."));
            }
            else
            {
                AddText(createHost, "App name", newAppName, v => newAppName = v);

                var platforms = new List<string> { "Android", "iOS" };
                var dropdown = new DropdownField("Platform", platforms, Mathf.Max(0, platforms.IndexOf(newAppPlatform)));
                dropdown.style.flexShrink = 0;
                dropdown.RegisterValueChangedCallback(e => newAppPlatform = e.newValue);
                createHost.Add(dropdown);
                createHost.Add(Hint("App not live: the platform creates its instances as inactive until it is published."));
            }

            var coppa = new Toggle("COPPA (child-directed)") { value = newCoppa };
            coppa.RegisterValueChangedCallback(e => newCoppa = e.newValue);
            createHost.Add(coppa);

            createHost.Add(ButtonRow(Action("Create application", CreateApplicationAsync, true)));
        }

        static void AddText(VisualElement parent, string label, string value, Action<string> set)
        {
            var field = new TextField(label) { value = value };
            field.style.marginBottom = 2;
            field.style.flexShrink = 0;
            field.RegisterValueChangedCallback(e => set(e.newValue));
            parent.Add(field);
        }

        async void CreateApplicationAsync()
        {
            var request = new LevelPlayApiClient.AppRequest { coppa = newCoppa ? 1 : 0 };

            if (createLiveApp)
            {
                if (string.IsNullOrWhiteSpace(newStoreUrl))
                {
                    SetStatus("Fill in the store URL.", ColFail);
                    return;
                }

                request.storeUrl = newStoreUrl.Trim();
                request.taxonomy = string.IsNullOrWhiteSpace(newTaxonomy) ? "puzzle" : newTaxonomy.Trim();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(newAppName))
                {
                    SetStatus("Fill in the app name.", ColFail);
                    return;
                }

                request.appName = newAppName.Trim();
                request.platform = newAppPlatform;
            }

            var what = createLiveApp ? request.storeUrl : $"{request.appName} ({request.platform})";
            if (!EditorUtility.DisplayDialog("Create application",
                $"Create this app on the LevelPlay dashboard?\n\n{what}", "Create", "Cancel"))
                return;

            SetStatus("Creating application...", ColAccent);
            var result = await LevelPlayApiClient.CreateApplicationAsync(request);

            if (!result.Ok)
            {
                SetStatus("Create failed: " + result.Error, ColFail);
                return;
            }

            SetStatus("Application created. Fetch applications to pick it up.", ColOk);
        }

        // ------------------------------------------------------------ ad units

        async void FetchAdUnitsAsync()
        {
            var targets = new[] { ("android", androidApp), ("ios", iosApp) }.Where(t => t.Item2 != null).ToList();
            if (targets.Count == 0)
            {
                SetStatus("Select an application for at least one platform first.", ColWarn);
                return;
            }

            SetStatus("Fetching ad units...", ColAccent);

            foreach (var (prefix, app) in targets)
            {
                var (result, units) = await LevelPlayApiClient.GetAdUnitsAsync(app.appKey);
                if (!result.Ok)
                {
                    SetStatus($"Ad units for {app.appName} failed: {result.Error}", ColFail);
                    return;
                }

                unitsByApp[prefix] = units;
                LevelPlayCloudCache.SetUnits(app.appKey, units);
            }

            RenderUnits();
            SetStatus("Ad units loaded.", ColOk);
        }

        void RenderUnits()
        {
            if (unitsHost == null) return;
            unitsHost.Clear();

            foreach (var prefix in new[] { "android", "ios" })
            {
                var app = prefix == "android" ? androidApp : iosApp;
                if (app == null) continue;

                var platform = prefix == "android" ? "Android" : "iOS";

                if (!unitsByApp.TryGetValue(prefix, out var units))
                {
                    unitsHost.Add(Hint(platform + ": ad units not fetched yet."));
                    continue;
                }

                var title = new Label($"{platform} - {app.appName}  ({units.Count} ad units)");
                title.style.fontSize = 11;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = ColText;
                title.style.marginTop = 8;
                unitsHost.Add(title);

                foreach (var unit in units)
                {
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.marginTop = 1;
                    row.style.flexShrink = 0;

                    var dot = new Label(unit.isPaused ? "\u25CB" : "\u25CF");
                    dot.style.width = 14;
                    dot.style.color = unit.isPaused ? ColWarn : ColOk;
                    row.Add(dot);

                    var l = new Label($"{unit.adFormat,-13} {unit.mediationAdUnitId}   {unit.mediationAdUnitName}{(unit.isPaused ? "   (PAUSED)" : "")}");
                    l.style.fontSize = 10.5f;
                    l.style.color = unit.isPaused ? ColWarn : ColText;
                    row.Add(l);

                    unitsHost.Add(row);
                }

                var missing = MissingFormats(units);
                if (missing.Count > 0)
                {
                    var warn = new Label("Missing formats: " + string.Join(", ", missing));
                    warn.style.fontSize = 10;
                    warn.style.color = ColWarn;
                    warn.style.marginTop = 2;
                    unitsHost.Add(warn);
                }
            }
        }

        static List<string> MissingFormats(List<LevelPlayApiClient.AdUnitDto> units)
        {
            var result = new List<string>();
            foreach (var format in Formats)
                if (!units.Any(u => string.Equals(u.adFormat, format, StringComparison.OrdinalIgnoreCase)))
                    result.Add(format);
            return result;
        }

        void ApplyAdUnitIds()
        {
            var writes = 0;

            foreach (var prefix in new[] { "android", "ios" })
            {
                if (!unitsByApp.TryGetValue(prefix, out var units)) continue;

                foreach (var unit in units)
                {
                    var property = PropertyFor(prefix, unit.adFormat);
                    if (property == null) continue;

                    SetHelperString(property, unit.mediationAdUnitId);
                    writes++;
                }
            }

            SetStatus(writes > 0
                ? $"Applied {writes} Ad Unit ID(s) to the helper prefab."
                : "No ad units to apply (fetch them first).", writes > 0 ? ColOk : ColWarn);
        }

        static string PropertyFor(string prefix, string format)
        {
            switch ((format ?? "").ToLowerInvariant())
            {
                case "rewarded": return prefix + "RewardedAdUnitId";
                case "interstitial": return prefix + "InterstitialAdUnitId";
                case "banner": return prefix + "BannerAdUnitId";
                default: return null;
            }
        }

        async void CreateMissingAdUnitsAsync()
        {
            var targets = new[] { ("android", androidApp), ("ios", iosApp) }
                .Where(t => t.Item2 != null && unitsByApp.ContainsKey(t.Item1))
                .ToList();

            if (targets.Count == 0)
            {
                SetStatus("Fetch ad units first so we know what is missing.", ColWarn);
                return;
            }

            var total = targets.Sum(t => MissingFormats(unitsByApp[t.Item1]).Count);
            if (total == 0)
            {
                SetStatus("Nothing to create: every format already exists.", ColOk);
                return;
            }

            if (!EditorUtility.DisplayDialog("Create ad units",
                $"Create {total} missing ad unit(s) on the LevelPlay dashboard? This writes to your account.",
                "Create", "Cancel"))
                return;

            foreach (var (prefix, app) in targets)
            {
                var missing = MissingFormats(unitsByApp[prefix]);
                if (missing.Count == 0) continue;

                var requests = missing.Select(f => new LevelPlayApiClient.AdUnitRequest
                {
                    mediationAdUnitName = f + "-1",
                    adFormat = f,
                    reward = f == "rewarded" ? new LevelPlayApiClient.Reward { rewardItemName = "Virtual Item", rewardAmount = 1 } : null
                });

                var result = await LevelPlayApiClient.CreateAdUnitsAsync(app.appKey, requests);
                if (!result.Ok)
                {
                    SetStatus($"Create failed for {app.appName}: {result.Error}", ColFail);
                    return;
                }
            }

            SetStatus("Ad units created. Fetch ad units again to pull the new IDs.", ColOk);
        }

        // ------------------------------------------------------------ networks

        void EnableDefaultNetworks()
        {
            var targets = new[] { ("android", androidApp), ("ios", iosApp) }
                .Where(t => t.Item2 != null && unitsByApp.ContainsKey(t.Item1))
                .ToList();

            if (targets.Count == 0)
            {
                SetStatus("Fetch ad units first.", ColWarn);
                return;
            }

            if (!EditorUtility.DisplayDialog("Enable default networks",
                "Add the default networks (ironSource + UnityAds) to every ad unit of the selected apps, on the LevelPlay dashboard?\n\n" +
                "Use this for the formats that currently show \"no active networks\" - an ad unit with no active network has no demand and never fills.\n\n" +
                "This writes to your LevelPlay account. Apps not live in the store get the instances created as inactive.\n\n" +
                "Continue?",
                "Enable", "Cancel"))
                return;

            _ = EnableDefaultNetworksAsync(targets);
        }

        async Task EnableDefaultNetworksAsync(List<(string prefix, LevelPlayApiClient.AppDto app)> targets)
        {
            var activated = 0;
            var created = 0;

            foreach (var (prefix, app) in targets)
            {
                var (read, instances) = await LevelPlayApiClient.GetInstancesAsync(app.appKey);
                if (!read.Ok)
                {
                    SetStatus($"Could not read instances for {app.appName}: {read.Error}", ColFail);
                    return;
                }

                var toActivate = new List<LevelPlayApiClient.InstanceUpdate>();
                var toCreate = new List<LevelPlayApiClient.InstanceRequest>();

                foreach (var unit in unitsByApp[prefix])
                {
                    var format = (unit.adFormat ?? "").ToLowerInvariant();
                    if (format != "rewarded" && format != "interstitial" && format != "banner") continue;

                    foreach (var network in DefaultNetworks)
                    {
                        var existing = instances.FirstOrDefault(i =>
                            string.Equals(i.adFormat, unit.adFormat, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(i.networkName, network, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            // The platform already creates a per-unit default instance: activate it
                            // instead of adding a duplicate (a duplicate is what made only ironSource
                            // show up before).
                            if (!existing.isLive)
                                toActivate.Add(new LevelPlayApiClient.InstanceUpdate { instanceId = existing.instanceId, isLive = true });
                        }
                        else
                        {
                            toCreate.Add(new LevelPlayApiClient.InstanceRequest
                            {
                                instanceName = "LevelPlayHelper",
                                networkName = network,
                                adFormat = unit.adFormat,
                                isBidder = false,
                                isLive = true
                            });
                        }
                    }
                }

                if (toActivate.Count > 0)
                {
                    var result = await LevelPlayApiClient.UpdateInstancesAsync(app.appKey, toActivate);
                    if (!result.Ok)
                    {
                        SetStatus($"Activating instances failed for {app.appName}: {result.Error}", ColFail);
                        return;
                    }
                    activated += toActivate.Count;
                }

                if (toCreate.Count > 0)
                {
                    var result = await LevelPlayApiClient.CreateInstancesAsync(app.appKey, toCreate);
                    if (!result.Ok)
                    {
                        SetStatus($"Creating instances failed for {app.appName}: {result.Error}", ColFail);
                        return;
                    }
                    created += toCreate.Count;
                }
            }

            // The dashboard state settles asynchronously, so re-read it instead of assuming.
            networksJustEnabled = true;
            SetStatus($"Networks ensured ({activated} activated, {created} created). Refreshing account state...", ColAccent);
            FetchApplicationsAsync();
        }

        // ------------------------------------------------------------ helper writes

        static void SetHelperString(string property, string value)
        {
            var helper = LevelPlayHelperLocator.FindPreferred();
            if (helper == null) return;

            Undo.RegisterCompleteObjectUndo(helper, "Edit LevelPlay credentials");

            var so = new SerializedObject(helper);
            var prop = so.FindProperty(property);
            if (prop == null) return;

            prop.stringValue = value;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(helper);
            AssetDatabase.SaveAssets();
        }
    }
}
