namespace Ses.Local.Core.Models;

/// <summary>
/// Aggregate health report returned by GET /api/health.
/// </summary>
public sealed class HealthReport
{
    public DateTime CheckedAt { get; init; }
    public OverallStatus Status { get; init; }
    public List<HealthCheckResult> Checks { get; init; } = [];
}

/// <summary>
/// Result for a single named health check.
/// </summary>
public sealed class HealthCheckResult
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";  // Auth, Config, Storage, Infrastructure
    public ComponentHealth Status { get; init; }
    public string? Message { get; init; }
    public DateTime? LastRepairAttempt { get; init; }
    public int RepairAttempts { get; init; }
}

public enum ComponentHealth { Healthy, Degraded, Unhealthy }
public enum OverallStatus { Healthy, Degraded, Unhealthy }
