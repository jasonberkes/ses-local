using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches Cowork local session directory.
///
/// INVESTIGATION RESULT (WI-984, 2026-03-02):
/// Cowork does NOT persist conversation data to any host-accessible macOS location.
/// Session data lives inside a Linux VM managed by coworkd:
///   - coworkd mounts an ext4 session disk at /sessions inside the VM
///   - The VM disk image (~Library/Application Support/Claude/vm_bundles/claudevm.bundle/sessiondata.img)
///     is a binary virtual disk, not a parseable file
///   - No ~/Library/Application Support/Cowork/ directory exists
///   - Log files (coworkd.log, cowork_vm_swift.log, cowork_vm_node.log) contain only
///     VM operational data (kernel boot, disk mount, VM startup) — no conversation content
///
/// Monitoring is not available until Anthropic exposes Cowork sessions via a host-accessible
/// API or file format. This watcher should be revisited if Cowork gains local persistence.
/// </summary>
public sealed class CoworkWatcher : BackgroundService
{
    private readonly ILogger<CoworkWatcher> _logger;

    public CoworkWatcher(ILogger<CoworkWatcher> logger) => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CoworkWatcher: no local conversation storage found for Cowork — " +
            "sessions are stored inside a Linux VM disk image and are not host-accessible. " +
            "Monitoring not available.");
        return Task.CompletedTask;
    }
}
