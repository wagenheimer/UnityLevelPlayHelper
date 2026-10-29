using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Wagenheimer.LevelPlayHelper
{
    /// <summary>
    /// Localized strings for the <see cref="ConsentGate"/> dialog.
    ///
    /// The package deliberately keeps localization data-driven instead of depending on a
    /// specific localization package: add one <see cref="Entry"/> per language, list the
    /// languages your game ships in <see cref="requiredLanguageCodes"/>, and the editor
    /// checklist will verify that every required language is complete.
    /// </summary>
    [CreateAssetMenu(
        menuName = "Tools/Wagenheimer/Level Play/Consent Localization",
        fileName = "ConsentLocalization",
        order = 20)]
    public sealed class ConsentLocalization : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("Language code, e.g. en, pt-BR, es, fr, de, it, nl, ru, ko.")]
            public string languageCode = "en";

            [Tooltip("Dialog title.")]
            public string title = "Privacy & consent";

            [Tooltip("Main explanation shown above the buttons.")]
            [TextArea(3, 8)]
            public string body =
                "We work with advertising partners to keep this game free. With your consent, they may use " +
                "your advertising identifier to show personalized ads. You can change this choice at any time.";

            [Tooltip("Accept button label.")]
            public string accept = "Accept";

            [Tooltip("Decline button label.")]
            public string decline = "Decline";

            [Tooltip("Privacy policy link label.")]
            public string privacyPolicy = "Privacy Policy";

            [Tooltip("Terms of service link label.")]
            public string terms = "Terms of Service";

            [Tooltip("Optional font for this language. Use it when the theme font does not cover the script " +
                     "(for example a Korean font for ko and a Cyrillic font for ru). Falls back to ConsentGate.UiFont.")]
            public Font font;
        }

        [Tooltip("Language used when the device language has no entry. Must exist in the list below.")]
        public string defaultLanguageCode = "en";

        [Tooltip("Languages the editor checklist requires to be complete. Codes not listed here are optional.")]
        public List<string> requiredLanguageCodes = new List<string> { "en" };

        [Tooltip("One entry per language.")]
        public List<Entry> entries = new List<Entry>();

        /// <summary>Finds an entry by language code, accepting exact, case-insensitive and prefix matches.</summary>
        public Entry Find(string languageCode)
        {
            if (entries == null || entries.Count == 0)
                return null;

            string wanted = Normalize(languageCode);
            if (string.IsNullOrEmpty(wanted))
                return null;

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry != null && Normalize(entry.languageCode) == wanted)
                    return entry;
            }

            // "pt-BR" falls back to "pt" (and the other way around).
            string wantedPrefix = Prefix(wanted);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry != null && Prefix(Normalize(entry.languageCode)) == wantedPrefix)
                    return entry;
            }

            return null;
        }

        /// <summary>Entry for the current device language, then the configured default, then the first entry.</summary>
        public Entry ResolveSystem()
        {
            Entry entry = Find(SystemLanguageCode());
            if (entry != null) return entry;

            entry = Find(defaultLanguageCode);
            if (entry != null) return entry;

            if (entries != null && entries.Count > 0)
                return entries[0];

            return null;
        }

        /// <summary>True when the language exists and has every mandatory string filled in.</summary>
        public bool HasCompleteEntry(string languageCode)
        {
            Entry entry = Find(languageCode);
            return entry != null
                   && !string.IsNullOrWhiteSpace(entry.title)
                   && !string.IsNullOrWhiteSpace(entry.body)
                   && !string.IsNullOrWhiteSpace(entry.accept)
                   && !string.IsNullOrWhiteSpace(entry.decline);
        }

        /// <summary>Returns every required language that is missing or incomplete.</summary>
        public List<string> MissingRequiredLanguages()
        {
            var missing = new List<string>();
            if (requiredLanguageCodes == null)
                return missing;

            for (int i = 0; i < requiredLanguageCodes.Count; i++)
            {
                string code = requiredLanguageCodes[i];
                if (string.IsNullOrWhiteSpace(code))
                    continue;
                if (!HasCompleteEntry(code) && !missing.Contains(code.Trim()))
                    missing.Add(code.Trim());
            }

            return missing;
        }

        /// <summary>Returns the mandatory fields that are empty for a language (empty when complete).</summary>
        public List<string> IncompleteFields(string languageCode)
        {
            var fields = new List<string>();
            Entry entry = Find(languageCode);
            if (entry == null)
            {
                fields.Add("entry missing");
                return fields;
            }

            if (string.IsNullOrWhiteSpace(entry.title)) fields.Add("title");
            if (string.IsNullOrWhiteSpace(entry.body)) fields.Add("body");
            if (string.IsNullOrWhiteSpace(entry.accept)) fields.Add("accept");
            if (string.IsNullOrWhiteSpace(entry.decline)) fields.Add("decline");
            return fields;
        }

        /// <summary>Current device language as a BCP-47-ish code, or <see cref="defaultLanguageCode"/>.</summary>
        public static string SystemLanguageCode()
        {
            string code = ToCode(Application.systemLanguage);
            if (!string.IsNullOrEmpty(code))
                return code;

            try
            {
                string culture = CultureInfo.CurrentUICulture != null
                    ? CultureInfo.CurrentUICulture.Name
                    : null;
                if (!string.IsNullOrWhiteSpace(culture))
                    return culture;
            }
            catch
            {
                // CultureInfo can throw on stripped platforms; the default entry is enough.
            }

            return "en";
        }

        /// <summary>Maps a Unity <see cref="SystemLanguage"/> to a language code.</summary>
        public static string ToCode(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.English: return "en";
                case SystemLanguage.Portuguese: return "pt-BR";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified: return "zh-Hans";
                case SystemLanguage.ChineseTraditional: return "zh-Hant";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Greek: return "el";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Romanian: return "ro";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Vietnamese: return "vi";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Arabic: return "ar";
                case SystemLanguage.Hebrew: return "he";
                case SystemLanguage.Hindi: return "hi";
                default: return null;
            }
        }

        /// <summary>Lower-cases and converts underscores to dashes so pt_BR and pt-BR match.</summary>
        public static string Normalize(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return string.Empty;

            return languageCode.Trim().Replace('_', '-').ToLowerInvariant();
        }

        static string Prefix(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
                return string.Empty;

            int dash = languageCode.IndexOf('-');
            return dash > 0 ? languageCode.Substring(0, dash) : languageCode;
        }
    }
}
