using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using Unity.Services.LevelPlay;

using UnityEngine;

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

        /// <summary>Ad Unit ID + estimated revenue in USD for every paid impression.</summary>
        public static event Action<string, double> OnAdRevenuePaid;

        /// <summary>Raw impression-level revenue data (background thread).</summary>
        public static event Action<LevelPlayImpressionData> OnImpressionDataReady;

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

        [Header("Testing")]
        [Tooltip("Launches the LevelPlay Test Suite on device builds after init. DISABLE BEFORE RELEASE.")]
        [SerializeField] private bool enableTestSuite = false;

        [Header("Debug")]
        [Tooltip("Automatically attaches the in-game LevelPlayDebugOverlay in the Editor and Development Builds. No effect in release builds.")]
        public bool enableDebugOverlay = true;

        public AdsConfiguration AdsConfig => adsConfig;
        public ConsentConfiguration ConsentConfig => consentConfig;

        #endregion

        #region Core State

        private const string CONSENT_KEY = "UserConsent";
        private const int MaxRetryAttempt = 6;
        private const float BaseRetryDelaySeconds = 2f;

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
        private int interstitialRetryAttempt;
        private int rewardedRetryAttempt;

        private Action onRewardSuccessCallback;

        public bool IsSdkInitialized => isSdkInitialized;

        // SDK init lifecycle. The overlay uses these to explain a stuck initialization
        // (the classic "nothing loads and the log stays empty").
        private SdkInitState initState = SdkInitState.NotStarted;
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

            if (initState == SdkInitState.Initializing)
                return $"Waiting for the SDK init callback ({(InitElapsedSeconds ?? 0f):F0}s elapsed)...";

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
        public string BuildDiagnosticReport()
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
            sb.AppendLine($"Test suite        : {enableTestSuite}");
            sb.AppendLine();
            sb.AppendLine(interstitialDiagnostics.ToSummaryLine(IsInterstitialReady()));
            AppendLastError(sb, interstitialDiagnostics);
            sb.AppendLine(rewardedDiagnostics.ToSummaryLine(IsRewardedAdReady()));
            AppendLastError(sb, rewardedDiagnostics);
            sb.AppendLine(bannerDiagnostics.ToSummaryLine(bannerAd != null));
            AppendLastError(sb, bannerDiagnostics);
            sb.AppendLine();
            sb.AppendLine($"Diagnosis         : {Diagnose() ?? "healthy"}");
            sb.AppendLine();
            sb.AppendLine("--- last events ---");
            foreach (var entry in SnapshotLog(60))
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
        /// Applies privacy settings and initializes the LevelPlay SDK.
        /// Safe to call multiple times; a call while initializing and any call after a
        /// successful init are ignored.
        /// </summary>
        public void Initialize()
        {
            if (isSdkInitialized || initState == SdkInitState.Initializing)
                return;

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

            ApplyPrivacySettings();

            // Remove before adding: the SDK events are static, so a retry after a failed init
            // would otherwise stack duplicate handlers and fire every callback N times.
            LevelPlay.OnInitSuccess -= OnInitSuccess;
            LevelPlay.OnInitFailed -= OnInitFailed;
            LevelPlay.OnInitSuccess += OnInitSuccess;
            LevelPlay.OnInitFailed += OnInitFailed;

            if (enableTestSuite)
                LevelPlay.SetMetaData("is_test_suite", "enable");

            initState = SdkInitState.Initializing;
            initStartedUtc = DateTime.UtcNow;
            lastInitError = null;

            LogAd(AdLogLevel.Info, $"Initializing LevelPlay SDK... (app key {Mask(appKey)})");
            LevelPlay.Init(appKey);

            StartCoroutine(InitWatchdog());
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
            LogAd(AdLogLevel.Error, $"SDK init callback did not arrive after {timeoutSeconds:F0}s. Check the App Key, the network connection and the LevelPlay dashboard setup.");
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
            LogAd(AdLogLevel.Success, "SDK initialized successfully.");

            CreateAdObjects();
            LoadAllAds();

            OnSdkInitialized?.Invoke(config);

            if (enableTestSuite)
                LevelPlay.LaunchTestSuite();
        }

        private void OnInitFailed(LevelPlayInitError error)
        {
            initState = SdkInitState.Failed;
            lastInitError = $"{error.ErrorCode}: {error.ErrorMessage}";
            LogAd(AdLogLevel.Error, $"SDK initialization failed: {error.ErrorMessage}. Retrying in 10s...");
            OnSdkInitializeFailed?.Invoke(error.ErrorMessage);
            Invoke(nameof(Initialize), 10f);
        }

        private void CreateAdObjects()
        {
            if (adObjectsCreated)
                return;

            adObjectsCreated = true;

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

        private void LoadAllAds()
        {
            if (!CanLoadAds)
                return;

            if (interstitialAd != null && !interstitialAd.IsAdReady() && !isInterstitialLoading)
            {
                isInterstitialLoading = true;
                MarkLoading(interstitialDiagnostics);
                interstitialAd.LoadAd();
            }

            if (rewardedAd != null && !rewardedAd.IsAdReady() && !isRewardedLoading)
            {
                isRewardedLoading = true;
                MarkLoading(rewardedDiagnostics);
                rewardedAd.LoadAd();
            }
        }

        private void LoadInterstitialWithRetry()
        {
            if (!CanLoadAds || interstitialAd == null || isInterstitialLoading)
                return;

            isInterstitialLoading = true;
            MarkLoading(interstitialDiagnostics);
            interstitialAd.LoadAd();
        }

        private void LoadRewardedWithRetry()
        {
            if (!CanLoadAds || rewardedAd == null || isRewardedLoading)
                return;

            isRewardedLoading = true;
            MarkLoading(rewardedDiagnostics);
            rewardedAd.LoadAd();
        }

        private static float GetRetryDelay(int attempt) =>
            Mathf.Min(MaxRetryAttempt, attempt) * BaseRetryDelaySeconds;

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
            diag.NextRetryAtUtc = null;
            diag.LastErrorCode = null;
            diag.LastErrorMessage = null;
            StoreAdInfo(diag, adInfo);
            diag.LastLoadedAtUtc = DateTime.UtcNow;
            diag.LoadsSucceeded++;
            diag.State = AdFormatState.Ready;
        }

        private void MarkLoadFailed(AdFormatDiagnostics diag, LevelPlayAdError error, float retryDelaySeconds)
        {
            diag.IsLoading = false;
            diag.LoadingSinceUtc = null;
            diag.RetryAttempt++;
            diag.LastErrorCode = error?.ErrorCode.ToString();
            diag.LastErrorMessage = error?.ErrorMessage;
            diag.LastErrorAtUtc = DateTime.UtcNow;
            diag.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(retryDelaySeconds);
            diag.LoadsFailed++;
            diag.State = AdFormatState.Failed;
        }

        private static void MarkDisplayed(AdFormatDiagnostics diag, LevelPlayAdInfo adInfo)
        {
            StoreAdInfo(diag, adInfo);
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

        public bool IsInterstitialReady() =>
            CanLoadAds && interstitialAd != null && interstitialAd.IsAdReady();

        public bool IsRewardedAdReady() =>
            CanLoadAds && rewardedAd != null && rewardedAd.IsAdReady();

        public void ShowInterstitial()
        {
            if (IsInterstitialReady())
            {
                interstitialAd.ShowAd();
            }
            else
            {
                Debug.LogWarning("[LevelPlayHelper] Interstitial not ready yet.");
                LoadInterstitialWithRetry();
            }
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
                onRewardSuccessCallback = onReward;
                rewardedAd.ShowAd();
            }
            else if (IsInterstitialReady())
            {
                Debug.LogWarning("[LevelPlayHelper] Rewarded not ready. Showing interstitial and granting reward.");
                interstitialAd.ShowAd();
                onReward?.Invoke();
                LoadRewardedWithRetry();
            }
            else
            {
                Debug.LogWarning("[LevelPlayHelper] No ads available. Granting reward directly.");
                onReward?.Invoke();
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

            Debug.LogWarning("[LevelPlayHelper] No ad available (rewarded or interstitial).");
            return false;
        }

        /// <summary>
        /// Clears the loading flags and requests a fresh load of every configured ad format.
        /// Intended for the in-game debug overlay (e.g. after a stuck load or a network change).
        /// </summary>
        public void ForceReloadAds()
        {
            isInterstitialLoading = false;
            isRewardedLoading = false;
            interstitialDiagnostics.IsLoading = false;
            rewardedDiagnostics.IsLoading = false;
            LoadAllAds();
        }

        /// <summary>
        /// Opens the LevelPlay Test Suite on device. The "is_test_suite" metadata must have been
        /// set before initialization, i.e. <see cref="enableTestSuite"/> has to be enabled in the Inspector.
        /// </summary>
        public void LaunchTestSuite() => LevelPlay.LaunchTestSuite();

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
            LogAd(AdLogLevel.Success, "Interstitial LOADED");
            OnAdLoaded?.Invoke(InterstitialFormat);
        }

        private void OnInterstitialLoadFailed(LevelPlayAdError error)
        {
            isInterstitialLoading = false;
            interstitialRetryAttempt++;

            float delay = GetRetryDelay(interstitialRetryAttempt);
            MarkLoadFailed(interstitialDiagnostics, error, delay);
            LogAd(AdLogLevel.Error, $"Interstitial LOAD FAILED ({error.ErrorCode}: {error.ErrorMessage}). Retrying in {delay:F0}s.");
            OnAdLoadFailed?.Invoke(InterstitialFormat, error.ErrorMessage);
            Invoke(nameof(LoadInterstitialWithRetry), delay);
        }

        private void OnInterstitialDisplayed(LevelPlayAdInfo adInfo)
        {
            MarkDisplayed(interstitialDiagnostics, adInfo);
            LogAd(AdLogLevel.Info, "Interstitial displayed.");
            OnAdDisplayed?.Invoke(InterstitialFormat);
        }

        private void OnInterstitialDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            interstitialDiagnostics.LastErrorCode = error?.ErrorCode.ToString();
            interstitialDiagnostics.LastErrorMessage = error?.ErrorMessage;
            LogAd(AdLogLevel.Error, $"Interstitial DISPLAY FAILED: {error.ErrorMessage}");
            OnAdDisplayFailed?.Invoke(InterstitialFormat, error.ErrorMessage);
            LoadInterstitialWithRetry();
        }

        private void OnInterstitialClosedInternal(LevelPlayAdInfo adInfo)
        {
            interstitialDiagnostics.State = AdFormatState.Closed;
            LogAd(AdLogLevel.Info, "Interstitial closed.");
            OnInterstitialClosed?.Invoke();
            LoadInterstitialWithRetry();
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
            LogAd(AdLogLevel.Success, "Rewarded LOADED");
            OnAdLoaded?.Invoke(RewardedFormat);
        }

        private void OnRewardedLoadFailed(LevelPlayAdError error)
        {
            isRewardedLoading = false;
            rewardedRetryAttempt++;

            float delay = GetRetryDelay(rewardedRetryAttempt);
            MarkLoadFailed(rewardedDiagnostics, error, delay);
            LogAd(AdLogLevel.Error, $"Rewarded LOAD FAILED ({error.ErrorCode}: {error.ErrorMessage}). Retrying in {delay:F0}s.");
            OnAdLoadFailed?.Invoke(RewardedFormat, error.ErrorMessage);
            Invoke(nameof(LoadRewardedWithRetry), delay);
        }

        private void OnRewardedDisplayed(LevelPlayAdInfo adInfo)
        {
            MarkDisplayed(rewardedDiagnostics, adInfo);
            LogAd(AdLogLevel.Info, "Rewarded ad displayed.");
            OnAdDisplayed?.Invoke(RewardedFormat);
        }

        private void OnRewardedDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            rewardedDiagnostics.LastErrorCode = error?.ErrorCode.ToString();
            rewardedDiagnostics.LastErrorMessage = error?.ErrorMessage;
            LogAd(AdLogLevel.Error, $"Rewarded DISPLAY FAILED: {error.ErrorMessage}");
            OnAdDisplayFailed?.Invoke(RewardedFormat, error.ErrorMessage);
            onRewardSuccessCallback = null;
            LoadRewardedWithRetry();
        }

        private void OnRewardedReceived(LevelPlayAdInfo adInfo, LevelPlayReward reward)
        {
            LogAd(AdLogLevel.Success, $"Reward received: {reward.Amount} {reward.Name}");

            // Consume the callback here so a later OnAdClosed cannot grant a second time.
            var callback = onRewardSuccessCallback;
            onRewardSuccessCallback = null;

            callback?.Invoke();
            OnRewardedAdGranted?.Invoke();
        }

        private void OnRewardedClosed(LevelPlayAdInfo adInfo)
        {
            rewardedDiagnostics.State = AdFormatState.Closed;
            LogAd(AdLogLevel.Info, "Rewarded ad closed.");

#if UNITY_EDITOR
            // The Editor mock ad does not raise OnAdRewarded in every SDK configuration. Treat a
            // close with a still-pending callback as earned so Play mode testing is not a dead end.
            // Editor only: on a device a pending callback means the player skipped the ad.
            var pending = onRewardSuccessCallback;
            if (pending != null)
            {
                LogAd(AdLogLevel.Warning, "Editor mock rewarded ad closed without the reward callback - granting anyway (Editor only).");
                onRewardSuccessCallback = null;
                pending.Invoke();
                OnRewardedAdGranted?.Invoke();
            }
#endif

            onRewardSuccessCallback = null;
            LoadRewardedWithRetry();
        }

        private void OnRewardedClicked(LevelPlayAdInfo adInfo) =>
            LogAd(AdLogLevel.Info, "Rewarded ad clicked.");

        private void OnRewardedInfoChanged(LevelPlayAdInfo adInfo) =>
            StoreAdInfo(rewardedDiagnostics, adInfo);

        #endregion

        #region Banner Callbacks

        private void OnBannerLoaded(LevelPlayAdInfo adInfo)
        {
            MarkLoaded(bannerDiagnostics, adInfo);
            LogAd(AdLogLevel.Success, "Banner LOADED");
            OnAdLoaded?.Invoke(BannerFormat);
        }

        private void OnBannerLoadFailed(LevelPlayAdError error)
        {
            MarkLoadFailed(bannerDiagnostics, error, 60f);
            LogAd(AdLogLevel.Error, $"Banner LOAD FAILED ({error.ErrorCode}: {error.ErrorMessage}). Retrying in 60s.");
            OnAdLoadFailed?.Invoke(BannerFormat, error.ErrorMessage);
            Invoke(nameof(BannerRetryLoad), 60f);
        }

        private void BannerRetryLoad()
        {
            if (bannerAd == null)
                return;

            MarkLoading(bannerDiagnostics);
            bannerAd.LoadAd();
        }

        private void OnBannerDisplayed(LevelPlayAdInfo adInfo)
        {
            MarkDisplayed(bannerDiagnostics, adInfo);
            LogAd(AdLogLevel.Info, "Banner displayed.");
            OnAdDisplayed?.Invoke(BannerFormat);
        }

        private void OnBannerDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            bannerDiagnostics.LastErrorCode = error?.ErrorCode.ToString();
            bannerDiagnostics.LastErrorMessage = error?.ErrorMessage;
            LogAd(AdLogLevel.Error, $"Banner DISPLAY FAILED: {error.ErrorMessage}");
            OnAdDisplayFailed?.Invoke(BannerFormat, error.ErrorMessage);
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

            OnAdRevenuePaid?.Invoke(impressionData.MediationAdUnitId, revenue);
            OnImpressionDataReady?.Invoke(impressionData);
        }

        #endregion

        #region Cleanup

        protected virtual void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            CancelInvoke();
            StopAllCoroutines();

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
                rewardedAd = null;
            }

            DestroyBanner();
        }

        #endregion
    }
}
