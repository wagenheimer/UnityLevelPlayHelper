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

        public VisualElement Root { get; }

        readonly List<LevelPlayApiClient.AppDto> apps = new List<LevelPlayApiClient.AppDto>();
        LevelPlayApiClient.AppDto androidApp;
        LevelPlayApiClient.AppDto iosApp;
        readonly Dictionary<string, List<LevelPlayApiClient.AdUnitDto>> unitsByApp = new Dictionary<string, List<LevelPlayApiClient.AdUnitDto>>();

        Label statusLabel;
        Label storedLabel;
        VisualElement appsHost;
        VisualElement unitsHost;
        TextField secretField;
        TextField refreshField;

        public LevelPlayCloudPanel()
        {
            Root = new VisualElement();
            Root.style.paddingTop = 10;
            Root.style.paddingBottom = 10;
            Root.style.paddingLeft = 12;
            Root.style.paddingRight = 12;
            Build();
        }

        // ------------------------------------------------------------ layout

        void Build()
        {
            Root.Clear();

            Root.Add(Section("API credentials",
                "Account-level secrets from LevelPlay > My Account > API. Stored in EditorPrefs on this machine only - never written to the project or logged."));

            secretField = CredentialField("Secret Key");
            refreshField = CredentialField("Refresh Token");
            Root.Add(secretField);
            Root.Add(refreshField);

            storedLabel = new Label();
            storedLabel.style.fontSize = 10;
            storedLabel.style.color = ColDim;
            storedLabel.style.marginTop = 2;
            Root.Add(storedLabel);

            var actions = Row();
            actions.Add(Primary("Connect", ConnectAsync));
            actions.Add(Secondary("Clear", () =>
            {
                LevelPlayApiCredentials.Clear();
                LevelPlayApiClient.InvalidateToken();
                secretField.value = "";
                refreshField.value = "";
                UpdateStoredLabel();
                SetStatus("Credentials cleared.", ColDim);
            }));
            Root.Add(actions);

            statusLabel = new Label();
            statusLabel.style.fontSize = 10.5f;
            statusLabel.style.color = ColDim;
            statusLabel.style.marginTop = 4;
            Root.Add(statusLabel);

            UpdateStoredLabel();

            Root.Add(Section("Applications",
                "Fetch your apps, then apply the per-platform App Key to the helper prefab."));
            Root.Add(Primary("Fetch applications", FetchApplicationsAsync));
            appsHost = new VisualElement();
            Root.Add(appsHost);

            Root.Add(Section("Ad units",
                "Fetch the ad units of each app and apply the IDs to the helper prefab. Create the missing formats in one click."));
            var unitActions = Row();
            unitActions.Add(Secondary("Fetch ad units", FetchAdUnitsAsync));
            unitActions.Add(Secondary("Apply Ad Unit IDs", ApplyAdUnitIds));
            unitActions.Add(Secondary("Create missing ad units", CreateMissingAdUnitsAsync));
            Root.Add(unitActions);
            unitsHost = new VisualElement();
            Root.Add(unitsHost);

            Root.Add(Section("Networks",
                "Mediation only fills when the ad unit has at least one active network. Enable the default pair (ironSource + UnityAds) for every used format."));
            Root.Add(Primary("Enable default networks", EnableDefaultNetworks));

            RenderApps();
            RenderUnits();
        }

        // The value is never echoed back: the field starts empty and only an explicit paste is
        // saved. An empty field keeps whatever is already stored for that key.
        static TextField CredentialField(string label)
        {
            var field = new TextField(label);
            field.tooltip = "Paste the value. Leave blank to keep the value already stored on this machine.";
            field.style.marginBottom = 2;
            return field;
        }

        void UpdateStoredLabel()
        {
            if (storedLabel == null) return;

            storedLabel.text = LevelPlayApiCredentials.HasCredentials
                ? "Credentials stored on this machine (EditorPrefs). Leave the fields blank to keep them."
                : "No credentials stored yet.";
            storedLabel.style.color = LevelPlayApiCredentials.HasCredentials ? ColOk : ColDim;
        }

        VisualElement Section(string title, string subtitle)
        {
            var box = new VisualElement();
            box.style.marginTop = 10;
            box.style.marginBottom = 4;

            var t = new Label(title);
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.fontSize = 12.5f;
            t.style.color = ColText;
            box.Add(t);

            var s = new Label(subtitle);
            s.style.fontSize = 10;
            s.style.color = ColDim;
            s.style.whiteSpace = WhiteSpace.Normal;
            box.Add(s);

            return box;
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4;
            return row;
        }

        static Button Primary(string text, Action clicked)
        {
            var b = new Button(clicked) { text = text };
            b.style.height = 24;
            b.style.marginRight = 6;
            b.style.backgroundColor = new Color(ColAccent.r, ColAccent.g, ColAccent.b, 0.35f);
            b.style.color = ColText;
            return b;
        }

        static Button Secondary(string text, Action clicked)
        {
            var b = new Button(clicked) { text = text };
            b.style.height = 24;
            b.style.marginRight = 6;
            return b;
        }

        void SetStatus(string text, Color color)
        {
            if (statusLabel == null) return;
            statusLabel.text = text;
            statusLabel.style.color = color;
        }

        // ------------------------------------------------------------ credentials / auth

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

        // ------------------------------------------------------------ apps

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
            androidApp = MatchApp("Android");
            iosApp = MatchApp("iOS");

            RenderApps();
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

            appsHost.Add(Secondary("Apply App Keys to helper prefab", ApplyAppKeys));
        }

        VisualElement AppPicker(string platform, string prefix, Func<LevelPlayApiClient.AppDto> get, Action<LevelPlayApiClient.AppDto> set)
        {
            var candidates = apps.Where(a => string.Equals(a.platform, platform, StringComparison.OrdinalIgnoreCase)).ToList();
            var current = get();

            var box = new VisualElement();
            box.style.marginTop = 6;

            var label = new Label(platform + "  (local bundle id: " + (string.IsNullOrEmpty(BundleIdFor(platform)) ? "?" : BundleIdFor(platform)) + ")");
            label.style.fontSize = 10.5f;
            label.style.color = ColDim;
            box.Add(label);

            if (candidates.Count == 0)
            {
                box.Add(Hint("No " + platform + " application found on the account."));
                return box;
            }

            var names = candidates.Select(a => $"{a.appName}  [{a.appKey}]  {a.bundleId}").ToList();
            var dropdown = new DropdownField(names, Mathf.Max(0, candidates.IndexOf(current)));
            dropdown.style.marginTop = 2;
            dropdown.RegisterValueChangedCallback(_ => set(candidates[dropdown.index]));
            box.Add(dropdown);

            var expected = BundleIdFor(platform);
            if (current != null && !string.IsNullOrEmpty(expected) && !string.Equals(current.bundleId, expected, StringComparison.OrdinalIgnoreCase))
            {
                var warn = new Label("Bundle id does not match the project (" + current.bundleId + " != " + expected + ") - make sure this is the right app.");
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

                if (!unitsByApp.TryGetValue(prefix, out var units))
                {
                    unitsHost.Add(Hint((prefix == "android" ? "Android" : "iOS") + ": ad units not fetched yet."));
                    continue;
                }

                var title = new Label((prefix == "android" ? "Android" : "iOS") + " - " + app.appName + " (" + units.Count + " ad units)");
                title.style.fontSize = 11;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = ColText;
                title.style.marginTop = 6;
                unitsHost.Add(title);

                foreach (var unit in units)
                {
                    var row = Row();
                    var dot = new Label(unit.isPaused ? "\u25CB" : "\u25CF");
                    dot.style.width = 14;
                    dot.style.color = unit.isPaused ? ColWarn : ColOk;
                    row.Add(dot);

                    var l = new Label($"{unit.adFormat,-13} {unit.mediationAdUnitId}  {unit.mediationAdUnitName}" + (unit.isPaused ? "  (PAUSED)" : ""));
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
                    unitsHost.Add(warn);
                }
            }
        }

        static List<string> MissingFormats(List<LevelPlayApiClient.AdUnitDto> units)
        {
            var result = new List<string>();
            foreach (var format in new[] { "rewarded", "interstitial", "banner" })
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

            if (!EditorUtility.DisplayDialog("Enable networks",
                "Create/activate the default network instances (ironSource + UnityAds) for every ad unit on the LevelPlay dashboard?\n\n" +
                "Note: for apps that are not live in the store the platform creates the instances as inactive.",
                "Enable", "Cancel"))
                return;

            _ = EnableDefaultNetworksAsync(targets);
        }

        async Task EnableDefaultNetworksAsync(List<(string prefix, LevelPlayApiClient.AppDto app)> targets)
        {
            foreach (var (prefix, app) in targets)
            {
                var units = unitsByApp[prefix];
                var requests = new List<LevelPlayApiClient.InstanceRequest>();

                foreach (var unit in units)
                {
                    var format = (unit.adFormat ?? "").ToLowerInvariant();
                    if (format != "rewarded" && format != "interstitial" && format != "banner") continue;

                    foreach (var network in DefaultNetworks)
                    {
                        requests.Add(new LevelPlayApiClient.InstanceRequest
                        {
                            instanceName = "Default",
                            networkName = network,
                            adFormat = unit.adFormat,
                            isBidder = false,
                            isLive = true
                        });
                    }
                }

                if (requests.Count == 0) continue;

                var result = await LevelPlayApiClient.CreateInstancesAsync(app.appKey, requests);
                if (!result.Ok)
                {
                    SetStatus($"Enabling networks failed for {app.appName}: {result.Error}", ColFail);
                    return;
                }
            }

            SetStatus("Default networks requested for every ad unit.", ColOk);
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

        static Label Hint(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 10;
            label.style.color = ColDim;
            label.style.marginTop = 2;
            return label;
        }
    }
}
