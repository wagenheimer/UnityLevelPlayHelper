using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// UI Toolkit credentials configurator for LevelPlayHelper.
    /// Uses native SerializedObject data-binding for immediate persistence, Undo/Redo support,
    /// and real-time credential validation with visual status badges.
    /// </summary>
    internal sealed class LevelPlayCredentialsPanel
    {
        const string DashboardUrl = "https://platform.ironsrc.com/";
        const string AdUnitsUrl = "https://platform.ironsrc.com/partners/adUnits";

        public VisualElement Root { get; }

        readonly ScrollView scroll;
        readonly VisualElement body;
        SerializedObject serializedObject;
        LevelPlayHelper helper;

        enum PlatformTab { Android, IOS }
        PlatformTab currentPlatform = PlatformTab.Android;

        VisualElement androidTabBtn;
        VisualElement iosTabBtn;
        VisualElement androidContainer;
        VisualElement iosContainer;

        Label androidStatusBadge;
        Label iosStatusBadge;

        readonly List<Action> validatorCallbacks = new List<Action>();

        public LevelPlayCredentialsPanel(SerializedObject serialized = null)
        {
            Root = new VisualElement();
            Root.AddToClassList("lp-root");
            LevelPlayUIStyle.Apply(Root);

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);

            body = scroll.contentContainer;

            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS)
            {
                currentPlatform = PlatformTab.IOS;
            }

            SetTarget(serialized);
        }

        public void SetTarget(SerializedObject serialized)
        {
            if (serialized != null && serialized.targetObject is LevelPlayHelper h)
            {
                serializedObject = serialized;
                helper = h;
            }
            else
            {
                helper = LevelPlayHelperLocator.FindPreferred();
                serializedObject = helper != null ? new SerializedObject(helper) : null;
            }

            Reload();
        }

        public void Reload()
        {
            body.Clear();
            validatorCallbacks.Clear();

            if (helper == null || serializedObject == null)
            {
                body.Add(LevelPlayUIStyle.CreateCallout(
                    "No LevelPlayHelper component found. Create or select the prefab " +
                    "(Assets/Resources/Monetization/LevelPlayHelper.prefab) to configure credentials.",
                    "fail"));
                return;
            }

            serializedObject.Update();

            var location = LevelPlayHelperLocator.LocationOf(helper);
            body.Add(LevelPlayUIStyle.CreateCallout(
                $"Configuring credentials on: {location}. Changes apply live with full Undo (Ctrl+Z) support.",
                "info"));

            // Platform Switcher
            body.Add(BuildPlatformSwitcher());

            // Platform Containers
            androidContainer = BuildPlatformCard("Android", "android");
            iosContainer = BuildPlatformCard("iOS", "ios");

            body.Add(androidContainer);
            body.Add(iosContainer);

            // Banner Preset Card
            body.Add(BuildBannerSettingsCard());

            // Footer Quick Links
            body.Add(BuildFooterActions());

            UpdatePlatformVisibility();
            ValidateAll();
        }

        VisualElement BuildPlatformSwitcher()
        {
            var bar = new VisualElement();
            bar.AddToClassList("lp-platform-tabs");

            androidTabBtn = new Button(() => SwitchPlatform(PlatformTab.Android)) { text = "Android Configuration" };
            androidTabBtn.AddToClassList("lp-platform-tab");

            iosTabBtn = new Button(() => SwitchPlatform(PlatformTab.IOS)) { text = "iOS Configuration" };
            iosTabBtn.AddToClassList("lp-platform-tab");

            bar.Add(androidTabBtn);
            bar.Add(iosTabBtn);

            return bar;
        }

        void SwitchPlatform(PlatformTab tab)
        {
            currentPlatform = tab;
            UpdatePlatformVisibility();
        }

        void UpdatePlatformVisibility()
        {
            if (androidTabBtn == null || iosTabBtn == null) return;

            bool isAndroid = currentPlatform == PlatformTab.Android;
            if (isAndroid)
            {
                androidTabBtn.AddToClassList("lp-platform-tab--active");
                iosTabBtn.RemoveFromClassList("lp-platform-tab--active");
                androidContainer.style.display = DisplayStyle.Flex;
                iosContainer.style.display = DisplayStyle.None;
            }
            else
            {
                iosTabBtn.AddToClassList("lp-platform-tab--active");
                androidTabBtn.RemoveFromClassList("lp-platform-tab--active");
                androidContainer.style.display = DisplayStyle.None;
                iosContainer.style.display = DisplayStyle.Flex;
            }
        }

        VisualElement BuildPlatformCard(string platformName, string prefix)
        {
            var badge = LevelPlayUIStyle.CreateBadge("Checking...", "info");
            if (prefix == "android") androidStatusBadge = badge;
            else iosStatusBadge = badge;

            var card = LevelPlayUIStyle.CreateCard(
                $"{platformName} Credentials",
                $"App Key & Ad Unit IDs configured in the LevelPlay Dashboard for {platformName}.",
                badge);

            var appKeyProp = serializedObject.FindProperty(prefix + "AppKey");
            var interstitialProp = serializedObject.FindProperty(prefix + "InterstitialAdUnitId");
            var rewardedProp = serializedObject.FindProperty(prefix + "RewardedAdUnitId");
            var bannerProp = serializedObject.FindProperty(prefix + "BannerAdUnitId");

            card.Add(BuildBoundCredentialRow(appKeyProp, "App Key", true, false));
            card.Add(BuildBoundCredentialRow(interstitialProp, "Interstitial Unit ID", false, false));
            card.Add(BuildBoundCredentialRow(rewardedProp, "Rewarded Unit ID", false, false));
            card.Add(BuildBoundCredentialRow(bannerProp, "Banner Unit ID (Optional)", false, true));

            return card;
        }

        VisualElement BuildBoundCredentialRow(SerializedProperty prop, string labelText, bool isAppKey, bool isOptional)
        {
            var row = new VisualElement();
            row.AddToClassList("lp-field-row");

            var label = new Label(labelText);
            label.AddToClassList("lp-field-label");
            row.Add(label);

            var input = new TextField();
            input.AddToClassList("lp-field-input");
            if (prop != null)
            {
                input.BindProperty(prop);
            }
            row.Add(input);

            var statusIcon = new Label("\u25CF");
            statusIcon.AddToClassList("lp-field-status");
            row.Add(statusIcon);

            var help = new Label();
            help.AddToClassList("lp-field-help");

            var wrapper = new VisualElement();
            wrapper.Add(row);
            wrapper.Add(help);

            void ValidateThis()
            {
                var val = input.value;
                if (!CredentialValidation.IsSet(val))
                {
                    if (isOptional)
                    {
                        statusIcon.text = "\u25CB";
                        statusIcon.style.color = new Color(0.6f, 0.6f, 0.6f);
                        help.text = "Format is disabled (optional). Real ads will not request banners.";
                    }
                    else
                    {
                        statusIcon.text = "!";
                        statusIcon.style.color = new Color(0.9f, 0.3f, 0.3f);
                        help.text = "Required field. Ad requests will fail without this credential.";
                    }
                    return;
                }

                if (CredentialValidation.IsPlaceholder(val))
                {
                    statusIcon.text = "!";
                    statusIcon.style.color = new Color(1f, 0.6f, 0.1f);
                    help.text = "Placeholder detected. Enter the real key from Dashboard.";
                    return;
                }

                var problem = CredentialValidation.DescribeProblem(val, isAppKey);
                if (problem != null)
                {
                    bool isHard = CredentialValidation.IsHardProblem(problem);
                    statusIcon.text = "!";
                    statusIcon.style.color = isHard ? new Color(0.9f, 0.3f, 0.3f) : new Color(1f, 0.7f, 0.2f);
                    help.text = problem;
                }
                else
                {
                    statusIcon.text = "\u2713";
                    statusIcon.style.color = new Color(0.3f, 0.8f, 0.4f);
                    help.text = "";
                }
            }

            input.RegisterValueChangedCallback(_ =>
            {
                ValidateThis();
                UpdatePlatformBadges();
            });

            validatorCallbacks.Add(ValidateThis);
            return wrapper;
        }

        VisualElement BuildBannerSettingsCard()
        {
            var card = LevelPlayUIStyle.CreateCard(
                "Banner Display Settings",
                "Control anchor position for banner ads on screen.");

            var bannerPosProp = serializedObject.FindProperty("bannerPosition");
            if (bannerPosProp != null)
            {
                var field = new PropertyField(bannerPosProp, "Banner Position");
                field.Bind(serializedObject);
                card.Add(field);
            }

            return card;
        }

        VisualElement BuildFooterActions()
        {
            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var dashBtn = new Button(() => Application.OpenURL(DashboardUrl)) { text = "LevelPlay Dashboard" };
            dashBtn.AddToClassList("lp-action-btn");
            actions.Add(dashBtn);

            var unitsBtn = new Button(() => Application.OpenURL(AdUnitsUrl)) { text = "Ad Units Dashboard" };
            unitsBtn.AddToClassList("lp-action-btn");
            actions.Add(unitsBtn);

            var recheckBtn = new Button(ValidateAll) { text = "Re-check Credentials" };
            recheckBtn.AddToClassList("lp-action-btn");
            actions.Add(recheckBtn);

            return actions;
        }

        void ValidateAll()
        {
            foreach (var callback in validatorCallbacks)
            {
                callback?.Invoke();
            }
            UpdatePlatformBadges();
        }

        void UpdatePlatformBadges()
        {
            if (serializedObject == null) return;
            serializedObject.Update();

            EvaluateBadge("android", androidStatusBadge);
            EvaluateBadge("ios", iosStatusBadge);
        }

        void EvaluateBadge(string prefix, Label badge)
        {
            if (badge == null) return;

            var appKey = serializedObject.FindProperty(prefix + "AppKey")?.stringValue;
            var interstitial = serializedObject.FindProperty(prefix + "InterstitialAdUnitId")?.stringValue;
            var rewarded = serializedObject.FindProperty(prefix + "RewardedAdUnitId")?.stringValue;

            if (!CredentialValidation.IsSet(appKey) || CredentialValidation.IsPlaceholder(appKey))
            {
                LevelPlayUIStyle.SetBadge(badge, "Missing App Key", "fail");
                return;
            }

            bool hasAnyFormat = CredentialValidation.IsSet(interstitial) || CredentialValidation.IsSet(rewarded);
            if (!hasAnyFormat)
            {
                LevelPlayUIStyle.SetBadge(badge, "No Ad Units", "warn");
                return;
            }

            if (!CredentialValidation.IsSet(interstitial) || !CredentialValidation.IsSet(rewarded))
            {
                LevelPlayUIStyle.SetBadge(badge, "Partially Configured", "warn");
                return;
            }

            LevelPlayUIStyle.SetBadge(badge, "Ready", "ok");
        }
    }
}
