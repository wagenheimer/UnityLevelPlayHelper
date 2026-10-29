using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper
{
    /// <summary>
    /// Standard, dependency-free consent dialog built with runtime UI Toolkit.
    ///
    /// It is created entirely in code (no prefab to import) and reads every string from a
    /// <see cref="ConsentLocalization"/> asset, which is what makes translations work. If the
    /// asset is missing or a language is incomplete, English defaults are used so the player is
    /// never blocked.
    /// </summary>
    public static class ConsentDialog
    {
        private const string ConsentThemeResource = "LevelPlayConsentTheme";
        private const string DebugThemeResource = "LevelPlayDebugTheme";

        private const string FallbackTitle = "Privacy & consent";
        private const string FallbackBody =
            "We work with advertising partners to keep this game free. With your consent, they may use " +
            "your advertising identifier to show personalized ads. You can change this choice at any time.";
        private const string FallbackAccept = "Accept";
        private const string FallbackDecline = "Decline";
        private const string FallbackPrivacy = "Privacy Policy";
        private const string FallbackTerms = "Terms of Service";

        private static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.72f);
        private static readonly Color Card = new Color(0.137f, 0.141f, 0.176f, 1f);
        private static readonly Color CardBorder = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color Text = new Color(0.93f, 0.94f, 0.97f, 1f);
        private static readonly Color TextMuted = new Color(0.72f, 0.74f, 0.80f, 1f);
        private static readonly Color Accent = new Color(0.298f, 0.686f, 0.314f, 1f);
        private static readonly Color Secondary = new Color(1f, 1f, 1f, 0.09f);

        private static GameObject current;
        private static PanelSettings currentPanel;

        /// <summary>True while the standard dialog is open.</summary>
        public static bool IsOpen => current != null;

        /// <summary>Opens the dialog for the given gate (interactive, not preview).</summary>
        public static void Show(ConsentGate gate) => Show(gate, false);

        /// <summary>Opens the dialog for the given gate. Reuses an already-open dialog.</summary>
        public static void Show(ConsentGate gate, bool preview)
        {
            if (gate == null || current != null)
                return;

            ThemeStyleSheet theme = ResolveTheme(gate);
            if (theme == null)
            {
                Debug.LogError("[LevelPlayHelper] Consent dialog needs a ThemeStyleSheet. " +
                               "Assign one on the ConsentGate or keep LevelPlayConsentTheme.tss imported.");
                return;
            }

            currentPanel = ScriptableObject.CreateInstance<PanelSettings>();
            currentPanel.name = "LevelPlayConsentPanelSettings";
            currentPanel.hideFlags = HideFlags.HideAndDontSave;
            currentPanel.themeStyleSheet = theme;
            currentPanel.scaleMode = PanelScaleMode.ConstantPixelSize;
            currentPanel.scale = ResolveScale();
            currentPanel.referenceResolution = new Vector2Int(1920, 1080);
            currentPanel.clearColor = false;
            currentPanel.sortingOrder = gate.SortingOrder;

            var go = new GameObject("LevelPlayConsentDialog");
            go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(go);

            var document = go.AddComponent<UIDocument>();
            document.panelSettings = currentPanel;
            document.sortingOrder = gate.SortingOrder;

            go.SetActive(true);
            current = go;

            Build(document.rootVisualElement, gate, preview);
        }

        /// <summary>
        /// Shows the dialog in a specific language without changing the player's answer.
        /// Used by the debug overlay to inspect every translation.
        /// </summary>
        public static void ShowPreview(ConsentGate gate, string languageCode)
        {
            if (gate == null)
                return;

            string previous = gate.LanguageOverride;
            gate.LanguageOverride = languageCode;
            Show(gate, true);
            gate.LanguageOverride = previous;
        }

        /// <summary>Closes the dialog and releases its runtime panel.</summary>
        public static void Close()
        {
            if (current != null)
            {
                UnityEngine.Object.Destroy(current);
                current = null;
            }

            if (currentPanel != null)
            {
                UnityEngine.Object.Destroy(currentPanel);
                currentPanel = null;
            }
        }

        // ---------------------------------------------------------------- layout

        private static void Build(VisualElement root, ConsentGate gate, bool preview)
        {
            ConsentLocalization.Entry entry = gate.ResolveEntry();

            // Per-language font wins over the gate-wide font; the theme font is the last resort.
            Font font = entry != null && entry.font != null ? entry.font : gate.UiFont;

            root.Clear();
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.right = 0;
            root.style.top = 0;
            root.style.bottom = 0;
            root.style.backgroundColor = Backdrop;
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;
            root.pickingMode = PickingMode.Position;

            var card = new VisualElement();
            card.style.width = 640;
            card.style.maxWidth = Length.Percent(92);
            card.style.maxHeight = Length.Percent(90);
            card.style.backgroundColor = Card;
            card.style.borderTopLeftRadius = 14;
            card.style.borderTopRightRadius = 14;
            card.style.borderBottomLeftRadius = 14;
            card.style.borderBottomRightRadius = 14;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            card.style.borderTopColor = CardBorder;
            card.style.borderBottomColor = CardBorder;
            card.style.borderLeftColor = CardBorder;
            card.style.borderRightColor = CardBorder;
            card.style.paddingTop = 26;
            card.style.paddingBottom = 22;
            card.style.paddingLeft = 26;
            card.style.paddingRight = 26;
            root.Add(card);

            if (preview)
            {
                var previewBar = new VisualElement();
                previewBar.style.flexDirection = FlexDirection.Row;
                previewBar.style.alignItems = Align.Center;
                previewBar.style.justifyContent = Justify.SpaceBetween;
                previewBar.style.marginBottom = 10;

                var badge = MakeLabel("PREVIEW - " + (entry != null ? entry.languageCode : "fallback"), font);
                badge.style.fontSize = 11;
                badge.style.color = new Color(0.98f, 0.75f, 0.14f, 1f);
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                previewBar.Add(badge);

                var close = new Button(Close) { text = "X" };
                close.style.width = 26;
                close.style.height = 24;
                close.style.fontSize = 12;
                close.style.unityFontStyleAndWeight = FontStyle.Bold;
                close.style.color = Text;
                close.style.backgroundColor = Secondary;
                close.style.borderTopWidth = 0;
                close.style.borderBottomWidth = 0;
                close.style.borderLeftWidth = 0;
                close.style.borderRightWidth = 0;
                ApplyFont(close, font);
                previewBar.Add(close);

                card.Add(previewBar);
            }

            var title = MakeLabel(entry != null ? entry.title : FallbackTitle, font);
            title.style.fontSize = 23;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Text;
            title.style.whiteSpace = WhiteSpace.Normal;
            card.Add(title);

            var body = MakeLabel(entry != null ? entry.body : FallbackBody, font);
            body.style.fontSize = 14;
            body.style.color = TextMuted;
            body.style.whiteSpace = WhiteSpace.Normal;
            body.style.width = Length.Percent(100);
            body.style.marginTop = 12;

            var bodyScroll = new ScrollView(ScrollViewMode.Vertical);
            bodyScroll.style.width = Length.Percent(100);
            bodyScroll.style.maxHeight = 320;
            bodyScroll.style.marginTop = 2;
            bodyScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            bodyScroll.Add(body);
            card.Add(bodyScroll);

            if (!string.IsNullOrWhiteSpace(gate.PrivacyPolicyUrl) || !string.IsNullOrWhiteSpace(gate.TermsUrl))
            {
                var links = new VisualElement();
                links.style.flexDirection = FlexDirection.Row;
                links.style.flexWrap = Wrap.Wrap;
                links.style.marginTop = 14;
                card.Add(links);

                if (!string.IsNullOrWhiteSpace(gate.PrivacyPolicyUrl))
                    links.Add(MakeLink(entry != null ? entry.privacyPolicy : FallbackPrivacy, font, gate.OpenPrivacyPolicy));

                if (!string.IsNullOrWhiteSpace(gate.TermsUrl))
                    links.Add(MakeLink(entry != null ? entry.terms : FallbackTerms, font, gate.OpenTerms));
            }

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            buttons.style.marginTop = 22;
            card.Add(buttons);

            var decline = MakeButton(entry != null ? entry.decline : FallbackDecline, font, false);
            decline.clicked += () =>
            {
                if (!preview)
                    gate.Deny();
                Close();
            };
            buttons.Add(decline);

            var accept = MakeButton(entry != null ? entry.accept : FallbackAccept, font, true);
            accept.style.marginLeft = 10;
            accept.clicked += () =>
            {
                if (!preview)
                    gate.Grant();
                Close();
            };
            buttons.Add(accept);

            // Give the accept button the initial focus so gamepad/keyboard users can answer
            // without a pointer. Input is polled by the runtime panel regardless of time scale.
            accept.schedule.Execute(() => accept.Focus()).ExecuteLater(30);
        }

        // ---------------------------------------------------------------- widgets

        private static Label MakeLabel(string text, Font font)
        {
            var label = new Label(string.IsNullOrEmpty(text) ? " " : text);
            ApplyFont(label, font);
            return label;
        }

        private static Button MakeLink(string text, Font font, Action clicked)
        {
            var button = new Button(clicked) { text = string.IsNullOrEmpty(text) ? " " : text };
            ApplyFont(button, font);
            button.style.fontSize = 13;
            button.style.color = Accent;
            button.style.backgroundColor = Color.clear;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 12;
            button.style.marginRight = 10;
            button.style.marginLeft = 0;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            return button;
        }

        private static Button MakeButton(string text, Font font, bool primary)
        {
            var button = new Button { text = string.IsNullOrEmpty(text) ? " " : text };
            ApplyFont(button, font);
            button.style.minWidth = 150;
            button.style.height = 44;
            button.style.fontSize = 15;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = primary ? Color.white : Text;
            button.style.backgroundColor = primary ? Accent : Secondary;
            button.style.borderTopLeftRadius = 8;
            button.style.borderTopRightRadius = 8;
            button.style.borderBottomLeftRadius = 8;
            button.style.borderBottomRightRadius = 8;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            return button;
        }

        private static void ApplyFont(VisualElement element, Font font)
        {
            if (font == null)
                return;

            // Works on 2022.3+; Unity 6 only warns that the property is obsolete.
            element.style.unityFont = font;
        }

        // ---------------------------------------------------------------- helpers

        private static ThemeStyleSheet ResolveTheme(ConsentGate gate)
        {
            if (gate != null && gate.Theme != null)
                return gate.Theme;

            var theme = Resources.Load<ThemeStyleSheet>(ConsentThemeResource);
            if (theme != null)
                return theme;

            theme = Resources.Load<ThemeStyleSheet>(DebugThemeResource);
            if (theme != null)
                return theme;

            var all = Resources.FindObjectsOfTypeAll(typeof(ThemeStyleSheet));
            if (all != null && all.Length > 0)
                return all[0] as ThemeStyleSheet;

            var loaded = Resources.LoadAll("", typeof(ThemeStyleSheet));
            return loaded != null && loaded.Length > 0 ? loaded[0] as ThemeStyleSheet : null;
        }

        private static float ResolveScale()
        {
            if (!Application.isMobilePlatform)
                return 1f;

            // Roughly 160 dpi = 1x; clamp so phones and tablets stay readable.
            return Mathf.Clamp(Screen.dpi / 160f, 1f, 2.5f);
        }
    }
}
