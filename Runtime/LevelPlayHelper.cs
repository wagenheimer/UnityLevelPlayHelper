using System;
using System.Collections;

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

        #endregion

        #region Initialization

        protected virtual void Start()
        {
            Initialize();
        }

        /// <summary>
        /// Applies privacy settings and initializes the LevelPlay SDK.
        /// Safe to call multiple times; only the first call has effect.
        /// </summary>
        public void Initialize()
        {
            if (isSdkInitialized)
                return;

            if (!AdsSupported)
            {
                Debug.Log("[LevelPlayHelper] Not a mobile platform - ads disabled.");
                return;
            }

            var appKey = AppKey;
            if (string.IsNullOrEmpty(appKey))
            {
#if UNITY_EDITOR
                appKey = EditorMockAppKey;
                Debug.LogWarning("[LevelPlayHelper] App Key is empty - initializing with the Editor mock key so Play mode can serve mock ads. Set the real key before building to device.");
#else
                Debug.LogError("[LevelPlayHelper] App Key is empty. Fill it in the Inspector.");
                return;
#endif
            }

            ApplyPrivacySettings();

            LevelPlay.OnInitSuccess += OnInitSuccess;
            LevelPlay.OnInitFailed += OnInitFailed;

            if (enableTestSuite)
                LevelPlay.SetMetaData("is_test_suite", "enable");

            Debug.Log($"[LevelPlayHelper] Initializing LevelPlay SDK...");
            LevelPlay.Init(appKey);
        }

        private void ApplyPrivacySettings()
        {
            if (!consentConfig.enableGDPRConsent && !consentConfig.ccpaOptOut && !consentConfig.coppaChildDirected)
                return;

            bool hasUserConsent = PlayerPrefs.GetInt(CONSENT_KEY, 0) == 1;

            if (consentConfig.enableGDPRConsent)
            {
                LevelPlayPrivacySettings.SetGDPRConsent(hasUserConsent);
                Debug.Log($"[LevelPlayHelper] GDPR consent applied: {hasUserConsent}");
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
            Debug.Log("[LevelPlayHelper] LevelPlay SDK initialized successfully.");
            isSdkInitialized = true;

            CreateAdObjects();

            OnSdkInitialized?.Invoke(config);

            if (enableTestSuite)
                LevelPlay.LaunchTestSuite();
        }

        private void OnInitFailed(LevelPlayInitError error)
        {
            Debug.LogError($"[LevelPlayHelper] LevelPlay initialization failed: {error.ErrorMessage}. Retrying in 10s...");
            OnSdkInitializeFailed?.Invoke(error.ErrorMessage);
            Invoke(nameof(Initialize), 10f);
        }

        private void CreateAdObjects()
        {
            var interstitialId = EffectiveInterstitialAdUnitId;
            if (!string.IsNullOrEmpty(interstitialId))
            {
                interstitialAd = new LevelPlayInterstitialAd(interstitialId);

                interstitialAd.OnAdLoaded += OnInterstitialLoaded;
                interstitialAd.OnAdLoadFailed += OnInterstitialLoadFailed;
                interstitialAd.OnAdDisplayed += OnInterstitialDisplayed;
                interstitialAd.OnAdDisplayFailed += OnInterstitialDisplayFailed;
                interstitialAd.OnAdClosed += OnInterstitialClosedInternal;
                interstitialAd.OnAdClicked += OnInterstitialClicked;
                interstitialAd.OnAdInfoChanged += OnInterstitialInfoChanged;
                interstitialAd.OnAdImpressionDataReady += OnImpressionDataReadyInternal;
            }
            else
            {
                Debug.Log("[LevelPlayHelper] No interstitial ad unit configured for this platform.");
            }

            var rewardedId = EffectiveRewardedAdUnitId;
            if (!string.IsNullOrEmpty(rewardedId))
            {
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
            }
            else
            {
                Debug.Log("[LevelPlayHelper] No rewarded ad unit configured for this platform.");
            }

            LoadAllAds();
        }

        #endregion

        #region Loading

        private void LoadAllAds()
        {
            if (!isSdkInitialized)
                return;

            if (interstitialAd != null && !interstitialAd.IsAdReady() && !isInterstitialLoading)
            {
                isInterstitialLoading = true;
                interstitialAd.LoadAd();
            }

            if (rewardedAd != null && !rewardedAd.IsAdReady() && !isRewardedLoading)
            {
                isRewardedLoading = true;
                rewardedAd.LoadAd();
            }
        }

        private void LoadInterstitialWithRetry()
        {
            if (interstitialAd == null || isInterstitialLoading)
                return;

            isInterstitialLoading = true;
            interstitialAd.LoadAd();
        }

        private void LoadRewardedWithRetry()
        {
            if (rewardedAd == null || isRewardedLoading)
                return;

            isRewardedLoading = true;
            rewardedAd.LoadAd();
        }

        private static float GetRetryDelay(int attempt) =>
            Mathf.Min(MaxRetryAttempt, attempt) * BaseRetryDelaySeconds;

        #endregion

        #region Public Ad Methods

        public bool IsInterstitialReady() =>
            isSdkInitialized && interstitialAd != null && interstitialAd.IsAdReady();

        public bool IsRewardedAdReady() =>
            isSdkInitialized && rewardedAd != null && rewardedAd.IsAdReady();

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
            LoadAllAds();
        }

        /// <summary>
        /// Opens the LevelPlay Test Suite on device. The "is_test_suite" metadata must have been
        /// set before initialization, i.e. <see cref="enableTestSuite"/> has to be enabled in the Inspector.
        /// </summary>
        public void LaunchTestSuite() => LevelPlay.LaunchTestSuite();

        #endregion

        #region Banner

        public void CreateBanner()
        {
            if (!isSdkInitialized || bannerAd != null || string.IsNullOrEmpty(BannerAdUnitId))
                return;

            var configBuilder = new LevelPlayBannerAd.Config.Builder();
            configBuilder.SetPosition(ToLevelPlayBannerPosition(bannerPosition));
            configBuilder.SetDisplayOnLoad(true);
            configBuilder.SetRespectSafeArea(true);

            bannerAd = new LevelPlayBannerAd(BannerAdUnitId, configBuilder.Build());

            bannerAd.OnAdLoaded += OnBannerLoaded;
            bannerAd.OnAdLoadFailed += OnBannerLoadFailed;
            bannerAd.OnAdDisplayed += OnBannerDisplayed;
            bannerAd.OnAdDisplayFailed += OnBannerDisplayFailed;
            bannerAd.OnAdClicked += OnBannerClicked;
            bannerAd.OnAdExpanded += OnBannerExpanded;
            bannerAd.OnAdCollapsed += OnBannerCollapsed;
            bannerAd.OnAdLeftApplication += OnBannerLeftApplication;
            bannerAd.OnAdImpressionDataReady += OnImpressionDataReadyInternal;

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
        }

        #endregion

        #region Interstitial Callbacks

        private void OnInterstitialLoaded(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Interstitial loaded.");
            isInterstitialLoading = false;
            interstitialRetryAttempt = 0;
            OnAdLoaded?.Invoke(InterstitialFormat);
        }

        private void OnInterstitialLoadFailed(LevelPlayAdError error)
        {
            isInterstitialLoading = false;
            interstitialRetryAttempt++;

            float delay = GetRetryDelay(interstitialRetryAttempt);
            Debug.LogWarning($"[LevelPlayHelper] Interstitial load failed ({error.ErrorCode}: {error.ErrorMessage}). Retrying in {delay:F0}s.");
            OnAdLoadFailed?.Invoke(InterstitialFormat, error.ErrorMessage);
            Invoke(nameof(LoadInterstitialWithRetry), delay);
        }

        private void OnInterstitialDisplayed(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Interstitial displayed.");
            OnAdDisplayed?.Invoke(InterstitialFormat);
        }

        private void OnInterstitialDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            Debug.LogError($"[LevelPlayHelper] Interstitial display failed: {error.ErrorMessage}");
            OnAdDisplayFailed?.Invoke(InterstitialFormat, error.ErrorMessage);
            LoadInterstitialWithRetry();
        }

        private void OnInterstitialClosedInternal(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Interstitial closed.");
            OnInterstitialClosed?.Invoke();
            LoadInterstitialWithRetry();
        }

        private void OnInterstitialClicked(LevelPlayAdInfo adInfo) =>
            Debug.Log("[LevelPlayHelper] Interstitial clicked.");

        private void OnInterstitialInfoChanged(LevelPlayAdInfo adInfo) { }

        #endregion

        #region Rewarded Callbacks

        private void OnRewardedLoaded(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Rewarded ad loaded.");
            isRewardedLoading = false;
            rewardedRetryAttempt = 0;
            OnAdLoaded?.Invoke(RewardedFormat);
        }

        private void OnRewardedLoadFailed(LevelPlayAdError error)
        {
            isRewardedLoading = false;
            rewardedRetryAttempt++;

            float delay = GetRetryDelay(rewardedRetryAttempt);
            Debug.LogWarning($"[LevelPlayHelper] Rewarded load failed ({error.ErrorCode}: {error.ErrorMessage}). Retrying in {delay:F0}s.");
            OnAdLoadFailed?.Invoke(RewardedFormat, error.ErrorMessage);
            Invoke(nameof(LoadRewardedWithRetry), delay);
        }

        private void OnRewardedDisplayed(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Rewarded ad displayed.");
            OnAdDisplayed?.Invoke(RewardedFormat);
        }

        private void OnRewardedDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            Debug.LogError($"[LevelPlayHelper] Rewarded display failed: {error.ErrorMessage}");
            OnAdDisplayFailed?.Invoke(RewardedFormat, error.ErrorMessage);
            onRewardSuccessCallback = null;
            LoadRewardedWithRetry();
        }

        private void OnRewardedReceived(LevelPlayAdInfo adInfo, LevelPlayReward reward)
        {
            Debug.Log($"[LevelPlayHelper] Reward received: {reward.Amount} {reward.Name}");

            // Consume the callback here so a later OnAdClosed cannot grant a second time.
            var callback = onRewardSuccessCallback;
            onRewardSuccessCallback = null;

            callback?.Invoke();
            OnRewardedAdGranted?.Invoke();
        }

        private void OnRewardedClosed(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Rewarded ad closed.");

#if UNITY_EDITOR
            // The Editor mock ad does not raise OnAdRewarded in every SDK configuration. Treat a
            // close with a still-pending callback as earned so Play mode testing is not a dead end.
            // Editor only: on a device a pending callback means the player skipped the ad.
            var pending = onRewardSuccessCallback;
            if (pending != null)
            {
                Debug.LogWarning("[LevelPlayHelper] Editor mock rewarded ad closed without the reward callback - granting anyway (Editor only).");
                onRewardSuccessCallback = null;
                pending.Invoke();
                OnRewardedAdGranted?.Invoke();
            }
#endif

            onRewardSuccessCallback = null;
            LoadRewardedWithRetry();
        }

        private void OnRewardedClicked(LevelPlayAdInfo adInfo) =>
            Debug.Log("[LevelPlayHelper] Rewarded ad clicked.");

        private void OnRewardedInfoChanged(LevelPlayAdInfo adInfo) { }

        #endregion

        #region Banner Callbacks

        private void OnBannerLoaded(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Banner loaded.");
            OnAdLoaded?.Invoke(BannerFormat);
        }

        private void OnBannerLoadFailed(LevelPlayAdError error)
        {
            Debug.LogWarning($"[LevelPlayHelper] Banner load failed: {error.ErrorMessage}. Retrying in 60s.");
            OnAdLoadFailed?.Invoke(BannerFormat, error.ErrorMessage);
            Invoke(nameof(BannerRetryLoad), 60f);
        }

        private void BannerRetryLoad() => bannerAd?.LoadAd();

        private void OnBannerDisplayed(LevelPlayAdInfo adInfo)
        {
            Debug.Log("[LevelPlayHelper] Banner displayed.");
            OnAdDisplayed?.Invoke(BannerFormat);
        }

        private void OnBannerDisplayFailed(LevelPlayAdInfo adInfo, LevelPlayAdError error)
        {
            Debug.LogError($"[LevelPlayHelper] Banner display failed: {error.ErrorMessage}");
            OnAdDisplayFailed?.Invoke(BannerFormat, error.ErrorMessage);
            Invoke(nameof(BannerRetryLoad), 60f);
        }

        private void OnBannerClicked(LevelPlayAdInfo adInfo) =>
            Debug.Log("[LevelPlayHelper] Banner clicked.");

        private void OnBannerExpanded(LevelPlayAdInfo adInfo) =>
            Debug.Log("[LevelPlayHelper] Banner expanded.");

        private void OnBannerCollapsed(LevelPlayAdInfo adInfo) =>
            Debug.Log("[LevelPlayHelper] Banner collapsed.");

        private void OnBannerLeftApplication(LevelPlayAdInfo adInfo) =>
            Debug.Log("[LevelPlayHelper] Banner left application.");

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
            Debug.Log($"[LevelPlayHelper] Impression: {impressionData.AdNetwork} / {impressionData.AdFormat} / ${revenue}");

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
