using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.UI
{
    /// <summary>
    /// In-game runtime debug overlay for inspecting and testing LevelPlay ads, built with
    /// runtime UI Toolkit (<see cref="UIDocument"/> + <see cref="PanelSettings"/>).
    ///
    /// Compared with the old IMGUI panel it gives a real layout (nothing gets clipped), a
    /// scrollable / filterable event log, copy-to-clipboard diagnostics and per-format
    /// "why is this not ready?" detail. Active in the Unity Editor and Development Builds.
    /// </summary>
    [AddComponentMenu("Tools/Wagenheimer/Level Play/Level Play Debug Overlay")]
    [DisallowMultipleComponent]
    public class LevelPlayDebugOverlay : MonoBehaviour
    {
        #region Settings

        [Header("Runtime Access")]
        [Tooltip("Hot key to toggle debug panel visibility in game.")]
        public KeyCode toggleKey = KeyCode.F8;

        [Tooltip("Whether to draw a small floating 'ADS DBG' button on screen.")]
        public bool showFloatingButton = true;

        [Tooltip("Allow overlay to run even in non-development / release builds. Strongly recommended FALSE for production.")]
        public bool enableInReleaseBuilds = false;

        [Header("Scale (mobile-friendly)")]
        [Tooltip("UI scale used automatically on Android/iOS (touch screens need bigger text/buttons than a desktop mouse UI). Adjustable at runtime with the +/- buttons in the panel header.")]
        [Range(1f, 3f)]
        public float mobileDefaultScale = 1.75f;

        [Tooltip("UI scale used on desktop/Editor. Adjustable at runtime with the +/- buttons in the panel header.")]
        [Range(0.75f, 3f)]
        public float desktopDefaultScale = 1f;

        [Tooltip("Sorting order of the overlay panel. Kept very high so it draws above the game UI.")]
        public int sortingOrder = 32760;

        private const float MinScale = 0.75f;
        private const float MaxScale = 3f;
        private const float ScaleStep = 0.25f;
        private const string ScalePrefsKey = "Wagenheimer.LevelPlayDebugOverlay.Scale";
        private const string ThemeResourceName = "LevelPlayDebugTheme";
        private const int MaxRenderedLogLines = 300;
        private const float RefreshIntervalSeconds = 0.25f;
        private const float PanelWidth = 520f;
        private const float PanelHeight = 640f;

        #endregion

        #region Palette

        private static readonly Color ColorBackground = new Color(0.106f, 0.114f, 0.157f, 0.97f);
        private static readonly Color ColorPanel = new Color(0.161f, 0.173f, 0.227f, 1f);
        private static readonly Color ColorPanelBorder = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color ColorHeaderFrom = new Color(0.137f, 0.427f, 0.478f, 1f);
        private static readonly Color ColorHeaderTo = new Color(0.204f, 0.463f, 0.902f, 1f);
        private static readonly Color ColorAccentGreen = new Color(0.298f, 0.851f, 0.392f, 1f);
        private static readonly Color ColorAccentAmber = new Color(0.984f, 0.749f, 0.141f, 1f);
        private static readonly Color ColorAccentRed = new Color(0.937f, 0.325f, 0.314f, 1f);
        private static readonly Color ColorAccentCyan = new Color(0.278f, 0.827f, 0.902f, 1f);
        private static readonly Color ColorAccentOrange = new Color(1f, 0.596f, 0.145f, 1f);
        private static readonly Color ColorTextMuted = new Color(0.62f, 0.65f, 0.71f, 1f);
        private static readonly Color ColorText = new Color(0.92f, 0.93f, 0.95f, 1f);

        #endregion

        #region State

        private bool _isOpen;
        private bool _isMaximized;
        private float _scale = 1f;

        private PanelSettings _panelSettings;
        private ThemeStyleSheet _theme;
        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _floatingButton;
        private VisualElement _panel;

        private VisualElement _diagnosisCard;
        private Label _diagnosisLabel;

        private Pill _sdkPill;
        private Pill _platformPill;
        private Pill _appKeyPill;
        private Label _statusLine;
        private Label _statusMutedLine;

        private FormatRow _rewardedRow;
        private FormatRow _interstitialRow;
        private FormatRow _bannerRow;

        private ScrollView _logScroll;
        private Label _logEmpty;
        private TextField _logFilter;

        private readonly List<AdLogEntry> _renderedLog = new List<AdLogEntry>();
        private int _renderedLogCount = -1;
        private string _renderedFilter;
        private float _nextRefreshTime;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (!Debug.isDebugBuild && !Application.isEditor && !enableInReleaseBuilds)
            {
                Destroy(this);
                return;
            }

            DontDestroyOnLoad(gameObject);

            bool isTouchPlatform = Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer;
            float defaultScale = isTouchPlatform ? mobileDefaultScale : desktopDefaultScale;
            _scale = Mathf.Clamp(PlayerPrefs.GetFloat(ScalePrefsKey, defaultScale), MinScale, MaxScale);

            BuildDocument();
            BuildUI();
        }

        private void OnEnable()
        {
            ApplyVisibility();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                _isOpen = !_isOpen;
                ApplyVisibility();
            }

            if (Time.unscaledTime < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.unscaledTime + RefreshIntervalSeconds;

            // Keep the safe-area insets / scale current even while closed, but only rebuild
            // the (more expensive) panel content while it is actually visible.
            ApplyLayout();

            if (_isOpen)
                Refresh();
        }

        private void OnDestroy()
        {
            if (_document != null)
                _document.panelSettings = null;

            if (_panelSettings != null)
            {
                Destroy(_panelSettings);
                _panelSettings = null;
            }
        }

        #endregion

        #region Panel setup

        /// <summary>
        /// Creates the runtime <see cref="PanelSettings"/> and the <see cref="UIDocument"/>.
        /// Everything is created in code so the package ships without a prefab.
        /// </summary>
        private void BuildDocument()
        {
            _theme = ResolveTheme();
            if (_theme == null)
            {
                Debug.LogError("[LevelPlayDebugOverlay] No ThemeStyleSheet available. The overlay cannot be styled; " +
                               "make sure 'Runtime/UI/Resources/LevelPlayDebugTheme.tss' is imported.");
                return;
            }

            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _panelSettings.name = "LevelPlayDebugOverlayPanelSettings";
            _panelSettings.hideFlags = HideFlags.HideAndDontSave;
            _panelSettings.themeStyleSheet = _theme;
            _panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _panelSettings.scale = _scale;
            _panelSettings.referenceResolution = new Vector2Int(1920, 1080);
            _panelSettings.clearColor = false;
            _panelSettings.sortingOrder = sortingOrder;

            // Create the document on an inactive GameObject so UIDocument does not warn about
            // a missing PanelSettings on its OnEnable.
            var go = new GameObject("LevelPlayDebugOverlayUI");
            go.SetActive(false);
            go.transform.SetParent(transform, false);

            _document = go.AddComponent<UIDocument>();
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = sortingOrder;

            go.SetActive(true);

            _root = _document.rootVisualElement;
        }

        /// <summary>
        /// Resolves the theme shipped with the package, falling back to any theme already
        /// loaded in the project so the overlay still works if the package asset is missing.
        /// </summary>
        private static ThemeStyleSheet ResolveTheme()
        {
            var theme = Resources.Load<ThemeStyleSheet>(ThemeResourceName);
            if (theme != null)
                return theme;

            var all = Resources.FindObjectsOfTypeAll(typeof(ThemeStyleSheet));
            if (all != null && all.Length > 0)
                return all[0] as ThemeStyleSheet;

            var loaded = Resources.LoadAll("", typeof(ThemeStyleSheet));
            if (loaded != null && loaded.Length > 0)
                return loaded[0] as ThemeStyleSheet;

            return null;
        }

        private void ApplyVisibility()
        {
            if (_root == null)
                return;

            // The root must stay visible so the floating button can render when the panel is
            // closed. It ignores picking, so it never swallows input meant for the game.
            _root.style.display = DisplayStyle.Flex;

            if (_panel != null)
                _panel.style.display = _isOpen ? DisplayStyle.Flex : DisplayStyle.None;

            if (_floatingButton != null)
                _floatingButton.style.display = showFloatingButton && !_isOpen ? DisplayStyle.Flex : DisplayStyle.None;

            if (_isOpen)
                Refresh();
        }

        #endregion

        #region UI construction

        private void BuildUI()
        {
            if (_root == null)
                return;

            _root.Clear();
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.top = 0;
            _root.style.right = 0;
            _root.style.bottom = 0;
            _root.pickingMode = PickingMode.Ignore;

            _floatingButton = BuildFloatingButton();
            _root.Add(_floatingButton);

            _panel = BuildPanel();
            _root.Add(_panel);

            ApplyVisibility();
            Refresh();
        }

        private VisualElement BuildFloatingButton()
        {
            var button = new Button(() =>
            {
                _isOpen = true;
                ApplyVisibility();
            })
            {
                text = "ADS DBG"
            };

            button.style.position = Position.Absolute;
            button.style.top = 8;
            button.style.left = Length.Percent(50);
            button.style.translate = new Translate(Length.Percent(-50), 0);
            button.style.paddingLeft = 14;
            button.style.paddingRight = 14;
            button.style.paddingTop = 8;
            button.style.paddingBottom = 8;
            button.style.fontSize = 13;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = Color.white;
            SetBackground(button, ColorHeaderTo);
            SetRadius(button, 6);
            button.style.borderTopWidth = 1;
            button.style.borderBottomWidth = 1;
            button.style.borderLeftWidth = 1;
            button.style.borderRightWidth = 1;
            SetBorderColor(button, new Color(1f, 1f, 1f, 0.25f));

            return button;
        }

        private VisualElement BuildPanel()
        {
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.backgroundColor = ColorBackground;
            SetRadius(panel, 8);
            panel.style.paddingLeft = 10;
            panel.style.paddingRight = 10;
            panel.style.paddingTop = 8;
            panel.style.paddingBottom = 8;
            panel.style.flexDirection = FlexDirection.Column;

            panel.Add(BuildHeader());

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.marginTop = 6;
            panel.Add(scroll);

            var content = scroll.contentContainer;
            content.Add(BuildDiagnosisCard());
            content.Add(BuildStatusCard());
            content.Add(BuildFormatsCard());
            content.Add(BuildActionsCard());
            content.Add(BuildLogCard());

            return panel;
        }

        private VisualElement BuildHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.height = 28;
            header.style.paddingLeft = 8;
            header.style.paddingRight = 4;
            SetBackground(header, ColorHeaderFrom);
            SetRadius(header, 6);

            var title = new Label("LevelPlay - Ads Debug Panel");
            title.style.color = Color.white;
            title.style.fontSize = 14;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            header.Add(title);

            header.Add(HeaderButton("A-", () => SetScale(_scale - ScaleStep)));
            header.Add(HeaderButton("A+", () => SetScale(_scale + ScaleStep)));
            header.Add(HeaderButton("Max", ToggleMaximize));
            header.Add(HeaderButton("Close", () =>
            {
                _isOpen = false;
                ApplyVisibility();
            }));

            return header;
        }

        private static Button HeaderButton(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.height = 20;
            button.style.minWidth = 30;
            button.style.marginLeft = 4;
            button.style.paddingLeft = 6;
            button.style.paddingRight = 6;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            button.style.fontSize = 11;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = Color.white;
            SetBackground(button, new Color(1f, 1f, 1f, 0.16f));
            SetRadius(button, 4);
            return button;
        }

        private VisualElement BuildDiagnosisCard()
        {
            _diagnosisCard = Card();

            _diagnosisLabel = new Label();
            _diagnosisLabel.style.fontSize = 12;
            _diagnosisLabel.style.whiteSpace = WhiteSpace.Normal;
            _diagnosisLabel.style.color = ColorText;
            _diagnosisCard.Add(_diagnosisLabel);

            return _diagnosisCard;
        }

        private VisualElement BuildStatusCard()
        {
            var card = Card();
            card.Add(SectionTitle("STATUS"));

            var pills = new VisualElement();
            pills.style.flexDirection = FlexDirection.Row;
            pills.style.flexWrap = Wrap.Wrap;
            pills.style.marginBottom = 6;
            _sdkPill = new Pill();
            _platformPill = new Pill();
            _appKeyPill = new Pill();
            pills.Add(_sdkPill.Element);
            pills.Add(_platformPill.Element);
            pills.Add(_appKeyPill.Element);
            card.Add(pills);

            _statusLine = Body();
            card.Add(_statusLine);

            _statusMutedLine = Muted();
            card.Add(_statusMutedLine);

            return card;
        }

        private VisualElement BuildFormatsCard()
        {
            var card = Card();
            card.Add(SectionTitle("AD FORMATS"));

            _rewardedRow = new FormatRow("Rewarded", "Show (grants reward)");
            _interstitialRow = new FormatRow("Interstitial", "Show");
            _bannerRow = new FormatRow("Banner", "Show");

            card.Add(_rewardedRow.Element);
            card.Add(_interstitialRow.Element);
            card.Add(_bannerRow.Element);

            return card;
        }

        private VisualElement BuildActionsCard()
        {
            var card = Card();
            card.Add(SectionTitle("ACTIONS & QA TRIGGERS"));

            var row1 = ActionRow();
            row1.Add(ActionButton("Force Init", ColorAccentCyan, helper =>
            {
                helper.Initialize();
                helper.LogAd(AdLogLevel.Info, "Initialize() called from the overlay.");
            }));
            row1.Add(ActionButton("Force Reload", ColorHeaderTo, helper =>
            {
                helper.ForceReloadAds();
                helper.LogAd(AdLogLevel.Info, "ForceReloadAds() called from the overlay.");
            }));
            row1.Add(ActionButton("Test Suite", ColorAccentOrange, helper =>
            {
                helper.LaunchTestSuite();
                helper.LogAd(AdLogLevel.Warning, "LaunchTestSuite() called (requires enableTestSuite before init).");
            }));
            card.Add(row1);

            var row2 = ActionRow();
            row2.Add(ActionButton("Consent ON", ColorAccentGreen, helper =>
            {
                helper.SetUserConsent(true);
                helper.LogAd(AdLogLevel.Info, "SetUserConsent(true) - re-init to apply.");
            }));
            row2.Add(ActionButton("Consent OFF", ColorAccentRed, helper =>
            {
                helper.SetUserConsent(false);
                helper.LogAd(AdLogLevel.Info, "SetUserConsent(false) - re-init to apply.");
            }));
            row2.Add(ActionButton("Try Show Any", ColorAccentCyan, helper =>
            {
                bool shown = helper.TryShowAd(() => helper.LogAd(AdLogLevel.Success, "Reward granted via TryShowAd()."));
                helper.LogAd(shown ? AdLogLevel.Success : AdLogLevel.Warning, $"TryShowAd() => {shown}");
            }));
            card.Add(row2);

            var row3 = ActionRow();
            row3.Add(ActionButton("Copy Report", ColorAccentGreen, helper => helper.CopyDiagnosticReportToClipboard()));
            row3.Add(ActionButton("Copy Log", ColorHeaderTo, CopyLogToClipboard));
            row3.Add(ActionButton("Clear Log", ColorTextMuted, helper =>
            {
                helper.ClearLog();
                _renderedLogCount = -1;
            }));
            card.Add(row3);

            return card;
        }

        private VisualElement BuildLogCard()
        {
            var card = Card();
            card.Add(SectionTitle("EVENT LOG"));

            _logFilter = new TextField("Filter");
            _logFilter.style.marginBottom = 4;
            _logFilter.style.fontSize = 11;
            _logFilter.RegisterValueChangedCallback(_ => _renderedLogCount = -1);
            card.Add(_logFilter);

            _logScroll = new ScrollView(ScrollViewMode.Vertical);
            _logScroll.style.maxHeight = 260;
            _logScroll.style.minHeight = 60;
            card.Add(_logScroll);

            _logEmpty = Muted();
            _logEmpty.text = "No ad events yet.";
            card.Add(_logEmpty);

            return card;
        }

        private static VisualElement ActionRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 4;
            return row;
        }

        private static Button ActionButton(string text, Color color, Action<LevelPlayHelper> onClick)
        {
            var button = new Button(() =>
            {
                var helper = LevelPlayHelper.Instance;
                if (helper == null)
                    return;

                onClick(helper);
            })
            {
                text = text
            };

            button.style.flexGrow = 1;
            button.style.flexBasis = 0;
            button.style.marginLeft = 2;
            button.style.marginRight = 2;
            button.style.paddingTop = 6;
            button.style.paddingBottom = 6;
            button.style.fontSize = 11;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = Color.white;
            SetBackground(button, color);
            SetRadius(button, 4);
            return button;
        }

        #endregion

        #region Refresh

        private void Refresh()
        {
            if (_root == null)
                return;

            ApplyLayout();

            var helper = LevelPlayHelper.Instance;
            if (helper == null)
            {
                _diagnosisLabel.text = "LevelPlayHelper.Instance is null - the helper has not been created yet. " +
                                       "The helper is instantiated on demand by the game's monetization service.";
                _diagnosisLabel.style.color = ColorAccentRed;

                _sdkPill.Set("HELPER MISSING", ColorAccentRed);
                _platformPill.Set("PLATFORM -", ColorTextMuted);
                _appKeyPill.Set("APP KEY -", ColorTextMuted);
                _statusLine.text = string.Empty;
                _statusMutedLine.text = string.Empty;

                _rewardedRow.SetUnavailable();
                _interstitialRow.SetUnavailable();
                _bannerRow.SetUnavailable();

                _logEmpty.style.display = DisplayStyle.Flex;
                _logEmpty.text = "Waiting for LevelPlayHelper...";
                return;
            }

            // ── Diagnosis banner ────────────────────────────────────────────────
            var diagnosis = helper.Diagnose();
            bool failing = helper.InterstitialDiagnostics.State == AdFormatState.Failed
                           || helper.RewardedDiagnostics.State == AdFormatState.Failed
                           || helper.BannerDiagnostics.State == AdFormatState.Failed;

            if (!string.IsNullOrEmpty(diagnosis))
            {
                _diagnosisLabel.text = "WHY NOT LOADING: " + diagnosis;
                _diagnosisLabel.style.color = failing ? ColorAccentRed : ColorAccentAmber;
            }
            else
            {
                _diagnosisLabel.text = "Ads look healthy.";
                _diagnosisLabel.style.color = ColorAccentGreen;
            }

            // ── Status ──────────────────────────────────────────────────────────
            bool sdkReady = helper.IsSdkInitialized;
            bool callbackMissing = helper.InitState == SdkInitState.CallbackMissing;

            _sdkPill.Set(
                sdkReady ? "SDK READY" : callbackMissing ? "SDK CALLBACK MISSING" : "SDK PENDING",
                sdkReady ? ColorAccentGreen : callbackMissing ? ColorAccentOrange : ColorAccentAmber);

            _platformPill.Set(
                helper.IsAdsSupported ? "PLATFORM OK" : "NO ADS HERE",
                helper.IsAdsSupported ? ColorAccentGreen : ColorTextMuted);

            _appKeyPill.Set(
                helper.EffectiveHasAppKey ? (helper.UsesMockAppKey ? "APP KEY: MOCK" : "APP KEY SET") : "NO APP KEY",
                helper.EffectiveHasAppKey ? (helper.UsesMockAppKey ? ColorAccentOrange : ColorAccentCyan) : ColorAccentRed);

            var elapsed = helper.InitElapsedSeconds;
            _statusLine.text =
                $"Platform: {Application.platform}    Init: {helper.InitState}{(elapsed.HasValue ? $" ({elapsed.Value:F0}s)" : string.Empty)}    " +
                $"GDPR: {(helper.ConsentConfig != null && helper.ConsentConfig.enableGDPRConsent ? "on" : "off")}    " +
                $"CCPA: {(helper.ConsentConfig != null && helper.ConsentConfig.ccpaOptOut ? "on" : "off")}    " +
                $"COPPA: {(helper.ConsentConfig != null && helper.ConsentConfig.coppaChildDirected ? "on" : "off")}";

            var muted = new StringBuilder();
            muted.Append("App Key: ").Append(Mask(helper.EffectiveAppKey));
            muted.Append("    Banner: ").Append(helper.IsBannerCreated ? "created" : "not created");
            if (!string.IsNullOrEmpty(helper.LastInitError))
                muted.Append("    Last init error: ").Append(helper.LastInitError);
            _statusMutedLine.text = muted.ToString();

            // ── Formats ─────────────────────────────────────────────────────────
            _rewardedRow.Bind(
                helper.RewardedDiagnostics,
                helper.EffectiveHasRewardedAdUnit,
                helper.IsRewardedAdReady(),
                () => helper.ShowRewardedAd(() => helper.LogAd(AdLogLevel.Success, "Reward granted via the overlay.")));

            _interstitialRow.Bind(
                helper.InterstitialDiagnostics,
                helper.EffectiveHasInterstitialAdUnit,
                helper.IsInterstitialReady(),
                () => helper.ShowInterstitial());

            _bannerRow.Bind(
                helper.BannerDiagnostics,
                helper.EffectiveHasBannerAdUnit,
                helper.IsBannerCreated,
                () => helper.ShowBanner(),
                extraActions: new (string, Action)[]
                {
                    ("Hide", () => helper.HideBanner()),
                    ("Destroy", () => helper.DestroyBanner())
                });

            // ── Log ─────────────────────────────────────────────────────────────
            RefreshLog(helper);
        }

        /// <summary>
        /// Keeps the panel inside the screen and inside the device safe area, and applies the
        /// current scale to the panel settings.
        /// </summary>
        private void ApplyLayout()
        {
            if (_panelSettings != null)
                _panelSettings.scale = _scale;

            float scale = Mathf.Max(MinScale, _scale);
            var safeArea = Screen.safeArea;

            float left = safeArea.xMin / scale;
            float right = (Screen.width - safeArea.xMax) / scale;
            float top = (Screen.height - safeArea.yMax) / scale;
            float bottom = safeArea.yMin / scale;

            _root.style.left = left;
            _root.style.right = right;
            _root.style.top = top;
            _root.style.bottom = bottom;

            if (_panel == null)
                return;

            if (_isMaximized)
            {
                _panel.style.width = StyleKeyword.Auto;
                _panel.style.height = StyleKeyword.Auto;
                _panel.style.left = 0;
                _panel.style.top = 0;
                _panel.style.right = 0;
                _panel.style.bottom = 0;
            }
            else
            {
                _panel.style.left = 0;
                _panel.style.top = 0;
                _panel.style.right = StyleKeyword.Auto;
                _panel.style.bottom = StyleKeyword.Auto;
                _panel.style.width = PanelWidth;
                _panel.style.height = PanelHeight;
                _panel.style.maxWidth = Length.Percent(100);
                _panel.style.maxHeight = Length.Percent(100);
            }
        }

        private void RefreshLog(LevelPlayHelper helper)
        {
            string filter = _logFilter != null ? _logFilter.value : string.Empty;

            if (helper.LogCount == _renderedLogCount && filter == _renderedFilter)
                return;

            _renderedLogCount = helper.LogCount;
            _renderedFilter = filter;

            _renderedLog.Clear();
            var entries = helper.SnapshotLog(MaxRenderedLogLines);

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (!MatchesFilter(entry, filter))
                    continue;

                _renderedLog.Add(entry);
                if (_renderedLog.Count >= MaxRenderedLogLines)
                    break;
            }

            _logScroll.Clear();

            if (_renderedLog.Count == 0)
            {
                _logEmpty.style.display = DisplayStyle.Flex;
                _logEmpty.text = entries.Count == 0 ? "No ad events yet." : "No entries match the filter.";
                return;
            }

            _logEmpty.style.display = DisplayStyle.None;

            foreach (var entry in _renderedLog)
            {
                var line = new Label(entry.ToLine());
                line.style.fontSize = 11;
                line.style.whiteSpace = WhiteSpace.Normal;
                line.style.marginBottom = 1;
                line.style.color = LevelColor(entry.Level);
                _logScroll.Add(line);
            }
        }

        private static bool MatchesFilter(AdLogEntry entry, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;

            return entry.Message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                   || entry.Level.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Color LevelColor(AdLogLevel level)
        {
            switch (level)
            {
                case AdLogLevel.Success: return ColorAccentGreen;
                case AdLogLevel.Warning: return ColorAccentAmber;
                case AdLogLevel.Error: return ColorAccentRed;
                case AdLogLevel.Revenue: return ColorAccentOrange;
                default: return ColorText;
            }
        }

        private void CopyLogToClipboard(LevelPlayHelper helper)
        {
            var sb = new StringBuilder();
            foreach (var entry in helper.SnapshotLog(MaxRenderedLogLines))
                sb.AppendLine(entry.ToLine());

            try
            {
                GUIUtility.systemCopyBuffer = sb.ToString();
                helper.LogAd(AdLogLevel.Info, "Event log copied to the clipboard.");
            }
            catch (Exception e)
            {
                helper.LogAd(AdLogLevel.Warning, $"Could not copy the log: {e.Message}");
            }
        }

        #endregion

        #region Helpers

        private void SetScale(float newScale)
        {
            _scale = Mathf.Clamp(newScale, MinScale, MaxScale);
            PlayerPrefs.SetFloat(ScalePrefsKey, _scale);
            PlayerPrefs.Save();

            if (_panelSettings != null)
                _panelSettings.scale = _scale;

            ApplyLayout();
        }

        private void ToggleMaximize()
        {
            _isMaximized = !_isMaximized;
            ApplyLayout();
        }

        private static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(none)";
            if (value.Length <= 8) return value;
            return value.Substring(0, 4) + "..." + value.Substring(value.Length - 4);
        }

        private static VisualElement Card()
        {
            var card = new VisualElement();
            card.style.backgroundColor = ColorPanel;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.marginBottom = 6;
            SetRadius(card, 6);
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            SetBorderColor(card, ColorPanelBorder);
            return card;
        }

        private static Label SectionTitle(string text)
        {
            var label = new Label(text);
            label.style.color = ColorAccentCyan;
            label.style.fontSize = 12;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 4;
            return label;
        }

        private static Label Body()
        {
            var label = new Label();
            label.style.color = ColorText;
            label.style.fontSize = 12;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private static Label Muted()
        {
            var label = new Label();
            label.style.color = ColorTextMuted;
            label.style.fontSize = 11;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private static void SetBackground(VisualElement element, Color color)
        {
            element.style.backgroundColor = color;
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        #endregion

        #region Nested widgets

        /// <summary>Small colored status chip that can be re-labelled every refresh.</summary>
        private sealed class Pill
        {
            public readonly VisualElement Element;
            private readonly Label _label;

            public Pill()
            {
                Element = new VisualElement();
                Element.style.paddingLeft = 8;
                Element.style.paddingRight = 8;
                Element.style.paddingTop = 3;
                Element.style.paddingBottom = 3;
                Element.style.marginRight = 6;
                Element.style.marginBottom = 4;
                SetRadius(Element, 4);

                _label = new Label();
                _label.style.fontSize = 11;
                _label.style.unityFontStyleAndWeight = FontStyle.Bold;
                _label.style.color = Color.white;
                Element.Add(_label);
            }

            public void Set(string text, Color color)
            {
                _label.text = text;
                Element.style.backgroundColor = new Color(color.r, color.g, color.b, 0.22f);
                _label.style.color = color;
            }
        }

        /// <summary>One ad format block: name, state chip, ad unit id, last error and action buttons.</summary>
        private sealed class FormatRow
        {
            public readonly VisualElement Element;

            private readonly string _format;
            private readonly string _showLabel;
            private readonly Pill _statePill;
            private readonly Label _detail;
            private readonly Label _error;
            private readonly Button _showButton;
            private readonly VisualElement _extraContainer;

            private Action _onShow;

            public FormatRow(string format, string showLabel)
            {
                _format = format;
                _showLabel = showLabel;

                Element = new VisualElement();
                Element.style.marginBottom = 6;

                var header = new VisualElement();
                header.style.flexDirection = FlexDirection.Row;
                header.style.alignItems = Align.Center;

                var name = new Label(format);
                name.style.color = ColorAccentCyan;
                name.style.fontSize = 12;
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.flexGrow = 1;
                header.Add(name);

                _statePill = new Pill();
                header.Add(_statePill.Element);
                Element.Add(header);

                _detail = Muted();
                Element.Add(_detail);

                _error = Muted();
                _error.style.color = ColorAccentRed;
                _error.style.display = DisplayStyle.None;
                Element.Add(_error);

                var buttons = new VisualElement();
                buttons.style.flexDirection = FlexDirection.Row;
                buttons.style.marginTop = 4;

                _showButton = ActionButton(showLabel, ColorHeaderTo, _ => _onShow?.Invoke());
                buttons.Add(_showButton);

                _extraContainer = new VisualElement();
                _extraContainer.style.flexDirection = FlexDirection.Row;
                _extraContainer.style.flexGrow = 1;
                buttons.Add(_extraContainer);

                Element.Add(buttons);
            }

            public void SetUnavailable()
            {
                _statePill.Set("NO HELPER", ColorTextMuted);
                _detail.text = string.Empty;
                _error.style.display = DisplayStyle.None;
                _showButton.SetEnabled(false);
            }

            public void Bind(AdFormatDiagnostics diag, bool configured, bool ready, Action onShow,
                (string label, Action action)[] extraActions = null)
            {
                _onShow = onShow;

                if (!configured)
                    _statePill.Set("NOT CONFIGURED", ColorTextMuted);
                else if (ready)
                    _statePill.Set("READY", ColorAccentGreen);
                else if (diag.IsLoading)
                {
                    var loadingFor = diag.LoadingForSeconds;
                    _statePill.Set(loadingFor.HasValue ? $"LOADING ({loadingFor.Value:F0}s)" : "LOADING...", ColorAccentAmber);
                }
                else if (diag.State == AdFormatState.Failed)
                    _statePill.Set("FAILED", ColorAccentRed);
                else
                    _statePill.Set(diag.State.ToString().ToUpperInvariant(), ColorTextMuted);

                var id = string.IsNullOrEmpty(diag.AdUnitId) ? "(none)" : diag.AdUnitId;
                if (diag.UsesMockId)
                    id += " (MOCK)";

                var detail = new StringBuilder();
                detail.Append("Ad Unit ID: ").Append(id);
                detail.Append("    Retries: ").Append(diag.RetryAttempt);

                var nextRetry = diag.NextRetryInSeconds;
                if (nextRetry.HasValue)
                    detail.Append("    Next retry: ").Append(nextRetry.Value.ToString("F0")).Append('s');

                if (!string.IsNullOrEmpty(diag.LastAdNetwork))
                    detail.Append("    Network: ").Append(diag.LastAdNetwork);
                if (!string.IsNullOrEmpty(diag.LastPlacementName))
                    detail.Append("    Placement: ").Append(diag.LastPlacementName);
                if (diag.LastRevenue.HasValue)
                    detail.Append("    Revenue: $").Append(diag.LastRevenue.Value.ToString("F4"));

                _detail.text = detail.ToString();

                if (!string.IsNullOrEmpty(diag.LastErrorMessage))
                {
                    _error.text = $"Last error {diag.LastErrorCode}: {diag.LastErrorMessage}";
                    _error.style.display = DisplayStyle.Flex;
                }
                else
                {
                    _error.style.display = DisplayStyle.None;
                }

                _showButton.text = _showLabel;
                _showButton.SetEnabled(configured);

                _extraContainer.Clear();
                if (extraActions == null)
                    return;

                foreach (var extra in extraActions)
                {
                    var action = extra.action;
                    _extraContainer.Add(ActionButton(extra.label, ColorAccentAmber, _ => action?.Invoke()));
                }
            }
        }

        #endregion

        #region Factory Method

        /// <summary>
        /// Creates a LevelPlayDebugOverlay GameObject at runtime if one does not already exist.
        /// </summary>
        public static LevelPlayDebugOverlay CreateOverlay()
        {
            var existing = FindObjectOfType<LevelPlayDebugOverlay>();
            if (existing != null) return existing;

            var go = new GameObject("LevelPlayDebugOverlay", typeof(LevelPlayDebugOverlay));
            return go.GetComponent<LevelPlayDebugOverlay>();
        }

        #endregion
    }
}

/// <summary>
/// Global namespace alias for convenience in inspector and prefab references.
/// </summary>
public class LevelPlayDebugOverlay : Wagenheimer.LevelPlayHelper.UI.LevelPlayDebugOverlay
{
}
