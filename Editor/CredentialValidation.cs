using System;
using System.Linq;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Single source of truth for how a LevelPlay credential should look. Shared by the
    /// LevelPlayHelper inspector and the Setup Checklist so both flag the same problems.
    /// </summary>
    internal static class CredentialValidation
    {
        public static bool IsSet(string value) => !string.IsNullOrWhiteSpace(value);

        public static bool IsPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            var lower = value.ToLowerInvariant();
            return lower.Contains("your_")
                   || lower.Contains("yourapp")
                   || lower.Contains("placeholder")
                   || lower == "test"
                   || lower == "editor"
                   || lower == "dummy";
        }

        /// <summary>
        /// Returns a human reason when the value cannot be a LevelPlay credential, otherwise null.
        /// These are exactly the mistakes that make the SDK reject an ad unit at runtime with
        /// "invalid ad unit id" even though the Inspector field is not empty.
        /// </summary>
        public static string DescribeProblem(string value, bool isAppKey)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            if (value.IndexOf("ca-app-pub", StringComparison.OrdinalIgnoreCase) >= 0)
                return "is an AdMob unit id - it belongs in Ads Mediation > Developer Settings, not in a LevelPlay field";
            if (value.Contains("://"))
                return "looks like a URL";
            if (value.Any(char.IsWhiteSpace))
                return "contains whitespace";
            if (value.Contains("-") || value.Contains("_"))
                return "contains '-' or '_' - LevelPlay credentials have neither";
            if (value.Any(char.IsUpper))
                return "contains uppercase letters - LevelPlay credentials are lowercase";
            if (!value.All(char.IsLetterOrDigit))
                return "contains non-alphanumeric characters";

            // Length is a soft signal: the canonical widths, but variations exist, so only warn.
            if (isAppKey && value.Length != 9)
                return "length " + value.Length + " (App Keys are usually 9 characters)";
            if (!isAppKey && (value.Length < 6 || value.Length > 32))
                return "length " + value.Length + " (Ad Unit IDs are usually 6-32 characters)";

            return null;
        }

        /// <summary>True when the problem is a hard error (vs. a soft length hint).</summary>
        public static bool IsHardProblem(string reason) => reason != null && !reason.StartsWith("length", StringComparison.Ordinal);
    }
}
