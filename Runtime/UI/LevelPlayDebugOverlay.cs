using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.UI
{
    /// <summary>
    /// In-game runtime debug overlay for inspecting and testing LevelPlay ads.
    /// Shows SDK / consent state, per-format readiness (interstitial, rewarded, banner),
    /// load retries and a live ad lifecycle event log, plus manual triggers
    /// (init, reload, show, test suite). Active in the Unity Editor and Development Builds.
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

        private const float MinScale = 0.75f;
        private const float MaxScale = 3f;
        private const float ScaleStep = 0.25f;
        private const string ScalePrefsKey = "Wagenheimer.LevelPlayDebugOverlay.Scale";

        #endregion

        #region Palette

        private static readonly Color ColorBackground = new Color(0.106f, 0.114f, 0.157f, 0.97f);
        private static readonly Color ColorPanel = new Color(0.161f, 0.173f, 0.227f, 1f);
        private static readonly Color ColorHeaderFrom = new Color(0.137f, 0.427f, 0.478f, 1f);
        private static readonly Color ColorHeaderTo = new Color(0.204f, 0.463f, 0.902f, 1f);
        private static readonly Color ColorAccentGreen = new Color(0.298f, 0.851f, 0.392f, 1f);
        private static readonly Color ColorAccentAmber = new Color(0.984f, 0.749f, 0.141f, 1f);
        private static readonly Color ColorAccentRed = new Color(0.937f, 0.325f, 0.314f, 1f);
        private static readonly Color ColorAccentCyan = new Color(0.278f, 0.827f, 0.902f, 1f);
        private static readonly Color ColorAccentOrange = new Color(1f, 0.596f, 0.145f, 1f);
        private static readonly Color ColorTextMuted = new Color(0.62f, 0.65f, 0.71f, 1f);

        #endregion

        #region Private Fields

        private bool _isOpen;
        private Rect _windowRect = new Rect(10, 10, 560, 620);
        private Vector2 _scrollPos;

        // Scale & maximize (logical/pre-scale coordinates — see OnGUI's GUI.matrix wrapping)
        private float _scale = 1f;
        private bool _isMaximized;
        private Rect _preMaximizeRect;

        // Event log
        private readonly List<(string text, Color color)> _eventLog = new List<(string, Color)>();
        private const int MaxLogLines = 16;

        // ILRD fires on a background thread; entries are queued here and drained on the main thread.
        private readonly ConcurrentQueue<(string text, Color color)> _pendingLogs = new ConcurrentQueue<(string, Color)>();

        // Lazily-built GUI skin (must be created inside OnGUI)
        private bool _skinReady;
        private GUIStyle _windowStyle;
        private GUIStyle _headerLabelStyle;
        private GUIStyle _panelStyle;
        private GUIStyle _sectionTitleStyle;
        private GUIStyle _bodyLabelStyle;
        private GUIStyle _mutedLabelStyle;
        private GUIStyle _pillStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _closeButtonStyle;
        private GUIStyle _floatingButtonStyle;
        private GUIStyle _headerButtonStyle;
        private readonly Dictionary<Color, Texture2D> _textureCache = new Dictionary<Color, Texture2D>();

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
        }

        private void OnEnable()
        {
            LevelPlayHelper.OnSdkInitialized += HandleSdkInitialized;
            LevelPlayHelper.OnSdkInitializeFailed += HandleSdkInitializeFailed;
            LevelPlayHelper.OnAdLoaded += HandleAdLoaded;
            LevelPlayHelper.OnAdLoadFailed += HandleAdLoadFailed;
            LevelPlayHelper.OnAdDisplayed += HandleAdDisplayed;
            LevelPlayHelper.OnAdDisplayFailed += HandleAdDisplayFailed;
            LevelPlayHelper.OnRewardedAdGranted += HandleRewardedGranted;
            LevelPlayHelper.OnInterstitialClosed += HandleInterstitialClosed;
            LevelPlayHelper.OnAdRevenuePaid += HandleAdRevenuePaid;
        }

        private void OnDisable()
        {
            LevelPlayHelper.OnSdkInitialized -= HandleSdkInitialized;
            LevelPlayHelper.OnSdkInitializeFailed -= HandleSdkInitializeFailed;
            LevelPlayHelper.OnAdLoaded -= HandleAdLoaded;
            LevelPlayHelper.OnAdLoadFailed -= HandleAdLoadFailed;
            LevelPlayHelper.OnAdDisplayed -= HandleAdDisplayed;
            LevelPlayHelper.OnAdDisplayFailed -= HandleAdDisplayFailed;
            LevelPlayHelper.OnRewardedAdGranted -= HandleRewardedGranted;
            LevelPlayHelper.OnInterstitialClosed -= HandleInterstitialClosed;
            LevelPlayHelper.OnAdRevenuePaid -= HandleAdRevenuePaid;
        }

        private void OnDestroy()
        {
            foreach (var tex in _textureCache.Values)
                if (tex != null) Destroy(tex);
            _textureCache.Clear();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
                _isOpen = !_isOpen;

            while (_pendingLogs.TryDequeue(out var pending))
                Log(pending.text, pending.color);
        }

        private void OnGUI()
        {
            if (!Debug.isDebugBuild && !Application.isEditor && !enableInReleaseBuilds)
                return;

            EnsureSkin();

            GUI.depth = -9999;

            // Scale the whole overlay around the top-left corner. Everything drawn below this point
            // (floating button, window, its contents) must use LOGICAL coordinates — i.e. divided by
            // _scale — since GUI.matrix stretches them back up to real screen pixels.
            var originalMatrix = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(_scale, _scale), Vector2.zero);
            float logicalWidth = Screen.width / _scale;
            float logicalHeight = Screen.height / _scale;

            if (showFloatingButton && !_isOpen)
            {
                if (GUI.Button(new Rect(logicalWidth / 2f - 66f, 5f, 132f, 34f), "▶ ADS DBG", _floatingButtonStyle))
                    _isOpen = true;
            }

            if (_isOpen)
            {
                if (_isMaximized)
                {
                    const float margin = 8f;
                    _windowRect = new Rect(margin, margin, logicalWidth - margin * 2, logicalHeight - margin * 2);
                }
                else
                {
                    // Keep the (movable) window on-screen if the scale/orientation changed since last frame.
                    _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Mathf.Max(0, logicalWidth - 40));
                    _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Mathf.Max(0, logicalHeight - 40));
                }

                _windowRect = GUI.Window(888122, _windowRect, DrawDebugWindow, GUIContent.none, _windowStyle);
            }

            GUI.matrix = originalMatrix;
        }

        #endregion

        #region Event Handlers

        private void HandleSdkInitialized(Unity.Services.LevelPlay.LevelPlayConfiguration _) =>
            Log("SDK INITIALIZED", ColorAccentGreen);

        private void HandleSdkInitializeFailed(string error) =>
            Log($"SDK INIT FAILED: {error}", ColorAccentRed);

        private void HandleAdLoaded(string format) =>
            Log($"{format} LOADED", ColorAccentGreen);

        private void HandleAdLoadFailed(string format, string error) =>
            Log($"{format} LOAD FAILED: {error}", ColorAccentRed);

        private void HandleAdDisplayed(string format) =>
            Log($"{format} DISPLAYED", ColorAccentCyan);

        private void HandleAdDisplayFailed(string format, string error) =>
            Log($"{format} DISPLAY FAILED: {error}", ColorAccentRed);

        private void HandleRewardedGranted() =>
            Log("REWARD GRANTED", ColorAccentGreen);

        private void HandleInterstitialClosed() =>
            Log("INTERSTITIAL CLOSED", ColorAccentCyan);

        // Raised on a background thread - queue it and let Update() append on the main thread.
        private void HandleAdRevenuePaid(string adUnitId, double revenueUsd) =>
            _pendingLogs.Enqueue(($"IMPRESSION: {adUnitId} = ${revenueUsd:F4}", ColorAccentOrange));

        private void Log(string msg, Color color)
        {
            _eventLog.Add(($"[{DateTime.Now:HH:mm:ss}] {msg}", color));
            if (_eventLog.Count > MaxLogLines)
                _eventLog.RemoveAt(0);
        }

        #endregion

        #region GUI Skin

        private Texture2D SolidTexture(Color color)
        {
            if (_textureCache.TryGetValue(color, out var cached) && cached != null)
                return cached;

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _textureCache[color] = tex;
            return tex;
        }

        private Texture2D GradientTexture(Color from, Color to, int width = 64)
        {
            var key = from * 1000f + to;
            if (_textureCache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            var tex = new Texture2D(width, 1, TextureFormat.RGBA32, false);
            for (int x = 0; x < width; x++)
                tex.SetPixel(x, 0, Color.Lerp(from, to, x / (float)(width - 1)));
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _textureCache[key] = tex;
            return tex;
        }

        private void EnsureSkin()
        {
            if (_skinReady) return;
            _skinReady = true;

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                normal = { background = SolidTexture(ColorBackground) },
                onNormal = { background = SolidTexture(ColorBackground) },
                padding = new RectOffset(10, 10, 30, 10),
                border = new RectOffset(6, 6, 26, 6)
            };

            _headerLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 4, 4)
            };

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = SolidTexture(ColorPanel) },
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 6)
            };

            _sectionTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                richText = true,
                normal = { textColor = ColorAccentCyan },
                margin = new RectOffset(2, 2, 2, 4)
            };

            _bodyLabelStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 12,
                normal = { textColor = Color.white },
                wordWrap = true
            };

            _mutedLabelStyle = new GUIStyle(_bodyLabelStyle)
            {
                normal = { textColor = ColorTextMuted },
                fontSize = 11
            };

            _pillStyle = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 3, 3)
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 6, 6)
            };

            _closeButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = SolidTexture(ColorAccentRed) },
                hover = { textColor = Color.white, background = SolidTexture(ColorAccentRed) }
            };

            _floatingButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleCenter
            };

            _headerButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white, background = SolidTexture(new Color(1f, 1f, 1f, 0.15f)) },
                hover = { textColor = Color.white, background = SolidTexture(new Color(1f, 1f, 1f, 0.28f)) }
            };
        }

        private void SetScale(float newScale)
        {
            _scale = Mathf.Clamp(newScale, MinScale, MaxScale);
            PlayerPrefs.SetFloat(ScalePrefsKey, _scale);
            PlayerPrefs.Save();
        }

        private void ToggleMaximize()
        {
            _isMaximized = !_isMaximized;
            if (_isMaximized)
                _preMaximizeRect = _windowRect;
            else
                _windowRect = _preMaximizeRect;
        }

        private void DrawPill(string text, Color color)
        {
            var content = new GUIContent(text);
            var size = _pillStyle.CalcSize(content);
            var rect = GUILayoutUtility.GetRect(size.x, size.y);
            GUI.DrawTexture(rect, SolidTexture(new Color(color.r, color.g, color.b, 0.22f)));
            GUI.Label(rect, text, _pillStyle);
        }

        #endregion

        #region GUI Layout

        private void DrawDebugWindow(int windowId)
        {
            // ── Colorful header bar ──────────────────────────────────────────────
            const float headerHeight = 26f;
            var headerRect = new Rect(0, 0, _windowRect.width, headerHeight);
            GUI.DrawTexture(headerRect, GradientTexture(ColorHeaderFrom, ColorHeaderTo, 128));
            GUI.Label(new Rect(10, 0, _windowRect.width - 210, headerHeight), "▶ LevelPlay — Ads Debug Panel", _headerLabelStyle);

            float x = _windowRect.width - 200;
            if (GUI.Button(new Rect(x, 3, 30, 20), "A-", _headerButtonStyle)) SetScale(_scale - ScaleStep);
            x += 32;
            if (GUI.Button(new Rect(x, 3, 30, 20), "A+", _headerButtonStyle)) SetScale(_scale + ScaleStep);
            x += 34;
            if (GUI.Button(new Rect(x, 3, 34, 20), _isMaximized ? "🗗" : "⛶", _headerButtonStyle)) ToggleMaximize();
            x += 38;
            if (GUI.Button(new Rect(x, 3, 56, 20), "Close ✕", _closeButtonStyle))
            {
                _isOpen = false;
                return;
            }

            GUI.DragWindow(new Rect(0, 0, _windowRect.width - 204, headerHeight));

            GUILayout.Space(6);

            var helper = LevelPlayHelper.Instance;
            if (helper == null)
            {
                GUILayout.BeginVertical(_panelStyle);
                GUILayout.Label("<b><color=#EF5350>LevelPlayHelper.Instance is null — not created yet.</color></b>", _bodyLabelStyle);
                GUILayout.Label("The helper is instantiated on demand by the game's monetization service.", _mutedLabelStyle);
                GUILayout.EndVertical();
                return;
            }

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            DrawStatusCard(helper);
            DrawFormatsCard(helper);
            DrawActionsCard(helper);
            DrawEventLog();

            GUILayout.EndScrollView();
        }

        // ── Status ────────────────────────────────────────────────────────────────

        private void DrawStatusCard(LevelPlayHelper helper)
        {
            GUILayout.BeginVertical(_panelStyle);
            GUILayout.Label("STATUS", _sectionTitleStyle);

            GUILayout.BeginHorizontal();
            DrawPill(helper.IsSdkInitialized ? "● SDK READY" : "● SDK PENDING", helper.IsSdkInitialized ? ColorAccentGreen : ColorAccentAmber);
            GUILayout.Space(6);
            DrawPill(helper.IsAdsSupported ? "PLATFORM OK" : "NO ADS HERE", helper.IsAdsSupported ? ColorAccentGreen : ColorTextMuted);
            GUILayout.Space(6);
            DrawPill(helper.HasAppKey ? "APP KEY SET" : "NO APP KEY", helper.HasAppKey ? ColorAccentCyan : ColorAccentRed);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label($"<b>Platform:</b> {Application.platform}   " +
                            $"<b>GDPR:</b> {(helper.ConsentConfig != null && helper.ConsentConfig.enableGDPRConsent ? "on" : "off")}   " +
                            $"<b>CCPA:</b> {(helper.ConsentConfig != null && helper.ConsentConfig.ccpaOptOut ? "on" : "off")}   " +
                            $"<b>COPPA:</b> {(helper.ConsentConfig != null && helper.ConsentConfig.coppaChildDirected ? "on" : "off")}",
                            _bodyLabelStyle);
            GUILayout.Label($"<b>App Key:</b> {Mask(helper.AppKeyResolved)}   " +
                            $"<b>Banner:</b> {(helper.IsBannerCreated ? "created" : "not created")}",
                            _mutedLabelStyle);

            GUILayout.EndVertical();
        }

        // ── Formats ───────────────────────────────────────────────────────────────

        private void DrawFormatsCard(LevelPlayHelper helper)
        {
            GUILayout.BeginVertical(_panelStyle);
            GUILayout.Label("AD FORMATS", _sectionTitleStyle);

            DrawFormatRow(
                "Rewarded",
                helper.HasRewardedAdUnit,
                helper.IsRewardedAdReady(),
                helper.IsRewardedLoading,
                helper.RewardedRetryAttempt,
                helper.RewardedAdUnitIdResolved,
                showLabel: "▶ Show (grants reward)",
                onShow: () =>
                {
                    helper.ShowRewardedAd(() => Log("Rewarded granted via overlay.", ColorAccentGreen));
                    Log("ShowRewardedAd() called.", ColorHeaderTo);
                });

            DrawFormatRow(
                "Interstitial",
                helper.HasInterstitialAdUnit,
                helper.IsInterstitialReady(),
                helper.IsInterstitialLoading,
                helper.InterstitialRetryAttempt,
                helper.InterstitialAdUnitIdResolved,
                showLabel: "▶ Show",
                onShow: () =>
                {
                    helper.ShowInterstitial();
                    Log("ShowInterstitial() called.", ColorHeaderTo);
                });

            DrawFormatRow(
                "Banner",
                helper.HasBannerAdUnit,
                helper.IsBannerCreated,
                false,
                0,
                helper.BannerAdUnitIdResolved,
                showLabel: "▶ Show",
                onShow: () =>
                {
                    helper.ShowBanner();
                    Log("ShowBanner() called.", ColorHeaderTo);
                },
                extraAction: (label: "Hide", action: () =>
                {
                    helper.HideBanner();
                    Log("HideBanner() called.", ColorAccentAmber);
                }),
                extraAction2: (label: "Destroy", action: () =>
                {
                    helper.DestroyBanner();
                    Log("DestroyBanner() called.", ColorAccentOrange);
                }));

            GUILayout.EndVertical();
        }

        private void DrawFormatRow(
            string format,
            bool configured,
            bool ready,
            bool loading,
            int retries,
            string adUnitId,
            string showLabel,
            Action onShow,
            (string label, Action action)? extraAction = null,
            (string label, Action action)? extraAction2 = null)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b><color=#4FC3F7>{format}</color></b>", _bodyLabelStyle);
            GUILayout.FlexibleSpace();

            if (!configured)
                DrawPill("NOT CONFIGURED", ColorTextMuted);
            else if (ready)
                DrawPill("READY", ColorAccentGreen);
            else if (loading)
                DrawPill("LOADING…", ColorAccentAmber);
            else
                DrawPill("NOT READY", ColorAccentRed);

            GUILayout.EndHorizontal();

            GUILayout.Label($"<b>Ad Unit ID:</b> {(string.IsNullOrEmpty(adUnitId) ? "(none)" : adUnitId)}   " +
                            $"<b>Retries:</b> {retries}",
                            _mutedLabelStyle);

            GUILayout.BeginHorizontal();

            GUI.enabled = configured;
            GUI.backgroundColor = ColorHeaderTo;
            if (GUILayout.Button(showLabel, _buttonStyle)) onShow?.Invoke();

            if (extraAction.HasValue)
            {
                GUI.backgroundColor = ColorAccentAmber;
                if (GUILayout.Button(extraAction.Value.label, _buttonStyle)) extraAction.Value.action?.Invoke();
            }

            if (extraAction2.HasValue)
            {
                GUI.backgroundColor = ColorAccentOrange;
                if (GUILayout.Button(extraAction2.Value.label, _buttonStyle)) extraAction2.Value.action?.Invoke();
            }

            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        // ── Actions ───────────────────────────────────────────────────────────────

        private void DrawActionsCard(LevelPlayHelper helper)
        {
            GUILayout.BeginVertical(_panelStyle);
            GUILayout.Label("ACTIONS & QA TRIGGERS", _sectionTitleStyle);

            GUILayout.BeginHorizontal();
            GUI.backgroundColor = ColorAccentCyan;
            if (GUILayout.Button("⟳ Force Init", _buttonStyle))
            {
                helper.Initialize();
                Log("Initialize() called.", ColorAccentCyan);
            }

            GUI.backgroundColor = ColorHeaderTo;
            if (GUILayout.Button("↻ Force Reload Ads", _buttonStyle))
            {
                helper.ForceReloadAds();
                Log("ForceReloadAds() called.", ColorHeaderTo);
            }

            GUI.backgroundColor = ColorAccentOrange;
            if (GUILayout.Button("🧪 Test Suite", _buttonStyle))
            {
                helper.LaunchTestSuite();
                Log("LaunchTestSuite() called (requires enableTestSuite before init).", ColorAccentOrange);
            }

            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.backgroundColor = ColorAccentGreen;
            if (GUILayout.Button("Consent ON", _buttonStyle))
            {
                helper.SetUserConsent(true);
                Log("SetUserConsent(true) — re-init to apply.", ColorAccentGreen);
            }

            GUI.backgroundColor = ColorAccentRed;
            if (GUILayout.Button("Consent OFF", _buttonStyle))
            {
                helper.SetUserConsent(false);
                Log("SetUserConsent(false) — re-init to apply.", ColorAccentRed);
            }

            GUI.backgroundColor = ColorAccentCyan;
            if (GUILayout.Button("Try Show Any Ad", _buttonStyle))
            {
                bool shown = helper.TryShowAd(() => Log("Reward granted via TryShowAd().", ColorAccentGreen));
                Log($"TryShowAd() => {shown}", shown ? ColorAccentGreen : ColorAccentAmber);
            }

            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        // ── Event Log ─────────────────────────────────────────────────────────────

        private void DrawEventLog()
        {
            GUILayout.BeginVertical(_panelStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("EVENT LOG", _sectionTitleStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", _buttonStyle, GUILayout.Width(70)))
                _eventLog.Clear();
            GUILayout.EndHorizontal();

            if (_eventLog.Count == 0)
            {
                GUILayout.Label("No ad events yet.", _mutedLabelStyle);
            }
            else
            {
                for (int i = _eventLog.Count - 1; i >= 0; i--)
                {
                    var (text, color) = _eventLog[i];
                    var hex = ColorUtility.ToHtmlStringRGB(color);
                    GUILayout.Label($"<color=#{hex}>{text}</color>", _bodyLabelStyle);
                }
            }

            GUILayout.EndVertical();
        }

        private static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(none)";
            if (value.Length <= 8) return value;
            return value.Substring(0, 4) + "…" + value.Substring(value.Length - 4);
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
