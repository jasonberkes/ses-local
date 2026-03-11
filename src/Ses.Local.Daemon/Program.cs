using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Core.Services;
using Ses.Local.Workers;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Telemetry;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Daemon;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Single-instance enforcement via named mutex
        using var mutex = new Mutex(true, "com.supereasysoftware.ses-local-daemon", out var isNewInstance);
        if (!isNewInstance)
        {
            Console.Error.WriteLine("ses-local-daemon is already running.");
            return;
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ses", "logs");
        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, "daemon-.log");

        var socketPath = DaemonSocketPath.GetPath();
        DaemonSocketPath.CleanupStaleSocket();

        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((ctx, config) => config
            .ReadFrom.Configuration(ctx.Configuration)
            .WriteTo.Console()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 50_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                flushToDiskInterval: TimeSpan.FromSeconds(5))
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "ses-local-daemon"));

        // Kestrel listens ONLY on Unix domain socket (or named pipe on Windows) — no TCP
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                options.ListenNamedPipe(socketPath);
            else
                options.ListenUnixSocket(socketPath);
        });

        builder.Services.AddSesLocalWorkers(builder.Configuration);

        // OpenTelemetry — metrics and tracing (conditioned on EnableTelemetry option)
        var sesLocalSection = builder.Configuration.GetSection(SesLocalOptions.SectionName);
        var enableTelemetry = sesLocalSection.GetValue("EnableTelemetry", defaultValue: true);
        if (enableTelemetry)
        {
            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics
                    .AddMeter(SesLocalMetrics.MeterName)
                    .AddConsoleExporter())
                .WithTracing(tracing => tracing
                    .AddSource(SesLocalMetrics.ActivitySourceName)
                    .AddConsoleExporter());
        }

        builder.Services.AddSingleton<ImportProgressTracker>();

        builder.Services.AddHostedService<LevelDbWatcher>();
        builder.Services.AddHostedService<ClaudeCodeWatcher>();
        builder.Services.AddHostedService<CoworkWatcher>();
        builder.Services.AddHostedService<ChatGptDesktopWatcher>();
        builder.Services.AddHostedService<CloudSyncWorker>();
        builder.Services.AddHostedService<CloudPullWorker>();
        builder.Services.AddHostedService<BrowserExtensionListener>();
        builder.Services.AddHostedService<AutoUpdateWorker>();
        builder.Services.AddHostedService<SesMcpManagerWorker>();
        builder.Services.AddHostedService<ClaudeDesktopSyncWorker>();
        builder.Services.AddHostedService<CompressionWorker>();

        // HealthMonitorWorker registered as singleton so /api/health can access the same instance
        builder.Services.AddSingleton<HealthMonitorWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorWorker>());

        var app = builder.Build();

        var startTimestamp = Stopwatch.GetTimestamp();

        // IPC endpoints served over Unix domain socket
        app.MapGet("/api/status", async (IAuthService auth, ILicenseService license) =>
        {
            var state   = await auth.GetStateAsync();
            var licState = await license.GetStateAsync();
            var uptime  = Stopwatch.GetElapsedTime(startTimestamp);
            return Results.Ok(new
            {
                authenticated    = state.IsAuthenticated,
                needsReauth      = state.NeedsReauth,
                loginTimedOut    = state.LoginTimedOut,
                licenseValid     = licState.IsValid,
                licenseStatus    = licState.Status.ToString(),
                uptime           = uptime.ToString(@"d\.hh\:mm\:ss")
            });
        });

        app.MapGet("/api/license", async (ILicenseService license) =>
        {
            var state = await license.GetStateAsync();
            return Results.Ok(new
            {
                status    = state.Status.ToString(),
                isValid   = state.IsValid,
                email     = state.Email,
                expiresAt = state.ExpiresAt,
            });
        });

        app.MapPost("/api/license/activate", async (HttpContext ctx, ILicenseService license) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<LicenseActivateRequest>(
                ctx.Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.LicenseKey))
                return Results.BadRequest(new { error = "License key is required." });

            var result = await license.ActivateAsync(body.LicenseKey, ctx.RequestAborted);

            if (!result.Succeeded)
                return Results.BadRequest(new { error = result.ErrorMessage });

            return Results.Ok(new
            {
                status    = result.State!.Status.ToString(),
                email     = result.State.Email,
                expiresAt = result.State.ExpiresAt,
            });
        });

        // Start an import in the background. Returns 202 immediately; poll /status for progress.
        app.MapPost("/api/conversations/import", async (HttpContext ctx, ConversationImportDispatcher dispatcher, ImportProgressTracker tracker, ILocalDbService db) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<ImportConversationsRequest>(
                ctx.Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.FilePath))
                return Results.BadRequest(new { error = "filePath is required." });

            if (!File.Exists(body.FilePath))
                return Results.BadRequest(new { error = "File not found." });

            if (tracker.IsRunning)
                return Results.Conflict(new { error = "An import is already in progress." });

            var format      = ConversationImportDispatcher.DetectFormat(body.FilePath);
            var formatLabel = ConversationImportDispatcher.FormatLabel(format);
            tracker.Start(formatLabel);

            var filePath = body.FilePath;
            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<ImportProgress>(p => tracker.UpdateProgress(p.Processed));
                    var result   = await dispatcher.ImportAsync(filePath, progress, tracker.CancellationToken);
                    tracker.Complete(result);

                    await db.RecordImportHistoryAsync(new ImportHistoryRecord
                    {
                        Source            = formatLabel.ToLowerInvariant(),
                        FilePath          = filePath,
                        ImportedAt        = DateTime.UtcNow,
                        SessionsImported  = result.SessionsImported,
                        MessagesImported  = result.MessagesImported,
                        DuplicatesSkipped = result.Duplicates,
                        Errors            = result.Errors,
                    });
                }
                catch (OperationCanceledException) { tracker.Cancel(); }
                catch (Exception ex)               { tracker.Fail(ex.Message); }
            });

            return Results.Accepted("/api/conversations/import/status", new { format = formatLabel });
        });

        app.MapGet("/api/conversations/import/status", (ImportProgressTracker tracker) =>
            Results.Ok(tracker.GetStatus()));

        app.MapPost("/api/conversations/import/cancel", (ImportProgressTracker tracker) =>
        {
            tracker.RequestCancel();
            return Results.Ok(new { message = "Cancel requested." });
        });

        app.MapGet("/api/conversations/import/history", async (ILocalDbService db, HttpContext ctx) =>
        {
            var history = await db.GetImportHistoryAsync(20, ctx.RequestAborted);
            return Results.Ok(history);
        });

        app.MapPost("/api/signout", async (IAuthService auth) =>
        {
            await auth.SignOutAsync();
            return Results.Ok(new { message = "Signed out" });
        });

        app.MapGet("/api/components", (SesMcpManager mcpManager) =>
        {
            var health = mcpManager.GetStatus();
            return Results.Ok(new
            {
                sesMcp = new
                {
                    installed  = health.IsInstalled,
                    configured = health.IsConfigured,
                    version    = health.InstalledVersion
                },
                daemon = new
                {
                    installed = true,
                    version   = System.Reflection.Assembly.GetExecutingAssembly()
                                    .GetName().Version?.ToString(3)
                },
                sesHooks = new
                {
                    installed = health.SesHooksInstalled
                }
            });
        });

        app.MapGet("/api/sync-stats", async (ILocalDbService db, HttpContext ctx) =>
        {
            var stats = await db.GetSyncStatsAsync(ctx.RequestAborted);
            return Results.Ok(stats);
        });

        app.MapGet("/api/logs", async (int? lines, string? level, CancellationToken ct) =>
        {
            string? latestLog = null;
            try
            {
                latestLog = Directory.GetFiles(logDir, "daemon-*.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
            }
            catch (DirectoryNotFoundException) { }

            if (latestLog is null)
                return Results.Ok(new { entries = Array.Empty<string>() });

            var tag = level is not null ? $"[{level.ToUpperInvariant()}]" : null;
            var allLines = File.ReadLines(latestLog);
            var filtered = tag is not null
                ? allLines.Where(l => l.Contains(tag, StringComparison.OrdinalIgnoreCase))
                : allLines;

            var result = filtered.TakeLast(lines ?? 50).ToArray();
            return Results.Ok(new { file = Path.GetFileName(latestLog), entries = result });
        });

        app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok(new { message = "Shutting down" });
        });

        app.MapGet("/api/hooks/status", async (SesMcpManager mcpManager, ILocalDbService db) =>
        {
            var health       = mcpManager.GetStatus();
            var settingsPath = SesMcpManager.GetClaudeCodeSettingsPath();
            var hooksPath    = SesMcpManager.GetSesHooksBinaryPath();

            var registered = ClaudeCodeSettings.LoadOrCreate(settingsPath).HasCorrectHooks(hooksPath);

            var lastActivity = await db.GetLastHookObservationTimeAsync();

            return Results.Ok(new
            {
                registered,
                binaryExists = health.SesHooksInstalled,
                lastActivity = lastActivity?.ToString("O")
            });
        });

        app.MapGet("/api/hooks/logs", async (ILocalDbService db) =>
        {
            var obs = await db.GetRecentHookObservationsAsync(20);
            return Results.Ok(obs.Select(o => new
            {
                timestamp = o.CreatedAt.ToString("O"),
                toolName  = o.ToolName,
                filePath  = o.FilePath
            }));
        });

        app.MapPost("/api/hooks/enable", async (SesMcpManager mcpManager) =>
        {
            await mcpManager.CheckAndRepairClaudeCodeHooksAsync();
            return Results.Ok(new { message = "Hooks registered" });
        });

        app.MapGet("/api/projects", async (ILocalDbService db, HttpContext ctx) =>
        {
            var paths = await db.GetKnownProjectsAsync(ctx.RequestAborted);
            return Results.Ok(paths);
        });

        // ── Updates & active sessions (TRAY-10) ───────────────────────────────

        app.MapGet("/api/updates/check", async (ComponentUpdateChecker checker, HttpContext ctx) =>
        {
            var updates = await checker.CheckAsync(ctx.RequestAborted);
            return Results.Ok(updates.Select(u => new
            {
                name             = u.Name,
                installedVersion = u.InstalledVersion,
                latestVersion    = u.LatestVersion,
                updateAvailable  = u.UpdateAvailable,
            }));
        });

        app.MapPost("/api/updates/apply/{component}", async (string component, SesLocalUpdater sesLocalUpdater, SesMcpUpdater sesMcpUpdater, HttpContext ctx) =>
        {
            UpdateResult result = component switch
            {
                "ses-local-daemon" => await sesLocalUpdater.CheckAndApplyAsync(ctx.RequestAborted),
                "ses-mcp"          => await sesMcpUpdater.CheckAndApplyAsync(ctx.RequestAborted),
                _                  => new UpdateResult(false, null, $"Unknown component: {component}"),
            };
            return Results.Ok(new { applied = result.UpdateApplied, newVersion = result.NewVersion, message = result.Message });
        });

        app.MapGet("/api/health", (HealthMonitorWorker health) =>
            Results.Ok(health.LatestReport));

        // OBS-6: Comprehensive one-shot repair — runs all component checks and returns a step-by-step report.
        app.MapPost("/api/repair", async (SesMcpManager mcpManager, IAuthService auth, ILocalDbService db, HttpContext ctx) =>
        {
            var steps = new List<string>();

            // Step 1: Socket — we're running so it's OK
            steps.Add("Socket: OK (daemon running)");

            // Step 2: Auth check/refresh
            try
            {
                var authState = await auth.GetStateAsync(ctx.RequestAborted);
                if (!authState.IsAuthenticated && authState.NeedsReauth)
                {
                    await auth.TriggerReauthAsync(ctx.RequestAborted);
                    steps.Add("Auth: Reauth triggered");
                }
                else
                {
                    steps.Add(authState.IsAuthenticated ? "Auth: OK" : "Auth: Not authenticated");
                }
            }
            catch (Exception ex) { steps.Add($"Auth: Error — {ex.GetType().Name}"); }

            // Step 3: Repair MCP config + ses-mcp binary
            try
            {
                var mcpResult = await mcpManager.CheckAndRepairAsync(ctx.RequestAborted);
                steps.Add($"ses-mcp: {(mcpResult.IsInstalled ? "Installed" : "Install attempted")}");
                steps.Add($"ClaudeDesktop: {(mcpResult.IsConfigured ? "Configured" : "Repair attempted")}");
            }
            catch (Exception ex) { steps.Add($"ses-mcp/ClaudeDesktop: Error — {ex.GetType().Name}"); }

            // Step 4: Re-register Claude Code hooks
            try
            {
                await mcpManager.CheckAndRepairClaudeCodeHooksAsync(ctx.RequestAborted);
                steps.Add("CCHooks: Re-registered");
            }
            catch (Exception ex) { steps.Add($"CCHooks: Error — {ex.GetType().Name}"); }

            // Step 5: SQLite integrity check
            try
            {
                await db.GetSyncStatsAsync(ctx.RequestAborted);
                steps.Add("SQLite: OK");
            }
            catch (Exception ex) { steps.Add($"SQLite: Error — {ex.GetType().Name}"); }

            return Results.Ok(new { steps });
        });

        app.MapGet("/api/sessions/active", async (ILocalDbService db, HttpContext ctx) =>
        {
            var since    = DateTime.UtcNow.AddHours(-24);
            var sessions = await db.GetActiveClaudeCodeSessionsAsync(since, ctx.RequestAborted);
            return Results.Ok(sessions.Select(s => new
            {
                projectName  = s.ProjectName,
                fullPath     = ProjectPathHelper.TryGetProjectFullPath(s.ProjectName),
                lastActivity = s.LastActivity.ToString("O"),
            }));
        });

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Ses.Local.Daemon.Program");

        await app.StartAsync();
        logger.LogInformation("Background services started");

        // Set socket permissions to owner-only (0600) on Unix platforms
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogInformation("Daemon IPC socket: {SocketPath}", socketPath);
        logger.LogInformation("OAuth/extension listener: http://localhost:37780");

        // Startup: require valid license OR OAuth tokens — never let failures kill the daemon
        try
        {
            var auth    = app.Services.GetRequiredService<IAuthService>();
            var license = app.Services.GetRequiredService<ILicenseService>();

            var authState    = await auth.GetStateAsync();
            var licenseState = await license.GetStateAsync();

            if (authState.IsAuthenticated)
            {
                logger.LogInformation("OAuth authentication active");

                // Periodic revocation check for license keys (if also have a license)
                if (licenseState.IsValid && await license.NeedsRevocationCheckAsync())
                {
                    logger.LogInformation("Performing license revocation check");
                    await license.CheckRevocationAsync();
                }
            }
            else if (licenseState.IsValid)
            {
                logger.LogInformation("License-only mode — Tier 1 user (license expires {ExpiresAt})", licenseState.ExpiresAt);

                // Periodic revocation check
                if (await license.NeedsRevocationCheckAsync())
                {
                    logger.LogInformation("Performing license revocation check");
                    var revocationOk = await license.CheckRevocationAsync();
                    if (!revocationOk)
                        logger.LogWarning("License revocation check failed — license may have been revoked");
                }
            }
            else if (licenseState.Status == LicenseStatus.NoLicense)
            {
                logger.LogInformation("No license and not authenticated — tray app will prompt for license key");
                await auth.TriggerReauthAsync();
            }
            else
            {
                logger.LogWarning("License invalid (status: {Status}) and not authenticated — prompting reauth", licenseState.Status);
                await auth.TriggerReauthAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup auth/license check failed — continuing headless");
        }

        // Block until SIGTERM/SIGINT
        await app.WaitForShutdownAsync();
    }
}

internal sealed record LicenseActivateRequest
{
    public string LicenseKey { get; init; } = string.Empty;
}

internal sealed record ImportConversationsRequest
{
    public string FilePath { get; init; } = string.Empty;
}

/// <summary>
/// Thread-safe singleton that tracks the state of an in-progress or recently completed import.
/// The tray polls GET /api/conversations/import/status every 500 ms during import.
/// </summary>
internal sealed class ImportProgressTracker : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();

    public bool    IsRunning       { get; private set; }
    public int     SessionsImported { get; private set; }
    public int     MessagesImported { get; private set; }
    public int     Duplicates       { get; private set; }
    public int     Errors           { get; private set; }
    public string  Format           { get; private set; } = string.Empty;
    public string? FailureMessage   { get; private set; }
    public bool    WasCancelled     { get; private set; }

    public CancellationToken CancellationToken
    {
        get { lock (_lock) return _cts.Token; }
    }

    public void Start(string format)
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old              = _cts;
            _cts             = new CancellationTokenSource();
            IsRunning        = true;
            SessionsImported = 0;
            MessagesImported = 0;
            Duplicates       = 0;
            Errors           = 0;
            Format           = format;
            FailureMessage   = null;
            WasCancelled     = false;
        }
        old.Dispose();
    }

    public void UpdateProgress(int sessionsProcessed)
    {
        lock (_lock) { SessionsImported = sessionsProcessed; }
    }

    public void Complete(ImportResult result)
    {
        lock (_lock)
        {
            IsRunning        = false;
            SessionsImported = result.SessionsImported;
            MessagesImported = result.MessagesImported;
            Duplicates       = result.Duplicates;
            Errors           = result.Errors;
        }
    }

    public void Cancel()
    {
        lock (_lock) { IsRunning = false; WasCancelled = true; }
    }

    public void Fail(string message)
    {
        lock (_lock) { IsRunning = false; FailureMessage = message; }
    }

    public void RequestCancel()
    {
        lock (_lock) { _cts.Cancel(); }
    }

    public object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                isRunning        = IsRunning,
                sessionsImported = SessionsImported,
                messagesImported = MessagesImported,
                duplicates       = Duplicates,
                errors           = Errors,
                format           = Format,
                wasCancelled     = WasCancelled,
                failureMessage   = FailureMessage,
            };
        }
    }

    public void Dispose()
    {
        lock (_lock) { _cts.Dispose(); }
    }
}

/// <summary>
/// Resolves a Claude Code project name to its full filesystem path by scanning
/// ~/.claude/projects/ and decoding the encoded directory names.
/// </summary>
internal static class ProjectPathHelper
{
    private static readonly string ProjectsRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static string? TryGetProjectFullPath(string projectName)
    {
        if (!Directory.Exists(ProjectsRoot)) return null;

        // Encoded dir names replace '/' and '.' with '-'. Suffix matches: "-projectName"
        var suffix = "-" + projectName;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            var name = Path.GetFileName(dir);
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var decoded = DecodeProjectPath(name);
            if (decoded is not null && Directory.Exists(decoded))
                return decoded;
        }

        return null;
    }

    /// <summary>
    /// Greedily reconstructs the original filesystem path from an encoded directory name.
    /// Encoding: each path separator '/' and '.' becomes '-'.
    /// Strategy: split on '-', then try combining adjacent segments from longest to shortest
    /// to match real directories, favouring paths that actually exist on disk.
    /// </summary>
    private static string? DecodeProjectPath(string encodedName)
    {
        // Encoded name starts with '-' (representing the leading '/')
        if (!encodedName.StartsWith('-')) return null;

        var parts = encodedName[1..].Split('-');
        return TryBuildPath("/", parts, 0);
    }

    private static string? TryBuildPath(string current, string[] parts, int idx)
    {
        if (idx == parts.Length) return current;

        // Try combining segments from longest to shortest to handle directory names with dashes
        for (var len = parts.Length - idx; len >= 1; len--)
        {
            var segment = string.Join("-", parts[idx..(idx + len)]);
            var candidate = Path.Combine(current, segment);
            if (Directory.Exists(candidate))
            {
                var result = TryBuildPath(candidate, parts, idx + len);
                if (result is not null) return result;
            }
        }

        return null;
    }
}
