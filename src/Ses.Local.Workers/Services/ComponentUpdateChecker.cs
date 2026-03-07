using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Checks for available updates for ses-local-daemon, ses-mcp, and ses-hooks by fetching
/// their manifests. Does NOT download or apply updates — use SesLocalUpdater/SesMcpUpdater for that.
/// </summary>
public sealed class ComponentUpdateChecker
{
    private readonly HttpClient _http;
    private readonly IOptions<SesLocalOptions> _options;
    private readonly ILogger<ComponentUpdateChecker> _logger;

    public ComponentUpdateChecker(HttpClient http, IOptions<SesLocalOptions> options, ILogger<ComponentUpdateChecker> logger)
    {
        _http    = http;
        _options = options;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<ComponentUpdate>> CheckAsync(CancellationToken ct = default)
    {
        var results = new List<ComponentUpdate>(3);
        var opts = _options.Value;

        // Version lookups run concurrently inside each task (GetBinaryVersion blocks up to 3 s).
        await Task.WhenAll(
            CheckOneAsync("ses-local-daemon", opts.SesLocalManifestUrl, GetDaemonVersion, results, ct),
            CheckOneAsync("ses-mcp",          opts.SesMcpManifestUrl,   () => GetBinaryVersion(SesMcpUpdater.GetSesMcpBinaryPath()), results, ct),
            CheckOneAsync("ses-hooks",        opts.SesHooksManifestUrl, () => GetBinaryVersion(SesMcpManager.GetSesHooksBinaryPath()), results, ct));

        return results.OrderBy(c => c.Name).ToList();
    }

    private async Task CheckOneAsync(
        string name, string manifestUrl, Func<string?> getInstalledVersion,
        List<ComponentUpdate> results, CancellationToken ct)
    {
        var installedVersion = await Task.Run(getInstalledVersion, ct);
        try
        {
            var response = await _http.GetAsync(manifestUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                lock (results)
                    results.Add(new ComponentUpdate(name, installedVersion, null, false));
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var manifest = JsonSerializer.Deserialize(json, UpdateManifestJsonContext.Default.UpdateManifest);
            if (manifest is null || !Version.TryParse(manifest.Version, out var latestVersion))
            {
                lock (results)
                    results.Add(new ComponentUpdate(name, installedVersion, null, false));
                return;
            }

            var updateAvailable = installedVersion is not null
                && Version.TryParse(installedVersion, out var currentVersion)
                && latestVersion > currentVersion;

            lock (results)
                results.Add(new ComponentUpdate(name, installedVersion, manifest.Version, updateAvailable));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed for {Component}", name);
            lock (results)
                results.Add(new ComponentUpdate(name, installedVersion, null, false));
        }
    }

    private static string? GetDaemonVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);

    private static string? GetBinaryVersion(string binaryPath)
    {
        if (!File.Exists(binaryPath)) return null;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(binaryPath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadLine();
            if (!proc.WaitForExit(3000)) proc.Kill();
            return output?.Trim();
        }
        catch { return null; }
    }
}
