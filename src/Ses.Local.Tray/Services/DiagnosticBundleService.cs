using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Options;

namespace Ses.Local.Tray.Services;

/// <summary>
/// Assembles a diagnostic ZIP bundle containing logs, health data, and system info.
/// All tokens, PATs, Bearer headers, and API keys are scrubbed before inclusion.
/// </summary>
public sealed class DiagnosticBundleService
{
    private static readonly JsonSerializerOptions s_jsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Patterns that capture sensitive values — replace value portion with ***REDACTED***
    private static readonly Regex[] s_scrubPatterns =
    [
        new Regex(@"(Authorization:\s*Bearer\s+)\S+",        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
        new Regex(@"(Bearer\s+)eyJ[A-Za-z0-9_\-\.]+",       RegexOptions.None,        TimeSpan.FromSeconds(1)),
        new Regex(@"(MCP_HEADERS["":\s]+)[^""\\]+",          RegexOptions.None,        TimeSpan.FromSeconds(1)),
        new Regex(@"(tm_pat_)[A-Za-z0-9_\-]{8,}",           RegexOptions.None,        TimeSpan.FromSeconds(1)),
        new Regex(@"(PAT_TOKEN["":\s=]+)[^\s,""}\]]+",       RegexOptions.None,        TimeSpan.FromSeconds(1)),
        new Regex(@"(password["":\s=]+)[^\s,""}\]]+",        RegexOptions.IgnoreCase,  TimeSpan.FromSeconds(1)),
        new Regex(@"(api.?key["":\s=]+)[^\s,""}\]]+",        RegexOptions.IgnoreCase,  TimeSpan.FromSeconds(1)),
        new Regex(@"(secret["":\s=]+)[^\s,""}\]]+",          RegexOptions.IgnoreCase,  TimeSpan.FromSeconds(1)),
    ];

    private readonly DaemonAuthProxy _proxy;
    private readonly SesLocalOptions _options;

    public DiagnosticBundleService(DaemonAuthProxy proxy, IOptions<SesLocalOptions> options)
    {
        _proxy   = proxy;
        _options = options.Value;
    }

    /// <summary>
    /// Creates a diagnostic ZIP at ~/.ses/diagnostics-{timestamp}.zip.
    /// Removes any previous diagnostic ZIPs first.
    /// Returns the full path to the created file.
    /// </summary>
    public async Task<string> CreateBundleAsync(CancellationToken ct = default)
    {
        var sesDir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ses");
        var logsDir   = Path.Combine(sesDir, "logs");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath   = Path.Combine(sesDir, $"diagnostics-{timestamp}.zip");

        // Remove stale diagnostic bundles before creating a new one
        foreach (var stale in Directory.GetFiles(sesDir, "diagnostics-*.zip"))
            try { File.Delete(stale); } catch { /* best-effort */ }

        // Fetch all three IPC payloads in parallel
        var healthTask     = FetchSafe(() => _proxy.GetHealthAsync(ct));
        var componentsTask = FetchSafe(() => _proxy.GetComponentsAsync(ct));
        var statsTask      = FetchSafe(() => _proxy.GetSyncStatsAsync(ct));
        await Task.WhenAll(healthTask, componentsTask, statsTask);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        // ── 1. Log files (last 24 hours) ───────────────────────────────────────
        await AddLogFilesAsync(zip, logsDir, ct);

        // ── 2. Health report ───────────────────────────────────────────────────
        AddJsonEntry(zip, "health.json", healthTask.Result);

        // ── 3. Component status ────────────────────────────────────────────────
        AddJsonEntry(zip, "components.json", componentsTask.Result);

        // ── 4. Sync stats ──────────────────────────────────────────────────────
        AddJsonEntry(zip, "sync-stats.json", statsTask.Result);

        // ── 5. System info ─────────────────────────────────────────────────────
        AddSystemInfo(zip);

        // ── 6. Scrubbed options (URLs only) ────────────────────────────────────
        AddScrubbedOptions(zip);

        // ── 7. MCP servers (names only) ────────────────────────────────────────
        AddMcpServerNames(zip);

        return zipPath;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static async Task<string> FetchSafe<T>(Func<Task<T?>> fetch) where T : class
    {
        try
        {
            var result = await fetch();
            return result is not null
                ? Scrub(JsonSerializer.Serialize(result, s_jsonOptions))
                : """{"error":"Daemon unreachable"}""";
        }
        catch { return """{"error":"Could not retrieve data"}"""; }
    }

    private static void AddJsonEntry(ZipArchive zip, string entryName, string json)
        => AddText(zip, entryName, json);

    private static async Task AddLogFilesAsync(ZipArchive zip, string logsDir, CancellationToken ct)
    {
        if (!Directory.Exists(logsDir)) return;

        var cutoff = DateTime.Now.AddHours(-24);
        var logFiles = Directory.GetFiles(logsDir, "*.log")
            .Where(f => File.GetLastWriteTime(f) >= cutoff)
            .ToArray();

        foreach (var logFile in logFiles)
        {
            try
            {
                var text = await File.ReadAllTextAsync(logFile, ct);
                var scrubbed = Scrub(text);
                var entry = zip.CreateEntry($"logs/{Path.GetFileName(logFile)}");
                await using var stream = entry.Open();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(scrubbed), ct);
            }
            catch { /* skip unreadable files */ }
        }
    }

    private static void AddSystemInfo(ZipArchive zip)
    {
        var info = new
        {
            generatedAt  = DateTime.UtcNow.ToString("O"),
            os           = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            dotnetVersion = Environment.Version.ToString(),
            sesLocalVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
        };
        AddText(zip, "system-info.json", JsonSerializer.Serialize(info, s_jsonOptions));
    }

    private void AddScrubbedOptions(ZipArchive zip)
    {
        // Include only URL fields — never tokens, secrets, or credentials
        var urls = new
        {
            identityBaseUrl        = _options.IdentityBaseUrl,
            documentServiceBaseUrl = _options.DocumentServiceBaseUrl,
            sesMcpManifestUrl      = _options.SesMcpManifestUrl,
        };
        AddText(zip, "options-urls.json", JsonSerializer.Serialize(urls, s_jsonOptions));
    }

    private static void AddMcpServerNames(ZipArchive zip)
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "settings.json");

            if (!File.Exists(settingsPath))
            {
                AddText(zip, "mcp-servers.json", """{"error":"settings.json not found"}""");
                return;
            }

            var text = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(text);
            var names = new List<string>();

            if (doc.RootElement.TryGetProperty("mcpServers", out var mcpServers))
            {
                foreach (var prop in mcpServers.EnumerateObject())
                    names.Add(prop.Name);
            }

            AddText(zip, "mcp-servers.json", JsonSerializer.Serialize(
                new { serverNames = names }, s_jsonOptions));
        }
        catch
        {
            AddText(zip, "mcp-servers.json", """{"error":"Could not read MCP server names"}""");
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static string Scrub(string input)
    {
        foreach (var pattern in s_scrubPatterns)
        {
            input = pattern.Replace(input, m =>
            {
                // Keep the first capture group (the key/label), redact the rest
                var g1 = m.Groups[1].Value;
                return g1 + "***REDACTED***";
            });
        }
        return input;
    }
}
