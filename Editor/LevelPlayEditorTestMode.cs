using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Editor-only "Enabled on Editor" switch. It temporarily adds the scripting defines that gate
    /// your game's ads/IAP assembly to the active build target, so that assembly compiles in Play
    /// mode: the LevelPlay SDK then serves mock ads and Unity IAP uses its fake store - no device,
    /// no credentials and no manual Player Settings editing.
    ///
    /// The switch snapshots the previous define set and restores it when turned off, and a build
    /// guard refuses to build while it is on so the defines can never leak into a release.
    /// </summary>
    internal static class LevelPlayEditorTestMode
    {
        const string Prefix = "Wagenheimer.LevelPlayHelper.TestMode.";
        const string EnabledKey = "Enabled";
        const string DefinesKey = "Defines";
        const string BlockBuildsKey = "BlockBuilds";
        const string SnapshotKey = "Snapshot";
        const string SnapshotTargetKey = "SnapshotTarget";

        // Unity's own symbols are never worth adding as "project" defines.
        static readonly string[] ReservedPrefixes = { "UNITY_", "UNITYEDITOR", "DEBUG", "TRACE" };

        static string ProjectKey
        {
            get
            {
                var path = Application.dataPath ?? "unknown";
                return Math.Abs(path.ToLowerInvariant().GetHashCode()).ToString("X8");
            }
        }

        static string Key(string name) => Prefix + name + "." + ProjectKey;

        // ------------------------------------------------------------------ state

        /// <summary>True while the defines are applied to the active build target.</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(Key(EnabledKey), false);
            private set => EditorPrefs.SetBool(Key(EnabledKey), value);
        }

        /// <summary>Refuse to build while the switch is on (default).</summary>
        public static bool BlockBuilds
        {
            get => EditorPrefs.GetBool(Key(BlockBuildsKey), true);
            set => EditorPrefs.SetBool(Key(BlockBuildsKey), value);
        }

        /// <summary>Defines the user configured, as raw text (one per line or separated by ';').</summary>
        public static string ConfiguredDefinesText
        {
            get => EditorPrefs.GetString(Key(DefinesKey), string.Empty);
            set => EditorPrefs.SetString(Key(DefinesKey), value ?? string.Empty);
        }

        /// <summary>
        /// Defines that will be applied: the configured ones, or - when nothing was configured -
        /// every project asmdef constraint that is not a Unity symbol (so a project gating its
        /// monetization assembly on e.g. FTDOTD_MONETIZATION needs no typing at all).
        /// </summary>
        public static List<string> EffectiveDefines
        {
            get
            {
                var configured = ParseDefines(ConfiguredDefinesText);
                if (configured.Count > 0)
                    return configured;

                return DetectCandidates().Where(IsProjectSpecific).ToList();
            }
        }

        static bool IsProjectSpecific(string define) =>
            !string.IsNullOrWhiteSpace(define)
            && !ReservedPrefixes.Any(p => define.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        static List<string> ParseDefines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();

            return text
                .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => d.Length > 0)
                .Distinct()
                .ToList();
        }

        /// <summary>Every <c>defineConstraints</c> entry found in the project's asmdefs.</summary>
        public static List<string> DetectCandidates()
        {
            var found = new List<string>();

            string[] files;
            try { files = Directory.GetFiles("Assets", "*.asmdef", SearchOption.AllDirectories); }
            catch { return found; }

            foreach (var file in files)
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }

                var block = Regex.Match(text, "\"defineConstraints\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (!block.Success)
                    continue;

                foreach (Match quoted in Regex.Matches(block.Groups[1].Value, "\"([^\"]+)\""))
                {
                    var define = quoted.Groups[1].Value.Trim();
                    if (define.Length > 0 && !found.Contains(define))
                        found.Add(define);
                }
            }

            return found.OrderBy(d => d).ToList();
        }

        // ------------------------------------------------------------------ target handling

        public static NamedBuildTarget ActiveTarget =>
            NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

        public static string ActiveTargetName => ActiveTarget.TargetName;

        static List<string> GetTargetDefines(NamedBuildTarget target)
        {
            var raw = PlayerSettings.GetScriptingDefineSymbols(target);
            return ParseDefines(raw);
        }

        static void SetTargetDefines(NamedBuildTarget target, IEnumerable<string> defines)
        {
            var value = string.Join(";", defines.Distinct());
            PlayerSettings.SetScriptingDefineSymbols(target, value);
        }

        // ------------------------------------------------------------------ apply / restore

        /// <summary>Turns the switch on or off, updating the active build target and recompiling.</summary>
        public static void SetEnabled(bool value)
        {
            if (value == Enabled)
                return;

            var target = ActiveTarget;

            if (value)
            {
                var defines = GetTargetDefines(target);
                EditorPrefs.SetString(Key(SnapshotKey), string.Join(";", defines));
                EditorPrefs.SetString(Key(SnapshotTargetKey), target.TargetName);
                EditorPrefs.SetBool(Key(EnabledKey), true);

                foreach (var define in EffectiveDefines)
                    if (!defines.Contains(define))
                        defines.Add(define);

                SetTargetDefines(target, defines);
                Debug.Log($"[LevelPlayHelper] Editor test mode ON for {target.TargetName}: {string.Join(", ", EffectiveDefines)}. " +
                          "Mock ads and the Unity IAP fake store are now available in Play mode. Builds are blocked until you turn it off.");
            }
            else
            {
                var snapshot = EditorPrefs.GetString(Key(SnapshotKey), string.Empty);
                var snapshotTarget = EditorPrefs.GetString(Key(SnapshotTargetKey), string.Empty);

                if (!string.IsNullOrEmpty(snapshotTarget) && snapshotTarget == target.TargetName)
                {
                    SetTargetDefines(target, ParseDefines(snapshot));
                    Debug.Log($"[LevelPlayHelper] Editor test mode OFF for {target.TargetName}: define set restored.");
                }
                else
                {
                    // The active target changed while the switch was on; removing them here is the
                    // best we can do, the snapshot is kept so the original target can be restored
                    // by switching back and toggling off again.
                    var defines = GetTargetDefines(target);
                    foreach (var define in EffectiveDefines)
                        defines.Remove(define);

                    SetTargetDefines(target, defines);
                    Debug.LogWarning($"[LevelPlayHelper] Editor test mode OFF, but the active target is now {target.TargetName} " +
                                     $"(the snapshot was taken on {snapshotTarget}). Removed the defines from {target.TargetName} only.");
                }

                EditorPrefs.SetBool(Key(EnabledKey), false);
            }

            CompilationPipeline.RequestScriptCompilation();
        }

        /// <summary>Defines that are applied right now but no longer wanted (or vice versa).</summary>
        public static bool IsConsistent(out string detail)
        {
            var target = ActiveTarget;
            var current = GetTargetDefines(target);
            var wanted = EffectiveDefines;

            var missing = wanted.Where(d => !current.Contains(d)).ToList();
            var present = wanted.Where(current.Contains).ToList();

            if (Enabled && missing.Count > 0)
            {
                detail = $"{target.TargetName} is missing {string.Join(", ", missing)} even though the switch is ON (Player Settings changed outside the checklist?).";
                return false;
            }

            if (!Enabled && present.Count > 0)
            {
                detail = $"{target.TargetName} still contains {string.Join(", ", present)} while the switch is OFF - it may leak into a build.";
                return false;
            }

            detail = Enabled
                ? $"Applied on {target.TargetName}: {string.Join(", ", present)}."
                : "No monetization define applied by the helper on " + target.TargetName + ".";
            return true;
        }

        /// <summary>One-line state for the checklist UI.</summary>
        public static string StatusText
        {
            get
            {
                if (Enabled)
                    return $"ON - applied to {ActiveTargetName}: {string.Join(", ", EffectiveDefines)}";

                var wanted = EffectiveDefines;
                return wanted.Count == 0
                    ? "OFF - no project define detected to enable (gate your monetization assembly with a define, or type one)."
                    : $"OFF - would apply {string.Join(", ", wanted)} to {ActiveTargetName}. Mock ads are off until you enable it.";
            }
        }
    }

    /// <summary>
    /// Stops a build while the Editor test mode switch is on, so the temporary defines can never
    /// end up in a shipped build (the build pipeline adds its own defines per profile).
    /// </summary>
    internal sealed class LevelPlayEditorTestModeBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!LevelPlayEditorTestMode.Enabled || !LevelPlayEditorTestMode.BlockBuilds)
                return;

            throw new BuildFailedException(
                "[LevelPlayHelper] Editor test mode is ON, so the scripting defines " +
                $"({string.Join(", ", LevelPlayEditorTestMode.EffectiveDefines)}) are applied to your Player Settings and would leak " +
                "into this build. Turn it off in Tools > Wagenheimer > Level Play Helper > Setup Checklist (section 'Editor play mode') " +
                "and build again - or uncheck 'Block builds while enabled' if you really want to build with them.");
        }
    }
}
