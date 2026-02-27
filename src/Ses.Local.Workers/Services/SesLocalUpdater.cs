using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

public sealed record UpdateResult(bool UpdateApplied, string? NewVersion, string? Message);

/// <summary>
/// Checks for and applies ses-local binary updates.
/// Same pattern as ses-mcp AutoUpdater.
/// </summary>
public sealed class SesLocalUpdater
{
    private readonly ILogger<SesLocalUpdater> _logger;
    private readonly HttpClient _http;
    private readonly Func<string?> _getBinaryPath;

    private const string ManifestUrl =
        "https://tmprodeus2data.blob.core.windows.net/artifacts/ses-local/latest.json";

    public SesLocalUpdater(ILogger<SesLocalUpdater> logger, HttpClient http)
        : this(logger, http, () => Environment.ProcessPath) { }

    internal SesLocalUpdater(ILogger<SesLocalUpdater> logger, HttpClient http, Func<string?> getBinaryPath)
    {
        _logger = logger;
        _http = http;
        _getBinaryPath = getBinaryPath;
    }

    public async Task<UpdateResult> CheckAndApplyAsync(CancellationToken ct = default)
    {
        try
        {
            var binaryPath = _getBinaryPath();
            if (string.IsNullOrEmpty(binaryPath))
                return Fail("cannot determine binary path");

            CleanupOldBinary(binaryPath);

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion is null)
                return Fail("cannot determine current version");

            var manifest = await FetchManifestAsync(ct);
            if (manifest is null) return Fail("could not fetch manifest");

            if (!Version.TryParse(manifest.Version, out var remoteVersion))
                return Fail($"invalid remote version '{manifest.Version}'");

            if (remoteVersion <= currentVersion)
                return new UpdateResult(false, null, "Already up to date");

            var rid = GetRid();
            if (!manifest.Binaries.TryGetValue(rid, out var downloadUrl))
                return Fail($"no binary for platform '{rid}'");

            return await DownloadAndApplyAsync(binaryPath, downloadUrl, manifest.Version, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-local update check failed");
            return Fail(ex.Message);
        }
    }

    private async Task<UpdateManifest?> FetchManifestAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(ManifestUrl, ct);
            return JsonSerializer.Deserialize(json, UpdateManifestJsonContext.Default.UpdateManifest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch ses-local manifest from {Url}", ManifestUrl);
            return null;
        }
    }

    private async Task<UpdateResult> DownloadAndApplyAsync(
        string binaryPath, string downloadUrl, string newVersion, CancellationToken ct)
    {
        var newBinaryPath = binaryPath + ".new";
        try
        {
            var bytes = await _http.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(newBinaryPath, bytes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-local download failed");
            TryDelete(newBinaryPath);
            return Fail(ex.Message);
        }

        var oldPath = binaryPath + ".old";
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.Move(binaryPath, oldPath, overwrite: true);
                File.Move(newBinaryPath, binaryPath, overwrite: true);
            }
            else
            {
                await SetExecutableBitAsync(newBinaryPath, ct);
                File.Move(binaryPath, oldPath, overwrite: true);
                File.Move(newBinaryPath, binaryPath, overwrite: true);
                TryDelete(oldPath);
            }
            _logger.LogInformation("ses-local updated to {Version}. Restart to apply.", newVersion);
            return new UpdateResult(true, newVersion, $"Updated to {newVersion}. Restart to apply.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-local update apply failed — rolling back");
            try { if (File.Exists(oldPath) && !File.Exists(binaryPath)) File.Move(oldPath, binaryPath); } catch { }
            TryDelete(newBinaryPath);
            return Fail($"apply error: {ex.Message}");
        }
    }

    private void CleanupOldBinary(string binaryPath)
    {
        var oldPath = binaryPath + ".old";
        if (!File.Exists(oldPath)) return;
        try { File.Delete(oldPath); } catch (Exception ex) { _logger.LogDebug(ex, "Could not clean up {Path}", oldPath); }
    }

    private static async Task SetExecutableBitAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("chmod") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi);
        if (p is not null) await p.WaitForExitAsync(ct);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static UpdateResult Fail(string reason) => new(false, null, $"Update failed: {reason}");

    public static string GetRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win-x64";
        return RuntimeInformation.RuntimeIdentifier;
    }
}
