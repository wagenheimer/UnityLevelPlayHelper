using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Edit the LevelPlayHelper credentials (App Keys + Ad Unit IDs per platform) with live
    /// validation, writing straight back to the helper prefab. This is the "configure" half of
    /// the setup window: the checklist only reports, this panel fixes.
    /// </summary>
    internal sealed class LevelPlayCredentialsPanel
    {
        const string DashboardUrl = "https://platform.ironsrc.com/";
        const string AdUnitsUrl = "https://platform.ironsrc.com/partners/adUnits";

        static readonly Color ColOk = new Color(0.298f, 0.686f, 0.314f);
        static readonly Color ColWarn = new Color(1.000f, 0.690f, 0.125f);
        static readonly Color ColFail = new Color(0.898f, 0.282f, 0.302f);
        static readonly Color ColDim = new Color(0.650f, 0.650f, 0.650f);
        static readonly Color ColAccent = new Color(0.290f, 0.565f, 0.851f);

        sealed class Field
        {
            public TextField Input;
            public string Property;
            public string Label;
            public bool IsAppKey;
        }

        public VisualElement Root { get; }

        VisualElement body;
        Label validationLabel;
        LevelPlayHelper helper;
        SerializedObject serialized;
        readonly List<Field> fields = new List<Field>();

        public LevelPlayCredentialsPanel()
        {
            Root = new VisualElement();
            Root.style.flexGrow = 1;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            Root.Add(scroll);

            body = scroll.contentContainer;
            body.style.paddingTop = 10;
            body.style.paddingBottom = 10;
            body.style.paddingLeft = 2;
            body.style.paddingRight = 14;

            Reload();
        }

        public void Reload()
        {
            body.Clear();
            fields.Clear();

            helper = LevelPlayHelperLocator.FindPreferred();

            if (helper == null)
            {
                body.Add(Info("No LevelPlayHelper found",
                    "No prefab or open scene contains the LevelPlayHelper component. Create the prefab " +
                    "(Assets/Resources/Monetization/LevelPlayHelper.prefab) and it will show up here for configuration.", ColFail));
                return;
            }

            serialized = new SerializedObject(helper);

            body.Add(Info("LevelPlayHelper credentials",
                "These values are written to the prefab " + LevelPlayHelperLocator.LocationOf(helper) +
                " - the object the runtime instantiates. These are NOT the Ads Mediation > Developer Settings.", ColAccent));

            body.Add(PlatformCard("Android", "android"));
            body.Add(PlatformCard("iOS", "ios"));
            body.Add(BuildActions());

            validationLabel = new Label();
            validationLabel.style.fontSize = 11;
            validationLabel.style.whiteSpace = WhiteSpace.Normal;
            validationLabel.style.marginTop = 6;
            validationLabel.style.marginBottom = 4;
            body.Add(validationLabel);

            body.Add(BuildLegend());

            Validate();
        }

        VisualElement PlatformCard(string platform, string prefix)
        {
            var card = new VisualElement();
            card.style.backgroundColor = new Color(1f, 1f, 1f, 0.055f);
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.marginBottom = 8;
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = ColAccent;

            var title = new Label(platform);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 12.5f;
            title.style.color = new Color(0.85f, 0.85f, 0.85f);
            card.Add(title);

            var hint = new Label(prefix == "ios"
                ? "Dashboard > Apps: copy the iOS app's App Key. Ad Units: one per format."
                : "Dashboard > Apps: copy the Android app's App Key. Ad Units: one per format.");
            hint.style.fontSize = 10;
            hint.style.color = ColDim;
            hint.style.marginBottom = 6;
            card.Add(hint);

            AddField(card, platform, prefix + "AppKey", "App Key", true);
            AddField(card, platform, prefix + "InterstitialAdUnitId", "Interstitial Ad Unit ID", false);
            AddField(card, platform, prefix + "RewardedAdUnitId", "Rewarded Ad Unit ID", false);
            AddField(card, platform, prefix + "BannerAdUnitId", "Banner Ad Unit ID (optional)", false);

            return card;
        }

        void AddField(VisualElement parent, string platform, string property, string label, bool isAppKey)
        {
            var prop = serialized.FindProperty(property);
            if (prop == null) return;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 2;

            var dot = new Label("\u25CF");
            dot.style.width = 14;
            dot.style.color = ColDim;
            dot.name = "dot-" + property;
            row.Add(dot);

            var fieldLabel = new Label(label);
            fieldLabel.style.width = 210;
            fieldLabel.style.fontSize = 11;
            fieldLabel.style.color = new Color(0.80f, 0.80f, 0.80f);
            row.Add(fieldLabel);

            var input = new TextField();
            input.value = prop.stringValue;
            input.style.flexGrow = 1;
            input.style.marginLeft = 4;
            input.style.marginRight = 4;
            row.Add(input);

            var note = new Label("");
            note.style.width = 230;
            note.style.fontSize = 10;
            note.style.color = ColDim;
            note.name = "note-" + property;
            row.Add(note);

            // Live validation while typing (does not write to the asset).
            input.RegisterValueChangedCallback(_ =>
            {
                ValidateField(property, input.value, isAppKey, note, dot);
                MarkDirty(true);
            });

            parent.Add(row);

            fields.Add(new Field { Input = input, Property = property, Label = platform + " " + label, IsAppKey = isAppKey });
        }

        VisualElement BuildActions()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.marginTop = 4;

            bar.style.flexWrap = Wrap.Wrap;

            var check = SmallButton("Check credentials", CheckAll);
            check.style.backgroundColor = new Color(ColAccent.r, ColAccent.g, ColAccent.b, 0.35f);
            bar.Add(check);

            var apply = SmallButton("Apply to prefab", Apply);
            apply.style.backgroundColor = new Color(ColOk.r, ColOk.g, ColOk.b, 0.35f);
            bar.Add(apply);

            bar.Add(SmallButton("Revert", Reload));
            bar.Add(SmallButton("Open Dashboard", () => Application.OpenURL(DashboardUrl)));
            bar.Add(SmallButton("Open Ad Units", () => Application.OpenURL(AdUnitsUrl)));

            return bar;
        }

        // The default Button style grows to fill its container, which made every button span the
        // whole inspector; zero the grow and let it size to its label.
        static Button SmallButton(string text, Action clicked)
        {
            var button = new Button(clicked) { text = text };
            button.style.height = 24;
            button.style.flexGrow = 0;
            button.style.alignSelf = Align.FlexStart;
            button.style.marginRight = 6;
            button.style.marginBottom = 4;
            button.style.paddingLeft = 12;
            button.style.paddingRight = 12;
            return button;
        }

        VisualElement BuildLegend()
        {
            var legend = new Label("\u25CF valid   \u25CB empty/placeholder   ! invalid format (the SDK rejects it as 'invalid ad unit id')");
            legend.style.fontSize = 9.5f;
            legend.style.color = ColDim;
            legend.style.marginTop = 6;
            return legend;
        }

        void Apply()
        {
            if (helper == null || serialized == null) return;

            Undo.RegisterCompleteObjectUndo(helper, "Edit LevelPlay credentials");

            foreach (var field in fields)
            {
                var prop = serialized.FindProperty(field.Property);
                if (prop != null) prop.stringValue = field.Input.value;
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(helper);
            AssetDatabase.SaveAssets();

            MarkDirty(false);
            Validate();
        }

        bool dirty;

        void MarkDirty(bool value)
        {
            dirty = value;
            Root.Query<Button>().ForEach(b =>
            {
                if (b.text == "Apply to prefab" || b.text == "Apply to prefab *")
                    b.text = value ? "Apply to prefab *" : "Apply to prefab";
            });
        }

        void Validate()
        {
            foreach (var field in fields)
            {
                var note = Root.Q<Label>("note-" + field.Property);
                var dot = Root.Q<Label>("dot-" + field.Property);
                ValidateField(field.Property, field.Input.value, field.IsAppKey, note, dot);
            }
        }

        // Explicit "does everything validate?" pass over every credential, with a summary.
        void CheckAll()
        {
            Validate();

            var problems = new List<string>();
            var warnings = new List<string>();

            foreach (var field in fields)
            {
                var value = field.Input.value;
                var optional = field.Property.EndsWith("BannerAdUnitId");

                if (!CredentialValidation.IsSet(value))
                {
                    if (optional) warnings.Add(field.Label + ": empty (banner disabled)");
                    else problems.Add(field.Label + ": empty");
                    continue;
                }

                if (CredentialValidation.IsPlaceholder(value))
                {
                    problems.Add(field.Label + ": placeholder value");
                    continue;
                }

                var reason = CredentialValidation.DescribeProblem(value, field.IsAppKey);
                if (CredentialValidation.IsHardProblem(reason)) problems.Add(field.Label + ": " + reason);
                else if (reason != null) warnings.Add(field.Label + ": " + reason);
            }

            if (problems.Count == 0 && warnings.Count == 0)
            {
                SetValidation("OK - every credential is valid.", ColOk);
                return;
            }

            if (problems.Count == 0)
            {
                SetValidation("Valid, with warnings:\n- " + string.Join("\n- ", warnings), ColWarn);
                return;
            }

            var text = "Problems found:\n- " + string.Join("\n- ", problems);
            if (warnings.Count > 0) text += "\nWarnings:\n- " + string.Join("\n- ", warnings);
            SetValidation(text, ColFail);
        }

        void SetValidation(string text, Color color)
        {
            if (validationLabel == null) return;
            validationLabel.text = text;
            validationLabel.style.color = color;
        }

        static void ValidateField(string property, string value, bool isAppKey, Label note, Label dot)
        {
            if (dot != null) dot.style.color = ColDim;
            if (note != null) note.text = "";

            if (!CredentialValidation.IsSet(value))
            {
                if (note != null)
                {
                    note.text = property.EndsWith("BannerAdUnitId") ? "empty (disables banner)" : "empty";
                    note.style.color = property.EndsWith("BannerAdUnitId") ? ColWarn : ColFail;
                }
                if (dot != null && !property.EndsWith("BannerAdUnitId")) dot.style.color = ColFail;
                return;
            }

            if (CredentialValidation.IsPlaceholder(value))
            {
                if (dot != null) dot.style.color = ColFail;
                if (note != null) { note.text = "placeholder"; note.style.color = ColFail; }
                return;
            }

            var reason = CredentialValidation.DescribeProblem(value, isAppKey);
            if (reason == null || !CredentialValidation.IsHardProblem(reason))
            {
                if (dot != null) dot.style.color = ColOk;
                if (note != null && reason != null) { note.text = reason; note.style.color = ColWarn; }
                return;
            }

            if (dot != null) dot.style.color = ColFail;
            if (note != null) { note.text = reason; note.style.color = ColFail; }
        }

        static VisualElement Info(string title, string body, Color accent)
        {
            var box = new VisualElement();
            box.style.backgroundColor = new Color(accent.r, accent.g, accent.b, 0.12f);
            box.style.borderLeftWidth = 3;
            box.style.borderLeftColor = accent;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.marginBottom = 8;

            var t = new Label(title);
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.fontSize = 11.5f;
            t.style.color = new Color(0.85f, 0.85f, 0.85f);
            box.Add(t);

            var b = new Label(body);
            b.style.fontSize = 10.5f;
            b.style.color = ColDim;
            b.style.whiteSpace = WhiteSpace.Normal;
            b.style.marginTop = 2;
            box.Add(b);

            return box;
        }
    }
}
