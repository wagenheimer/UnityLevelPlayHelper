using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Unity.Services.LevelPlay;

namespace Wagenheimer.LevelPlayHelper
{
    /// <summary>Session totals for one ad network (provider), as reported by the LevelPlay callbacks.</summary>
    public sealed class AdNetworkStats
    {
        public AdNetworkStats(string network) => Network = network;

        /// <summary>Network name as the SDK reports it (for example <c>unityads</c>, <c>ironsource</c>).</summary>
        public string Network { get; }

        /// <summary>Loads this network won in the mediation auction.</summary>
        public int LoadsWon { get; internal set; }

        /// <summary>Ads from this network that reached the screen.</summary>
        public int Displays { get; internal set; }

        /// <summary>Impression-level revenue callbacks received.</summary>
        public int Impressions { get; internal set; }

        /// <summary>Sum of the impression revenue in USD (estimated by the SDK).</summary>
        public double Revenue { get; internal set; }

        public string LastFormat { get; internal set; }
        public string LastInstanceName { get; internal set; }
        public string LastPrecision { get; internal set; }
        public DateTime? LastSeenUtc { get; internal set; }

        internal AdNetworkStats Copy() => (AdNetworkStats)MemberwiseClone();
    }

    /// <summary>
    /// Per-network session statistics. Impression callbacks arrive on a background thread, so every
    /// member is thread-safe and <see cref="Snapshot"/> returns copies the UI can read freely.
    /// </summary>
    public sealed class LevelPlayNetworkStats
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, AdNetworkStats> networks =
            new Dictionary<string, AdNetworkStats>(StringComparer.OrdinalIgnoreCase);

        private string lastImpressionSummary;
        private string lastImpressionRaw;
        private int version;

        /// <summary>Bumped on every change, so a UI only rebuilds when something new arrived.</summary>
        public int Version { get { lock (gate) return version; } }

        /// <summary>Readable description of the last impression, or null before the first one.</summary>
        public string LastImpressionSummary { get { lock (gate) return lastImpressionSummary; } }

        /// <summary>Raw JSON of the last impression (<c>LevelPlayImpressionData.AllData</c>).</summary>
        public string LastImpressionRaw { get { lock (gate) return lastImpressionRaw; } }

        internal void RecordLoad(string format, LevelPlayAdInfo adInfo)
        {
            if (adInfo == null)
                return;

            lock (gate)
            {
                var stats = Get(adInfo.AdNetwork);
                stats.LoadsWon++;
                Touch(stats, format, adInfo.InstanceName, adInfo.Precision);
            }
        }

        internal void RecordDisplay(string format, LevelPlayAdInfo adInfo)
        {
            if (adInfo == null)
                return;

            lock (gate)
            {
                var stats = Get(adInfo.AdNetwork);
                stats.Displays++;
                Touch(stats, format, adInfo.InstanceName, adInfo.Precision);
            }
        }

        internal void RecordImpression(LevelPlayImpressionData impression)
        {
            if (impression == null)
                return;

            lock (gate)
            {
                var stats = Get(impression.AdNetwork);
                stats.Impressions++;
                stats.Revenue += impression.Revenue ?? 0d;
                Touch(stats, impression.AdFormat, impression.InstanceName, impression.Precision);

                lastImpressionSummary = DescribeImpression(impression);
                lastImpressionRaw = impression.AllData;
            }
        }

        /// <summary>Copies of every network, highest revenue first (then most loads won).</summary>
        public IReadOnlyList<AdNetworkStats> Snapshot()
        {
            lock (gate)
            {
                return networks.Values
                    .Select(s => s.Copy())
                    .OrderByDescending(s => s.Revenue)
                    .ThenByDescending(s => s.LoadsWon)
                    .ToList();
            }
        }

        public void Reset()
        {
            lock (gate)
            {
                networks.Clear();
                lastImpressionSummary = null;
                lastImpressionRaw = null;
                version++;
            }
        }

        /// <summary>One line per network, for the copy/paste diagnostic report.</summary>
        public void AppendReport(StringBuilder sb)
        {
            var snapshot = Snapshot();
            if (snapshot.Count == 0)
            {
                sb.AppendLine("Networks          : no loads or impressions yet");
                return;
            }

            sb.AppendLine("Networks (session):");
            foreach (var s in snapshot)
                sb.AppendLine($"    {s.Network,-14} won={s.LoadsWon} shown={s.Displays} imp={s.Impressions} revenue=${s.Revenue:F4} last={s.LastFormat ?? "-"}/{s.LastInstanceName ?? "-"}");

            var last = LastImpressionSummary;
            if (!string.IsNullOrEmpty(last))
                sb.AppendLine("Last impression   : " + last);
        }

        private AdNetworkStats Get(string network)
        {
            var key = string.IsNullOrWhiteSpace(network) ? "(unknown)" : network.Trim();
            if (!networks.TryGetValue(key, out var stats))
            {
                stats = new AdNetworkStats(key);
                networks[key] = stats;
            }

            return stats;
        }

        private void Touch(AdNetworkStats stats, string format, string instance, string precision)
        {
            if (!string.IsNullOrEmpty(format)) stats.LastFormat = format;
            if (!string.IsNullOrEmpty(instance)) stats.LastInstanceName = instance;
            if (!string.IsNullOrEmpty(precision)) stats.LastPrecision = precision;
            stats.LastSeenUtc = DateTime.UtcNow;
            version++;
        }

        private static string DescribeImpression(LevelPlayImpressionData i) =>
            $"{i.AdNetwork ?? "?"} / {i.AdFormat ?? "?"} / ${i.Revenue ?? 0d:F4} ({i.Precision ?? "?"}) " +
            $"instance={i.InstanceName ?? "-"} placement={i.Placement ?? "-"} country={i.Country ?? "-"} " +
            $"segment={i.SegmentName ?? "-"} adUnit={i.MediationAdUnitName ?? i.MediationAdUnitId ?? "-"}";
    }
}
