using System;
using System.Collections.Generic;
using System.Linq;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Last account state pulled from the LevelPlay API (applications + their ad units). The Cloud
    /// tab fills it on every fetch; the checklist reads it to cross-check the local credentials
    /// against the dashboard without issuing new HTTP calls on every refresh.
    /// </summary>
    internal static class LevelPlayCloudCache
    {
        static readonly List<LevelPlayApiClient.AppDto> apps = new List<LevelPlayApiClient.AppDto>();
        static readonly Dictionary<string, List<LevelPlayApiClient.AdUnitDto>> unitsByAppKey =
            new Dictionary<string, List<LevelPlayApiClient.AdUnitDto>>(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<LevelPlayApiClient.AppDto> Apps => apps;

        public static DateTime LastFetchUtc { get; private set; }

        public static bool HasData => apps.Count > 0;

        public static void SetApps(IEnumerable<LevelPlayApiClient.AppDto> list)
        {
            apps.Clear();
            if (list != null) apps.AddRange(list);
            LastFetchUtc = DateTime.UtcNow;
        }

        public static void SetUnits(string appKey, IEnumerable<LevelPlayApiClient.AdUnitDto> units)
        {
            if (string.IsNullOrEmpty(appKey)) return;

            unitsByAppKey[appKey] = units?.ToList() ?? new List<LevelPlayApiClient.AdUnitDto>();
            LastFetchUtc = DateTime.UtcNow;
        }

        public static void Clear()
        {
            apps.Clear();
            unitsByAppKey.Clear();
            LastFetchUtc = default;
        }

        public static LevelPlayApiClient.AppDto FindAppByKey(string appKey) =>
            string.IsNullOrEmpty(appKey)
                ? null
                : apps.FirstOrDefault(a => string.Equals(a.appKey, appKey, StringComparison.OrdinalIgnoreCase));

        public static List<LevelPlayApiClient.AdUnitDto> UnitsFor(string appKey) =>
            !string.IsNullOrEmpty(appKey) && unitsByAppKey.TryGetValue(appKey, out var units) ? units : null;

        /// <summary>Active ad networks the application report lists for a format, or null when unknown.</summary>
        public static string[] ActiveNetworks(LevelPlayApiClient.AppDto app, string format)
        {
            var units = app?.adUnits;
            if (units == null) return null;

            switch ((format ?? "").ToLowerInvariant())
            {
                case "rewarded": return units.rewardedVideo?.activeNetworks;
                case "interstitial": return units.interstitial?.activeNetworks;
                case "banner": return units.banner?.activeNetworks;
                default: return null;
            }
        }
    }
}
