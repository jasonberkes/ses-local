using System.Diagnostics.Metrics;
using Ses.Local.Workers.Telemetry;
using Xunit;

namespace Ses.Local.Workers.Tests.Telemetry;

/// <summary>
/// Verifies that ses-local metric instruments increment correctly.
/// Uses MeterListener to capture counter additions without requiring a real OTel exporter.
/// </summary>
public sealed class SesLocalMetricsTests
{
    // ── Watcher counter tests ────────────────────────────────────────────────

    [Fact]
    public void SessionsProcessed_Add_IncrementsCorrectly()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.SessionsProcessed.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.SessionsProcessed.Add(1, new KeyValuePair<string, object?>("source", "CC"));
        SesLocalMetrics.SessionsProcessed.Add(2, new KeyValuePair<string, object?>("source", "Desktop"));

        Assert.Equal(3, Volatile.Read(ref captured));
    }

    [Fact]
    public void ObservationsExtracted_Add_IncrementsCorrectly()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.ObservationsExtracted.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.ObservationsExtracted.Add(5, new KeyValuePair<string, object?>("type", "ToolUse"));
        SesLocalMetrics.ObservationsExtracted.Add(3, new KeyValuePair<string, object?>("type", "ToolResult"));

        Assert.Equal(8, Volatile.Read(ref captured));
    }

    // ── Sync counter tests ───────────────────────────────────────────────────

    [Fact]
    public void UploadsAttempted_Add_IncrementsCorrectly()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.UploadsAttempted.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.UploadsAttempted.Add(1);
        SesLocalMetrics.UploadsAttempted.Add(1);
        SesLocalMetrics.UploadsAttempted.Add(1);

        Assert.Equal(3, Volatile.Read(ref captured));
    }

    [Fact]
    public void UploadsSucceeded_Add_IncrementsCorrectly()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.UploadsSucceeded.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.UploadsSucceeded.Add(2);

        Assert.Equal(2, Volatile.Read(ref captured));
    }

    [Fact]
    public void UploadsFailed_Add_IncrementsCorrectly()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.UploadsFailed.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.UploadsFailed.Add(1);

        Assert.Equal(1, Volatile.Read(ref captured));
    }

    // ── Compression counter tests ─────────────────────────────────────────────

    [Fact]
    public void SessionsCompressed_Add_IncrementsCorrectly()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.SessionsCompressed.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.SessionsCompressed.Add(1, new KeyValuePair<string, object?>("layer", 1));
        SesLocalMetrics.SessionsCompressed.Add(1, new KeyValuePair<string, object?>("layer", 1));

        Assert.Equal(2, Volatile.Read(ref captured));
    }

    // ── Auth counter tests ────────────────────────────────────────────────────

    [Fact]
    public void TokenRefreshes_Add_TracksSuccessAndFailure()
    {
        long captured = 0;
        using var listener = BuildListener(SesLocalMetrics.TokenRefreshes.Name,
            (value, _) => Interlocked.Add(ref captured, value));

        SesLocalMetrics.TokenRefreshes.Add(1, new KeyValuePair<string, object?>("result", "success"));
        SesLocalMetrics.TokenRefreshes.Add(1, new KeyValuePair<string, object?>("result", "failure"));

        Assert.Equal(2, Volatile.Read(ref captured));
    }

    // ── Histogram tests ───────────────────────────────────────────────────────

    [Fact]
    public void QueryDurationMs_Record_CapturesValue()
    {
        double captured = 0;
        using var listener = BuildHistogramListener(SesLocalMetrics.QueryDurationMs.Name,
            value => Interlocked.Exchange(ref captured, value));

        SesLocalMetrics.QueryDurationMs.Record(42.5);

        Assert.Equal(42.5, Volatile.Read(ref captured));
    }

    // ── Constants tests ───────────────────────────────────────────────────────

    [Fact]
    public void MeterName_IsCorrect()
    {
        Assert.Equal("ses-local", SesLocalMetrics.MeterName);
    }

    [Fact]
    public void ActivitySourceName_IsCorrect()
    {
        Assert.Equal("ses-local", SesLocalMetrics.ActivitySourceName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MeterListener BuildListener(string instrumentName, Action<long, ReadOnlySpan<KeyValuePair<string, object?>>> callback)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == instrumentName)
                callback(value, tags);
        });
        listener.Start();
        return listener;
    }

    private static MeterListener BuildHistogramListener(string instrumentName, Action<double> callback)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == instrumentName)
                callback(value);
        });
        listener.Start();
        return listener;
    }
}
