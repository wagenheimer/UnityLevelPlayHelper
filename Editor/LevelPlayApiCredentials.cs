using UnityEditor;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Stores the LevelPlay API credentials (Secret Key + Refresh Token) in EditorPrefs.
    /// These are account-level secrets: they are machine-local by design and are never written to
    /// the project, logged, or committed. The UI only ever shows them masked.
    /// </summary>
    internal static class LevelPlayApiCredentials
    {
        const string SecretKeyPref = "Wagenheimer.LevelPlayHelper.Api.SecretKey";
        const string RefreshTokenPref = "Wagenheimer.LevelPlayHelper.Api.RefreshToken";

        public static string SecretKey
        {
            get => EditorPrefs.GetString(SecretKeyPref, string.Empty);
            set => EditorPrefs.SetString(SecretKeyPref, value ?? string.Empty);
        }

        public static string RefreshToken
        {
            get => EditorPrefs.GetString(RefreshTokenPref, string.Empty);
            set => EditorPrefs.SetString(RefreshTokenPref, value ?? string.Empty);
        }

        public static bool HasCredentials =>
            !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(RefreshToken);

        public static void Save(string secretKey, string refreshToken)
        {
            SecretKey = secretKey;
            RefreshToken = refreshToken;
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(SecretKeyPref);
            EditorPrefs.DeleteKey(RefreshTokenPref);
        }
    }
}
