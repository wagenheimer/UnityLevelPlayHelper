#if UNITY_IOS
using System.IO;

using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Writes the helper's App Tracking Transparency purpose string into the generated
    /// Info.plist. Unity 6 has no Player Settings field for NSUserTrackingUsageDescription,
    /// and iOS silently skips the ATT alert when the key is missing.
    /// </summary>
    internal static class LevelPlayTrackingBuildProcessor
    {
        [PostProcessBuild(1000)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            var helper = LevelPlayHelperLocator.FindPreferred();
            if (helper == null)
                return;

            var config = helper.TrackingConfig ?? new TrackingConfiguration();
            if (!config.requestAuthorization)
                return;

            if (string.IsNullOrWhiteSpace(config.usageDescription))
            {
                // The setup checklist reports this as a failure; do not write an empty key.
                UnityEngine.Debug.LogWarning("[LevelPlayHelper] ATT is enabled but the usage description is empty - NSUserTrackingUsageDescription was not written to Info.plist.");
                return;
            }

            var plistPath = Path.Combine(path, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetString("NSUserTrackingUsageDescription", config.usageDescription.Trim());
            File.WriteAllText(plistPath, plist.WriteToString());
        }
    }
}
#endif
