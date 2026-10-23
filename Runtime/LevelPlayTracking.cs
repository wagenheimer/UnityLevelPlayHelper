using System;

using UnityEngine;

namespace Wagenheimer.LevelPlayHelper
{
    /// <summary>
    /// App Tracking Transparency status. Only iOS can return anything other than
    /// <see cref="NotSupported"/>; the helper resolves it before <c>LevelPlay.Init()</c>.
    /// </summary>
    public enum TrackingAuthorizationState
    {
        /// <summary>The status could not be read.</summary>
        Unknown,

        /// <summary>The native ATT alert has not been answered yet.</summary>
        NotDetermined,

        /// <summary>Tracking is blocked by a device restriction (for example Screen Time).</summary>
        Restricted,

        /// <summary>The player declined tracking (or "Allow Apps to Request to Track" is off).</summary>
        Denied,

        /// <summary>The player allowed tracking; the advertising identifier is available.</summary>
        Authorized,

        /// <summary>Not an iOS build - ATT does not apply.</summary>
        NotSupported
    }

    [Serializable]
    public class TrackingConfiguration
    {
        [Header("App Tracking Transparency (iOS)")]
        [Tooltip("Request ATT authorization before LevelPlay.Init(). Without the advertising identifier, personalized fill is lower.")]
        public bool requestAuthorization = true;

        [Tooltip("Value written to NSUserTrackingUsageDescription in the generated Info.plist. Describe how the advertising identifier is used - Apple rejects missing or vague descriptions.")]
        [TextArea(2, 4)]
        public string usageDescription =
            "Your advertising identifier is used to show you ads based on your interests and to measure ad performance.";
    }
}
