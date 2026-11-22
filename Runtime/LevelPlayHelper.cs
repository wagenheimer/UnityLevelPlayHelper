using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using Unity.Services.LevelPlay;

using UnityEngine;

#if UNITY_IOS
using Unity.Advertisement.IosSupport;
#endif

namespace Wagenheimer.LevelPlayHelper
{
    [Serializable]
    public class AdsConfiguration
    {
        [Header("Interval Settings")]
        [Tooltip("Minimum number of checks between ads")]
        public int minAdInterval = 2;

        [Tooltip("Initial check interval before showing ads")]
        public int initialAdInterval = 5;

        [Tooltip("Ads shown needed to reduce the interval")]
        public int adsNeededToReduceInterval = 3;
    }

    public enum BannerPositionPreset
    {
        TopLeft,
        TopCenter,
        TopRight,
        CenterLeft,
        Center,
        CenterRight,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    [Serializable]
    public class ConsentConfiguration
    {
        [Header("GDPR Settings")]
        [Tooltip("Apply GDPR consent flag to the SDK before initialization")]
        public bool enableGDPRConsent = true;

        [Tooltip("CCPA: user opted out of data sale")]
        public bool ccpaOptOut = false;

        [Tooltip("COPPA: child-directed app")]
        public bool coppaChildDirected = false;
    }

    /// <summary>
    /// Reusable Unity LevelPlay (Ads Mediation) manager.
    /// Handles SDK init, consent, interstitial / rewarded / banner lifecycle,
    /// exponential-backoff load retries and impression-level revenue events.
    ///
    /// Setup: add to a persistent GameObject in the first scene and fill in the
    /// App Key + Ad Unit IDs from the LevelPlay dashboard for each platform.
    /// Leave an Ad Unit ID empty to disable that format on that platform.
    /// </summary>
    public class LevelPlayHelper : MonoBehaviour
    {
        #region Singleton

        public static LevelPlayHelper Instance { get; private set; }

        protected virtual void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Prefabs serialized before 2.13.0 have no trackingConfig block.
            if (trackingConfig == null)
                trackingConfig = new TrackingConfiguration();

            trackingState = ReadTrackingState();

            if (enableDebugOverlay && (Application.isEditor || Debug.isDebugBuild))
            {
                EnsureDebugOverlay();
            }
        }

        /// <summary>
        /// Attaches a <see cref="UI.LevelPlayDebugOverlay"/> to this GameObject when
        /// <see cref="enableDebugOverlay"/> is true in the Editor or a Development Build.
        /// </summary>
        private void EnsureDebugOverlay()
        {
            if (GetComponent<UI.LevelPlayDebugOverlay>() == null && FindObjectOfType<UI.LevelPlayDebugOverlay>() == null)
            {
                gameObject.AddComponent<UI.LevelPlayDebugOverlay>();
            }
        }

        #endregion

        #region Public Events

        /// <summary>Raised after LevelPlay.Init succeeds.</summary>
        public static event Action<LevelPlayConfiguration> OnSdkInitialized;

        /// <summary>Raised when LevelPlay.Init fails (error message).</summary>
        public static event Action<string> OnSdkInitializeFailed;

        /// <summary>Raised when an interstitial is dismissed.</summary>
        public static event Action OnInterstitialClosed;

        /// <summary>
        /// Raised when a full-screen ad is dismissed: "Interstitial" or "Rewarded". Use it to
        /// restore anything paused while the ad was on screen (game audio, timers, input).
        /// </summary>
        public static event Action<string> OnAdClosed;

        /// <summary>Raised when a rewarded ad grants its reward.</summary>
        public static event Action OnRewardedAdGranted;

        /// <summary>Raised when an ad format finishes loading: "Interstitial", "Rewarded" or "Banner".</summary>
        public static event Action<string> OnAdLoaded;

        /// <summary>Raised when an ad format fails to load (format, error message).</summary>
        public static event Action<string, string> OnAdLoadFailed;

        /// <summary>Raised when an ad format is displayed (format).</summary>
        public static event Action<string> OnAdDisplayed;

        /// <summary>Raised when an ad format fails to display (format, error message).</summary>
        public static event Action<string, string> OnAdDisplayFailed;

        /// <summary>
        /// Raised when <c>ShowAd()</c> was called but the SDK never reported displayed / failed / closed
        /// within ~50 s of foreground time (format, full diagnostic report). Attach the report to your
        /// crash reporter; <see cref="OnAdDisplayFailed"/> follows with "show timeout".
        /// </summary>
        public static event Action<string, string> OnAdShowTimeout;

        /// <summary>Ad Unit ID + estimated revenue in USD for every paid impression.</summary>
        public static event Action<string, double> OnAdRevenuePaid;

        /// <summary>Raw impression-level revenue data (background thread).</summary>
        public static event Action<LevelPlayImpressionData> OnImpressionDataReady;

        /// <summary>
        /// Optional coroutine shown on iOS before the native ATT alert. Set it before the
        /// helper initializes. The coroutine must only close its own UI; the helper calls the
        /// ATT API when it completes. Use a single "Continue"/"Next" action (App Store 5.1.1(iv)).
        /// </summary>
        public static Func<IEnumerator> PreTrackingPrompt { get; set; }

        /// <summary>
        /// Optional external consent gate. When set and it returns false, <see cref="Initialize"/>
        /// waits (up to <see cref="InitializationGateTimeoutSeconds"/>) before initializing the SDK.
        /// Set it before the helper is created when another package owns the ATT request (for
        /// example com.wagenheimer.tracking), so the SDK never reads the IDFA too early.
        /// </summary>
        public static Func<bool> InitializationGate { get; set; }

        /// <summary>Maximum seconds <see cref="InitializationGate"/> may keep initialization waiting.</summary>
        public static float InitializationGateTimeoutSeconds { get; set; } = 60f;

        /// <summary>Raised once the ATT status is resolved (immediately on non-iOS platforms).</summary>
        public static event Action<TrackingAuthorizationState> OnTrackingAuthorizationCompleted;

        #endregion

        #region Configuration

        [Header("App Key (LevelPlay Dashboard)")]
        [SerializeField] private string androidAppKey = "";
        [SerializeField] private string iosAppKey = "";

        [Header("Ad Unit IDs - Android")]
        [SerializeField] private string androidInterstitialAdUnitId = "";
        [SerializeField] private string androidRewardedAdUnitId = "";
        [SerializeField] private string androidBannerAdUnitId = "";

        [Header("Ad Unit IDs - iOS")]
        [SerializeField] private string iosInterstitialAdUnitId = "";
        [SerializeField] private string iosRewardedAdUnitId = "";
        [SerializeField] private string iosBannerAdUnitId = "";

        [Header("Banner")]
        [SerializeField] private BannerPositionPreset bannerPosition = BannerPositionPreset.BottomCenter;

        [Header("Settings")]
        [SerializeField] private AdsConfiguration adsConfig = new AdsConfiguration();
        [SerializeField] private ConsentConfiguration consentConfig = new ConsentConfiguration();
        [SerializeField] private TrackingConfiguration trackingConfig = new TrackingConfiguration();

        [Header("Testing")]
        [Tooltip("Launches the LevelPlay Test Suite on device builds after init. DISABLE BEFORE RELEASE.")]
        [SerializeField] private bool enableTestSuite = false;

        [Header("Debug")]
        [Tooltip("Automatically attaches the in-game LevelPlayDebugOverlay in the Editor and Development Builds. No effect in release builds.")]
        public bool enableDebugOverlay = true;

        public AdsConfiguration AdsConfig => adsConfig;
        public ConsentConfiguration ConsentConfig => consentConfig;
        public TrackingConfiguration TrackingConfig => trackingConfig;

        #endregion

        #region Core State

        private const string CONSENT_KEY = "UserConsent";
        private const int MaxLoadRetryAttempts = 10;
        private const float BaseRetryDelaySeconds = 2f;
        private const float MaxRetryDelaySeconds = 120f;
        private const float LoadTimeoutSeconds = 45f;
        private const float ShowTimeoutSeconds = 50f;
        private const float OfflineRecheckSeconds = 10f;
        private const float OfflineGiveUpSeconds = 60f;
        private const int MaxInitAttempts = 8;

        /// <summary>Ring-buffer size of the central diagnostic log.</summary>
        private const int MaxLogEntries = 500;

        // Format names used by the ad lifecycle events / debug overlay.
        private const string InterstitialFormat = "Interstitial";
        private const string RewardedFormat = "Rewarded";
        private const string BannerFormat = "Banner";

        private LevelPlayInterstitialAd interstitialAd;
        private LevelPlayRewardedAd rewardedAd;
        private LevelPlayBannerAd bannerAd;

        private bool isSdkInitialized;
        private bool isInterstitialLoading;
        private bool isRewardedLoading;
        private float fullscreenBusyUntil;
        private bool adLoadingRaised;

        /// <summary>
        /// true from the moment a full-screen ad is requested until it is on screen (or failed / closed / 60 s passed),
        /// false afterwards. The SDK can take 30+ s to show an ad on an old device: drive a "Loading ad..." indicator
        /// from this so the game never looks frozen.
        /// </summary>
        public static event Action<bool> OnAdLoadingChanged;
        private const float FullscreenBusyTimeoutSeconds = 30f;
        private const float DeferredLoadDelaySeconds = 3f;
        private int interstitialRetryAttempt;
        private int rewardedRetryAttempt;

        // Bumped whenever a show request ends (displayed / failed / closed) so the matching watchdog stops.
        private int showWatchToken;
        private bool isPaused;
        private DateTime? pausedAtUtc;
        private int initFailureCount;

        private TrackingAuthorizationState trackingState = TrackingAuthorizationState.Unknown;

        private Action onRewardSuccessCallback;

        public bool IsSdkInitialized => isSdkInitialized;

        /// <summary>Current ATT status. <see cref="TrackingAuthorizationState.NotSupported"/> outside iOS.</summary>
        public TrackingAuthorizationState TrackingState => trackingState;

        // SDK init lifecycle. The overlay uses these to explain a stuck initialization
        // (the classic "nothing loads and the log stays empty").
        private SdkInitState initState = SdkInitState.NotStarted;
        private bool waitingForInitializationGate;
        private DateTime? initStartedUtc;
        private string lastInitError;
        private bool adObjectsCreated;

        // Set in the Editor when the SDK init callback never arrives but mock ads are created
        // anyway, so load helpers must not gate on isSdkInitialized only.
        private bool usingEditorMockFallback;

        // Central diagnostic log. Shared by every consumer, survives the overlay being created
        // late, and is safe to write from the ILRD background thread.
        private readonly List<AdLogEntry> diagnosticLog = new List<AdLogEntry>();
        private readonly object diagnosticLogLock = new object();
        private int mainThreadId;

        private readonly AdFormatDiagnostics interstitialDiagnostics = new AdFormatDiagnostics { Format = InterstitialFormat };
        private readonly AdFormatDiagnostics rewardedDiagnostics = new AdFormatDiagnostics { Format = RewardedFormat };
        private readonly AdFormatDiagnostics bannerDiagnostics = new AdFormatDiagnostics { Format = BannerFormat };
        private readonly LevelPlayNetworkStats networkStats = new LevelPlayNetworkStats();

        /// <summary>Per-network (provider) session statistics: auctions won, displays, impressions, revenue.</summary>
        public LevelPlayNetworkStats NetworkStats => networkStats;

        #endregion

        #region Diagnostics

        // Read-only view of the internal state, surfaced for the in-game debug overlay.
        // All of these are safe to poll every frame.

        /// <summary>True while the interstitial is downloading.</summary>
        public bool IsInterstitialLoading => isInterstitialLoading;

        /// <summary>True while the rewarded ad is downloading.</summary>
        public bool IsRewardedLoading => isRewardedLoading;

        /// <summary>Consecutive interstitial load failures (drives the retry backoff).</summary>
        public int InterstitialRetryAttempt => interstitialRetryAttempt;

        /// <summary>Consecutive rewarded load failures (drives the retry backoff).</summary>
        public int RewardedRetryAttempt => rewardedRetryAttempt;

        /// <summary>True when an interstitial Ad Unit ID is configured for the current platform.</summary>
        public bool HasInterstitialAdUnit => !string.IsNullOrEmpty(InterstitialAdUnitId);

        /// <summary>True when a rewarded Ad Unit ID is configured for the current platform.</summary>
        public bool HasRewardedAdUnit => !string.IsNullOrEmpty(RewardedAdUnitId);

        /// <summary>True when a banner Ad Unit ID is configured for the current platform.</summary>
        public bool HasBannerAdUnit => !string.IsNullOrEmpty(BannerAdUnitId);

        /// <summary>True when an App Key is configured for the current platform.</summary>
        public bool HasAppKey => !string.IsNullOrEmpty(AppKey);

        /// <summary>True once the banner object has been created (loaded on demand).</summary>
        public bool IsBannerCreated => bannerAd != null;

        /// <summary>True when the LevelPlay SDK can serve ads on this platform (Editor counts as supported).</summary>
        public bool IsAdsSupported => AdsSupported;

        /// <summary>App Key resolved for the current platform (empty when unset).</summary>
        public string AppKeyResolved => AppKey;

        /// <summary>Interstitial Ad Unit ID resolved for the current platform.</summary>
        public string InterstitialAdUnitIdResolved => InterstitialAdUnitId;

        /// <summary>Rewarded Ad Unit ID resolved for the current platform.</summary>
        public string RewardedAdUnitIdResolved => RewardedAdUnitId;

        /// <summary>Banner Ad Unit ID resolved for the current platform.</summary>
        public string BannerAdUnitIdResolved => BannerAdUnitId;

        // ── Effective / mock-aware credentials ──────────────────────────────────────
        // The raw fields above are what the Inspector holds. In the Editor the helper falls
        // back to mock credentials, so the raw fields can be empty while ads still work.
        // The overlay must report the effective values, otherwise it contradicts the runtime.

        /// <summary>True when the App Key that will actually be used is the Editor mock one.</summary>
        public bool UsesMockAppKey =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(AppKey);
#else
            false;
#endif

        /// <summary>True when the interstitial Editor mock Ad Unit ID is standing in for the real one.</summary>
        public bool UsesMockInterstitialId =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(InterstitialAdUnitId);
#else
            false;
#endif

        /// <summary>True when the rewarded Editor mock Ad Unit ID is standing in for the real one.</summary>
        public bool UsesMockRewardedId =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(RewardedAdUnitId);
#else
            false;
#endif

        /// <summary>True when the banner Editor mock Ad Unit ID is standing in for the real one.</summary>
        public bool UsesMockBannerId =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(BannerAdUnitId);
#else
            false;
#endif

        /// <summary>True when any credential in use is an Editor mock (nothing is really configured).</summary>
        public bool UsesAnyMockCredential =>
            UsesMockAppKey || UsesMockInterstitialId || UsesMockRewardedId || UsesMockBannerId;

        /// <summary>True when an App Key will actually be used (real or Editor mock).</summary>
        public bool EffectiveHasAppKey => !string.IsNullOrEmpty(EffectiveAppKey);

        /// <summary>True when an interstitial Ad Unit ID will actually be used (real or Editor mock).</summary>
        public bool EffectiveHasInterstitialAdUnit => !string.IsNullOrEmpty(EffectiveInterstitialAdUnitId);

        /// <summary>True when a rewarded Ad Unit ID will actually be used (real or Editor mock).</summary>
        public bool EffectiveHasRewardedAdUnit => !string.IsNullOrEmpty(EffectiveRewardedAdUnitId);

        /// <summary>True when a banner Ad Unit ID will actually be used (real or Editor mock).</summary>
        public bool EffectiveHasBannerAdUnit => !string.IsNullOrEmpty(EffectiveBannerAdUnitId);

        /// <summary>The App Key that will actually be used (real or Editor mock).</summary>
        public string EffectiveAppKey =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(AppKey) ? EditorMockAppKey : AppKey;
#else
            AppKey;
#endif

        // ── Init lifecycle ───────────────────────────────────────────────────────────

        /// <summary>Current SDK init lifecycle state.</summary>
        public SdkInitState InitState => initState;

        /// <summary>Seconds since initialization last started (null before the first attempt).</summary>
        public float? InitElapsedSeconds =>
            initStartedUtc.HasValue
                ? (float?)(DateTime.UtcNow - initStartedUtc.Value).TotalSeconds
                : null;

        /// <summary>"code: message" of the last SDK init failure (null when none).</summary>
        public string LastInitError => lastInitError;

        /// <summary>True while ads are served by the Editor mock fallback because the SDK callback never arrived.</summary>
        public bool IsUsingEditorMockFallback => usingEditorMockFallback;

        /// <summary>True once the ad objects have been created (guards against double creation).</summary>
        public bool AreAdObjectsCreated => adObjectsCreated;

        // ── Per-format snapshots ─────────────────────────────────────────────────────

        /// <summary>Diagnostic snapshot for the interstitial format.</summary>
        public AdFormatDiagnostics InterstitialDiagnostics => interstitialDiagnostics;

        /// <summary>Diagnostic snapshot for the rewarded format.</summary>
        public AdFormatDiagnostics RewardedDiagnostics => rewardedDiagnostics;

        /// <summary>Diagnostic snapshot for the banner format.</summary>
        public AdFormatDiagnostics BannerDiagnostics => bannerDiagnostics;

        // ── Log ──────────────────────────────────────────────────────────────────────

        /// <summary>Raised for every diagnostic log entry. Fires on a background thread for ILRD.</summary>
        public static event Action<AdLogEntry> OnDiagnosticLog;

        private bool IsMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;

        /// <summary>
        /// Appends a diagnostic entry, mirrors it to the Unity console and notifies
        /// <see cref="OnDiagnosticLog"/>. Safe to call from any thread.
        /// </summary>
        public void LogAd(AdLogLevel level, string message)
        {
            var entry = new AdLogEntry(DateTime.UtcNow, level, message, !IsMainThread);

            lock (diagnosticLogLock)
            {
                diagnosticLog.Add(entry);
                if (diagnosticLog.Count > MaxLogEntries)
                    diagnosticLog.RemoveRange(0, diagnosticLog.Count - MaxLogEntries);
            }

            try { OnDiagnosticLog?.Invoke(entry); }
            catch (Exception e) { Debug.LogError($"[LevelPlayHelper] Diagnostic log subscriber threw: {e.Message}"); }

            switch (level)
            {
                case AdLogLevel.Error:
                    Debug.LogError($"[LevelPlayHelper] {message}");
                    break;
                case AdLogLevel.Warning:
                    Debug.LogWarning($"[LevelPlayHelper] {message}");
                    break;
                default:
                    Debug.Log($"[LevelPlayHelper] {message}");
                    break;
            }
        }

        /// <summary>Number of buffered diagnostic entries.</summary>
        public int LogCount
        {
            get { lock (diagnosticLogLock) return diagnosticLog.Count; }
        }

        /// <summary>Snapshot of the most recent <paramref name="max"/> entries, oldest first.</summary>
        public List<AdLogEntry> SnapshotLog(int max = 200)
        {
            lock (diagnosticLogLock)
            {
                int take = Mathf.Clamp(max, 0, diagnosticLog.Count);
                return diagnosticLog.GetRange(diagnosticLog.Count - take, take);
            }
        }

        /// <summary>Clears the buffered diagnostic log.</summary>
        public void ClearLog()
        {
            lock (diagnosticLogLock)
                diagnosticLog.Clear();
        }

        // ── Diagnosis ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Single most likely reason ads are not loading right now, or null when the setup
        /// looks healthy. Drives the "why is nothing loading?" banner in the debug overlay.
        /// </summary>
        public string Diagnose()
        {
            if (!AdsSupported)
                return "This platform is not mobile (and not the Editor), so the LevelPlay SDK is disabled entirely.";

            if (initState == SdkInitState.NotStarted)
                return "SDK initialization has not started yet (Initialize() was never reached).";

            if (initState == SdkInitState.WaitingForConsent)
                return "Waiting for the external consent gate (ATT) before initializing the SDK...";

            if (initState == SdkInitState.Initializing)
            {
                if (IsIos && trackingState == TrackingAuthorizationState.NotDetermined)
                    return "Waiting for the App Tracking Transparency answer before initializing the SDK...";

                return $"Waiting for the SDK init callback ({(InitElapsedSeconds ?? 0f):F0}s elapsed)...";
            }

            if (initState == SdkInitState.Failed)
                return $"SDK init failed: {lastInitError}. The helper retries automatically.";

            if (initState == SdkInitState.CallbackMissing && !usingEditorMockFallback)
                return "The SDK init callback never arrived. In the Editor this is harmless (mock ads do not need it); on device check the App Key, network and the LevelPlay dashboard.";

            if (!EffectiveHasAppKey)
                return "No App Key for this platform. Fill it in the Inspector.";

            if (!EffectiveHasInterstitialAdUnit && !EffectiveHasRewardedAdUnit && !EffectiveHasBannerAdUnit)
                return "No Ad Unit ID configured for this platform (interstitial, rewarded and banner are all empty).";

            if (IsAnyAdReady)
                return null;

            var failing = FirstFailingFormat();
            if (failing != null)
                return $"{failing.Format} keeps failing to load: {failing.LastErrorMessage}";

            if (UsesAnyMockCredential)
                return "Running on Editor mock credentials - ads are placeholders and only fill on a device build.";

            if (consentConfig.enableGDPRConsent && PlayerPrefs.GetInt(CONSENT_KEY, 0) != 1)
                return "GDPR consent is not granted, which limits personalized fill.";

            return "No ad ready yet and nothing is failing - loading is probably still in progress.";
        }

        private bool IsAnyAdReady =>
            IsInterstitialReady() || IsRewardedAdReady() || (bannerAd != null);

        private AdFormatDiagnostics FirstFailingFormat()
        {
            if (interstitialDiagnostics.State == AdFormatState.Failed) return interstitialDiagnostics;
            if (rewardedDiagnostics.State == AdFormatState.Failed) return rewardedDiagnostics;
            if (bannerDiagnostics.State == AdFormatState.Failed) return bannerDiagnostics;
            return null;
        }

        /// <summary>
        /// Builds a copy/paste friendly diagnostic report (SDK state, credentials, per-format
        /// state machine, last errors and the log tail) suitable for a bug report.
        /// </summary>
        public string BuildDiagnosticReport(int logLines = 60)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== LevelPlay Helper - diagnostic report ===");
            sb.AppendLine($"Generated (UTC)   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Unity             : {Application.unityVersion}");
            sb.AppendLine($"Platform / device : {Application.platform} / {SystemInfo.deviceModel}");
            sb.AppendLine($"Build             : {(Debug.isDebugBuild ? "development" : "release")}{(Application.isEditor ? " (Editor)" : string.Empty)}");
            sb.AppendLine($"Ads supported     : {AdsSupported}");
            sb.AppendLine();
            sb.AppendLine($"SDK init state    : {initState}");
            sb.AppendLine($"SDK initialized   : {isSdkInitialized}");
            sb.AppendLine($"Init elapsed (s)  : {(InitElapsedSeconds.HasValue ? InitElapsedSeconds.Value.ToString("F1") : "-")}");
            sb.AppendLine($"Mock fallback     : {usingEditorMockFallback}");
            sb.AppendLine($"Last init error   : {lastInitError ?? "-"}");
            sb.AppendLine($"App key           : {Mask(EffectiveAppKey)} [{(UsesMockAppKey ? "MOCK" : EffectiveHasAppKey ? "set" : "MISSING")}]");
            sb.AppendLine($"Consent           : GDPR={(consentConfig.enableGDPRConsent ? "on" : "off")} CCPA={(consentConfig.ccpaOptOut ? "on" : "off")} COPPA={(consentConfig.coppaChildDirected ? "on" : "off")} stored={PlayerPrefs.GetInt(CONSENT_KEY, 0) == 1}");
            sb.AppendLine($"ATT               : {trackingState} (request={(trackingConfig.requestAuthorization ? "on" : "off")}, usage description={(string.IsNullOrWhiteSpace(trackingConfig.usageDescription) ? "MISSING" : "set")})");
            sb.AppendLine($"LevelPlay plugin  : {LevelPlay.PluginVersion}");
            sb.AppendLine($"Test suite        : {IsTestSuiteActive} (inspector={enableTestSuite}, next launch={TestSuiteOnNextLaunch}) adapters debug={AdaptersDebugEnabled}");
            sb.AppendLine($"OS                : {SystemInfo.operatingSystem}");
            sb.AppendLine($"Memory (MB)       : system={SystemInfo.systemMemorySize} gpu={SystemInfo.graphicsMemorySize} managed={GC.GetTotalMemory(false) / (1024 * 1024)}");
            sb.AppendLine($"Network           : {Application.internetReachability}{(wasOffline ? " (deferring loads)" : string.Empty)}");
            sb.AppendLine($"App state         : {(isPaused ? "paused" : "active")}, uptime {Time.realtimeSinceStartup:F0}s");
            sb.AppendLine($"Full-screen busy  : {IsFullscreenBusy} (loading indicator={adLoadingRaised}, pending reward callback={onRewardSuccessCallback != null}, show watch armed={IsShowWatchArmed})");
            sb.AppendLine($"Init failures     : {initFailureCount}");
            sb.AppendLine();
            sb.AppendLine(interstitialDiagnostics.ToSummaryLine(IsInterstitialReady()));
            AppendLastError(sb, interstitialDiagnostics);
            AppendTimeline(sb, interstitialDiagnostics);
            sb.AppendLine(rewardedDiagnostics.ToSummaryLine(IsRewardedAdReady()));
            AppendLastError(sb, rewardedDiagnostics);
            AppendTimeline(sb, rewardedDiagnostics);
            sb.AppendLine(bannerDiagnostics.ToSummaryLine(bannerAd != null));
            AppendLastError(sb, bannerDiagnostics);
            sb.AppendLine();
            sb.AppendLine($"Diagnosis         : {Diagnose() ?? "healthy"}");
            sb.AppendLine();
            networkStats.AppendReport(sb);
            sb.AppendLine();
            sb.AppendLine("--- last events ---");
            foreach (var entry in SnapshotLog(logLines))
                sb.AppendLine(entry.ToLine());
            return sb.ToString();
        }

        private static void AppendLastError(StringBuilder sb, AdFormatDiagnostics diag)
        {
            if (!string.IsNullOrEmpty(diag.LastErrorMessage))
                sb.AppendLine($"    last error: {diag.LastErrorCode} {diag.LastErrorMessage}");
            else if (!string.IsNullOrEmpty(diag.LastAdInfo))
                sb.AppendLine($"    last ad info: {diag.LastAdInfo}");
        }

        private bool IsShowWatchArmed => interstitialDiagnostics.LastShowRequestedUtc.HasValue || rewardedDiagnostics.LastShowRequestedUtc.HasValue;

        private static void AppendTimeline(StringBuilder sb, AdFormatDiagnostics diag)
        {
            if (!diag.LastShowRequestedUtc.HasValue && !diag.LastLoadedAtUtc.HasValue)
                return;

            sb.AppendLine($"    timeline: loaded {Ago(diag.LastLoadedAtUtc)}, show requested {Ago(diag.LastShowRequestedUtc)}, displayed {Ago(diag.LastDisplayedUtc)}, closed {Ago(diag.LastClosedUtc)}");
        }

        private static string Ago(DateTime? utc) =>
            utc.HasValue ? $"{(DateTime.UtcNow - utc.Value).TotalSeconds:F0}s ago" : "never";

        private static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(none)";
            if (value.Length <= 8) return value;
            return value.Substring(0, 4) + "..." + value.Substring(value.Length - 4);
        }

        #endregion

        #region Platform Resolution

        private string AppKey =>
            IsIos ? iosAppKey : androidAppKey;

        private string InterstitialAdUnitId =>
            IsIos ? iosInterstitialAdUnitId : androidInterstitialAdUnitId;

        private string RewardedAdUnitId =>
            IsIos ? iosRewardedAdUnitId : androidRewardedAdUnitId;

        private string BannerAdUnitId =>
            IsIos ? iosBannerAdUnitId : androidBannerAdUnitId;

        private static bool IsIos =>
#if UNITY_IOS && !UNITY_EDITOR
            true;
#else
            false;
#endif

        private static bool IsMobilePlatform =>
            Application.isMobilePlatform;

        /// <summary>
        /// True when the LevelPlay SDK can serve ads here. The SDK serves mock ads inside the
        /// Editor, so Play mode is supported no matter which build target is active.
        /// </summary>
        private static bool AdsSupported =>
#if UNITY_EDITOR
            true;
#else
            IsMobilePlatform;
#endif

#if UNITY_EDITOR
        // Mock ads accept any credential value, so Play mode works with an empty Inspector.
        private const string EditorMockAppKey = "editor-mock-app-key";
        private const string EditorMockInterstitialId = "editor-mock-interstitial";
        private const string EditorMockRewardedId = "editor-mock-rewarded";
        private const string EditorMockBannerId = "editor-mock-banner";
#endif

        /// <summary>Interstitial id actually used, falling back to a mock id in the Editor.</summary>
        private string EffectiveInterstitialAdUnitId =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(InterstitialAdUnitId) ? EditorMockInterstitialId : InterstitialAdUnitId;
#else
            InterstitialAdUnitId;
#endif

        /// <summary>Rewarded id actually used, falling back to a mock id in the Editor.</summary>
        private string EffectiveRewardedAdUnitId =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(RewardedAdUnitId) ? EditorMockRewardedId : RewardedAdUnitId;
#else
            RewardedAdUnitId;
#endif

        /// <summary>Banner id actually used, falling back to a mock id in the Editor.</summary>
        private string EffectiveBannerAdUnitId =>
#if UNITY_EDITOR
            string.IsNullOrEmpty(BannerAdUnitId) ? EditorMockBannerId : BannerAdUnitId;
#else
            BannerAdUnitId;
#endif

        /// <summary>True when ad objects may load: SDK initialized, or the Editor mock fallback is active.</summary>
        private bool CanLoadAds => isSdkInitialized || usingEditorMockFallback;

        #endregion

        #region Initialization

        protected virtual void Start()
        {
            Initialize();
        }

        /// <summary>
        /// Resolves ATT on iOS (optional pre-prompt, then the native alert), applies privacy
        /// settings and initializes the LevelPlay SDK. Safe to call multiple times; a call while
        /// initializing and any call after a successful init are ignored.
        /// </summary>
        public void Initialize()
        {
            if (isSdkInitialized || initState == SdkInitState.Initializing || waitingForInitializationGate)
                return;

            if (InitializationGate != null && !InitializationGate())
            {
                waitingForInitializationGate = true;
                initState = SdkInitState.WaitingForConsent;
                LogAd(AdLogLevel.Info, "Waiting for the external consent gate before initializing LevelPlay...");
                StartCoroutine(WaitForInitializationGateRoutine());
                return;
            }

            if (!AdsSupported)
            {
                LogAd(AdLogLevel.Warning, "Not a mobile platform (and not the Editor) - ads disabled.");
                return;
            }

            var appKey = AppKey;
            if (string.IsNullOrEmpty(appKey))
            {
#if UNITY_EDITOR
                appKey = EditorMockAppKey;
                LogAd(AdLogLevel.Warning, "App Key is empty - initializing with the Editor mock key so Play mode can serve mock ads. Set the real key before building to device.");
#else
                LogAd(AdLogLevel.Error, "App Key is empty. Fill it in the Inspector.");
                return;
#endif
            }

            initState = SdkInitState.Initializing;
            initStartedUtc = DateTime.UtcNow;
            lastInitError = null;

            StartCoroutine(InitializeRoutine(appKey));
        }

        /// <summary>
        /// Waits for <see cref="InitializationGate"/> (bounded) so another consent owner, such as
        /// com.wagenheimer.tracking, can resolve ATT first even if the helper is created early.
        /// </summary>
        private IEnumerator WaitForInitializationGateRoutine()
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, InitializationGateTimeoutSeconds);

            while (InitializationGate != null && !InitializationGate() && Time.realtimeSinceStartup < deadline)
                yield return null;

            waitingForInitializationGate = false;
            initState = SdkInitState.NotStarted;

            if (InitializationGate != null && !InitializationGate())
                LogAd(AdLogLevel.Warning, "The external consent gate did not open in time - initializing LevelPlay anyway.");

            Initialize();
        }

        /// <summary>
        /// ATT must be resolved before LevelPlay.Init(): the SDK reads the advertising
        /// identifier during initialization and cannot recover it later in the session.
        /// </summary>
        private IEnumerator InitializeRoutine(string appKey)
        {
            yield return EnsureTrackingAuthorization();

            ApplyPrivacySettings();

            // Remove before adding: the SDK events are static, so a retry after a failed init
            // would otherwise stack duplicate handlers and fire every callback N times.
            LevelPlay.OnInitSuccess -= OnInitSuccess;
            LevelPlay.OnInitFailed -= OnInitFailed;
            LevelPlay.OnInitSuccess += OnInitSuccess;
            LevelPlay.OnInitFailed += OnInitFailed;

            // Both must be set before Init: the SDK reads them only while initializing.
            testSuiteActive = enableTestSuite || TestSuiteOnNextLaunch;
            if (testSuiteActive)
                LevelPlay.SetMetaData("is_test_suite", "enable");

            if (AdaptersDebugEnabled)
                LevelPlay.SetAdaptersDebug(true);

            LogAd(AdLogLevel.Info, $"Initializing LevelPlay SDK... (app key {Mask(appKey)}, plugin {LevelPlay.PluginVersion}{(testSuiteActive ? ", test suite ON" : string.Empty)}{(AdaptersDebugEnabled ? ", adapters debug ON" : string.Empty)})");
            LevelPlay.Init(appKey);

            StartCoroutine(InitWatchdog());
        }

        private IEnumerator EnsureTrackingAuthorization()
        {
#if UNITY_IOS && !UNITY_EDITOR
            trackingState = ReadTrackingState();

            if (!trackingConfig.requestAuthorization)
            {
                LogAd(AdLogLevel.Info, "ATT request disabled in the helper - the SDK will run without the advertising identifier.");
                NotifyTrackingCompleted();
                yield break;
            }

            if (string.IsNullOrWhiteSpace(trackingConfig.usageDescription))
            {
                LogAd(AdLogLevel.Error, "ATT usage description is empty - skipping the tracking request. Set it in the LevelPlayHelper inspector; the build processor writes it to Info.plist.");
                NotifyTrackingCompleted();
                yield break;
            }

            if (!IsTrackingApiAvailable())
            {
                trackingState = TrackingAuthorizationState.NotSupported;
                LogAd(AdLogLevel.Info, "App Tracking Transparency requires iOS 14.5+ - skipping the tracking request.");
                NotifyTrackingCompleted();
                yield break;
            }

            if (trackingState == TrackingAuthorizationState.NotDetermined)
            {
                if (PreTrackingPrompt != null)
                {
                    LogAd(AdLogLevel.Info, "Showing the pre-tracking prompt...");
                    yield return StartCoroutine(PreTrackingPrompt());
                }

                // Re-read: a project pre-prompt must not call ATT itself, but guard anyway.
                if (ReadTrackingState() == TrackingAuthorizationState.NotDetermined)
                {
                    LogAd(AdLogLevel.Info, "Requesting App Tracking Transparency authorization...");
                    ATTrackingStatusBinding.RequestAuthorizationTracking();

                    // The native alert is answered by the player; wait for the status to change.
                    // 60s is generous and keeps a missing Info.plist key from stalling init forever.
                    float deadline = Time.realtimeSinceStartup + 60f;
                    while (ReadTrackingState() == TrackingAuthorizationState.NotDetermined &&
                           Time.realtimeSinceStartup < deadline)
                    {
                        yield return null;
                    }
                }

                trackingState = ReadTrackingState();
            }

            switch (trackingState)
            {
                case TrackingAuthorizationState.Authorized:
                    LogAd(AdLogLevel.Success, "ATT authorized - the advertising identifier is available.");
                    break;
                case TrackingAuthorizationState.NotDetermined:
                    LogAd(AdLogLevel.Warning, "ATT status is still NotDetermined (missing NSUserTrackingUsageDescription, iOS below 14.5, or the alert was not answered). Continuing without the advertising identifier.");
                    break;
                default:
                    LogAd(AdLogLevel.Warning, $"ATT {trackingState} - ads will run without the advertising identifier.");
                    break;
            }

            NotifyTrackingCompleted();
#else
            trackingState = TrackingAuthorizationState.NotSupported;
            yield break;
#endif
        }

        private static TrackingAuthorizationState ReadTrackingState()
        {
#if UNITY_IOS && !UNITY_EDITOR
            switch (ATTrackingStatusBinding.GetAuthorizationTrackingStatus())
            {
                case ATTrackingStatusBinding.AuthorizationTrackingStatus.NOT_DETERMINED:
                    return TrackingAuthorizationState.NotDetermined;
                case ATTrackingStatusBinding.AuthorizationTrackingStatus.RESTRICTED:
                    return TrackingAuthorizationState.Restricted;
                case ATTrackingStatusBinding.AuthorizationTrackingStatus.DENIED:
                    return TrackingAuthorizationState.Denied;
                case ATTrackingStatusBinding.AuthorizationTrackingStatus.AUTHORIZED:
                    return TrackingAuthorizationState.Authorized;
                default:
                    return TrackingAuthorizationState.Unknown;
            }
#else
            return TrackingAuthorizationState.NotSupported;
#endif
        }

        /// <summary>
        /// ATT exists from iOS 14.5. Older versions report NOT_DETERMINED forever, so asking
        /// would only stall initialization until the polling timeout.
        /// </summary>
        private static bool IsTrackingApiAvailable()
        {
#if UNITY_IOS && !UNITY_EDITOR
            // SystemInfo keeps this independent of the iOS module assembly. The string is
            // typically "iOS 17.4"; scan its tokens for the first major.minor version.
            foreach (var token in SystemInfo.operatingSystem.Split(' '))
            {
                var parts = token.Split('.');
                if (parts.Length < 2)
                    continue;

                if (int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
                    return major > 14 || (major == 14 && minor >= 5);
            }

            return false;
#else
            return false;
#endif
        }

        private void NotifyTrackingCompleted()
        {
            try { OnTrackingAuthorizationCompleted?.Invoke(trackingState); }
            catch (Exception e) { Debug.LogError($"[LevelPlayHelper] ATT completion subscriber threw: {e.Message}"); }
        }

        /// <summary>
        /// Guards against the SDK init callback never arriving. In the Editor the mock ads work
        /// without <c>OnInitSuccess</c>, so the ad objects are created anyway; on device this only
        /// records the stall so the debug overlay can explain why nothing loads.
        /// </summary>
        private IEnumerator InitWatchdog()
        {
            const float timeoutSeconds = 6f;
            yield return new WaitForSecondsRealtime(timeoutSeconds);

            if (isSdkInitialized || initState != SdkInitState.Initializing)
                yield break;

            initState = SdkInitState.CallbackMissing;

#if UNITY_EDITOR
            usingEditorMockFallback = true;
            LogAd(AdLogLevel.Warning, $"SDK init callback did not arrive after {timeoutSeconds:F0}s. Editor mock ads do not need it - creating ad objects anyway.");
            CreateAdObjects();
            LoadAllAds();
#else
            // A slow device or network can take longer than this, and OnInitSuccess still works afterwards,
            // so the first notice is only a warning; a second look 24 s later decides whether it is an error.
            LogAd(AdLogLevel.Warning, $"SDK init callback did not arrive after {timeoutSeconds:F0}s (network={Application.internetReachability}). Still waiting.");
            yield return new WaitForSecondsRealtime(24f);

            if (!isSdkInitialized && initState == SdkInitState.CallbackMissing)
                LogAd(AdLogLevel.Error, "SDK init callback still missing after 30s. Check the App Key, the network connection and the LevelPlay dashboard setup.");
#endif
        }

        private void ApplyPrivacySettings()
        {
            if (!consentConfig.enableGDPRConsent && !consentConfig.ccpaOptOut && !consentConfig.coppaChildDirected)
                return;

            bool hasUserConsent = PlayerPrefs.GetInt(CONSENT_KEY, 0) == 1;

            if (consentConfig.enableGDPRConsent)
            {
                LevelPlayPrivacySettings.SetGDPRConsent(hasUserConsent);
                LogAd(AdLogLevel.Info, $"GDPR consent applied: {hasUserConsent}");
            }

            if (consentConfig.ccpaOptOut)
                LevelPlayPrivacySettings.SetCCPA(true);

            if (consentConfig.coppaChildDirected)
                LevelPlayPrivacySettings.SetCOPPA(true);
        }

        /// <summary>
        /// Updates the stored user consent and re-applies it to the SDK.
        /// Call before Initialize() for it to take effect on this session's init.
        /// </summary>
        public void SetUserConsent(bool hasConsent)
        {
            PlayerPrefs.SetInt(CONSENT_KEY, hasConsent ? 1 : 0);
            PlayerPrefs.Save();
            LevelPlayPrivacySettings.SetGDPRConsent(hasConsent);
        }

        private void OnInitSuccess(LevelPlayConfiguration config)
        {
            if (isSdkInitialized)
                return;

            isSdkInitialized = true;
            initState = SdkInitState.Initialized;
            initFailureCount = 0;
            LogAd(AdLogLevel.Success, $"SDK initialized successfully ({(InitElapsedSeconds ?? 0f):F1}s after Init).");

            CreateAdObjects();
            LoadAllAds();

            Raise(OnSdkInitialized, config, nameof(OnSdkInitialized));

            if (enableTestSuite)
                LevelPlay.LaunchTestSuite();
        }

        private void OnInitFailed(LevelPlayInitError error)
        {
            initState = SdkInitState.Failed;
            initFailureCount++;
            lastInitError = $"{error?.ErrorCode}: {error?.ErrorMessage ?? "no details"}";
            Raise(OnSdkInitializeFailed, error?.ErrorMessage ?? "unknown", nameof(OnSdkInitializeFailed));

            if (initFailureCount >= MaxInitAttempts)
            {
                LogAd(AdLogLevel.Error, $"SDK initialization failed {initFailureCount} times ({lastInitError}). Giving up until the app resumes or Initialize() is called again.");
                return;
            }

            float delay = Mathf.Min(60f, 5f * Mathf.Pow(2f, initFailureCount - 1)) * UnityEngine.Random.Range(0.8f, 1.2f);
            LogAd(AdLogLevel.Warning, $"SDK initialization failed ({lastInitError}). Attempt {initFailureCount}/{MaxInitAttempts}, retrying in {delay:F0}s.");
            CancelInvoke(nameof(Initialize));
            Invoke(nameof(Initialize), delay);
        }

        private void CreateAdObjects()
        {
            if (adObjectsCreated)
                return;

            adObjectsCreated = true;
            StartCoroutine(LoadWatchdogRoutine());

            var interstitialId = EffectiveInterstitialAdUnitId;
            if (!string.IsNullOrEmpty(interstitialId))
            {
                interstitialDiagnostics.Configured = !string.IsNullOrEmpty(InterstitialAdUnitId);
                interstitialDiagnostics.UsesMockId = UsesMockInterstitialId;
                interstitialDiagnostics.AdUnitId = interstitialId;
                interstitialDiagnostics.State = AdFormatState.Idle;

                interstitialAd = new LevelPlayInterstitialAd(interstitialId);

                interstitialAd.OnAdLoaded += OnInterstitialLoaded;
                interstitialAd.OnAdLoadFailed += OnInterstitialLoadFailed;
                interstitialAd.OnAdDisplayed += OnInterstitialDisplayed;
                interstitialAd.OnAdDisplayFailed += OnInterstitialDisplayFailed;
                interstitialAd.OnAdClosed += OnInterstitialClosedInternal;
                interstitialAd.OnAdClicked += OnInterstitialClicked;
                interstitialAd.OnAdInfoChanged += OnInterstitialInfoChanged;
                interstitialAd.OnAdImpressionDataReady += OnImpressionDataReadyInternal;

                LogAd(AdLogLevel.Info, $"Interstitial ad object created ({interstitialId}{(UsesMockInterstitialId ? " - MOCK" : string.Empty)}).");
            }
            else
            {
                interstitialDiagnostics.State = AdFormatState.NotConfigured;
                LogAd(AdLogLevel.Info, "No interstitial ad unit configured for this platform.");
            }

            var rewardedId = EffectiveRewardedAdUnitId;
            if (!string.IsNullOrEmpty(rewardedId))
            {
                rewardedDiagnostics.Configured = !string.IsNullOrEmpty(RewardedAdUnitId);
                rewardedDiagnostics.UsesMockId = UsesMockRewardedId;
                rewardedDiagnostics.AdUnitId = rewardedId;
                rewardedDiagnostics.State = AdFormatState.Idle;

                rewardedAd = new LevelPlayRewardedAd(rewardedId);

                rewardedAd.OnAdLoaded += OnRewardedLoaded;
                rewardedAd.OnAdLoadFailed += OnRewardedLoadFailed;
                rewardedAd.OnAdDisplayed += OnRewardedDisplayed;
                rewardedAd.OnAdDisplayFailed += OnRewardedDisplayFailed;
                rewardedAd.OnAdRewarded += OnRewardedReceived;
                rewardedAd.OnAdClosed += OnRewardedClosed;
                rewardedAd.OnAdClicked += OnRewardedClicked;
                rewardedAd.OnAdInfoChanged += OnRewardedInfoChanged;
                rewardedAd.OnAdImpressionDataReady += OnImpressionDataReadyInternal;

                LogAd(AdLogLevel.Info, $"Rewarded ad object created ({rewardedId}{(UsesMockRewardedId ? " - MOCK" : string.Empty)}).");
            }
            else
            {
                rewardedDiagnostics.State = AdFormatState.NotConfigured;
                LogAd(AdLogLevel.Info, "No rewarded ad unit configured for this platform.");
            }

            bannerDiagnostics.Configured = !string.IsNullOrEmpty(BannerAdUnitId);
            bannerDiagnostics.UsesMockId = UsesMockBannerId;
            bannerDiagnostics.AdUnitId = EffectiveBannerAdUnitId;
            bannerDiagnostics.State = EffectiveHasBannerAdUnit ? AdFormatState.Idle : AdFormatState.NotConfigured;

            LoadAllAds();
        }

        #endregion

        #region Loading

        private bool wasOffline;
        private DateTime? offlineSinceUtc;
        private float nextReadyCheckLogAt;

        /// <summary>LevelPlay error 629: "Load is called while ad is showing". Not a real failure.</summary>
        private const int LoadWhileShowingErrorCode = 629;

        /// <summary>LevelPlay error 509: mediation had no fill. Expected and frequent, not a bug.</summary>
        private const int NoFillErrorCode = 509;

        private void LoadAllAds()
        {
            if (DeferLoadWhileBusy())
                return;

            if (!CanLoadAds || DeferLoadWhileOffline())
                return;

            RequestInterstitialLoad();
            RequestRewardedLoad();
        }

        private void RequestInterstitialLoad()
        {
            if (interstitialAd == null || isInterstitialLoading || IsInterstitialReady())
                return;

            isInterstitialLoading = true;
            interstitialDiagnostics.RetriesExhausted = false;
            MarkLoading(interstitialDiagnostics);

            try
            {
                interstitialAd.LoadAd();
            }
            catch (Exception e)
            {
                isInterstitialLoading = false;
                interstitialRetryAttempt++;
                ScheduleLoadRetry(interstitialDiagnostics, "LoadAdThrew", e.Message, interstitialRetryAttempt, nameof(LoadInterstitialWithRetry));
            }
        }

        private void RequestRewardedLoad()
        {
            if (rewardedAd == null || isRewardedLoading || IsRewardedAdReady())
                return;

            isRewardedLoading = true;
            rewardedDiagnostics.RetriesExhausted = false;
            MarkLoading(rewardedDiagnostics);

            try
            {
                rewardedAd.LoadAd();
            }
            catch (Exception e)
            {
                isRewardedLoading = false;
                rewardedRetryAttempt++;
                ScheduleLoadRetry(rewardedDiagnostics, "LoadAdThrew", e.Message, rewardedRetryAttempt, nameof(LoadRewardedWithRetry));
            }
        }

        /// <summary>
        /// "Loading ad..." indicator: raised from the moment a full-screen ad is requested until it is
        /// on screen (or failed / closed / timed out). A safety timer ends it if no callback arrives.
        /// </summary>
        private void BeginAdLoading()
        {
            CancelInvoke(nameof(EndAdLoading));
            Invoke(nameof(EndAdLoading), FullscreenBusyTimeoutSeconds * 2f);
            if (adLoadingRaised) return;
            adLoadingRaised = true;
            Raise(OnAdLoadingChanged, true, nameof(OnAdLoadingChanged));
        }

        private void EndAdLoading()
        {
            CancelInvoke(nameof(EndAdLoading));
            if (!adLoadingRaised) return;
            adLoadingRaised = false;
            Raise(OnAdLoadingChanged, false, nameof(OnAdLoadingChanged));
        }

        /// <summary>
        /// True while a full-screen ad is on screen (or was just asked to show). LevelPlay rejects loads in that
        /// window with error 629, which used to burn the retry budget and leave the next ad unavailable.
        /// </summary>
        private bool IsFullscreenBusy => Time.unscaledTime < fullscreenBusyUntil;

        private void MarkFullscreenBusy() => fullscreenBusyUntil = Time.unscaledTime + FullscreenBusyTimeoutSeconds;

        private void MarkFullscreenFree() => fullscreenBusyUntil = 0f;

        private bool DeferLoadWhileBusy()
        {
            if (!IsFullscreenBusy)
                return false;

            if (!IsInvoking(nameof(LoadAllAds)))
                Invoke(nameof(LoadAllAds), DeferredLoadDelaySeconds);
            return true;
        }

        /// <summary>
        /// Waits (without burning the retry budget) while the device reports no network. A false
        /// "NotReachable" exists on some Android builds (VPN, captive portals), so after
        /// <see cref="OfflineGiveUpSeconds"/> the load is attempted anyway.
        /// </summary>
        private bool DeferLoadWhileOffline()
        {
            if (Application.internetReachability != NetworkReachability.NotReachable)
            {
                if (wasOffline)
                {
                    wasOffline = false;
                    offlineSinceUtc = null;
                    LogAd(AdLogLevel.Info, "Network is reachable again - resuming ad loads.");
                }

                return false;
            }

            if (!wasOffline)
            {
                wasOffline = true;
                offlineSinceUtc = DateTime.UtcNow;
                LogAd(AdLogLevel.Warning, "No network reachability - ad loads wait until the connection is back.");
            }

            if (offlineSinceUtc.HasValue && (DateTime.UtcNow - offlineSinceUtc.Value).TotalSeconds > OfflineGiveUpSeconds)
                return false;

            if (!IsInvoking(nameof(LoadAllAds)))
                Invoke(nameof(LoadAllAds), OfflineRecheckSeconds);
            return true;
        }

        private void LoadInterstitialWithRetry()
        {
            if (!CanLoadAds || interstitialAd == null || isInterstitialLoading || DeferLoadWhileBusy() || DeferLoadWhileOffline())
                return;

            RequestInterstitialLoad();
        }

        private void LoadRewardedWithRetry()
        {
            if (!CanLoadAds || rewardedAd == null || isRewardedLoading || DeferLoadWhileBusy() || DeferLoadWhileOffline())
                return;

            RequestRewardedLoad();
        }

        /// <summary>Exponential backoff (2, 4, 8 ... capped) with +-20% jitter so devices do not retry in lockstep.</summary>
        private static float GetRetryDelay(int attempt)
        {
            float delay = Mathf.Min(MaxRetryDelaySeconds, BaseRetryDelaySeconds * Mathf.Pow(2f, Mathf.Max(0, attempt - 1)));
            return delay * UnityEngine.Random.Range(0.8f, 1.2f);
        }

        /// <summary>
        /// Records a failed load and schedules the next attempt, or pauses loading once the retry
        /// budget is spent (resumed by a show request, an app resume or <see cref="ForceReloadAds"/>).
        /// Load failures are logged as warnings: no-fill is routine and must not become a crash-reporter event.
        /// </summary>
        private void ScheduleLoadRetry(AdFormatDiagnostics diag, string errorCode, string errorMessage, int attempt, string retryMethod)
        {
            bool exhausted = attempt >= MaxLoadRetryAttempts;
            float delay = exhausted ? 0f : GetRetryDelay(attempt);
            MarkLoadFailed(diag, errorCode, errorMessage, delay);

            string noFillTag = errorCode == NoFillErrorCode.ToString() ? " [no fill]" : string.Empty;
            string reason = $"{errorCode ?? "?"}: {errorMessage ?? "no details"}{noFillTag}";

            if (exhausted)
            {
                diag.RetriesExhausted = true;
                diag.NextRetryAtUtc = null;
                LogAd(AdLogLevel.Warning, $"{diag.Format} LOAD FAILED ({reason}), attempt {attempt}. Retry budget spent - loads paused until the player asks for an ad, the app resumes or ForceReloadAds().");
                return;
            }

            LogAd(AdLogLevel.Warning, $"{diag.Format} LOAD FAILED ({reason}). Retry {attempt}/{MaxLoadRetryAttempts} in {delay:F0}s.");

            CancelInvoke(retryMethod);
            Invoke(retryMethod, delay);
        }

        /// <summary>Error 629 means a load collided with an on-screen ad: wait for it to close, do not count a failure.</summary>
        private bool HandleLoadWhileShowing(AdFormatDiagnostics diag, LevelPlayAdError error)
        {
            if (error == null || error.ErrorCode != LoadWhileShowingErrorCode)
                return false;

            diag.IsLoading = false;
            diag.LoadingSinceUtc = null;
            if (diag.State == AdFormatState.Loading)
                diag.State = AdFormatState.Idle;

            LogAd(AdLogLevel.Info, $"{diag.Format} load rejected (629): a full-screen ad is on screen - retrying once it closes.");
            MarkFullscreenBusy();
            DeferLoadWhileBusy();
            return true;
        }

        /// <summary>
        /// The SDK occasionally never answers a load (no loaded / failed callback). Without this the
        /// "loading" flag stays true forever and that format never loads again.
        /// </summary>
        private IEnumerator LoadWatchdogRoutine()
        {
            var wait = new WaitForSecondsRealtime(5f);

            while (true)
            {
                yield return wait;

                if (isPaused || IsFullscreenBusy)
                    continue;

                if (isInterstitialLoading && IsLoadStuck(interstitialDiagnostics))
                {
                    isInterstitialLoading = false;
                    interstitialDiagnostics.LoadTimeouts++;
                    interstitialRetryAttempt++;
                    ScheduleLoadRetry(interstitialDiagnostics, "LoadTimeout", $"no callback within {LoadTimeoutSeconds:0}s", interstitialRetryAttempt, nameof(LoadInterstitialWithRetry));
                }

                if (isRewardedLoading && IsLoadStuck(rewardedDiagnostics))
                {
                    isRewardedLoading = false;
                    rewardedDiagnostics.LoadTimeouts++;
                    rewardedRetryAttempt++;
                    ScheduleLoadRetry(rewardedDiagnostics, "LoadTimeout", $"no callback within {LoadTimeoutSeconds:0}s", rewardedRetryAttempt, nameof(LoadRewardedWithRetry));
                }
            }
        }

        private static bool IsLoadStuck(AdFormatDiagnostics diag)
        {
            var seconds = diag.LoadingForSeconds;
            return seconds.HasValue && seconds.Value > LoadTimeoutSeconds;
        }

        // ── Diagnostics bookkeeping (kept out of the callbacks for readability) ──────

        private static void MarkLoading(AdFormatDiagnostics diag)
        {
            diag.IsLoading = true;
            diag.LoadingSinceUtc = DateTime.UtcNow;
            diag.State = AdFormatState.Loading;
        }

        private void MarkLoaded(AdFormatDiagnostics diag, LevelPlayAdInfo adInfo)
        {
            diag.IsLoading = false;
            diag.LoadingSinceUtc = null;
            diag.RetryAttempt = 0;
            diag.RetriesExhausted = false;
            diag.NextRetryAtUtc = null;
            diag.LastErrorCode = null;
            diag.LastErrorMessage = null;
            StoreAdInfo(diag, adInfo);
            diag.LastLoadedAtUtc = DateTime.UtcNow;
            diag.LoadsSucceeded++;
            diag.State = AdFormatState.Ready;
            networkStats.RecordLoad(diag.Format, adInfo);
        }

        private static void MarkLoadFailed(AdFormatDiagnostics diag, string errorCode, string errorMessage, float retryDelaySeconds)
        {
            diag.IsLoading = false;
            diag.LoadingSinceUtc = null;
            diag.RetryAttempt++;
            diag.LastErrorCode = errorCode;
            diag.LastErrorMessage = errorMessage;
            diag.LastErrorAtUtc = DateTime.UtcNow;
            diag.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(retryDelaySeconds);
            diag.LoadsFailed++;
            diag.State = AdFormatState.Failed;
        }

        private void MarkDisplayed(AdFormatDiagnostics diag, LevelPlayAdInfo adInfo)
        {
            StoreAdInfo(diag, adInfo);
            networkStats.RecordDisplay(diag.Format, adInfo);
            var now = DateTime.UtcNow;
            diag.LastDisplayedUtc = now;
            diag.ShowsDisplayed++;
            if (diag.LastShowRequestedUtc.HasValue)
                diag.LastDisplayLatencySeconds = (float)(now - diag.LastShowRequestedUtc.Value).TotalSeconds;
            diag.State = AdFormatState.Showing;
        }

        /// <summary>Captures network / placement / revenue from an <c>LevelPlayAdInfo</c>.</summary>
        private static void StoreAdInfo(AdFormatDiagnostics diag, LevelPlayAdInfo adInfo)
        {
            if (adInfo == null)
                return;

            diag.LastAdInfo = adInfo.ToString();
            diag.LastAdNetwork = adInfo.AdNetwork;
            diag.LastPlacementName = adInfo.PlacementName;
            diag.LastRevenue = adInfo.Revenue;
        }

        #endregion

        #region Public Ad Methods

        public bool IsInterstitialReady()
        {
            if (!CanLoadAds || interstitialAd == null)
                return false;

            try { return interstitialAd.IsAdReady(); }
            catch (Exception e) { LogReadyCheckFailure(InterstitialFormat, e); return false; }
        }

        public bool IsRewardedAdReady()
        {
            if (!CanLoadAds || rewardedAd == null)
                return false;

            try { return rewardedAd.IsAdReady(); }
            catch (Exception e) { LogReadyCheckFailure(RewardedFormat, e); return false; }
        }

        /// <summary>IsAdReady() is polled every frame by some UIs: log a native failure at most every 30 s.</summary>
        private void LogReadyCheckFailure(string format, Exception e)
        {
            if (Time.unscaledTime < nextReadyCheckLogAt)
                return;

            nextReadyCheckLogAt = Time.unscaledTime + 30f;
            LogAd(AdLogLevel.Error, $"{format} IsAdReady() threw: {e.Message}");
        }

        public void ShowInterstitial()
        {
            if (!IsInterstitialReady())
            {
                LogAd(AdLogLevel.Warning, $"Interstitial not ready yet (state={interstitialDiagnostics.State}).");
                LoadInterstitialWithRetry();
                return;
            }

            StartShow(InterstitialFormat, interstitialDiagnostics, () => interstitialAd.ShowAd());
        }

        /// <summary>
        /// Shows a rewarded ad granting <paramref name="onReward"/> on success.
        /// Falls back to an interstitial, then to granting directly when nothing
        /// is available so the player never gets blocked.
        /// </summary>
        public void ShowRewardedAd(Action onReward)
        {
            if (IsRewardedAdReady())
            {
                if (onRewardSuccessCallback != null)
                    LogAd(AdLogLevel.Warning, "A previous rewarded request was still pending - its callback is replaced.");

                onRewardSuccessCallback = onReward;
                StartShow(RewardedFormat, rewardedDiagnostics, () => rewardedAd.ShowAd());
            }
            else if (IsInterstitialReady())
            {
                LogAd(AdLogLevel.Warning, $"Rewarded not ready (state={rewardedDiagnostics.State}). Showing interstitial and granting reward.");

                if (StartShow(InterstitialFormat, interstitialDiagnostics, () => interstitialAd.ShowAd()))
                    Raise(onReward, "reward callback");

                // The rewarded reload is deferred until the interstitial closes (see DeferLoadWhileBusy).
                LoadRewardedWithRetry();
            }
            else
            {
                LogAd(AdLogLevel.Warning, $"No ads available (rewarded={rewardedDiagnostics.State}, interstitial={interstitialDiagnostics.State}). Granting reward directly.");
                Raise(onReward, "reward callback");
                LoadRewardedWithRetry();
            }
        }

        /// <summary>
        /// Tries to show any available ad, prioritizing rewarded. Returns false when
        /// nothing is available.
        /// </summary>
        public bool TryShowAd(Action onRewardGranted = null)
        {
            if (IsRewardedAdReady())
            {
                ShowRewardedAd(onRewardGranted);
                return true;
            }

            if (IsInterstitialReady())
            {
                ShowInterstitial();
                return true;
            }

            LogAd(AdLogLevel.Warning, "No ad available (rewarded or interstitial).");
            return false;
        }

        /// <summary>
        /// Marks the format busy, records the request, arms the show watchdog and calls the SDK.
        /// Returns false (after raising <see cref="OnAdDisplayFailed"/>) when the native call threw.
        /// </summary>
        private bool StartShow(string format, AdFormatDiagnostics diag, Action show)
        {
            MarkFullscreenBusy();
            BeginAdLoading();

            diag.ShowsRequested++;
            diag.LastShowRequestedUtc = DateTime.UtcNow;
            diag.LastDisplayLatencySeconds = null;
            LogAd(AdLogLevel.Info, $"{format} SHOW requested (state={diag.State}, adUnit={diag.AdUnitId}, lastNetwork={diag.LastAdNetwork ?? "-"}, net={Application.internetReachability}, paused={isPaused}).");

            StopShowWatch();
            StartCoroutine(ShowWatchdogRoutine(format, diag, showWatchToken));

            try
            {
                show();
                return true;
            }
            catch (Exception e)
            {
                FailShow(format, diag, $"ShowAd() threw: {e.Message}");
                return false;
            }
        }

        /// <summary>Cancels the pending show watchdog (displayed / failed / closed arrived).</summary>
        private void StopShowWatch() => showWatchToken++;

        private void FailShow(string format, AdFormatDiagnostics diag, string message)
        {
            StopShowWatch();
            diag.State = AdFormatState.Failed;
            diag.LastErrorCode = "ShowFailed";
            diag.LastErrorMessage = message;
            diag.LastErrorAtUtc = DateTime.UtcNow;

            LogAd(AdLogLevel.Error, $"{format} SHOW FAILED: {message}");
            MarkFullscreenFree();
            EndAdLoading();

            if (format == RewardedFormat)
                onRewardSuccessCallback = null;

            Raise(OnAdDisplayFailed, format, message, nameof(OnAdDisplayFailed));
            ScheduleReloadSoon();
        }

        private void ScheduleReloadSoon()
        {
            CancelInvoke(nameof(LoadAllAds));
            Invoke(nameof(LoadAllAds), 5f);
        }

        /// <summary>
        /// ShowAd() was called and the SDK reported neither displayed, failed nor closed. Counts only
        /// foreground time, so a long ad (app paused behind it) is never mistaken for a hang.
        /// </summary>
        private IEnumerator ShowWatchdogRoutine(string format, AdFormatDiagnostics diag, int token)
        {
            float waited = 0f;

            while (waited < ShowTimeoutSeconds)
            {
                yield return null;

                if (token != showWatchToken)
                    yield break;

                if (!isPaused)
                    waited += Mathf.Min(1f, Time.unscaledDeltaTime);
            }

            if (token != showWatchToken)
                yield break;

            OnShowTimedOut(format, diag);
        }

        private void OnShowTimedOut(string format, AdFormatDiagnostics diag)
        {
            StopShowWatch();
            diag.ShowTimeouts++;
            diag.State = AdFormatState.Failed;
            diag.LastErrorCode = "ShowTimeout";
            diag.LastErrorMessage = $"no displayed/failed/closed callback within {ShowTimeoutSeconds:0}s";
            diag.LastErrorAtUtc = DateTime.UtcNow;

            string report = BuildDiagnosticReport(25);

            // The full snapshot goes out as an Info line (a crash reporter keeps it as a breadcrumb) and the
            // Error line stays constant so every occurrence groups into one issue.
            LogAd(AdLogLevel.Info, "Show timeout snapshot:\n" + report);
            // Raised before the Error line so a subscriber can attach the report to the scope the error event captures.
            Raise(OnAdShowTimeout, format, report, nameof(OnAdShowTimeout));
            LogAd(AdLogLevel.Error, $"{format} ShowAd() gave no callback within {ShowTimeoutSeconds:0}s - giving up.");

            MarkFullscreenFree();
            EndAdLoading();

            if (format == RewardedFormat)
                onRewardSuccessCallback = null;

            Raise(OnAdDisplayFailed, format, "show timeout", nameof(OnAdDisplayFailed));
            ScheduleReloadSoon();
        }

        /// <summary>
        /// Clears the loading flags and requests a fresh load of every configured ad format.
        /// Intended for the in-game debug overlay (e.g. after a stuck load or a network change).
        /// </summary>
        public void ForceReloadAds()
        {
            MarkFullscreenFree();
            CancelLoadRetries();
            isInterstitialLoading = false;
            isRewardedLoading = false;
            interstitialDiagnostics.IsLoading = false;
            rewardedDiagnostics.IsLoading = false;
            ResetRetryBudget();
            LoadAllAds();
        }

        /// <summary>Forgets the failure history so a fresh round of backoff retries can start.</summary>
        private void ResetRetryBudget()
        {
            interstitialRetryAttempt = 0;
            rewardedRetryAttempt = 0;
            interstitialDiagnostics.RetryAttempt = 0;
            rewardedDiagnostics.RetryAttempt = 0;
            interstitialDiagnostics.RetriesExhausted = false;
            rewardedDiagnostics.RetriesExhausted = false;
        }

        private void CancelLoadRetries()
        {
            CancelInvoke(nameof(LoadAllAds));
            CancelInvoke(nameof(LoadInterstitialWithRetry));
            CancelInvoke(nameof(LoadRewardedWithRetry));
            CancelInvoke(nameof(BannerRetryLoad));
        }

        private const string TestSuitePrefKey = "Wagenheimer.LevelPlayHelper.TestSuiteNextLaunch";
        private const string AdaptersDebugPrefKey = "Wagenheimer.LevelPlayHelper.AdaptersDebug";
        private bool testSuiteActive;

        /// <summary>
        /// True when this session initialized with the Test Suite enabled (Inspector flag or
        /// <see cref="TestSuiteOnNextLaunch"/>). Only then can <see cref="LaunchTestSuite"/> open it.
        /// </summary>
        public bool IsTestSuiteActive => testSuiteActive;

        /// <summary>
        /// Enables the Test Suite on the next app launch without a rebuild. The SDK only accepts the
        /// "is_test_suite" flag before Init, so the change applies after a restart. Stored in PlayerPrefs.
        /// </summary>
        public static bool TestSuiteOnNextLaunch
        {
            get => PlayerPrefs.GetInt(TestSuitePrefKey, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(TestSuitePrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Verbose adapter logs (logcat / Xcode console). Stored in PlayerPrefs and applied before Init.</summary>
        public static bool AdaptersDebugEnabled => PlayerPrefs.GetInt(AdaptersDebugPrefKey, 0) == 1;

        /// <summary>Turns verbose adapter logging on or off now and for the next launches.</summary>
        public void SetAdaptersDebug(bool enabled)
        {
            PlayerPrefs.SetInt(AdaptersDebugPrefKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            LevelPlay.SetAdaptersDebug(enabled);
            LogAd(AdLogLevel.Info, $"Adapters debug {(enabled ? "ON" : "OFF")} (fully applies from the next launch).");
        }

        /// <summary>
        /// Opens the LevelPlay Test Suite on device. Works only when <see cref="IsTestSuiteActive"/>;
        /// otherwise enable <see cref="TestSuiteOnNextLaunch"/> and restart the app.
        /// </summary>
        public void LaunchTestSuite()
        {
            if (!testSuiteActive)
            {
                LogAd(AdLogLevel.Warning, "Test Suite is not active in this session - enable it for the next launch and restart the app.");
                return;
            }

            LevelPlay.LaunchTestSuite();
        }

        /// <summary>
        /// Runs the SDK integration check. The result (adapters, SDK versions, manifest/plist) goes to
        /// logcat on Android (tag "IntegrationHelper") and to the Xcode console on iOS.
        /// </summary>
        public void ValidateIntegration()
        {
            LevelPlay.ValidateIntegration();
            LogAd(AdLogLevel.Info, "ValidateIntegration() called - read the result in logcat (tag IntegrationHelper) or the Xcode console.");
        }

        /// <summary>
        /// Copies <see cref="BuildDiagnosticReport"/> to the system clipboard. No-op on
        /// platforms without a clipboard.
        /// </summary>
        public void CopyDiagnosticReportToClipboard()
        {
            try
            {
                GUIUtility.systemCopyBuffer = BuildDiagnosticReport();
                LogAd(AdLogLevel.Info, "Diagnostic report copied to the clipboard.");
            }
            catch (Exception e)
            {
                LogAd(AdLogLevel.Warning, $"Could not copy the report to the clipboard: {e.Message}");
            }
        }

        #endregion

        #region Banner

        public void CreateBanner()
        {
            if (!CanLoadAds || bannerAd != null || string.IsNullOrEmpty(EffectiveBannerAdUnitId))
                return;

            var configBuilder = new LevelPlayBannerAd.Config.Builder();
            configBuilder.SetPosition(ToLevelPlayBannerPosition(bannerPosition));
            configBuilder.SetDisplayOnLoad(true);
            configBuilder.SetRespectSafeArea(true);

            bannerAd = new LevelPlayBannerAd(EffectiveBannerAdUnitId, configBuilder.Build());

            bannerAd.OnAdLoaded += OnBannerLoaded;
            bannerAd.OnAdLoadFailed += OnBannerLoadFailed;
            bannerAd.OnAdDisplayed += OnBannerDisplayed;
            bannerAd.OnAdDisplayFailed += OnBannerDisplayFailed;
            bannerAd.OnAdClicked += OnBannerClicked;
            bannerAd.OnAdExpanded += OnBannerExpanded;
            bannerAd.OnAdCollapsed += OnBannerCollapsed;
            bannerAd.OnAdLeftApplication += OnBannerLeftApplication;
            bannerAd.OnAdImpressionDataReady += OnImpressionDataReadyInternal;

            MarkLoading(bannerDiagnostics);
            LogAd(AdLogLevel.Info, $"Banner ad object created ({EffectiveBannerAdUnitId}{(UsesMockBannerId ? " - MOCK" : string.Empty)}).");
            bannerAd.LoadAd();
        }

        public void ShowBanner()
        {
            if (bannerAd == null)
            {
                CreateBanner();
                return;
            }

            bannerAd.ShowAd();
        }

        public void HideBanner()
        {
            bannerAd?.HideAd();
        }

        public void DestroyBanner()
        {
            if (bannerAd == null)
                return;

            UnsubscribeBannerEvents();
            bannerAd.DestroyAd();
            bannerAd = null;

            bannerDiagnostics.IsLoading = false;
            bannerDiagnostics.LoadingSinceUtc = null;
            bannerDiagnostics.State = EffectiveHasBannerAdUnit ? AdFormatState.Idle : AdFormatState.NotConfigured;
        }

        #endregion

        #region Interstitial Callbacks

        private void OnInterstitialLoaded(LevelPlayAdInfo adInfo)
        {
            isInterstitialLoading = false;
            interstitialRetryAttempt = 0;
            MarkLoaded(interstitialDiagnostics, adInfo);
            LogAd(AdLogLevel.Success, $"Interstitial LOADED ({adInfo?.AdNetwork ?? "?"}).");
            Raise(OnAdLoaded, InterstitialFormat, nameof(OnAdLoaded));
        }

        private void OnInterstitialLoadFailed(LevelPlayAdError error)
        {
            isInterstitialLoading = false;

            if (HandleLoadWhileShowing(interstitialDiagnostics, error))
                return;

            interstitialRetryAttempt++;
            ScheduleLoadRetry(interstitialDiagnostics, error?.ErrorCode.ToString(), error?.ErrorMessage, interstitialRetryAttempt, nameof(LoadInterstitialWithRetry));
            Raise(OnAdLoadFailed, InterstitialFormat, error?.ErrorMessage ?? "unknown", nameof(OnAdLoadFailed));
        }

        private void OnInterstitialDisplayed(LevelPlayAdInfo adInfo)
        {
            StopShowWatch();
            EndAdLoading();
            MarkDisplayed(interstitialDiagnostics, adInfo);
            LogAd(AdLogLevel.Info, $"Interstitial displayed{FormatDisplayLatency(interstitialDiagnostics)}.");
            Raise(OnAdDisplayed, InterstitialFormat, nameof(OnAdDisplayed));
        }

        private void OnInterstitialDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            StopShowWatch();
            interstitialDiagnostics.State = AdFormatState.Failed;
            interstitialDiagnostics.LastErrorCode = error?.ErrorCode.ToString();
            interstitialDiagnostics.LastErrorMessage = error?.ErrorMessage;
            interstitialDiagnostics.LastErrorAtUtc = DateTime.UtcNow;
            LogAd(AdLogLevel.Error, $"Interstitial DISPLAY FAILED ({error?.ErrorCode}: {error?.ErrorMessage ?? "no details"}).");
            MarkFullscreenFree();
            EndAdLoading();
            Raise(OnAdDisplayFailed, InterstitialFormat, error?.ErrorMessage ?? "unknown", nameof(OnAdDisplayFailed));
            LoadAllAds();
        }

        private void OnInterstitialClosedInternal(LevelPlayAdInfo adInfo)
        {
            StopShowWatch();
            interstitialDiagnostics.State = AdFormatState.Closed;
            interstitialDiagnostics.LastClosedUtc = DateTime.UtcNow;
            LogAd(AdLogLevel.Info, "Interstitial closed.");
            MarkFullscreenFree();
            EndAdLoading();

            // Subscribers are isolated (Raise) so a throwing handler can never skip the reload below.
            Raise(OnAdClosed, InterstitialFormat, nameof(OnAdClosed));
            Raise(OnInterstitialClosed, nameof(OnInterstitialClosed));
            LoadAllAds();
        }

        private void OnInterstitialClicked(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Interstitial clicked.");

        private void OnInterstitialInfoChanged(LevelPlayAdInfo adInfo) =>
            StoreAdInfo(interstitialDiagnostics, adInfo);

        #endregion

        #region Rewarded Callbacks

        private void OnRewardedLoaded(LevelPlayAdInfo adInfo)
        {
            isRewardedLoading = false;
            rewardedRetryAttempt = 0;
            MarkLoaded(rewardedDiagnostics, adInfo);
            LogAd(AdLogLevel.Success, $"Rewarded LOADED ({adInfo?.AdNetwork ?? "?"}).");
            Raise(OnAdLoaded, RewardedFormat, nameof(OnAdLoaded));
        }

        private void OnRewardedLoadFailed(LevelPlayAdError error)
        {
            isRewardedLoading = false;

            if (HandleLoadWhileShowing(rewardedDiagnostics, error))
                return;

            rewardedRetryAttempt++;
            ScheduleLoadRetry(rewardedDiagnostics, error?.ErrorCode.ToString(), error?.ErrorMessage, rewardedRetryAttempt, nameof(LoadRewardedWithRetry));
            Raise(OnAdLoadFailed, RewardedFormat, error?.ErrorMessage ?? "unknown", nameof(OnAdLoadFailed));
        }

        private void OnRewardedDisplayed(LevelPlayAdInfo adInfo)
        {
            StopShowWatch();
            EndAdLoading();
            MarkDisplayed(rewardedDiagnostics, adInfo);
            LogAd(AdLogLevel.Info, $"Rewarded ad displayed{FormatDisplayLatency(rewardedDiagnostics)}.");
            Raise(OnAdDisplayed, RewardedFormat, nameof(OnAdDisplayed));
        }

        private void OnRewardedDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            StopShowWatch();
            rewardedDiagnostics.State = AdFormatState.Failed;
            rewardedDiagnostics.LastErrorCode = error?.ErrorCode.ToString();
            rewardedDiagnostics.LastErrorMessage = error?.ErrorMessage;
            rewardedDiagnostics.LastErrorAtUtc = DateTime.UtcNow;
            LogAd(AdLogLevel.Error, $"Rewarded DISPLAY FAILED ({error?.ErrorCode}: {error?.ErrorMessage ?? "no details"}).");
            MarkFullscreenFree();
            EndAdLoading();
            Raise(OnAdDisplayFailed, RewardedFormat, error?.ErrorMessage ?? "unknown", nameof(OnAdDisplayFailed));
            onRewardSuccessCallback = null;
            LoadAllAds();
        }

        private void OnRewardedReceived(LevelPlayAdInfo adInfo, LevelPlayReward reward)
        {
            LogAd(AdLogLevel.Success, $"Reward received: {reward?.Amount} {reward?.Name}");

            // Consume the callback here so a later OnAdClosed cannot grant a second time.
            var callback = onRewardSuccessCallback;
            onRewardSuccessCallback = null;

            if (callback == null)
                LogAd(AdLogLevel.Warning, "Reward received but no request is pending (late reward after a timeout, or a duplicate event).");

            Raise(callback, "reward callback");
            Raise(OnRewardedAdGranted, nameof(OnRewardedAdGranted));
        }

        private void OnRewardedClosed(LevelPlayAdInfo adInfo)
        {
            StopShowWatch();
            rewardedDiagnostics.State = AdFormatState.Closed;
            rewardedDiagnostics.LastClosedUtc = DateTime.UtcNow;
            LogAd(AdLogLevel.Info, "Rewarded ad closed.");
            Raise(OnAdClosed, RewardedFormat, nameof(OnAdClosed));

#if UNITY_EDITOR
            // The Editor mock rewarded ad does not raise OnAdRewarded in every SDK configuration. Treat a
            // close with a still-pending callback as earned so Play mode testing is not a dead end.
            // Editor only: on a device a pending callback means the player skipped the ad.
            var pending = onRewardSuccessCallback;
            if (pending != null)
            {
                LogAd(AdLogLevel.Warning, "Editor mock rewarded ad closed without the reward callback - granting anyway (Editor only).");
                onRewardSuccessCallback = null;
                Raise(pending, "reward callback");
                Raise(OnRewardedAdGranted, nameof(OnRewardedAdGranted));
            }
#endif

            onRewardSuccessCallback = null;
            MarkFullscreenFree();
            EndAdLoading();
            LoadAllAds();
        }

        private void OnRewardedClicked(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Rewarded ad clicked.");

        private void OnRewardedInfoChanged(LevelPlayAdInfo adInfo) =>
            StoreAdInfo(rewardedDiagnostics, adInfo);

        private static string FormatDisplayLatency(AdFormatDiagnostics diag) =>
            diag.LastDisplayLatencySeconds.HasValue ? $" {diag.LastDisplayLatencySeconds.Value:F1}s after the request" : string.Empty;

        #endregion

        #region Event Safety

        // Every public event is raised through these helpers: a throwing subscriber must never abort the
        // helper's own bookkeeping (reload after close, busy flag, reward consumption).

        private void Raise(Action handler, string name)
        {
            try { handler?.Invoke(); }
            catch (Exception e) { LogAd(AdLogLevel.Error, $"Subscriber of {name} threw: {e}"); }
        }

        private void Raise<T>(Action<T> handler, T arg, string name)
        {
            try { handler?.Invoke(arg); }
            catch (Exception e) { LogAd(AdLogLevel.Error, $"Subscriber of {name} threw: {e}"); }
        }

        private void Raise<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2, string name)
        {
            try { handler?.Invoke(arg1, arg2); }
            catch (Exception e) { LogAd(AdLogLevel.Error, $"Subscriber of {name} threw: {e}"); }
        }

        #endregion

        #region Banner Callbacks

        private void OnBannerLoaded(LevelPlayAdInfo adInfo)
        {
            MarkLoaded(bannerDiagnostics, adInfo);
            LogAd(AdLogLevel.Success, "Banner LOADED");
            Raise(OnAdLoaded, BannerFormat, nameof(OnAdLoaded));
        }

        private void OnBannerLoadFailed(LevelPlayAdError error)
        {
            int attempt = bannerDiagnostics.RetryAttempt + 1;
            float delay = Mathf.Min(300f, 30f * Mathf.Pow(2f, attempt - 1));
            MarkLoadFailed(bannerDiagnostics, error?.ErrorCode.ToString(), error?.ErrorMessage, delay);
            Raise(OnAdLoadFailed, BannerFormat, error?.ErrorMessage ?? "unknown", nameof(OnAdLoadFailed));

            if (attempt >= MaxLoadRetryAttempts)
            {
                bannerDiagnostics.RetriesExhausted = true;
                bannerDiagnostics.NextRetryAtUtc = null;
                LogAd(AdLogLevel.Warning, $"Banner LOAD FAILED ({error?.ErrorCode}: {error?.ErrorMessage}). Retry budget spent - paused until the app resumes.");
                return;
            }

            LogAd(AdLogLevel.Warning, $"Banner LOAD FAILED ({error?.ErrorCode}: {error?.ErrorMessage}). Retry {attempt}/{MaxLoadRetryAttempts} in {delay:F0}s.");
            CancelInvoke(nameof(BannerRetryLoad));
            Invoke(nameof(BannerRetryLoad), delay);
        }

        private void BannerRetryLoad()
        {
            if (bannerAd == null)
                return;

            bannerDiagnostics.RetriesExhausted = false;
            MarkLoading(bannerDiagnostics);
            bannerAd.LoadAd();
        }

        private void OnBannerDisplayed(LevelPlayAdInfo adInfo)
        {
            MarkDisplayed(bannerDiagnostics, adInfo);
            LogAd(AdLogLevel.Info, "Banner displayed.");
            Raise(OnAdDisplayed, BannerFormat, nameof(OnAdDisplayed));
        }

        private void OnBannerDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            bannerDiagnostics.LastErrorCode = error?.ErrorCode.ToString();
            bannerDiagnostics.LastErrorMessage = error?.ErrorMessage;
            LogAd(AdLogLevel.Error, $"Banner DISPLAY FAILED: {error?.ErrorMessage}");
            Raise(OnAdDisplayFailed, BannerFormat, error?.ErrorMessage ?? "unknown", nameof(OnAdDisplayFailed));
            CancelInvoke(nameof(BannerRetryLoad));
            Invoke(nameof(BannerRetryLoad), 60f);
        }

        private void OnBannerClicked(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Banner clicked.");

        private void OnBannerExpanded(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Banner expanded.");

        private void OnBannerCollapsed(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Banner collapsed.");

        private void OnBannerLeftApplication(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Banner left application.");

        private void UnsubscribeBannerEvents()
        {
            if (bannerAd == null)
                return;

            bannerAd.OnAdLoaded -= OnBannerLoaded;
            bannerAd.OnAdLoadFailed -= OnBannerLoadFailed;
            bannerAd.OnAdDisplayed -= OnBannerDisplayed;
            bannerAd.OnAdDisplayFailed -= OnBannerDisplayFailed;
            bannerAd.OnAdClicked -= OnBannerClicked;
            bannerAd.OnAdExpanded -= OnBannerExpanded;
            bannerAd.OnAdCollapsed -= OnBannerCollapsed;
            bannerAd.OnAdLeftApplication -= OnBannerLeftApplication;
            bannerAd.OnAdImpressionDataReady -= OnImpressionDataReadyInternal;
        }

        private static LevelPlayBannerPosition ToLevelPlayBannerPosition(BannerPositionPreset preset)
        {
            switch (preset)
            {
                case BannerPositionPreset.TopLeft: return LevelPlayBannerPosition.TopLeft;
                case BannerPositionPreset.TopCenter: return LevelPlayBannerPosition.TopCenter;
                case BannerPositionPreset.TopRight: return LevelPlayBannerPosition.TopRight;
                case BannerPositionPreset.CenterLeft: return LevelPlayBannerPosition.CenterLeft;
                case BannerPositionPreset.Center: return LevelPlayBannerPosition.Center;
                case BannerPositionPreset.CenterRight: return LevelPlayBannerPosition.CenterRight;
                case BannerPositionPreset.BottomLeft: return LevelPlayBannerPosition.BottomLeft;
                case BannerPositionPreset.BottomRight: return LevelPlayBannerPosition.BottomRight;
                default: return LevelPlayBannerPosition.BottomCenter;
            }
        }

        #endregion

        #region Revenue Callback

        private void OnImpressionDataReadyInternal(LevelPlayImpressionData impressionData)
        {
            // Fires on a BACKGROUND thread - keep this handler cheap and
            // forward to consumers, which are responsible for their own
            // thread-safety (e.g. dispatch to main thread for analytics SDKs).
            double revenue = impressionData.Revenue ?? 0d;
            LogAd(AdLogLevel.Revenue, $"IMPRESSION: {impressionData.AdNetwork} / {impressionData.AdFormat} / ${revenue:F4} ({impressionData.MediationAdUnitId})");
            networkStats.RecordImpression(impressionData);

            Raise(OnAdRevenuePaid, impressionData.MediationAdUnitId, revenue, nameof(OnAdRevenuePaid));
            Raise(OnImpressionDataReady, impressionData, nameof(OnImpressionDataReady));
        }

        #endregion

        #region App Lifecycle

        /// <summary>
        /// Stops the retry timers while the app is in the background (they would only burn the retry
        /// budget against a suspended network stack) and restarts everything on resume.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            isPaused = paused;

            if (paused)
            {
                pausedAtUtc = DateTime.UtcNow;
                LogAd(AdLogLevel.Info, "App paused.");
                CancelLoadRetries();
                return;
            }

            double away = pausedAtUtc.HasValue ? (DateTime.UtcNow - pausedAtUtc.Value).TotalSeconds : 0d;
            pausedAtUtc = null;
            LogAd(AdLogLevel.Info, $"App resumed after {away:F0}s (net={Application.internetReachability}, fullscreenBusy={IsFullscreenBusy}).");

            if (initState == SdkInitState.Failed && !isSdkInitialized)
            {
                initFailureCount = 0;
                CancelInvoke(nameof(Initialize));
                Initialize();
                return;
            }

            if (!adObjectsCreated || !CanLoadAds)
                return;

            ResetRetryBudget();
            LoadAllAds();

            if (bannerAd != null && bannerDiagnostics.State == AdFormatState.Failed)
            {
                bannerDiagnostics.RetryAttempt = 0;
                BannerRetryLoad();
            }
        }

        #endregion

        #region Cleanup

        protected virtual void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            CancelInvoke();
            StopAllCoroutines();

            if (adLoadingRaised)
            {
                adLoadingRaised = false;
                Raise(OnAdLoadingChanged, false, nameof(OnAdLoadingChanged));
            }

            LevelPlay.OnInitSuccess -= OnInitSuccess;
            LevelPlay.OnInitFailed -= OnInitFailed;

            if (interstitialAd != null)
            {
                interstitialAd.OnAdLoaded -= OnInterstitialLoaded;
                interstitialAd.OnAdLoadFailed -= OnInterstitialLoadFailed;
                interstitialAd.OnAdDisplayed -= OnInterstitialDisplayed;
                interstitialAd.OnAdDisplayFailed -= OnInterstitialDisplayFailed;
                interstitialAd.OnAdClosed -= OnInterstitialClosedInternal;
                interstitialAd.OnAdClicked -= OnInterstitialClicked;
                interstitialAd.OnAdInfoChanged -= OnInterstitialInfoChanged;
                interstitialAd.OnAdImpressionDataReady -= OnImpressionDataReadyInternal;
                interstitialAd.DestroyAd();
                interstitialAd = null;
            }

            if (rewardedAd != null)
            {
                rewardedAd.OnAdLoaded -= OnRewardedLoaded;
                rewardedAd.OnAdLoadFailed -= OnRewardedLoadFailed;
                rewardedAd.OnAdDisplayed -= OnRewardedDisplayed;
                rewardedAd.OnAdDisplayFailed -= OnRewardedDisplayFailed;
                rewardedAd.OnAdRewarded -= OnRewardedReceived;
                rewardedAd.OnAdClosed -= OnRewardedClosed;
                rewardedAd.OnAdClicked -= OnRewardedClicked;
                rewardedAd.OnAdInfoChanged -= OnRewardedInfoChanged;
                rewardedAd.OnAdImpressionDataReady -= OnImpressionDataReadyInternal;
                rewardedAd.DestroyAd();
                rewardedAd = null;
            }

            DestroyBanner();
        }

        #endregion
    }
}
