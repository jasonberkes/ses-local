using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Checks for and applies ses-mcp binary updates.
/// ses-local manages ses-mcp — not the other way around.
/// </summary>
public sealed class SesMcpUpdater
{
    private readonly ILogger<SesMcpUpdater> _logger;
    private readonly HttpClient _http;
    private readonly Func<string> _getBinaryPath;

    private const string ManifestUrl =
        "https://tmprodeus2data.blob.core.windows.net/artifacts/ses-mcp/latest.json";

    public SesMcpUpdater(ILogger<SesMcpUpdater> logger, HttpClient http)
        : this(logger, http, GetSesMcpBinaryPath) { }

    internal SesMcpUpdater(ILogger<SesMcpUpdater> logger, HttpClient http, Func<string> getBinaryPath)
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

            // If ses-mcp not installed, skip update (WI-947 handles installation)
            if (!File.Exists(binaryPath))
                return new UpdateResult(false, null, "ses-mcp not installed — skipping update check");

            CleanupOldBinary(binaryPath);

            var installedVersion = GetInstalledVersion(binaryPath);
            var manifest = await FetchManifestAsync(ct);
            if (manifest is null) return Fail("could not fetch ses-mcp manifest");

            if (!Version.TryParse(manifest.Version, out var remoteVersion))
                return Fail($"invalid remote version '{manifest.Version}'");

            if (installedVersion is not null && remoteVersion <= installedVersion)
                return new UpdateResult(false, null, "ses-mcp already up to date");

            var rid = SesLocalUpdater.GetRid();
            if (!manifest.Binaries.TryGetValue(rid, out var downloadUrl))
                return Fail($"no ses-mcp binary for platform '{rid}'");

            return await DownloadAndApplyAsync(binaryPath, downloadUrl, manifest.Version, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-mcp update check failed");
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
            _logger.LogWarning(ex, "Failed to fetch ses-mcp manifest");
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
            TryDelete(newBinaryPath);
            return Fail($"download failed: {ex.Message}");
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
            _logger.LogInformation("ses-mcp updated to {Version}", newVersion);
            return new UpdateResult(true, newVersion, $"ses-mcp updated to {newVersion}");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(oldPath) && !File.Exists(binaryPath)) File.Move(oldPath, binaryPath); } catch { }
            TryDelete(newBinaryPath);
            return Fail($"apply error: {ex.Message}");
        }
    }

    private static Version? GetInstalledVersion(string binaryPath)
    {
        try
        {
            var psi = new ProcessStartInfo(binaryPath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return Version.TryParse(output, out var v) ? v : null;
        }
        catch { return null; }
    }

    public static string GetSesMcpBinaryPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "SuperEasySoftware", "ses-mcp.exe");
        }
        return Path.Combine(home, ".ses", "ses-mcp");
    }

    private void CleanupOldBinary(string binaryPath)
    {
        var oldPath = binaryPath + ".old";
        if (!File.Exists(oldPath)) return;
        try { File.Delete(oldPath); } catch (Exception ex) { _logger.LogDebug(ex, "Could not clean {Path}", oldPath); }
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
    private static UpdateResult Fail(string reason) => new(false, null, $"ses-mcp update failed: {reason}");
}
