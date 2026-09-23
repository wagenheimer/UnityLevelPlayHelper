using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// UI Toolkit panel for ad pacing rules (AdsConfiguration) and
    /// privacy/consent regulations (ConsentConfiguration: GDPR, CCPA, COPPA).
    /// </summary>
    internal sealed class LevelPlayRulesPanel
    {
        const string PrivacyDocsUrl = "https://docs.unity.com/en-us/grow/levelplay/platform/regulation/privacy";

        public VisualElement Root { get; }

        readonly ScrollView scroll;
        readonly VisualElement body;
        SerializedObject serializedObject;
        LevelPlayHelper helper;

        public LevelPlayRulesPanel(SerializedObject serialized = null)
        {
            Root = new VisualElement();
            Root.AddToClassList("lp-root");
            LevelPlayUIStyle.Apply(Root);

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);

            body = scroll.contentContainer;
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

            if (helper == null || serializedObject == null)
            {
                body.Add(LevelPlayUIStyle.CreateCallout("No LevelPlayHelper found. Please configure on prefab.", "fail"));
                return;
            }

            serializedObject.Update();

            body.Add(LevelPlayUIStyle.CreateCallout(
                "Configure gameplay ad intervals to balance monetization with player retention, and ensure legal privacy compliance.",
                "info"));

            body.Add(BuildPacingCard());
            body.Add(BuildPrivacyCard());
            body.Add(BuildFooterActions());
        }

        VisualElement BuildPacingCard()
        {
            var badge = LevelPlayUIStyle.CreateBadge("Pacing Rules", "ok");
            var card = LevelPlayUIStyle.CreateCard(
                "Ad Pacing & Frequency",
                "Controls how often interstitial ads can appear during normal gameplay.",
                badge);

            var adsConfigProp = serializedObject.FindProperty("adsConfig");
            if (adsConfigProp != null)
            {
                var minInterval = adsConfigProp.FindPropertyRelative("minAdInterval");
                var initInterval = adsConfigProp.FindPropertyRelative("initialAdInterval");
                var neededToReduce = adsConfigProp.FindPropertyRelative("adsNeededToReduceInterval");

                if (initInterval != null)
                {
                    var f = new PropertyField(initInterval, "Initial Interval (Levels/Checks)");
                    f.Bind(serializedObject);
                    f.tooltip = "Number of game checkpoints/levels to wait before displaying the very first ad.";
                    card.Add(f);
                }

                if (minInterval != null)
                {
                    var f = new PropertyField(minInterval, "Minimum Interval (Levels/Checks)");
                    f.Bind(serializedObject);
                    f.tooltip = "Absolute minimum gameplay interval between consecutive interstitial ads.";
                    card.Add(f);
                }

                if (neededToReduce != null)
                {
                    var f = new PropertyField(neededToReduce, "Ads Needed to Reduce Interval");
                    f.Bind(serializedObject);
                    f.tooltip = "How many ads must be displayed before reducing the interval towards the minimum.";
                    card.Add(f);
                }
            }

            return card;
        }

        VisualElement BuildPrivacyCard()
        {
            var badge = LevelPlayUIStyle.CreateBadge("Compliance", "info");
            var card = LevelPlayUIStyle.CreateCard(
                "Privacy, Consent & Regulations",
                "Flag requirements for GDPR (Europe), CCPA (California), and COPPA (Child protection).",
                badge);

            var consentProp = serializedObject.FindProperty("consentConfig");
            if (consentProp != null)
            {
                var gdpr = consentProp.FindPropertyRelative("enableGDPRConsent");
                var ccpa = consentProp.FindPropertyRelative("ccpaOptOut");
                var coppa = consentProp.FindPropertyRelative("coppaChildDirected");

                if (gdpr != null)
                {
                    var f = new PropertyField(gdpr, "GDPR Consent Applied");
                    f.Bind(serializedObject);
                    f.tooltip = "Apply GDPR consent flag to the SDK prior to initialization.";
                    card.Add(f);
                }

                if (ccpa != null)
                {
                    var f = new PropertyField(ccpa, "CCPA Opt-Out (Do Not Sell)");
                    f.Bind(serializedObject);
                    f.tooltip = "Set CCPA opt-out flag for users in California who decline personal data sale.";
                    card.Add(f);
                }

                if (coppa != null)
                {
                    var f = new PropertyField(coppa, "COPPA Child-Directed App");
                    f.Bind(serializedObject);
                    f.tooltip = "Set true if your application is directed primarily to children under 13.";
                    card.Add(f);
                }
            }

            return card;
        }

        VisualElement BuildFooterActions()
        {
            var actions = new VisualElement();
            actions.AddToClassList("lp-actions-row");

            var docsBtn = new Button(() => Application.OpenURL(PrivacyDocsUrl)) { text = "LevelPlay Privacy Guidelines" };
            docsBtn.AddToClassList("lp-action-btn");
            actions.Add(docsBtn);

            return actions;
        }
    }
}

