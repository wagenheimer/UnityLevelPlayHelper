using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEditor;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Consent-gate checks for <see cref="SetupChecklistView"/>.
    ///
    /// The checklist must find the gate the project actually uses (prefab or open scene) and
    /// verify that its <see cref="ConsentLocalization"/> covers every required language.
    /// </summary>
    internal sealed partial class SetupChecklistView
    {
        private const string ConsentDocsUrl =
            "https://docs.unity.com/en-us/grow/levelplay/sdk/unity/regulation-advanced-settings";

        private void CheckConsentGate()
        {
            var gates = FindConsentGates();

            if (gates.Count == 0)
            {
                bool gdprEnabled = helpers
                    .Select(h => h.Component)
                    .Distinct()
                    .Any(h =>
                    {
                        var so = new SerializedObject(h);
                        return GetNestedBool(so.FindProperty("consentConfig"), "enableGDPRConsent", true);
                    });

                Add(gdprEnabled ? CheckStatus.Warning : CheckStatus.Info, "Consent gate not found",
                    gdprEnabled
                        ? "GDPR consent is enabled on the helper, but there is no ConsentGate in the project. " +
                          "The SDK is initialized before the player can answer, which is not a valid consent flow. " +
                          "Add the ConsentGate component next to LevelPlayHelper."
                        : "No ConsentGate component found. Add one when your app must collect GDPR, US-state or COPPA consent before ads initialize.",
                    ConsentDocsUrl);
                return;
            }

            foreach (var gate in gates)
            {
                var so = new SerializedObject(gate);
                string where = DescribeGate(gate);

                var localizationProp = so.FindProperty("localization");
                var localization = localizationProp != null
                    ? localizationProp.objectReferenceValue as ConsentLocalization
                    : gate.Localization;

                if (localization == null)
                {
                    Add(CheckStatus.Fail, "Consent gate has no localization",
                        "The player would see the built-in English fallback. Assign a ConsentLocalization asset so the dialog follows the device language.",
                        ConsentDocsUrl,
                        "Create localization asset",
                        () => CreateConsentLocalizationFor(gate))
                        .WithFacts("gate: " + where);
                }
                else
                {
                    Add(CheckStatus.Pass, "Consent gate has localization",
                        "The gate resolves the dialog strings from '" + localization.name + "'.",
                        ConsentDocsUrl)
                        .WithFacts("gate: " + where,
                                   "languages: " + DescribeLanguages(localization));
                }

                bool gateInit = true;
                var gateInitProp = so.FindProperty("gateSdkInitialization");
                if (gateInitProp != null)
                    gateInit = gateInitProp.boolValue;

                Add(gateInit ? CheckStatus.Pass : CheckStatus.Warning,
                    "Consent gate controls SDK initialization",
                    gateInit
                        ? "LevelPlayHelper.InitializationGate stays closed until the player answers."
                        : "The gate is not wired to InitializationGate. LevelPlay may initialize before consent is collected.",
                    ConsentDocsUrl)
                    .WithFacts("gate: " + where);

                var privacy = so.FindProperty("privacyPolicyUrl");
                var terms = so.FindProperty("termsUrl");
                bool hasPrivacy = privacy != null && !string.IsNullOrWhiteSpace(privacy.stringValue);
                bool hasTerms = terms != null && !string.IsNullOrWhiteSpace(terms.stringValue);

                if (!hasPrivacy || !hasTerms)
                {
                    Add(CheckStatus.Warning, "Consent gate legal links",
                        "Store policies expect a reachable privacy policy (and usually terms). Empty links are hidden from the dialog.",
                        ConsentDocsUrl)
                        .WithFacts("privacyPolicyUrl: " + (hasPrivacy ? "set" : "empty"),
                                   "termsUrl: " + (hasTerms ? "set" : "empty"));
                }
            }
        }

        private void CheckConsentTranslations()
        {
            var assets = FindConsentLocalizations();
            if (assets.Count == 0)
                return;

            foreach (var localization in assets)
            {
                var required = localization.requiredLanguageCodes;
                if (required == null || required.Count == 0)
                {
                    Add(CheckStatus.Warning, "Consent translations not declared (" + localization.name + ")",
                        "No required languages are listed, so the checklist cannot prove the dialog is translated. " +
                        "List every shipped language code (for example en, pt-BR, es, fr, de, it, nl, ru, ko).",
                        ConsentDocsUrl);
                    continue;
                }

                var missing = localization.MissingRequiredLanguages();
                var incomplete = new List<string>();

                foreach (string code in required)
                {
                    if (string.IsNullOrWhiteSpace(code))
                        continue;

                    var fields = localization.IncompleteFields(code.Trim());
                    if (fields.Count > 0)
                        incomplete.Add(code.Trim() + " (" + string.Join(", ", fields) + ")");
                }

                var facts = new List<string>
                {
                    "required: " + string.Join(", ", required.Where(c => !string.IsNullOrWhiteSpace(c))),
                    "available: " + DescribeLanguages(localization)
                };

                if (missing.Count > 0)
                    facts.Add("missing: " + string.Join(", ", missing));

                if (incomplete.Count > 0)
                    facts.Add("incomplete: " + string.Join("; ", incomplete));

                var defaultEntry = localization.Find(localization.defaultLanguageCode);
                if (defaultEntry == null)
                    facts.Add("default language '" + localization.defaultLanguageCode + "' has no entry");

                CheckResult item = Add(
                    missing.Count == 0 && incomplete.Count == 0 && defaultEntry != null
                        ? CheckStatus.Pass
                        : CheckStatus.Fail,
                    "Consent translations (" + localization.name + ")",
                    missing.Count == 0 && incomplete.Count == 0 && defaultEntry != null
                        ? "Every required language is complete."
                        : "The consent dialog would fall back to English for the languages above. Fill the missing strings before shipping.",
                    ConsentDocsUrl);

                item.WithFacts(facts.ToArray());

                if (missing.Count > 0 || incomplete.Count > 0)
                {
                    item.ActionLabel = "Select localization";
                    item.Action = () =>
                    {
                        Selection.activeObject = localization;
                        EditorGUIUtility.PingObject(localization);
                    };
                }
            }
        }

        // ---------------------------------------------------------------- discovery

        private List<ConsentGate> FindConsentGates()
        {
            var result = new List<ConsentGate>();
            var seen = new HashSet<ConsentGate>();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                foreach (var gate in prefab.GetComponentsInChildren<ConsentGate>(true))
                {
                    if (gate != null && seen.Add(gate))
                        result.Add(gate);
                }
            }

            foreach (var gate in Resources.FindObjectsOfTypeAll<ConsentGate>())
            {
                if (gate == null || EditorUtility.IsPersistent(gate))
                    continue;
                if (!gate.gameObject.scene.IsValid())
                    continue;
                if (seen.Add(gate))
                    result.Add(gate);
            }

            return result;
        }

        private List<ConsentLocalization> FindConsentLocalizations()
        {
            var result = new List<ConsentLocalization>();

            foreach (string guid in AssetDatabase.FindAssets("t:ConsentLocalization"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ConsentLocalization>(path);
                if (asset != null && !result.Contains(asset))
                    result.Add(asset);
            }

            foreach (var gate in FindConsentGates())
            {
                if (gate.Localization != null && !result.Contains(gate.Localization))
                    result.Add(gate.Localization);
            }

            return result;
        }

        // ---------------------------------------------------------------- helpers

        private static string DescribeGate(ConsentGate gate)
        {
            string path = AssetDatabase.GetAssetPath(gate);
            if (!string.IsNullOrEmpty(path))
                return path;

            var scene = gate.gameObject.scene;
            if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
                return scene.path;

            return gate.gameObject.name;
        }

        private static string DescribeLanguages(ConsentLocalization localization)
        {
            if (localization.entries == null || localization.entries.Count == 0)
                return "none";

            return string.Join(", ", localization.entries
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.languageCode))
                .Select(e => e.languageCode.Trim()));
        }

        private static void CreateConsentLocalizationFor(ConsentGate gate)
        {
            const string folder = "Assets/LevelPlay/Resources";
            EnsureFolder(folder);

            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/ConsentLocalization.asset");
            var asset = ScriptableObject.CreateInstance<ConsentLocalization>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            var so = new SerializedObject(gate);
            var prop = so.FindProperty("localization");
            if (prop != null)
            {
                prop.objectReferenceValue = asset;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(gate);
                AssetDatabase.SaveAssets();
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
                return;

            parent = parent.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
