using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

public sealed class SesMcpHealthStatus
{
    public bool IsInstalled { get; set; }
    public bool IsConfigured { get; set; }
    public bool HasConfigDrift { get; set; }
    public string? InstalledVersion { get; set; }
    public string? ConfigPath { get; set; }
}

/// <summary>
/// Manages the ses-mcp binary lifecycle on behalf of ses-local.
/// - Detects and installs ses-mcp if missing
/// - Monitors Claude Desktop config for drift
/// - Migrates PATs from plain text to OS keychain
/// </summary>
public sealed class SesMcpManager
{
    private readonly ICredentialStore _keychain;
    private readonly SesMcpUpdater _updater;
    private readonly IAuthService _auth;
    private readonly ILogger<SesMcpManager> _logger;

    private const string SesLocalMcpKey = "ses-local";
    private const string SesCloudMcpKey = "ses-cloud";
    private const string CloudMcpUrl = "https://mcp.tm.supereasysoftware.com/mcp";
    private static readonly string[] SesLocalArgs = ["--transport", "stdio", "--skip-update"];
    private static readonly string[] SesCloudArgs = ["-y", "@anthropic-ai/mcp-proxy", CloudMcpUrl];

    public SesMcpManager(
        ICredentialStore keychain,
        SesMcpUpdater updater,
        IAuthService auth,
        ILogger<SesMcpManager> logger)
    {
        _keychain = keychain;
        _updater  = updater;
        _auth     = auth;
        _logger   = logger;
    }

    public async Task<SesMcpHealthStatus> CheckAndRepairAsync(CancellationToken ct = default)
    {
        var status = new SesMcpHealthStatus
        {
            ConfigPath = GetClaudeDesktopConfigPath()
        };

        // 1. Check if binary is installed
        var binaryPath = SesMcpUpdater.GetSesMcpBinaryPath();
        status.IsInstalled = File.Exists(binaryPath);

        if (!status.IsInstalled)
        {
            _logger.LogInformation("ses-mcp not found at {Path} — downloading...", binaryPath);
            await InstallSesMcpAsync(binaryPath, ct);
            status.IsInstalled = File.Exists(binaryPath);
        }

        if (!status.IsInstalled)
        {
            _logger.LogWarning("ses-mcp install failed");
            return status;
        }

        // 2. Migrate PAT from plain text if present
        await MigratePatAsync(ct);

        // 3. Check and repair Claude Desktop config
        var (configured, hasDrift) = await CheckAndRepairConfigAsync(binaryPath, ct);
        status.IsConfigured  = configured;
        status.HasConfigDrift = hasDrift;

        // 4. Register ses-hooks in ~/.claude/settings.json for Claude Code
        await CheckAndRepairClaudeCodeHooksAsync(ct);

        return status;
    }

    // ── Installation ──────────────────────────────────────────────────────────

    private async Task InstallSesMcpAsync(string targetPath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            // Use the updater's manifest to get the download URL
            var manifest = await FetchManifestAsync(ct);
            if (manifest is null)
            {
                _logger.LogWarning("Could not fetch ses-mcp manifest for installation");
                return;
            }

            var rid = SesLocalUpdater.GetRid();
            if (!manifest.Binaries.TryGetValue(rid, out var downloadUrl))
            {
                _logger.LogWarning("No ses-mcp binary available for platform '{Rid}'", rid);
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var bytes = await http.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(targetPath, bytes, ct);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("chmod")
                    { CreateNoWindow = true, UseShellExecute = false };
                psi.ArgumentList.Add("+x");
                psi.ArgumentList.Add(targetPath);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is not null) await p.WaitForExitAsync(ct);
            }

            _logger.LogInformation("ses-mcp installed to {Path}", targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-mcp installation failed");
        }
    }

    private static async Task<UpdateManifest?> FetchManifestAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync(
                "https://tmprodeus2data.blob.core.windows.net/artifacts/ses-mcp/latest.json", ct);
            return JsonSerializer.Deserialize(json, UpdateManifestJsonContext.Default.UpdateManifest);
        }
        catch { return null; }
    }

    // ── PAT Migration ─────────────────────────────────────────────────────────

    private async Task MigratePatAsync(CancellationToken ct)
    {
        var configPath = GetClaudeDesktopConfigPath();
        if (!File.Exists(configPath)) return;

        // Check if PAT already in keychain
        var existingPat = await _keychain.GetAsync("ses-local-pat", ct);
        if (!string.IsNullOrEmpty(existingPat)) return;

        try
        {
            var config = ClaudeDesktopConfig.Load(configPath);
            string? plainTextPat = null;

            // Look for PAT in ses-local or ses-cloud MCP_HEADERS
            foreach (var server in config.McpServers.Values)
            {
                if (server.Env is null) continue;
                if (!server.Env.TryGetValue("MCP_HEADERS", out var headers)) continue;

                // Format: "Authorization: Bearer tm_pat_..."
                var bearerPrefix = "Authorization: Bearer ";
                if (!headers.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var token = headers[bearerPrefix.Length..].Trim();
                if (token.StartsWith("tm_pat_", StringComparison.OrdinalIgnoreCase))
                {
                    plainTextPat = token;
                    break;
                }
            }

            if (plainTextPat is null) return;

            // Migrate to keychain
            await _keychain.SetAsync("ses-local-pat", plainTextPat, ct);
            _logger.LogInformation("PAT migrated from Claude Desktop config to OS keychain");

            // Decision: keep PAT in config for compatibility (Claude Desktop reads it directly)
            // We store in keychain as backup/source-of-truth for ses-mcp tools (WI-949)
            // but do NOT remove from config — removing would break existing Claude Desktop setup
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PAT migration failed — non-fatal");
        }
    }

    // ── Config Health ─────────────────────────────────────────────────────────

    private async Task<(bool configured, bool hasDrift)> CheckAndRepairConfigAsync(
        string binaryPath, CancellationToken ct)
    {
        var configPath = GetClaudeDesktopConfigPath();
        if (!Directory.Exists(Path.GetDirectoryName(configPath)))
        {
            _logger.LogDebug("Claude Desktop config directory not found — Claude Desktop may not be installed");
            return (false, false);
        }

        var config = ClaudeDesktopConfig.Load(configPath);
        var pat = await _keychain.GetAsync("ses-local-pat", ct)
               ?? GetPatFromConfig(config);

        if (pat is null)
        {
            _logger.LogDebug("No PAT available — cannot configure ses-mcp");
            return (false, false);
        }

        bool hasDrift = false;

        // Check ses-local entry
        if (!config.McpServers.TryGetValue(SesLocalMcpKey, out var sesLocal)
            || sesLocal.Command != binaryPath
            || !sesLocal.Args.SequenceEqual(SesLocalArgs))
        {
            hasDrift = true;
            _logger.LogInformation("Claude Desktop ses-local config drift detected — repairing");
        }

        // Check ses-cloud entry
        if (!config.McpServers.TryGetValue(SesCloudMcpKey, out var sesCloud)
            || sesCloud.Command != "npx"
            || !sesCloud.Args.SequenceEqual(SesCloudArgs))
        {
            hasDrift = true;
        }

        if (hasDrift)
        {
            var authHeader = $"Authorization: Bearer {pat}";

            config.McpServers[SesLocalMcpKey] = new McpServerEntry
            {
                Command = binaryPath,
                Args    = [.. SesLocalArgs],
                Env     = new Dictionary<string, string> { ["MCP_HEADERS"] = authHeader }
            };

            config.McpServers[SesCloudMcpKey] = new McpServerEntry
            {
                Command = "npx",
                Args    = [.. SesCloudArgs],
                Env     = new Dictionary<string, string> { ["MCP_HEADERS"] = authHeader }
            };

            config.Save(configPath);
            _logger.LogInformation("Claude Desktop config repaired at {Path}", configPath);
        }

        return (true, hasDrift);
    }

    private static string? GetPatFromConfig(ClaudeDesktopConfig config)
    {
        foreach (var server in config.McpServers.Values)
        {
            if (server.Env is null) continue;
            if (!server.Env.TryGetValue("MCP_HEADERS", out var h) || h is null) continue;
            var prefix = "Authorization: Bearer ";
            if (h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return h[prefix.Length..].Trim();
        }
        return null;
    }

    public static string GetClaudeDesktopConfigPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Claude", "claude_desktop_config.json");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
    }

    // ── Claude Code Hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// Ensures ses-hooks is registered in ~/.claude/settings.json for all 6 CC lifecycle events.
    /// Runs on startup and every 30 minutes as part of the existing SesMcpManagerWorker cadence.
    /// No-op if Claude Code is not installed (~/.claude/ does not exist).
    /// </summary>
    public async Task CheckAndRepairClaudeCodeHooksAsync(CancellationToken ct = default)
    {
        var settingsPath = GetClaudeCodeSettingsPath();
        var hooksPath    = GetSesHooksBinaryPath();

        if (!Directory.Exists(Path.GetDirectoryName(settingsPath)))
        {
            _logger.LogDebug("Claude Code not installed (~/.claude/ not found) — skipping hooks registration");
            return;
        }

        if (!File.Exists(hooksPath))
        {
            _logger.LogDebug("ses-hooks binary not found at {Path} — skipping registration", hooksPath);
            return;
        }

        var settings = ClaudeCodeSettings.LoadOrCreate(settingsPath);

        if (settings.HasCorrectHooks(hooksPath))
        {
            _logger.LogDebug("Claude Code hooks already correctly registered");
            return;
        }

        _logger.LogInformation("Registering/repairing ses-hooks in Claude Code settings: {Path}", settingsPath);
        settings.UpsertSesHooks(hooksPath);
        settings.Save(settingsPath);
        _logger.LogInformation("ses-hooks registered for all 6 events in {Path}", settingsPath);

        await Task.CompletedTask;
    }

    public static string GetClaudeCodeSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "settings.json");
    }

    public static string GetSesHooksBinaryPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ext  = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return Path.Combine(home, ".ses", "bin", $"ses-hooks{ext}");
    }
}
