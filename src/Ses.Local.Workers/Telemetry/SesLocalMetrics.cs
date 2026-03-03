using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ses.Local.Workers.Telemetry;

/// <summary>
/// Central registry for ses-local OpenTelemetry metrics and activity sources.
/// All instruments live here to avoid scattered Meter creation across the codebase.
/// </summary>
public static class SesLocalMetrics
{
    public const string MeterName = "ses-local";
    public const string ActivitySourceName = "ses-local";

    private static readonly Meter _meter = new(MeterName, version: "1.0");

    /// <summary>Activity source for distributed tracing spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, version: "1.0");

    // ── Watcher metrics ───────────────────────────────────────────────────────

    /// <summary>
    /// Number of sessions processed by file watchers.
    /// Tag: source = CC | Desktop | Cowork
    /// </summary>
    public static readonly Counter<long> SessionsProcessed =
        _meter.CreateCounter<long>(
            "ses.watcher.sessions_processed",
            description: "Number of sessions processed by file watchers");

    /// <summary>
    /// Number of observations extracted from session files.
    /// Tag: type = ToolUse | ToolResult | Text | Thinking | Error | GitCommit | TestResult
    /// </summary>
    public static readonly Counter<long> ObservationsExtracted =
        _meter.CreateCounter<long>(
            "ses.watcher.observations_extracted",
            description: "Number of observations extracted from session JSONL files");

    // ── Sync metrics ──────────────────────────────────────────────────────────

    /// <summary>Number of cloud sync uploads attempted.</summary>
    public static readonly Counter<long> UploadsAttempted =
        _meter.CreateCounter<long>(
            "ses.sync.uploads_attempted",
            description: "Number of cloud sync uploads attempted");

    /// <summary>Number of cloud sync uploads that succeeded.</summary>
    public static readonly Counter<long> UploadsSucceeded =
        _meter.CreateCounter<long>(
            "ses.sync.uploads_succeeded",
            description: "Number of cloud sync uploads that succeeded");

    /// <summary>Number of cloud sync uploads that failed.</summary>
    public static readonly Counter<long> UploadsFailed =
        _meter.CreateCounter<long>(
            "ses.sync.uploads_failed",
            description: "Number of cloud sync uploads that failed");

    /// <summary>Number of cloud pull downloads attempted.</summary>
    public static readonly Counter<long> PullsAttempted =
        _meter.CreateCounter<long>(
            "ses.sync.pulls_attempted",
            description: "Number of cloud pull downloads attempted");

    /// <summary>Number of cloud pull downloads that resulted in a new or updated local session.</summary>
    public static readonly Counter<long> PullsImported =
        _meter.CreateCounter<long>(
            "ses.sync.pulls_imported",
            description: "Number of sessions imported from cloud pull");

    /// <summary>Number of cloud pull documents skipped (already local, own device, or parse failure).</summary>
    public static readonly Counter<long> PullsSkipped =
        _meter.CreateCounter<long>(
            "ses.sync.pulls_skipped",
            description: "Number of pull documents skipped (deduplication or parse failure)");

    // ── Compression metrics ───────────────────────────────────────────────────

    /// <summary>
    /// Number of sessions compressed.
    /// Tag: layer = 1 | 2 | 3
    /// </summary>
    public static readonly Counter<long> SessionsCompressed =
        _meter.CreateCounter<long>(
            "ses.compression.sessions_compressed",
            description: "Number of sessions compressed by the compression pipeline");

    // ── Auth metrics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Number of token refresh attempts.
    /// Tag: result = success | failure
    /// </summary>
    public static readonly Counter<long> TokenRefreshes =
        _meter.CreateCounter<long>(
            "ses.auth.token_refreshes",
            description: "Number of OAuth token refresh attempts");

    // ── Database metrics ──────────────────────────────────────────────────────

    /// <summary>Duration of database queries in milliseconds.</summary>
    public static readonly Histogram<double> QueryDurationMs =
        _meter.CreateHistogram<double>(
            "ses.db.query_duration_ms",
            unit: "ms",
            description: "Duration of SQLite database queries");
}
