using System;
using System.Collections.Generic;
using System.Linq;

using UnityEditor;

namespace Wagenheimer.LevelPlayHelper.Editor
{
    /// <summary>
    /// Cross-checks the credentials configured on the helper against the account state pulled from
    /// the LevelPlay API. Shared by the Cloud tab (Verify button) and the checklist, so both report
    /// the same findings. It can only confirm what the API exposes: whether an Ad Unit ID belongs
    /// to the app, is not paused, uses the right format, and has an active network.
    /// </summary>
    internal static class LevelPlayVerifier
    {
        internal enum Level
        {
            Ok,
            Warning,
            Error
        }

        internal sealed class Finding
        {
            public Level Level;
            public string Platform = "";
            public string Format = "";
            public string Text = "";
        }

        internal sealed class Report
        {
            public readonly List<Finding> Findings = new List<Finding>();
            public bool HasAccountData;

            public int Errors => Findings.Count(f => f.Level == Level.Error);
            public int Warnings => Findings.Count(f => f.Level == Level.Warning);
            public int OkCount => Findings.Count(f => f.Level == Level.Ok);

            public bool Healthy => Errors == 0;
        }

        static readonly (string Label, string Prefix)[] Platforms =
        {
            ("Android", "android"),
            ("iOS", "ios")
        };

        static readonly string[] Formats = { "rewarded", "interstitial", "banner" };

        /// <summary>Verifies one helper. Pass the account data through <see cref="LevelPlayCloudCache"/> first.</summary>
        public static Report Verify(SerializedObject helper)
        {
            var report = new Report { HasAccountData = LevelPlayCloudCache.HasData };

            if (helper == null)
            {
                report.Findings.Add(new Finding { Level = Level.Error, Text = "No LevelPlayHelper found in the project." });
                return report;
            }

            if (!LevelPlayCloudCache.HasData)
            {
                report.Findings.Add(new Finding
                {
                    Level = Level.Warning,
                    Text = "No account data yet - the verification fetches it first (or press Fetch applications)."
                });
                return report;
            }

            foreach (var platform in Platforms)
            {
                var label = platform.Label;
                var prefix = platform.Prefix;

                var appKey = GetString(helper, prefix + "AppKey");
                if (string.IsNullOrEmpty(appKey))
                {
                    report.Findings.Add(new Finding { Level = Level.Error, Platform = label, Text = $"{label} App Key is empty." });
                    continue;
                }

                var app = LevelPlayCloudCache.FindAppByKey(appKey);
                if (app == null)
                {
                    report.Findings.Add(new Finding
                    {
                        Level = Level.Error,
                        Platform = label,
                        Text = $"{label} App Key {Mask(appKey)} is not an application on this account."
                    });
                    continue;
                }

                report.Findings.Add(new Finding
                {
                    Level = Level.Ok,
                    Platform = label,
                    Text = $"{label} App Key {Mask(appKey)} is valid ({app.appName})."
                });

                var units = LevelPlayCloudCache.UnitsFor(appKey);
                if (units == null)
                {
                    report.Findings.Add(new Finding
                    {
                        Level = Level.Warning,
                        Platform = label,
                        Text = $"{label}: ad units of {app.appName} were not fetched."
                    });
                    continue;
                }

                foreach (var format in Formats)
                {
                    var id = GetString(helper, FormatProperty(prefix, format));

                    // A format with no local Ad Unit ID is simply disabled here - not a problem.
                    if (string.IsNullOrEmpty(id)) continue;

                    var unit = units.FirstOrDefault(u => string.Equals(u.mediationAdUnitId, id, StringComparison.OrdinalIgnoreCase));

                    if (unit == null)
                    {
                        report.Findings.Add(new Finding
                        {
                            Level = Level.Error,
                            Platform = label,
                            Format = format,
                            Text = $"{label} {format}: Ad Unit ID {Mask(id)} does not belong to {app.appName} - the SDK rejects it as 'invalid ad unit id'."
                        });
                        continue;
                    }

                    if (!string.Equals(unit.adFormat, format, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Findings.Add(new Finding
                        {
                            Level = Level.Error,
                            Platform = label,
                            Format = format,
                            Text = $"{label} {format}: the ID is registered as '{unit.adFormat}' in the dashboard but used as '{format}'."
                        });
                        continue;
                    }

                    if (unit.isPaused)
                    {
                        report.Findings.Add(new Finding
                        {
                            Level = Level.Warning,
                            Platform = label,
                            Format = format,
                            Text = $"{label} {format}: the ad unit is PAUSED in the dashboard - it will not fill."
                        });
                        continue;
                    }

                    var networks = LevelPlayCloudCache.ActiveNetworks(app, format);
                    if (networks == null || networks.All(string.IsNullOrEmpty))
                    {
                        report.Findings.Add(new Finding
                        {
                            Level = Level.Warning,
                            Platform = label,
                            Format = format,
                            Text = $"{label} {format}: active and valid, but the ad unit has NO active network - no demand, it will not fill. Use Enable default networks."
                        });
                        continue;
                    }

                    report.Findings.Add(new Finding
                    {
                        Level = Level.Ok,
                        Platform = label,
                        Format = format,
                        Text = $"{label} {format}: OK - ID valid, not paused, networks: {string.Join(", ", networks.Where(n => !string.IsNullOrEmpty(n)))}."
                    });
                }
            }

            return report;
        }

        static string FormatProperty(string prefix, string format)
        {
            switch (format)
            {
                case "rewarded": return prefix + "RewardedAdUnitId";
                case "interstitial": return prefix + "InterstitialAdUnitId";
                case "banner": return prefix + "BannerAdUnitId";
                default: return prefix + "AdUnitId";
            }
        }

        static string GetString(SerializedObject so, string property)
        {
            var prop = so.FindProperty(property);
            return prop != null ? prop.stringValue : "";
        }

        static string Mask(string value) =>
            string.IsNullOrEmpty(value) ? "" : (value.Length > 4 ? value.Substring(0, 4) + "***" : value);
    }
}
