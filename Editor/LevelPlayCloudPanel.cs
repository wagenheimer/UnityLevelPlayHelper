using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Cloud API Hub for LevelPlay / ironSource.
    /// Manages account API connection, application synchronization, ad units creation,
    /// and deep mediation network diagnostics (ironSource + Unity Ads setup and troubleshooting).
    /// </summary>
    internal sealed class LevelPlayCloudPanel
    {
        const string SdkNetworksUrl = "https://platform.ironsrc.com/partners/next/networks";
        const string DashboardAdUnitsUrl = "https://platform.ironsrc.com/partners/next/mediation/instances/";
        const string DashboardHomeUrl = "https://platform.ironsrc.com/partners/next/";
        const string ApiDocsUrl = "https://docs.unity.com/en-us/grow/levelplay/platform/api/api-authentication";

        static readonly string[] DefaultNetworks = { "ironSource", "unityAds" };
        static readonly string[] Formats = { "rewarded", "interstitial", "banner" };

        public VisualElement Root { get; }

        readonly ScrollView scroll;
        readonly VisualElement body;

        readonly List<LevelPlayApiClient.AppDto> apps = new List<LevelPlayApiClient.AppDto>();
        LevelPlayApiClient.AppDto androidApp;
        LevelPlayApiClient.AppDto iosApp;
        readonly Dictionary<string, List<LevelPlayApiClient.AdUnitDto>> unitsByApp = new Dictionary<string, List<LevelPlayApiClient.AdUnitDto>>();
        readonly Dictionary<string, List<LevelPlayApiClient.InstanceDto>> instancesByApp = new Dictionary<string, List<LevelPlayApiClient.InstanceDto>>();

        Label connectionBadge;
        TextField secretField;
        TextField refreshField;
        Label storedNoticeLabel;

        VisualElement appsHost;
        VisualElement unitsHost;
        VisualElement networksHost;
        VisualElement unityAdsAuditHost;
        VisualElement instancesDetailHost;
        VisualElement verifyReportHost;

        Foldout createSection;
        bool createLiveApp;
        string newAppName = "";
        string newAppPlatform = "Android";
        string newStoreUrl = "";
        string newTaxonomy = "puzzle";
        bool newCoppa;

        float instanceRate = 0.01f;
        bool networksJustEnabled;

        Foldout logFoldout;
        VisualElement logHost;
        readonly List<string> logLines = new List<string>();

        public LevelPlayCloudPanel()
        {
            Root = new VisualElement();
            Root.AddToClassList("lp-root");
            LevelPlayUIStyle.Apply(Root);

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);

            body = scroll.contentContainer;
            Build();
        }

        void Build()
        {
            body.Clear();

            body.Add(LevelPlayUIStyle.CreateCallout(
                "Cloud API connects directly to your ironSource / LevelPlay publisher account. " +
                "Verify your live App Keys, inspect Ad Units, and troubleshoot mediation network demand (ironSource + Unity Ads).",
                "info"));

            // 1. Connection Card
            body.Add(BuildConnectionCard());

            // 2. Applications Card
            body.Add(BuildApplicationsCard());

            // 3. Ad Units Card
            body.Add(BuildAdUnitsCard());

            // 4. Mediation Networks & Unity Ads Diagnostics Card
            body.Add(BuildNetworksCard());

            // 5. Verification Report Card
            body.Add(BuildVerificationCard());

            // 6. Activity Log (collapsible)
            body.Add(BuildActivityCard());

            Log("Cloud API tab initialized. Connect and click 'Connect & Sync Account' to inspect.");
        }

        #region 1. Connection & Auth Card

        VisualElement BuildConnectionCard()
        {
            bool hasCreds = LevelPlayApiCredentials.HasCredentials;
            connectionBadge = LevelPlayUIStyle.CreateBadge(hasCreds ? "Credentials Stored" : "Not Connected", hasCreds ? "ok" : "warn");

            var card = LevelPlayUIStyle.CreateCard(
                "LevelPlay API Credentials",
                "Account-level secrets from LevelPlay Dashboard > My Account > API. Stored safely in EditorPrefs on this machine.",
                connectionBadge);

            secretField = new TextField("Secret Key")
            {
                isPasswordField = true,
                tooltip = "Enter your ironSource Secret Key. Leave blank to retain stored value."
            };
            secretField.AddToClassList("lp-field-input");
            card.Add(secretField);

            refreshField = new TextField("Refresh Token")
            {
                isPasswordField = true,
                tooltip = "Enter your ironSource Refresh Token. Leave blank to retain stored value."
            };
            refreshField.AddToClassList("lp-field-input");
            card.Add(refreshField);

            storedNoticeLabel = new Label();
            storedNoticeLabel.AddToClassList("lp-card-subtitle");
            storedNoticeLabel.style.marginTop = 4;
            storedNoticeLabel.style.marginBottom = 6;
            UpdateStoredNotice();
            card.Add(storedNoticeLabel);

            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var connectBtn = new Button(ConnectAndSyncAsync) { text = "Connect & Sync Account" };
            connectBtn.AddToClassList("lp-action-btn");
            connectBtn.AddToClassList("lp-action-btn--primary");
            actions.Add(connectBtn);

            var saveBtn = new Button(SaveCredentialsOnly) { text = "Save Secrets" };
            saveBtn.AddToClassList("lp-action-btn");
            actions.Add(saveBtn);

            var clearBtn = new Button(ClearCredentials) { text = "Clear Secrets" };
            clearBtn.AddToClassList("lp-action-btn");
            actions.Add(clearBtn);

            var docBtn = new Button(() => Application.OpenURL(ApiDocsUrl)) { text = "API Docs" };
            docBtn.AddToClassList("lp-action-btn");
            actions.Add(docBtn);

            card.Add(actions);
            return card;
        }

        void UpdateStoredNotice()
        {
            if (storedNoticeLabel == null) return;
            storedNoticeLabel.text = LevelPlayApiCredentials.HasCredentials
                ? $"Stored locally: Secret Key [{LevelPlayApiCredentials.Mask(LevelPlayApiCredentials.SecretKey)}] | Refresh Token [{LevelPlayApiCredentials.Mask(LevelPlayApiCredentials.RefreshToken)}]"
                : "No credentials stored. Paste your Secret Key and Refresh Token above.";
        }

        void SaveCredentialsOnly()
        {
            if (!string.IsNullOrWhiteSpace(secretField.value))
                LevelPlayApiCredentials.SecretKey = secretField.value.Trim();
            if (!string.IsNullOrWhiteSpace(refreshField.value))
                LevelPlayApiCredentials.RefreshToken = refreshField.value.Trim();

            secretField.value = "";
            refreshField.value = "";
            UpdateStoredNotice();
            LevelPlayUIStyle.SetBadge(connectionBadge, LevelPlayApiCredentials.HasCredentials ? "Credentials Stored" : "Not Connected", LevelPlayApiCredentials.HasCredentials ? "ok" : "warn");
            Log("API credentials updated in local EditorPrefs.");
        }

        void ClearCredentials()
        {
            LevelPlayApiCredentials.Clear();
            secretField.value = "";
            refreshField.value = "";
            UpdateStoredNotice();
            LevelPlayUIStyle.SetBadge(connectionBadge, "Not Connected", "warn");
            Log("API credentials cleared from local EditorPrefs.");
        }

        async void ConnectAndSyncAsync()
        {
            SaveCredentialsOnly();

            if (!LevelPlayApiCredentials.HasCredentials)
            {
                Log("Connect: failed - please provide Secret Key and Refresh Token.");
                return;
            }

            LevelPlayUIStyle.SetBadge(connectionBadge, "Authenticating...", "info");
            Log("Connect: requesting authentication token...");

            var auth = await LevelPlayApiClient.AuthenticateAsync(LevelPlayApiCredentials.SecretKey, LevelPlayApiCredentials.RefreshToken);
            if (!auth.Ok)
            {
                LevelPlayUIStyle.SetBadge(connectionBadge, "Auth Failed", "fail");
                Log($"Connect failed: {auth.Error}");
                return;
            }

            LevelPlayUIStyle.SetBadge(connectionBadge, "Connected", "ok");
            Log("Connect: authentication successful!");

            await FullSyncAsync();
        }

        async Task FullSyncAsync()
        {
            Log("Sync: fetching applications, ad units and instances...");
            await FetchApplicationsAsync();
            await FetchAdUnitsAsync();
            await FetchInstancesAsync();
            RunLocalVerifier();
        }

        #endregion

        #region 2. Applications Card

        VisualElement BuildApplicationsCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Account Applications",
                "Applications registered on your LevelPlay account. Click 'Apply Key to Prefab' to copy App Keys to the helper.");

            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var fetchBtn = new Button(() => _ = FetchApplicationsAsync()) { text = "Refresh Apps List" };
            fetchBtn.AddToClassList("lp-action-btn");
            actions.Add(fetchBtn);

            card.Add(actions);

            appsHost = new VisualElement();
            appsHost.style.marginTop = 6;
            card.Add(appsHost);

            // Collapsible Create App section
            createSection = new Foldout { text = "Register New Application on Dashboard", value = false };
            createSection.AddToClassList("lp-card");
            createSection.style.marginTop = 8;

            var mode = new Toggle("Already published on Google Play / App Store") { value = createLiveApp };
            mode.RegisterValueChangedCallback(e =>
            {
                createLiveApp = e.newValue;
                RenderCreateFields();
            });
            createSection.Add(mode);

            var createFieldsHost = new VisualElement();
            createFieldsHost.name = "createFieldsHost";
            createSection.Add(createFieldsHost);

            card.Add(createSection);
            RenderCreateFields();
            RenderApps();

            return card;
        }

        async Task FetchApplicationsAsync()
        {
            var (result, list) = await LevelPlayApiClient.GetApplicationsAsync();
            if (!result.Ok)
            {
                Log($"Fetch Apps failed: {result.Error}");
                return;
            }

            apps.Clear();
            apps.AddRange(list);
            LevelPlayCloudCache.SetApps(list);

            androidApp = MatchApp("Android");
            iosApp = MatchApp("iOS");

            RenderApps();
            Log($"Apps synchronized: {apps.Count} found (Android: {androidApp?.appName ?? "Not found"}, iOS: {iosApp?.appName ?? "Not found"}).");
        }

        void RenderApps()
        {
            if (appsHost == null) return;
            appsHost.Clear();

            if (apps.Count == 0)
            {
                appsHost.Add(new Label("No applications loaded yet. Click 'Connect & Sync Account' above."));
                return;
            }

            var helper = LevelPlayHelperLocator.FindPreferred();
            var so = helper != null ? new SerializedObject(helper) : null;

            RenderAppRow("Android", androidApp, so, "androidAppKey");
            RenderAppRow("iOS", iosApp, so, "iosAppKey");
        }

        void RenderAppRow(string platform, LevelPlayApiClient.AppDto app, SerializedObject so, string propertyName)
        {
            var row = new VisualElement();
            row.AddToClassList("lp-table-row");

            var platLabel = new Label(platform) { style = { width = 70, unityFontStyleAndWeight = FontStyle.Bold } };
            row.Add(platLabel);

            if (app == null)
            {
                var missingLabel = new Label($"No matching {platform} application found on account.")
                {
                    style = { flexGrow = 1, color = new Color(0.9f, 0.5f, 0.2f) }
                };
                row.Add(missingLabel);
            }
            else
            {
                var nameLabel = new Label($"{app.appName} [{app.appKey}]")
                {
                    style = { flexGrow = 1, color = new Color(0.9f, 0.9f, 0.9f) },
                    tooltip = $"Bundle ID: {app.bundleId}"
                };
                row.Add(nameLabel);

                if (so != null)
                {
                    var prop = so.FindProperty(propertyName);
                    bool matches = prop != null && string.Equals(prop.stringValue, app.appKey, StringComparison.OrdinalIgnoreCase);

                    var badge = LevelPlayUIStyle.CreateBadge(matches ? "Applied to Prefab" : "Different in Prefab", matches ? "ok" : "warn");
                    row.Add(badge);

                    if (!matches)
                    {
                        var applyBtn = new Button(() => ApplyAppKey(so, propertyName, app.appKey))
                        {
                            text = "Apply Key"
                        };
                        applyBtn.AddToClassList("lp-action-btn");
                        applyBtn.style.marginLeft = 6;
                        row.Add(applyBtn);
                    }
                }
            }

            appsHost.Add(row);
        }

        void ApplyAppKey(SerializedObject so, string propName, string key)
        {
            if (so == null || string.IsNullOrEmpty(key)) return;
            var prop = so.FindProperty(propName);
            if (prop != null)
            {
                prop.stringValue = key;
                so.ApplyModifiedProperties();
                if (so.targetObject != null)
                {
                    EditorUtility.SetDirty(so.targetObject);
                    AssetDatabase.SaveAssets();
                }
                Log($"Applied {propName} = {key} to helper prefab.");
                RenderApps();
            }
        }

        LevelPlayApiClient.AppDto MatchApp(string platform)
        {
            var candidates = apps.Where(a => string.Equals(a.platform, platform, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) return null;

            var expected = BundleIdFor(platform);
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
            catch { return ""; }
        }

        void RenderCreateFields()
        {
            if (createSection == null) return;
            var host = createSection.Q<VisualElement>("createFieldsHost");
            if (host == null) return;
            host.Clear();

            var nameField = new TextField("App Name") { value = newAppName };
            nameField.RegisterValueChangedCallback(e => newAppName = e.newValue);
            host.Add(nameField);

            var platformField = new EnumField("Platform", newAppPlatform == "iOS" ? BuildTarget.iOS : BuildTarget.Android);
            platformField.RegisterValueChangedCallback(e => newAppPlatform = e.newValue.ToString() == "iOS" ? "iOS" : "Android");
            host.Add(platformField);

            var createBtn = new Button(CreateAppAsync) { text = "Submit New App to Dashboard" };
            createBtn.AddToClassList("lp-action-btn");
            createBtn.AddToClassList("lp-action-btn--primary");
            createBtn.style.marginTop = 6;
            host.Add(createBtn);
        }

        async void CreateAppAsync()
        {
            if (string.IsNullOrWhiteSpace(newAppName))
            {
                Log("Create App failed: name is required.");
                return;
            }

            var req = new LevelPlayApiClient.AppRequest
            {
                appName = newAppName.Trim(),
                platform = newAppPlatform
            };

            Log($"POST create app -> {req.appName} ({req.platform})...");
            var res = await LevelPlayApiClient.CreateApplicationAsync(req);
            if (!res.Ok)
            {
                Log($"Create App failed: {res.Error}");
                return;
            }

            Log($"Create App succeeded! Re-fetching applications...");
            await FetchApplicationsAsync();
        }

        #endregion

        #region 3. Ad Units Card

        VisualElement BuildAdUnitsCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Ad Units & Formats (ironSource Dashboard)",
                "Comparison of configured Ad Unit IDs between your local helper prefab and the remote LevelPlay dashboard.");

            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var fetchBtn = new Button(() => _ = FetchAdUnitsAsync()) { text = "Fetch Ad Units" };
            fetchBtn.AddToClassList("lp-action-btn");
            actions.Add(fetchBtn);

            var createMissingBtn = new Button(CreateMissingAdUnits) { text = "Create Missing Units on Dashboard" };
            createMissingBtn.AddToClassList("lp-action-btn");
            createMissingBtn.AddToClassList("lp-action-btn--primary");
            actions.Add(createMissingBtn);

            var openUnitsBtn = new Button(() => Application.OpenURL(DashboardAdUnitsUrl)) { text = "Dashboard Ad Units" };
            openUnitsBtn.AddToClassList("lp-action-btn");
            actions.Add(openUnitsBtn);

            card.Add(actions);

            unitsHost = new VisualElement();
            unitsHost.style.marginTop = 6;
            card.Add(unitsHost);

            RenderUnits();
            return card;
        }

        async Task FetchAdUnitsAsync()
        {
            foreach (var target in new[] { ("android", androidApp), ("ios", iosApp) })
            {
                if (target.Item2 == null) continue;
                var (result, units) = await LevelPlayApiClient.GetAdUnitsAsync(target.Item2.appKey);
                if (!result.Ok)
                {
                    Log($"Fetch Ad Units failed for {target.Item2.appName}: {result.Error}");
                    continue;
                }
                unitsByApp[target.Item1] = units;
                LevelPlayCloudCache.SetUnits(target.Item2.appKey, units);
                Log($"Ad Units fetched for {target.Item2.appName}: {units.Count} unit(s).");
            }
            RenderUnits();
            RenderNetworks();
        }

        void RenderUnits()
        {
            if (unitsHost == null) return;
            unitsHost.Clear();

            if (unitsByApp.Count == 0)
            {
                unitsHost.Add(new Label("No ad units fetched yet. Connect and click 'Fetch Ad Units'."));
                return;
            }

            foreach (var prefix in new[] { "android", "ios" })
            {
                var app = prefix == "android" ? androidApp : iosApp;
                if (app == null) continue;

                var platName = prefix == "android" ? "Android" : "iOS";
                var header = new Label($"{platName} - {app.appName}")
                {
                    style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, marginTop = 6, marginBottom = 2 }
                };
                unitsHost.Add(header);

                if (!unitsByApp.TryGetValue(prefix, out var units) || units.Count == 0)
                {
                    unitsHost.Add(new Label($"No ad units registered on dashboard for {platName}."));
                    continue;
                }

                foreach (var format in Formats)
                {
                    var localId = HelperAdUnitId(prefix, format);
                    var remote = units.FirstOrDefault(u => string.Equals(u.adFormat, format, StringComparison.OrdinalIgnoreCase));

                    var row = new VisualElement();
                    row.AddToClassList("lp-table-row");

                    var formatLbl = new Label(format.ToUpperInvariant()) { style = { width = 100, unityFontStyleAndWeight = FontStyle.Bold } };
                    row.Add(formatLbl);

                    if (remote != null)
                    {
                        var info = new Label($"Dashboard ID: {remote.mediationAdUnitId} ({remote.mediationAdUnitName})")
                        {
                            style = { flexGrow = 1, color = new Color(0.85f, 0.85f, 0.85f) }
                        };
                        row.Add(info);

                        bool matches = !string.IsNullOrEmpty(localId) && string.Equals(localId, remote.mediationAdUnitId, StringComparison.OrdinalIgnoreCase);
                        var badge = LevelPlayUIStyle.CreateBadge(matches ? "Matched Locally" : (string.IsNullOrEmpty(localId) ? "Not in Prefab" : "ID Mismatch"), matches ? "ok" : "warn");
                        row.Add(badge);

                        if (!matches && !string.IsNullOrEmpty(remote.mediationAdUnitId))
                        {
                            var applyBtn = new Button(() => ApplyAdUnitId(prefix, format, remote.mediationAdUnitId))
                            {
                                text = "Apply to Prefab"
                            };
                            applyBtn.AddToClassList("lp-action-btn");
                            applyBtn.style.marginLeft = 6;
                            row.Add(applyBtn);
                        }
                    }
                    else
                    {
                        var info = new Label("Missing on LevelPlay Dashboard") { style = { flexGrow = 1, color = new Color(0.9f, 0.5f, 0.2f) } };
                        row.Add(info);
                        row.Add(LevelPlayUIStyle.CreateBadge("Missing", "fail"));
                    }

                    unitsHost.Add(row);
                }
            }
        }

        void ApplyAdUnitId(string prefix, string format, string unitId)
        {
            var helper = LevelPlayHelperLocator.FindPreferred();
            if (helper == null) return;

            var so = new SerializedObject(helper);
            var propName = prefix + Capitalize(format) + "AdUnitId";
            var prop = so.FindProperty(propName);
            if (prop != null)
            {
                prop.stringValue = unitId;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(helper);
                AssetDatabase.SaveAssets();
                Log($"Applied {propName} = {unitId} to helper prefab.");
                RenderUnits();
            }
        }

        static string Capitalize(string text) => string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);

        async void CreateMissingAdUnits()
        {
            var targets = new[] { ("android", androidApp), ("ios", iosApp) }
                .Where(t => t.Item2 != null)
                .ToList();

            if (targets.Count == 0)
            {
                Log("Create Ad Units: please fetch applications first.");
                return;
            }

            foreach (var (prefix, app) in targets)
            {
                unitsByApp.TryGetValue(prefix, out var existing);
                existing ??= new List<LevelPlayApiClient.AdUnitDto>();

                var missingFormats = Formats.Where(f => !existing.Any(e => string.Equals(e.adFormat, f, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missingFormats.Count == 0) continue;

                var requests = missingFormats.Select(f => new LevelPlayApiClient.AdUnitRequest
                {
                    mediationAdUnitName = "LevelPlayHelper-" + f,
                    adFormat = f,
                    reward = f == "rewarded" ? new LevelPlayApiClient.Reward { rewardItemName = "Virtual Item", rewardAmount = 1 } : null
                }).ToList();

                Log($"Creating {requests.Count} missing ad unit(s) for {app.appName}...");
                var res = await LevelPlayApiClient.CreateAdUnitsAsync(app.appKey, requests);
                if (!res.Ok)
                {
                    Log($"Failed creating ad units for {app.appName}: {res.Error}");
                }
                else
                {
                    Log($"Ad units created successfully for {app.appName}!");
                }
            }

            await FetchAdUnitsAsync();
        }

        #endregion

        #region 4. Mediation Networks & Unity Ads Investigation Card

        static (bool installed, string version) CheckLocalUnityAdsAdapter()
        {
            var xmlPath = "Assets/LevelPlay/Editor/ISUnityAdsAdapterDependencies.xml";
            if (File.Exists(xmlPath))
            {
                try
                {
                    var content = File.ReadAllText(xmlPath);
                    var match = Regex.Match(content, @"<unityversion>(.*?)</unityversion>");
                    var ver = match.Success ? match.Groups[1].Value : "installed (xml)";
                    return (true, ver);
                }
                catch { return (true, "installed (xml)"); }
            }

            try
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(p => p.name.Contains("levelplay.adapters.unityads") || p.name.Contains("unityads"));
                if (pkg != null)
                    return (true, pkg.version);
            }
            catch { }

            return (false, "Not detected");
        }

        VisualElement BuildNetworksCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Mediation Networks & Demand (ironSource + Unity Ads)",
                "To maximize fill rate and auction competition, LevelPlay requires ironSource and Unity Ads to run concurrently.");

            // 1. Unity Ads Readiness Assessment Box
            unityAdsAuditHost = new VisualElement();
            card.Add(unityAdsAuditHost);
            RenderUnityAdsReadinessBox();

            // eCPM rate input
            var rateRow = new VisualElement();
            rateRow.AddToClassList("lp-field-row");
            var rateLbl = new Label("Instance Rate (eCPM)") { style = { width = 160 } };
            rateRow.Add(rateLbl);
            var rateField = new FloatField { value = instanceRate, style = { width = 120 } };
            rateField.RegisterValueChangedCallback(e => instanceRate = Mathf.Clamp(e.newValue, 0.01f, 3000f));
            rateRow.Add(rateField);
            var rateTip = new Label("Min: $0.01 (required by API for non-bidding instances)") { style = { fontSize = 10, color = new Color(0.6f, 0.6f, 0.6f), marginLeft = 6 } };
            rateRow.Add(rateTip);
            card.Add(rateRow);

            // Action Buttons
            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var enableDefaultBtn = new Button(EnableDefaultNetworks) { text = "⚡ Enable Default Networks (ironSource + UnityAds via API)" };
            enableDefaultBtn.AddToClassList("lp-action-btn");
            enableDefaultBtn.AddToClassList("lp-action-btn--primary");
            actions.Add(enableDefaultBtn);

            var openNetManagerBtn = new Button(OpenUnityNetworkManager) { text = "📦 Open Network Manager (Unity)" };
            openNetManagerBtn.AddToClassList("lp-action-btn");
            actions.Add(openNetManagerBtn);

            var openDashboardNetworksBtn = new Button(() => Application.OpenURL(SdkNetworksUrl))
            {
                text = "🌐 Open SDK Networks (Dashboard)",
                tooltip = "Direct link: " + SdkNetworksUrl
            };
            openDashboardNetworksBtn.AddToClassList("lp-action-btn");
            actions.Add(openDashboardNetworksBtn);

            var openDashboardUnitsBtn = new Button(() => Application.OpenURL(DashboardAdUnitsUrl))
            {
                text = "🌐 Open Instances (Dashboard)",
                tooltip = "Direct link: " + DashboardAdUnitsUrl
            };
            openDashboardUnitsBtn.AddToClassList("lp-action-btn");
            actions.Add(openDashboardUnitsBtn);

            var inspectInstancesBtn = new Button(() => _ = FetchInstancesAsync()) { text = "🔍 Inspect Detailed Instances" };
            inspectInstancesBtn.AddToClassList("lp-action-btn");
            actions.Add(inspectInstancesBtn);

            card.Add(actions);

            networksHost = new VisualElement();
            networksHost.style.marginTop = 8;
            card.Add(networksHost);

            instancesDetailHost = new VisualElement();
            instancesDetailHost.style.marginTop = 8;
            card.Add(instancesDetailHost);

            RenderNetworks();
            return card;
        }

        void RenderUnityAdsReadinessBox()
        {
            if (unityAdsAuditHost == null) return;
            unityAdsAuditHost.Clear();

            var auditBox = new VisualElement();
            auditBox.AddToClassList("lp-callout");
            auditBox.AddToClassList("lp-callout--info");
            auditBox.style.marginBottom = 10;

            var title = new Label("📋 Step-by-Step Guide: Unity Ads & LevelPlay Integration")
            {
                style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6, color = new Color(0.95f, 0.95f, 0.95f) }
            };
            auditBox.Add(title);

            // Step 1: Client Adapter
            var (adapterInstalled, adapterVer) = CheckLocalUnityAdsAdapter();
            var step1Row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            var step1Label = new Label("1. Unity Ads Adapter (Client):") { style = { width = 230, fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold } };
            step1Row.Add(step1Label);
            var step1Badge = LevelPlayUIStyle.CreateBadge(adapterInstalled ? $"Installed ({adapterVer})" : "Not Detected", adapterInstalled ? "ok" : "fail");
            step1Row.Add(step1Badge);

            var step1Btn = new Button(OpenUnityNetworkManager) { text = "Open Network Manager" };
            step1Btn.AddToClassList("lp-action-btn");
            step1Btn.style.marginLeft = 8;
            step1Row.Add(step1Btn);
            auditBox.Add(step1Row);

            var step1Hint = new Label(adapterInstalled
                ? "   ISUnityAdsAdapterDependencies is installed. Native Android (Gradle) and iOS (CocoaPods) libraries resolve automatically."
                : "   The Unity Ads adapter is required. Open Ads Mediation > Network Manager in Unity to install it.");
            step1Hint.style.fontSize = 10.5f;
            step1Hint.style.color = new Color(0.75f, 0.75f, 0.75f);
            step1Hint.style.marginBottom = 6;
            step1Hint.style.whiteSpace = WhiteSpace.Normal;
            auditBox.Add(step1Hint);

            // Step 2: Dashboard SDK Network Link
            var step2Row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            var step2Label = new Label("2. Network Link in Dashboard:") { style = { width = 230, fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold } };
            step2Row.Add(step2Label);
            var step2Badge = LevelPlayUIStyle.CreateBadge("Account Level", "info");
            step2Row.Add(step2Badge);

            var step2Btn = new Button(() => Application.OpenURL(SdkNetworksUrl)) { text = "Open SDK Networks" };
            step2Btn.AddToClassList("lp-action-btn");
            step2Btn.style.marginLeft = 8;
            step2Row.Add(step2Btn);
            auditBox.Add(step2Row);

            var step2Hint = new Label(
                "   • In Monetize > Setup > Networks > Unity Ads: Enter API Key and Organization Core ID.\n" +
                "   • Turn OFF 'Bidder auto-setup' (Unity Cloud migration requirement) and click Save.");
            step2Hint.style.fontSize = 10.5f;
            step2Hint.style.color = new Color(0.75f, 0.75f, 0.75f);
            step2Hint.style.marginBottom = 6;
            step2Hint.style.whiteSpace = WhiteSpace.Normal;
            auditBox.Add(step2Hint);

            // Step 3: Game ID & Instances
            var step3Row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            var step3Label = new Label("3. Game ID & Ad Instances:") { style = { width = 230, fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold } };
            step3Row.Add(step3Label);
            var step3Badge = LevelPlayUIStyle.CreateBadge("Per-App Setup", "info");
            step3Row.Add(step3Badge);

            var step3Btn = new Button(() => Application.OpenURL(DashboardAdUnitsUrl)) { text = "Open Instances Dashboard" };
            step3Btn.AddToClassList("lp-action-btn");
            step3Btn.style.marginLeft = 8;
            step3Row.Add(step3Btn);
            auditBox.Add(step3Row);

            var step3Hint = new Label(
                "   • In Monetize > Setup > Instances (or Mediation Management): Select your app.\n" +
                "   • Enter your Unity Game ID (from cloud.unity.com > Monetization > Apps).\n" +
                "   • Add an instance for each format: Name = 'UnityAds_Rewarded', Placement ID = 'Rewarded_iOS' / 'Rewarded_Android', Status = Active.");
            step3Hint.style.fontSize = 10.5f;
            step3Hint.style.color = new Color(0.75f, 0.75f, 0.75f);
            step3Hint.style.marginBottom = 6;
            step3Hint.style.whiteSpace = WhiteSpace.Normal;
            auditBox.Add(step3Hint);

            // Step 4: Verification
            var step4Row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            var step4Label = new Label("4. Live API Verification:") { style = { width = 230, fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold } };
            step4Row.Add(step4Label);
            var step4Badge = LevelPlayUIStyle.CreateBadge("Real-Time Check", "ok");
            step4Row.Add(step4Badge);

            var step4Btn = new Button(() => _ = FullSyncAsync()) { text = "Fetch & Verify Setup" };
            step4Btn.AddToClassList("lp-action-btn");
            step4Btn.AddToClassList("lp-action-btn--primary");
            step4Btn.style.marginLeft = 8;
            step4Row.Add(step4Btn);
            auditBox.Add(step4Row);

            var step4Hint = new Label(
                "   • Click 'Fetch & Verify Setup' to query the live LevelPlay Management API.\n" +
                "   • The table below displays real-time status of ironSource and Unity Ads instances across iOS and Android.");
            step4Hint.style.fontSize = 10.5f;
            step4Hint.style.color = new Color(0.75f, 0.75f, 0.75f);
            step4Hint.style.whiteSpace = WhiteSpace.Normal;
            auditBox.Add(step4Hint);

            unityAdsAuditHost.Add(auditBox);
        }

        void OpenUnityNetworkManager()
        {
            Log("Opening Ads Mediation > Network Manager in Unity...");
            bool opened = EditorApplication.ExecuteMenuItem("Ads Mediation/Network Manager");
            if (!opened)
            {
                Log("Menu 'Ads Mediation/Network Manager' not available. Check if com.unity.services.levelplay package is installed.");
            }
        }

        async Task FetchInstancesAsync()
        {
            instancesByApp.Clear();
            foreach (var target in new[] { ("android", androidApp), ("ios", iosApp) })
            {
                if (target.Item2 == null) continue;
                var (res, instances) = await LevelPlayApiClient.GetInstancesAsync(target.Item2.appKey);
                if (res.Ok)
                {
                    instancesByApp[target.Item1] = instances;
                    Log($"Instances fetched for {target.Item2.appName}: {instances.Count} instance(s).");
                }
                else
                {
                    Log($"Failed fetching instances for {target.Item2.appName}: {res.Error}");
                }
            }
            RenderNetworks();
            RenderInstancesDetail();
        }

        void RenderNetworks()
        {
            if (networksHost == null) return;
            networksHost.Clear();

            foreach (var prefix in new[] { "android", "ios" })
            {
                var app = prefix == "android" ? androidApp : iosApp;
                if (app == null) continue;

                var platName = prefix == "android" ? "Android" : "iOS";
                var header = new Label($"Demand Network Status - {platName} ({app.appName})")
                {
                    style = { fontSize = 12, unityFontStyleAndWeight = FontStyle.Bold, marginTop = 6, marginBottom = 4 }
                };
                networksHost.Add(header);

                instancesByApp.TryGetValue(prefix, out var appInstances);
                appInstances ??= new List<LevelPlayApiClient.InstanceDto>();

                foreach (var format in Formats)
                {
                    var configuredLocally = !string.IsNullOrEmpty(HelperAdUnitId(prefix, format));

                    var activeNets = LevelPlayCloudCache.ActiveNetworks(app, format) ?? Array.Empty<string>();
                    bool hasIronSourceInApp = activeNets.Any(n => n.IndexOf("ironSource", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hasUnityAdsInApp = activeNets.Any(n => n.IndexOf("unity", StringComparison.OrdinalIgnoreCase) >= 0);

                    // Cross check with actual instances from API
                    var uaInstance = appInstances.FirstOrDefault(i =>
                        string.Equals(i.adFormat, format, StringComparison.OrdinalIgnoreCase) &&
                        (i.networkName != null && i.networkName.IndexOf("unity", StringComparison.OrdinalIgnoreCase) >= 0));

                    var isInstance = appInstances.FirstOrDefault(i =>
                        string.Equals(i.adFormat, format, StringComparison.OrdinalIgnoreCase) &&
                        (i.networkName != null && i.networkName.IndexOf("ironSource", StringComparison.OrdinalIgnoreCase) >= 0));

                    var row = new VisualElement();
                    row.AddToClassList("lp-table-row");

                    var formatLbl = new Label(format.ToUpperInvariant()) { style = { width = 100, unityFontStyleAndWeight = FontStyle.Bold } };
                    row.Add(formatLbl);

                    string uaStatusText;
                    string uaBadgeType;
                    if (uaInstance != null && uaInstance.isLive)
                    {
                        uaStatusText = $"Unity Ads: LIVE ({uaInstance.instanceName})";
                        uaBadgeType = "ok";
                    }
                    else if (uaInstance != null && !uaInstance.isLive)
                    {
                        uaStatusText = $"Unity Ads: PAUSED ({uaInstance.instanceName})";
                        uaBadgeType = "warn";
                    }
                    else if (hasUnityAdsInApp)
                    {
                        uaStatusText = "Unity Ads: LIVE (Active Bidding Network)";
                        uaBadgeType = "ok";
                    }
                    else
                    {
                        uaStatusText = "Unity Ads: MISSING in Dashboard";
                        uaBadgeType = "fail";
                    }

                    string isStatusText = (isInstance != null && isInstance.isLive || hasIronSourceInApp)
                        ? "ironSource: LIVE"
                        : "ironSource: INACTIVE";

                    string localBinding = configuredLocally ? "✓ Synced to Prefab" : "⚠ Not bound to Prefab";

                    var statusDesc = new Label($"{isStatusText} | {uaStatusText}  [{localBinding}]")
                    {
                        style = { flexGrow = 1, color = new Color(0.85f, 0.85f, 0.85f) }
                    };
                    row.Add(statusDesc);

                    // Badge ironSource
                    var isBadge = LevelPlayUIStyle.CreateBadge("ironSource", (isInstance != null && isInstance.isLive || hasIronSourceInApp) ? "ok" : "fail");
                    isBadge.style.marginRight = 4;
                    row.Add(isBadge);

                    // Badge UnityAds
                    var uaBadge = LevelPlayUIStyle.CreateBadge("Unity Ads", uaBadgeType);
                    row.Add(uaBadge);

                    networksHost.Add(row);
                }
            }
        }

        void RenderInstancesDetail()
        {
            if (instancesDetailHost == null) return;
            instancesDetailHost.Clear();

            if (instancesByApp.Count == 0) return;

            var title = new Label("API Technical Instances:")
            {
                style = { fontSize = 11, unityFontStyleAndWeight = FontStyle.Bold, marginTop = 6, marginBottom = 4 }
            };
            instancesDetailHost.Add(title);

            foreach (var (prefix, instances) in instancesByApp)
            {
                var platName = prefix == "android" ? "Android" : "iOS";
                foreach (var inst in instances)
                {
                    var row = new VisualElement();
                    row.AddToClassList("lp-table-row");

                    var netLbl = new Label($"{platName} | {inst.networkName}") { style = { width = 160, unityFontStyleAndWeight = FontStyle.Bold } };
                    row.Add(netLbl);

                    var formatLbl = new Label($"[{inst.adFormat}] {inst.instanceName} (ID: {inst.instanceId})")
                    {
                        style = { flexGrow = 1, color = new Color(0.8f, 0.8f, 0.8f) }
                    };
                    row.Add(formatLbl);

                    var liveBadge = LevelPlayUIStyle.CreateBadge(inst.isLive ? "LIVE" : "INACTIVE", inst.isLive ? "ok" : "warn");
                    row.Add(liveBadge);

                    instancesDetailHost.Add(row);
                }
            }
        }

        void EnableDefaultNetworks()
        {
            var targets = new[] { ("android", androidApp), ("ios", iosApp) }
                .Where(t => t.Item2 != null && unitsByApp.ContainsKey(t.Item1))
                .ToList();

            if (targets.Count == 0)
            {
                Log("Enable networks skipped: fetch ad units first.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Enable Default Networks",
                "Do you want to create/activate ironSource and Unity Ads instances for all active formats?\n\n" +
                "If Unity Ads instances fail to create, ensure the Unity Ads network is configured with your Game ID in the ironSource dashboard (Monetize > Setup > Networks).",
                "Enable", "Cancel"))
                return;

            _ = EnableDefaultNetworksAsync(targets);
        }

        async Task EnableDefaultNetworksAsync(List<(string prefix, LevelPlayApiClient.AppDto app)> targets)
        {
            Log("Enable Default Networks: starting process...");
            int activated = 0;
            int created = 0;

            foreach (var (prefix, app) in targets)
            {
                var (read, instances) = await LevelPlayApiClient.GetInstancesAsync(app.appKey);
                if (!read.Ok)
                {
                    Log($"Could not read instances for {app.appName}: {read.Error}");
                    continue;
                }

                var toActivate = new List<LevelPlayApiClient.InstanceUpdate>();
                var toCreate = new List<LevelPlayApiClient.InstanceRequest>();

                foreach (var unit in unitsByApp[prefix])
                {
                    var format = (unit.adFormat ?? "").ToLowerInvariant();
                    if (format != "rewarded" && format != "interstitial" && format != "banner") continue;
                    if (string.IsNullOrEmpty(HelperAdUnitId(prefix, format))) continue;

                    foreach (var network in DefaultNetworks)
                    {
                        var existing = instances.FirstOrDefault(i =>
                            string.Equals(i.adFormat, unit.adFormat, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(i.networkName, network, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
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

                if (toActivate.Count > 0)
                {
                    Log($"Activating {toActivate.Count} existing instance(s) on {app.appName}...");
                    var actRes = await LevelPlayApiClient.UpdateInstancesAsync(app.appKey, toActivate);
                    if (actRes.Ok) activated += toActivate.Count;
                    else Log($"Activation error: {actRes.Error}");
                }

                if (toCreate.Count > 0)
                {
                    Log($"Creating {toCreate.Count} instance(s) with rate ${instanceRate:0.00} on {app.appName}...");
                    var createRes = await LevelPlayApiClient.CreateInstancesAsync(app.appKey, toCreate);
                    if (createRes.Ok) created += toCreate.Count;
                    else Log($"Create instances response: {createRes.Error}");
                }
            }

            networksJustEnabled = true;
            Log($"Enable networks complete: {activated} activated, {created} created. Re-fetching...");
            await FullSyncAsync();
        }

        #endregion

        #region 5. Verification Card

        VisualElement BuildVerificationCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Cross-Verification Report (Account vs Project)",
                "Cross-checks local helper prefab against cloud state to find mismatching App Keys, paused ad units or missing networks.");

            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var verifyBtn = new Button(RunLocalVerifier) { text = "Re-Run Verifier" };
            verifyBtn.AddToClassList("lp-action-btn");
            actions.Add(verifyBtn);

            card.Add(actions);

            verifyReportHost = new VisualElement();
            verifyReportHost.style.marginTop = 6;
            card.Add(verifyReportHost);

            RunLocalVerifier();
            return card;
        }

        void RunLocalVerifier()
        {
            if (verifyReportHost == null) return;
            verifyReportHost.Clear();

            var helper = LevelPlayHelperLocator.FindPreferred();
            var report = LevelPlayVerifier.Verify(helper != null ? new SerializedObject(helper) : null);
            if (report == null) return;

            var banner = new Label(report.Errors > 0
                ? $"VERIFICATION FAILED: {report.Errors} error(s), {report.Warnings} warning(s)"
                : report.Warnings > 0
                    ? $"VERIFICATION PASSED WITH WARNINGS: {report.Warnings} warning(s)"
                    : $"ALL CHECKS PASSED: {report.OkCount} valid items. Configuration is healthy!");
            banner.style.fontSize = 11.5f;
            banner.style.unityFontStyleAndWeight = FontStyle.Bold;
            banner.style.color = report.Errors > 0 ? new Color(0.9f, 0.3f, 0.3f) : report.Warnings > 0 ? new Color(1f, 0.7f, 0.2f) : new Color(0.3f, 0.8f, 0.4f);
            banner.style.marginBottom = 6;
            verifyReportHost.Add(banner);

            foreach (var finding in report.Findings)
            {
                var row = new VisualElement();
                row.AddToClassList("lp-table-row");

                string glyph = finding.Level == LevelPlayVerifier.Level.Error ? "✕" : finding.Level == LevelPlayVerifier.Level.Warning ? "!" : "✓";
                Color col = finding.Level == LevelPlayVerifier.Level.Error ? new Color(0.9f, 0.3f, 0.3f) : finding.Level == LevelPlayVerifier.Level.Warning ? new Color(1f, 0.7f, 0.2f) : new Color(0.3f, 0.8f, 0.4f);

                var icon = new Label(glyph) { style = { width = 16, color = col, unityFontStyleAndWeight = FontStyle.Bold } };
                row.Add(icon);

                var txt = new Label(finding.Text) { style = { flexGrow = 1, color = col } };
                row.Add(txt);

                verifyReportHost.Add(row);
            }
        }

        #endregion

        #region 6. Activity Log Card

        VisualElement BuildActivityCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Activity History & Logs",
                "Real-time chronological log of all API operations, requests, responses, and network validations.");

            logFoldout = new Foldout { text = "View Activity Log", value = true };
            logFoldout.style.marginTop = 4;

            var logActions = new VisualElement();
            logActions.AddToClassList("lp-actions-row");

            var clearBtn = new Button(ClearLog) { text = "Clear Log" };
            clearBtn.AddToClassList("lp-action-btn");
            logActions.Add(clearBtn);

            var copyBtn = new Button(CopyLog) { text = "Copy Log" };
            copyBtn.AddToClassList("lp-action-btn");
            logActions.Add(copyBtn);

            logFoldout.Add(logActions);

            logHost = new ScrollView(ScrollViewMode.Vertical);
            logHost.AddToClassList("lp-log-box");
            logHost.style.marginTop = 6;
            logFoldout.Add(logHost);

            card.Add(logFoldout);
            return card;
        }

        void ClearLog()
        {
            logLines.Clear();
            logHost?.Clear();
        }

        void CopyLog()
        {
            EditorGUIUtility.systemCopyBuffer = string.Join("\n", logLines);
            Log("Activity log copied to clipboard.");
        }

        void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss} - {message}";
            logLines.Add(line);
            if (logLines.Count > 400) logLines.RemoveAt(0);

            if (logHost == null) return;
            var lbl = new Label(line);
            lbl.AddToClassList("lp-log-line");
            logHost.Add(lbl);

            while (logHost.childCount > 150) logHost.RemoveAt(0);
        }

        #endregion

        static string HelperAdUnitId(string prefix, string format)
        {
            var helper = LevelPlayHelperLocator.FindPreferred();
            if (helper == null) return "";

            var so = new SerializedObject(helper);
            var propName = prefix + Capitalize(format) + "AdUnitId";
            return so.FindProperty(propName)?.stringValue ?? "";
        }
    }
}
