using System;

using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.UI
{
    /// <summary>
    /// "Networks / providers" and "Test tools" cards: which ad networks are winning auctions and
    /// paying, the last impression in full, and the SDK test switches (Test Suite, integration check,
    /// adapter logs) that otherwise need a rebuild.
    /// </summary>
    public partial class LevelPlayDebugOverlay
    {
        private const float NetworksRebuildSeconds = 1f;

        private Label _networksTotals;
        private VisualElement _networksList;
        private Label _networksEmpty;
        private Label _lastImpression;
        private int _renderedNetworksVersion = -1;
        private float _nextNetworksRebuild;

        private Label _testSuiteLine;
        private Button _launchTestSuiteButton;
        private Button _testSuiteNextLaunchButton;
        private Button _adaptersDebugButton;

        #region Networks card

        private VisualElement BuildNetworksCard()
        {
            var card = Card();
            card.Add(SectionTitle("NETWORKS / PROVIDERS (this session)"));

            _networksTotals = Body();
            card.Add(_networksTotals);

            _networksList = new VisualElement();
            _networksList.style.marginTop = 4;
            card.Add(_networksList);

            _networksEmpty = Muted();
            _networksEmpty.text = "No network has won an auction yet.";
            card.Add(_networksEmpty);

            var lastTitle = Muted();
            lastTitle.text = "Last impression:";
            lastTitle.style.marginTop = 6;
            lastTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(lastTitle);

            _lastImpression = Muted();
            _lastImpression.style.color = ColorAccentOrange;
            card.Add(_lastImpression);

            var actions = ActionRow();
            actions.style.marginTop = 4;
            actions.Add(ActionButton("Copy Impression JSON", ColorHeaderTo, CopyLastImpression));
            actions.Add(ActionButton("Reset Stats", ColorTextMuted, helper =>
            {
                helper.NetworkStats.Reset();
                helper.LogAd(AdLogLevel.Info, "Network stats reset from the overlay.");
            }));
            card.Add(actions);

            return card;
        }

        private void RefreshNetworks(LevelPlayHelper helper)
        {
            var stats = helper.NetworkStats;
            int version = stats.Version;

            // Rebuild on new data, and once a second so the "seen Xs ago" column stays current.
            if (version == _renderedNetworksVersion && Time.unscaledTime < _nextNetworksRebuild)
                return;

            _renderedNetworksVersion = version;
            _nextNetworksRebuild = Time.unscaledTime + NetworksRebuildSeconds;

            var snapshot = stats.Snapshot();
            _networksList.Clear();

            int impressions = 0;
            double revenue = 0d;
            foreach (var network in snapshot)
            {
                impressions += network.Impressions;
                revenue += network.Revenue;
                _networksList.Add(NetworkRow(network));
            }

            _networksTotals.text = snapshot.Count == 0
                ? string.Empty
                : $"{snapshot.Count} network(s)    {impressions} impression(s)    ${revenue:F4} estimated";
            _networksEmpty.style.display = snapshot.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            var last = stats.LastImpressionSummary;
            _lastImpression.text = string.IsNullOrEmpty(last) ? "none yet" : last;
        }

        private static VisualElement NetworkRow(AdNetworkStats network)
        {
            var row = new VisualElement();
            row.style.marginBottom = 4;
            row.style.paddingLeft = 6;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = network.Revenue > 0 ? ColorAccentGreen : ColorAccentCyan;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;

            var name = new Label(network.Network);
            name.style.color = ColorText;
            name.style.fontSize = 12;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.flexGrow = 1;
            header.Add(name);

            var revenue = new Label($"${network.Revenue:F4}");
            revenue.style.color = network.Revenue > 0 ? ColorAccentGreen : ColorTextMuted;
            revenue.style.fontSize = 12;
            revenue.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(revenue);
            row.Add(header);

            var detail = Muted();
            detail.text =
                $"won {network.LoadsWon}    shown {network.Displays}    impressions {network.Impressions}    " +
                $"last {network.LastFormat ?? "-"} / {network.LastInstanceName ?? "-"}" +
                (string.IsNullOrEmpty(network.LastPrecision) ? string.Empty : $" ({network.LastPrecision})") +
                $"    seen {SecondsAgo(network.LastSeenUtc)}";
            row.Add(detail);

            return row;
        }

        private static string SecondsAgo(DateTime? utc) =>
            utc.HasValue ? $"{(DateTime.UtcNow - utc.Value).TotalSeconds:F0}s ago" : "never";

        private static void CopyLastImpression(LevelPlayHelper helper)
        {
            var raw = helper.NetworkStats.LastImpressionRaw;
            if (string.IsNullOrEmpty(raw))
            {
                helper.LogAd(AdLogLevel.Warning, "No impression yet - show an ad first.");
                return;
            }

            try
            {
                GUIUtility.systemCopyBuffer = raw;
                helper.LogAd(AdLogLevel.Info, "Last impression JSON copied to the clipboard.");
            }
            catch (Exception e)
            {
                helper.LogAd(AdLogLevel.Warning, $"Could not copy the impression: {e.Message}");
            }
        }

        #endregion

        #region Test tools card

        private VisualElement BuildTestToolsCard()
        {
            var card = Card();
            card.Add(SectionTitle("TEST TOOLS"));

            _testSuiteLine = Body();
            card.Add(_testSuiteLine);

            var suite = ActionRow();
            suite.style.marginTop = 4;
            _launchTestSuiteButton = ActionButton("Launch Test Suite", ColorAccentOrange, helper => helper.LaunchTestSuite());
            suite.Add(_launchTestSuiteButton);
            _testSuiteNextLaunchButton = ActionButton("Test Suite: next launch", ColorAccentAmber, helper =>
            {
                bool enable = !LevelPlayHelper.TestSuiteOnNextLaunch;
                LevelPlayHelper.TestSuiteOnNextLaunch = enable;
                helper.LogAd(AdLogLevel.Warning, enable
                    ? "Test Suite ENABLED for the next launch - close and reopen the app."
                    : "Test Suite disabled from the next launch.");
            });
            suite.Add(_testSuiteNextLaunchButton);
            card.Add(suite);

            var sdk = ActionRow();
            sdk.Add(ActionButton("Validate Integration", ColorAccentCyan, helper => helper.ValidateIntegration()));
            _adaptersDebugButton = ActionButton("Adapters Debug", ColorHeaderTo,
                helper => helper.SetAdaptersDebug(!LevelPlayHelper.AdaptersDebugEnabled));
            sdk.Add(_adaptersDebugButton);
            card.Add(sdk);

            var hint = Muted();
            hint.text = "Validate Integration and Adapters Debug write to logcat (IntegrationHelper / adapter tags) " +
                        "or the Xcode console. The Test Suite lists every adapter, its SDK version and lets you " +
                        "load/show each network on its own.";
            card.Add(hint);

            return card;
        }

        private void RefreshTestTools(LevelPlayHelper helper)
        {
            bool active = helper.IsTestSuiteActive;
            bool nextLaunch = LevelPlayHelper.TestSuiteOnNextLaunch;
            bool adaptersDebug = LevelPlayHelper.AdaptersDebugEnabled;

            _testSuiteLine.text =
                $"Test Suite this session: {(active ? "ON" : "OFF")}    next launch: {(nextLaunch ? "ON" : "OFF")}    " +
                $"Adapters debug: {(adaptersDebug ? "ON" : "OFF")}";
            _testSuiteLine.style.color = active ? ColorAccentGreen : ColorText;

            _launchTestSuiteButton.SetEnabled(active && helper.IsSdkInitialized);
            _testSuiteNextLaunchButton.text = nextLaunch ? "Disable on next launch" : "Enable on next launch";
            _adaptersDebugButton.text = adaptersDebug ? "Adapters Debug: ON" : "Adapters Debug: OFF";
        }

        #endregion
    }
}
