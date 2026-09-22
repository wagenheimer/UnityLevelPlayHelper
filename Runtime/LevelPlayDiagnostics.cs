using System;
using System.Text;

namespace Wagenheimer.LevelPlayHelper
{
    /// <summary>Lifecycle of the LevelPlay SDK initialization, as seen by the helper.</summary>
    public enum SdkInitState
    {
        /// <summary><see cref="LevelPlayHelper.Initialize"/> has not run yet.</summary>
        NotStarted,

        /// <summary><see cref="Unity.Services.LevelPlay.LevelPlay.Init"/> was called and the callback has not arrived yet.</summary>
        Initializing,

        /// <summary>The SDK reported success.</summary>
        Initialized,

        /// <summary>The init callback never arrived (harmless in the Editor: mock ads work without it).</summary>
        CallbackMissing,

        /// <summary>The SDK reported a failure; the helper retries automatically.</summary>
        Failed
    }

    /// <summary>State of a single ad format (interstitial / rewarded / banner).</summary>
    public enum AdFormatState
    {
        /// <summary>No Ad Unit ID for this platform - the format is disabled.</summary>
        NotConfigured,

        /// <summary>Configured and idle (nothing loading, nothing loaded).</summary>
        Idle,

        /// <summary>A load is in flight.</summary>
        Loading,

        /// <summary>Loaded and ready to show.</summary>
        Ready,

        /// <summary>Currently on screen.</summary>
        Showing,

        /// <summary>Shown and dismissed by the user.</summary>
        Closed,

        /// <summary>The last load or display failed (see <see cref="AdFormatDiagnostics.LastErrorMessage"/>).</summary>
        Failed
    }

    /// <summary>Severity of a diagnostic log entry.</summary>
    public enum AdLogLevel
    {
        Info,
        Success,
        Warning,
        Error,
        Revenue
    }

    /// <summary>
    /// A single timestamped diagnostic log entry. <see cref="AdLogEntry.BackgroundThread"/> is true
    /// for impression/ILRD callbacks, which fire off the main thread - UI consumers must marshal.
    /// </summary>
    public sealed class AdLogEntry
    {
        public AdLogEntry(DateTime timestampUtc, AdLogLevel level, string message, bool backgroundThread)
        {
            TimestampUtc = timestampUtc;
            Level = level;
            Message = message;
            BackgroundThread = backgroundThread;
        }

        public DateTime TimestampUtc { get; }
        public AdLogLevel Level { get; }
        public string Message { get; }
        public bool BackgroundThread { get; }

        /// <summary>Single-line representation: <c>[12:34:56.789] WARNING [bg] message</c>.</summary>
        public string ToLine()
        {
            var bg = BackgroundThread ? "[bg] " : string.Empty;
            return $"[{TimestampUtc.ToLocalTime():HH:mm:ss.fff}] {Level.ToString().ToUpperInvariant(),-7} {bg}{Message}";
        }

        public override string ToString() => ToLine();
    }

    /// <summary>
    /// Per-format diagnostic snapshot: the state machine plus the last error and the retry schedule.
    /// Read-only for consumers - the helper owns the values.
    /// </summary>
    public sealed class AdFormatDiagnostics
    {
        /// <summary>"Interstitial", "Rewarded" or "Banner".</summary>
        public string Format;

        /// <summary>True when a real Ad Unit ID is set in the Inspector for the current platform.</summary>
        public bool Configured;

        /// <summary>True when the Editor mock Ad Unit ID is standing in for the real one.</summary>
        public bool UsesMockId;

        /// <summary>Ad Unit ID actually used (real or Editor mock).</summary>
        public string AdUnitId;

        /// <summary>Current state of the format.</summary>
        public AdFormatState State;

        /// <summary>True while a load is in flight.</summary>
        public bool IsLoading;

        /// <summary>UTC instant the current load started (null when not loading).</summary>
        public DateTime? LoadingSinceUtc;

        /// <summary>Consecutive load failures (drives the exponential backoff).</summary>
        public int RetryAttempt;

        /// <summary>UTC instant of the next automatic retry (null when no retry is scheduled).</summary>
        public DateTime? NextRetryAtUtc;

        /// <summary>Error code of the last failure, as reported by the SDK.</summary>
        public string LastErrorCode;

        /// <summary>Message of the last failure.</summary>
        public string LastErrorMessage;

        /// <summary>UTC instant of the last failure.</summary>
        public DateTime? LastErrorAtUtc;

        /// <summary><c>LevelPlayAdInfo.ToString()</c> of the last load (network, placement, ...).</summary>
        public string LastAdInfo;

        /// <summary>Ad network that served the last ad ("UnityAds", "AdMob", ...).</summary>
        public string LastAdNetwork;

        /// <summary>Placement name of the last ad.</summary>
        public string LastPlacementName;

        /// <summary>Revenue (USD) of the last loaded/displayed ad, when reported.</summary>
        public double? LastRevenue;

        /// <summary>UTC instant of the last successful load.</summary>
        public DateTime? LastLoadedAtUtc;

        /// <summary>Total successful loads since launch.</summary>
        public int LoadsSucceeded;

        /// <summary>Total failed loads since launch.</summary>
        public int LoadsFailed;

        /// <summary>Seconds until the next retry, or null when none is scheduled.</summary>
        public float? NextRetryInSeconds =>
            NextRetryAtUtc.HasValue
                ? (float?)Math.Max(0d, (NextRetryAtUtc.Value - DateTime.UtcNow).TotalSeconds)
                : null;

        /// <summary>Seconds the current load has been running, or null when not loading.</summary>
        public float? LoadingForSeconds =>
            IsLoading && LoadingSinceUtc.HasValue
                ? (float?)(DateTime.UtcNow - LoadingSinceUtc.Value).TotalSeconds
                : null;

        /// <summary>Short one-line summary used by the overlay and the diagnostic report.</summary>
        public string ToSummaryLine(bool ready)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(Format).Append("] state=").Append(State);
            sb.Append(" ready=").Append(ready);
            sb.Append(" loading=").Append(IsLoading);
            sb.Append(" configured=").Append(Configured);
            if (UsesMockId) sb.Append(" (mock)");
            sb.Append(" id=").Append(string.IsNullOrEmpty(AdUnitId) ? "(none)" : AdUnitId);
            sb.Append(" retry=").Append(RetryAttempt);
            var next = NextRetryInSeconds;
            if (next.HasValue) sb.Append(" nextRetryIn=").Append(next.Value.ToString("F0")).Append('s');
            sb.Append(" loads=").Append(LoadsSucceeded).Append('/').Append(LoadsFailed);
            return sb.ToString();
        }
    }
}
