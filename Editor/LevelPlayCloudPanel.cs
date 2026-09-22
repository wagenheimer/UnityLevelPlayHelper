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
        VisualElement verifyHost;
        VisualElement logHost;
        readonly List<string> logLines = new List<string>();
        bool appsFetched;
        bool networksJustEnabled;
        float instanceRate = 0.01f;
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
            BuildVerify();
            BuildApplications();
            BuildCreateApp();
            BuildAdUnits();
            BuildNetworks();
            BuildActivity();
            Log("Cloud tab ready. Connect, then press Verify configuration.");
        }

        void BuildCredentials()
        {
            AddSection("Connect - API credentials",
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

        void BuildVerify()
        {
            AddSection("Verify configuration (API)",
                "The one-click health check: confirms, against your LevelPlay account, that every App Key and Ad Unit ID on the helper exists, is not paused, uses the right format and has an active network. " +
                "It fetches the account state itself, so it is safe to press at any time.");

            body.Add(ButtonRow(Action("Verify configuration", VerifyAsync, true)));

            verifyHost = new VisualElement();
            verifyHost.style.marginTop = 6;
            body.Add(verifyHost);
        }

        /// <summary>
        /// One-click health check: refresh the account state, then verify every configured
        /// credential against it and render the report.
        /// </summary>
        async void VerifyAsync()
        {
            if (!LevelPlayApiCredentials.HasCredentials)
            {
                SetStatus("Store the API credentials first (Secret Key + Refresh Token).", ColFail);
                Log("Verify: aborted - no API credentials.");
                return;
            }

            SetStatus("Verifying against the API...", ColAccent);
            Log("Verify: starting.");

            var (appsResult, list) = await LevelPlayApiClient.GetApplicationsAsync();
            if (!appsResult.Ok)
            {
                SetStatus("Verify failed while listing applications: " + appsResult.Error, ColFail);
                Log("Verify: listing applications failed - " + appsResult.Error);
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
            UpdateCreateVisibility();
            Log($"Verify: {apps.Count} application(s) on the account.");

            foreach (var target in new[] { ("android", androidApp), ("ios", iosApp) })
            {
                var app = target.Item2;
                if (app == null) continue;

                var (unitsResult, units) = await LevelPlayApiClient.GetAdUnitsAsync(app.appKey);
                if (!unitsResult.Ok)
                {
                    Log($"Verify: ad units of {app.appName} failed - {unitsResult.Error}");
                    continue;
                }

                unitsByApp[target.Item1] = units;
                LevelPlayCloudCache.SetUnits(app.appKey, units);
                Log($"Verify: {app.appName} has {units.Count} ad unit(s).");
            }

            RenderUnits();
            RenderNetworks();

            var helper = LevelPlayHelperLocator.FindPreferred();
            var report = LevelPlayVerifier.Verify(helper != null ? new SerializedObject(helper) : null);
            RenderVerifyReport(report);

            Log($"Verify: {report.Errors} error(s), {report.Warnings} warning(s), {report.OkCount} ok.");
            SetStatus(report.Errors > 0
                    ? $"Verification found {report.Errors} error(s) - see the report above."
                    : report.Warnings > 0
                        ? "Verification passed with warnings - see the report above."
                        : "Verification passed: everything is configured and active.",
                report.Errors > 0 ? ColFail : report.Warnings > 0 ? ColWarn : ColOk);
        }

        void RenderVerifyReport(LevelPlayVerifier.Report report)
        {
            if (verifyHost == null || report == null) return;

            verifyHost.Clear();

            var banner = new Label(report.Errors > 0
                ? $"FAIL - {report.Errors} error(s), {report.Warnings} warning(s)"
                : report.Warnings > 0
                    ? $"PASS with {report.Warnings} warning(s)"
                    : $"PASS - {report.OkCount} check(s) OK, nothing to fix");
            banner.style.fontSize = 12;
            banner.style.unityFontStyleAndWeight = FontStyle.Bold;
            banner.style.color = report.Errors > 0 ? ColFail : report.Warnings > 0 ? ColWarn : ColOk;
            banner.style.whiteSpace = WhiteSpace.Normal;
            verifyHost.Add(banner);

            foreach (var finding in report.Findings)
            {
                var color = finding.Level == LevelPlayVerifier.Level.Error ? ColFail
                    : finding.Level == LevelPlayVerifier.Level.Warning ? ColWarn
                    : ColOk;
                var glyph = finding.Level == LevelPlayVerifier.Level.Error ? "\u2715"
                    : finding.Level == LevelPlayVerifier.Level.Warning ? "!"
                    : "\u2713";
                verifyHost.Add(NetworkRow(finding.Text, color, glyph));
            }
        }

        void BuildApplications()
        {
            AddSection("1 - Applications",
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

            createSection.Add(SectionHeader("- Create application",
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

        static VisualElement NetworkRow(string text, Color color, string glyph)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 1;
            row.style.flexShrink = 0;

            if (!string.IsNullOrEmpty(glyph))
            {
                var dot = new Label(glyph);
                dot.style.width = 14;
                dot.style.color = color;
                row.Add(dot);
            }
            else
            {
                var pad = new VisualElement();
                pad.style.width = 14;
                row.Add(pad);
            }

            var label = new Label(text);
            label.style.fontSize = 10.5f;
            label.style.color = color;
            row.Add(label);

            return row;
        }

        /// <summary>Ad Unit ID configured on the helper prefab for that platform+format, or "".</summary>
        static string HelperAdUnitId(string prefix, string format)
        {
            var property = PropertyFor(prefix, format);
            if (property == null) return "";

            var helper = LevelPlayHelperLocator.FindPreferred();
            if (helper == null) return "";

            var prop = new SerializedObject(helper).FindProperty(property);
            return prop != null ? prop.stringValue : "";
        }

        void BuildAdUnits()
        {
            AddSection("2 - Ad units",
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
                "What the button does, for every format the game uses (one with an Ad Unit ID configured on the helper): it activates the existing default instance " +
                "and adds the default pair (ironSource + UnityAds) when it is missing, on the LevelPlay dashboard, at the instance rate below.\n" +
                "Apps not live in the store: the platform creates the instances as inactive; they activate once the app is published.");
            note.style.fontSize = 10;
            note.style.color = ColDim;
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 4;
            body.Add(note);

            // The API requires an instance-level rate for non-bidding instances (ERR-1216).
            var rateField = new FloatField("Instance rate (eCPM)") { value = instanceRate };
            rateField.style.flexShrink = 0;
            rateField.style.maxWidth = 340;
            rateField.RegisterValueChangedCallback(e => instanceRate = Mathf.Clamp(e.newValue, 0.01f, 3000f));
            body.Add(rateField);

            var rateHint = new Label(
                "The rate is mandatory for non-bidding instances - without it the API answers HTTP 400 (ERR-1216) and nothing is created. " +
                "0.01 is the minimum; raise it once you have real eCPM data.");
            rateHint.style.fontSize = 10;
            rateHint.style.color = ColDim;
            rateHint.style.whiteSpace = WhiteSpace.Normal;
            rateHint.style.marginBottom = 4;
            body.Add(rateHint);

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
                    // Only formats the game actually uses (an Ad Unit ID is configured locally)
                    // matter here - a banner row is noise when no banner ID exists.
                    if (string.IsNullOrEmpty(HelperAdUnitId(prefix, format)))
                    {
                        networksHost.Add(NetworkRow(format + ": not used by the game (no Ad Unit ID configured) - skipped", ColDim, ""));
                        continue;
                    }

                    var networks = LevelPlayCloudCache.ActiveNetworks(app, format);
                    var active = networks != null && networks.Any(n => !string.IsNullOrEmpty(n));
                    if (!active) anyMissing = true;

                    networksHost.Add(NetworkRow(
                        active
                            ? $"{format}: {string.Join(", ", networks.Where(n => !string.IsNullOrEmpty(n)))}"
                            : $"{format}: no active networks - this format will NOT fill",
                        active ? ColText : ColFail,
                        active ? "\u25CF" : "\u25CB"));
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

        /// <summary>Appends a line to the activity log: every operation reports what it is doing.</summary>
        void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            logLines.Add(line);
            if (logLines.Count > 300) logLines.RemoveAt(0);

            if (logHost == null) return;

            var label = new Label(line);
            label.style.fontSize = 10;
            label.style.color = ColDim;
            label.style.whiteSpace = WhiteSpace.Normal;
            logHost.Add(label);

            while (logHost.childCount > 120) logHost.RemoveAt(0);
        }

        void BuildActivity()
        {
            AddSection("Activity log",
                "Everything the Cloud tab does, in order. Copy it into a bug report if something fails.");

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.maxHeight = 170;
            scroll.style.backgroundColor = new Color(0f, 0f, 0f, 0.20f);
            scroll.style.paddingTop = 4;
            scroll.style.paddingBottom = 4;
            scroll.style.paddingLeft = 6;
            scroll.style.paddingRight = 6;

            logHost = scroll.contentContainer;
            foreach (var line in logLines)
            {
                var label = new Label(line);
                label.style.fontSize = 10;
                label.style.color = ColDim;
                label.style.whiteSpace = WhiteSpace.Normal;
                logHost.Add(label);
            }

            body.Add(scroll);
            body.Add(ButtonRow(Action("Copy log", CopyLog)));
        }

        void CopyLog()
        {
            EditorGUIUtility.systemCopyBuffer = string.Join("\n", logLines);
            Log("Log copied to the clipboard.");
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
            Log(result.Ok ? "Auth OK (bearer token acquired)." : "Auth failed: " + result.Error);
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
            Log($"Fetched {apps.Count} application(s): " + string.Join(", ", apps.Select(a => $"{a.appName} [{a.platform}/{a.appKey}]")));
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
            Log(writes > 0 ? $"Applied {writes} App Key(s) to the helper prefab." : "Apply App Keys: no application selected.");
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
                Log($"Ad units for {app.appName}: {units.Count} (" + string.Join(", ", units.Select(u => u.adFormat + "/" + u.mediationAdUnitId)) + ")");
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
            Log(writes > 0 ? $"Applied {writes} Ad Unit ID(s) to the helper prefab." : "Apply Ad Unit IDs: nothing to apply.");
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
                Log("Create ad units: skipped - fetch ad units first.");
                return;
            }

            var plan = new List<(string prefix, LevelPlayApiClient.AppDto app, List<string> formats)>();
            foreach (var (prefix, app) in targets)
            {
                var missing = MissingFormats(unitsByApp[prefix]);
                if (missing.Count > 0) plan.Add((prefix, app, missing));
            }

            var total = plan.Sum(p => p.formats.Count);
            if (total == 0)
            {
                SetStatus("Nothing to create: every format already exists.", ColOk);
                Log("Create ad units: nothing to do, every format already exists.");
                return;
            }

            var details = string.Join("\n", plan.Select(p =>
                $"- {(p.prefix == "android" ? "Android" : "iOS")} / {p.app.appName}: {string.Join(", ", p.formats)}"));

            if (!EditorUtility.DisplayDialog("Create ad units",
                $"Create {total} ad unit(s) on the LevelPlay dashboard?\n\n{details}\n\nThis writes to your account.",
                "Create", "Cancel"))
                return;

            Log($"Create ad units: starting ({total} unit(s)).");
            foreach (var (prefix, app, formats) in plan)
                Log($"  - {(prefix == "android" ? "Android" : "iOS")} / {app.appName} [{app.appKey}]: {string.Join(", ", formats)}");

            foreach (var (prefix, app, formats) in plan)
            {
                var requests = formats.Select(f => new LevelPlayApiClient.AdUnitRequest
                {
                    mediationAdUnitName = "LevelPlayHelper-" + f,
                    adFormat = f,
                    reward = f == "rewarded" ? new LevelPlayApiClient.Reward { rewardItemName = "Virtual Item", rewardAmount = 1 } : null
                }).ToList();

                Log($"POST ad units -> {app.appName}: {string.Join(", ", formats)}");
                var result = await LevelPlayApiClient.CreateAdUnitsAsync(app.appKey, requests);

                if (!result.Ok)
                {
                    Log($"  FAILED for {app.appName}: {result.Error}");
                    SetStatus($"Create failed for {app.appName}: {result.Error}", ColFail);
                    return;
                }

                Log($"  OK for {app.appName}: {result.Json}");
            }

            SetStatus("Ad units created. Re-fetching to show the new IDs...", ColAccent);
            Log("Create ad units: done - re-fetching ad units.");
            FetchAdUnitsAsync();
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
                Log("Enable networks: skipped - fetch ad units first.");
                return;
            }

            var plan = new List<string>();
            foreach (var (prefix, app) in targets)
            {
                var formats = Formats.Where(f => !string.IsNullOrEmpty(HelperAdUnitId(prefix, f))).ToList();
                if (formats.Count > 0)
                    plan.Add($"- {(prefix == "android" ? "Android" : "iOS")} / {app.appName}: {string.Join(", ", formats)}");
            }

            if (plan.Count == 0)
            {
                SetStatus("No format has an Ad Unit ID configured on the helper - nothing to enable.", ColWarn);
                Log("Enable networks: skipped - no Ad Unit ID configured on the helper.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Enable default networks",
                "Activate/add the default networks (ironSource + UnityAds) for the formats below, on the LevelPlay dashboard?\n\n" +
                string.Join("\n", plan) + "\n\n" +
                $"Instance rate: {instanceRate:0.####}\n" +
                "The rate is mandatory for non-bidding instances - without it the API answers HTTP 400 (ERR-1216).\n\n" +
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

                    // Only the formats the game uses (an Ad Unit ID is configured locally).
                    if (string.IsNullOrEmpty(HelperAdUnitId(prefix, format))) continue;

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
                                instanceName = "LevelPlayHelper-" + network,
                                networkName = network,
                                adFormat = unit.adFormat,
                                isBidder = false,
                                isLive = true,
                                rate = instanceRate
                            });
                        }
                    }
                }

                if (toActivate.Count == 0 && toCreate.Count == 0)
                {
                    Log($"  {app.appName}: nothing to do (formats already have the networks).");
                    continue;
                }

                Log($"  {app.appName}: {toActivate.Count} instance(s) to activate, {toCreate.Count} to create (rate {instanceRate:0.####}).");

                if (toActivate.Count > 0)
                {
                    Log($"PUT instances -> {app.appName}: activating {toActivate.Count}");
                    var result = await LevelPlayApiClient.UpdateInstancesAsync(app.appKey, toActivate);
                    if (!result.Ok)
                    {
                        Log($"  FAILED activating for {app.appName}: {result.Error}");
                        SetStatus($"Activating instances failed for {app.appName}: {result.Error}", ColFail);
                        return;
                    }
                    activated += toActivate.Count;
                }

                if (toCreate.Count > 0)
                {
                    Log($"POST instances -> {app.appName}: {string.Join(", ", toCreate.Select(r => r.networkName + "/" + r.adFormat).Distinct())}");
                    var result = await LevelPlayApiClient.CreateInstancesAsync(app.appKey, toCreate);
                    if (!result.Ok)
                    {
                        Log($"  FAILED creating for {app.appName}: {result.Error}");
                        SetStatus($"Creating instances failed for {app.appName}: {result.Error}", ColFail);
                        return;
                    }
                    Log($"  OK for {app.appName}: {result.Json}");
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
