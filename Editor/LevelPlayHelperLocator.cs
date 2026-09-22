using UnityEditor;
using UnityEngine;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Finds the LevelPlayHelper instance the runtime will actually use. The prefab that lives
    /// under a Resources folder wins (that is the one <c>Resources.Load</c> instantiates), then
    /// any prefab, then the open scenes.
    /// </summary>
    internal static class LevelPlayHelperLocator
    {
        public static LevelPlayHelper FindPreferred()
        {
            // Fast path: the prefab is conventionally named after the component.
            var named = AssetDatabase.FindAssets("LevelPlayHelper t:Prefab");
            var found = SearchPrefabs(named);
            if (found != null) return found;

            // Fallback: any prefab carrying the component.
            return SearchPrefabs(AssetDatabase.FindAssets("t:Prefab"));
        }

        public static string LocationOf(LevelPlayHelper helper)
        {
            if (helper == null) return "";
            return AssetDatabase.GetAssetPath(helper);
        }

        static LevelPlayHelper SearchPrefabs(string[] guids)
        {
            LevelPlayHelper best = null;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var helper = prefab.GetComponentInChildren<LevelPlayHelper>(true);
                if (helper == null) continue;

                if (IsResources(path)) return helper;   // the runtime loads this one
                if (best == null) best = helper;
            }

            return best;
        }

        static bool IsResources(string assetPath) =>
            assetPath.Replace('\\', '/').IndexOf("/Resources/", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
