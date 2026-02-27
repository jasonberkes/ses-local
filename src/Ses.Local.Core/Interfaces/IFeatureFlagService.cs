using Ses.Local.Core.Enums;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Feature flag service backed by PAT scopes from TaskMaster identity server.
/// Local cache: ~/.ses/config.json synced from cloud on startup.
/// </summary>
public interface IFeatureFlagService
{
    bool IsEnabled(SesFeature feature);
    Task SyncFromCloudAsync(CancellationToken ct = default);
}
