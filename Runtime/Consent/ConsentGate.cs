using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper
{
    /// <summary>
    /// Collects and persists the player's ad-consent answer before LevelPlay initializes.
    ///
    /// Add this component next to <see cref="LevelPlayHelper"/> (same prefab). On first launch it
    /// shows the package's standard dialog; the answer is written to <c>PlayerPrefs</c> under
    /// <see cref="ConsentPrefKey"/> (the same key the helper reads) and pushed to
    /// <see cref="LevelPlayHelper.SetUserConsent"/>. While unresolved it keeps
    /// <see cref="LevelPlayHelper.InitializationGate"/> closed.
    ///
    /// The dialog is optional: call <see cref="Grant"/> / <see cref="Deny"/> from your own UI.
    /// </summary>
    [AddComponentMenu("Tools/Wagenheimer/Level Play/Consent Gate")]
    [DisallowMultipleComponent]
    public sealed class ConsentGate : MonoBehaviour
    {
        /// <summary>PlayerPrefs key read by <see cref="LevelPlayHelper"/> (1 = consented).</summary>
        public const string ConsentPrefKey = "UserConsent";

        [Header("Consent")]
        [Tooltip("Localized strings for the standard dialog. Required for translations.")]
        [SerializeField] private ConsentLocalization localization;

        [Tooltip("Keep LevelPlay initialization paused until the player answers. Recommended.")]
        [SerializeField] private bool gateSdkInitialization = true;

        [Tooltip("Show the package's standard consent dialog on first launch when no answer is stored.")]
        [SerializeField] private bool showDialogOnStart = true;

        [Header("Legal links (optional)")]
        [SerializeField] private string privacyPolicyUrl = "";
        [SerializeField] private string termsUrl = "";

        [Header("Presentation")]
        [Tooltip("Optional theme with a font that covers your languages (for example Korean). Falls back to the package theme.")]
        [SerializeField] private ThemeStyleSheet theme;

        [Tooltip("Optional legacy font applied to every dialog label and button. Useful when the theme font lacks CJK glyphs.")]
        [SerializeField] private Font uiFont;

        [Tooltip("Sorting order of the dialog panel. Keep it above the game UI.")]
        [SerializeField] private int sortingOrder = 32000;

        /// <summary>Most recently enabled gate. Null when the project does not use one.</summary>
        public static ConsentGate Instance { get; private set; }

        /// <summary>True when the player already answered (accepted or declined) on this device.</summary>
        public static bool IsResolved => PlayerPrefs.HasKey(ConsentPrefKey);

        /// <summary>True when the stored answer allows personalized ads.</summary>
        public static bool HasConsent => PlayerPrefs.GetInt(ConsentPrefKey, 0) == 1;

        /// <summary>Raised after an answer is stored, with the player's choice.</summary>
        public event Action<bool> ConsentChanged;

        /// <summary>
        /// Optional language override used by tooling (the debug overlay previews every entry).
        /// Null or empty resolves the device language.
        /// </summary>
        public string LanguageOverride { get; set; }

        public ConsentLocalization Localization => localization;
        public string PrivacyPolicyUrl => privacyPolicyUrl;
        public string TermsUrl => termsUrl;
        public ThemeStyleSheet Theme => theme;
        public Font UiFont => uiFont;
        public int SortingOrder => sortingOrder;
        public bool GateSdkInitialization => gateSdkInitialization;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            if (gateSdkInitialization)
                InstallGate();

            if (IsResolved)
                ApplyStoredConsent();
        }

        private void Start()
        {
            if (!IsResolved && showDialogOnStart)
                ShowDialog();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private static bool gateInstalled;

        /// <summary>
        /// Installs the consent predicate on <see cref="LevelPlayHelper.InitializationGate"/>.
        ///
        /// Any gate already installed (for example the ATT gate owned by
        /// <c>com.wagenheimer.tracking</c>) is preserved: initialization waits for both.
        /// </summary>
        public static void InstallGate()
        {
            if (gateInstalled)
                return;

            gateInstalled = true;
            Func<bool> previous = LevelPlayHelper.InitializationGate;
            LevelPlayHelper.InitializationGate =
                () => IsResolved && (previous == null || previous());
        }

        /// <summary>
        /// Entry used by the dialog: the language override when set, otherwise the device
        /// language, the configured default and finally the first entry.
        /// </summary>
        public ConsentLocalization.Entry ResolveEntry()
        {
            if (localization == null)
                return null;

            if (!string.IsNullOrWhiteSpace(LanguageOverride))
            {
                var overridden = localization.Find(LanguageOverride);
                if (overridden != null)
                    return overridden;
            }

            return localization.ResolveSystem();
        }

        /// <summary>Opens the package's standard dialog. Does nothing when one is already open.</summary>
        public void ShowDialog()
        {
            ConsentDialog.Show(this, false);
        }

        /// <summary>Opens the dialog in preview mode (Accept/Decline only close it).</summary>
        public void ShowDialogPreview()
        {
            ConsentDialog.Show(this, true);
        }

        /// <summary>Stores consent = true and lets LevelPlay initialize on the next attempt.</summary>
        public void Grant() => Resolve(true);

        /// <summary>Stores consent = false (non-personalized ads) and lets LevelPlay initialize.</summary>
        public void Deny() => Resolve(false);

        /// <summary>Forgets the answer so the dialog can be shown again (useful in settings).</summary>
        public void ResetConsent()
        {
            PlayerPrefs.DeleteKey(ConsentPrefKey);
            PlayerPrefs.Save();
        }

        public void OpenPrivacyPolicy()
        {
            if (!string.IsNullOrWhiteSpace(privacyPolicyUrl))
                Application.OpenURL(privacyPolicyUrl);
        }

        public void OpenTerms()
        {
            if (!string.IsNullOrWhiteSpace(termsUrl))
                Application.OpenURL(termsUrl);
        }

        private void Resolve(bool consent)
        {
            PlayerPrefs.SetInt(ConsentPrefKey, consent ? 1 : 0);
            PlayerPrefs.Save();

            if (LevelPlayHelper.Instance != null)
                LevelPlayHelper.Instance.SetUserConsent(consent);

            ConsentChanged?.Invoke(consent);
        }

        private void ApplyStoredConsent()
        {
            if (LevelPlayHelper.Instance != null)
                LevelPlayHelper.Instance.SetUserConsent(HasConsent);
        }
    }
}
