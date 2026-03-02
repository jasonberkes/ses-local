namespace Ses.Local.Core.Options;

/// <summary>
/// Top-level configuration options for ses-local.
/// Populated from appsettings.json and/or environment variables.
/// Feature flags will be driven by PAT scopes in WI-936; for now use these bools.
/// </summary>
public sealed class SesLocalOptions
{
    public const string SectionName = "SesLocal";

    /// <summary>Identity server base URL.</summary>
    public string IdentityBaseUrl { get; init; } = "https://identity.tm.supereasysoftware.com";

    /// <summary>TaskMaster DocumentService base URL.</summary>
    public string DocumentServiceBaseUrl { get; init; } =
        "https://tm-documentservice-prod-eus2.redhill-040b1667.eastus2.azurecontainerapps.io";

    /// <summary>Cloud memory service base URL.</summary>
    public string MemoryBaseUrl { get; init; } = "https://memory.tm.supereasysoftware.com";

    /// <summary>Claude.ai web app base URL (used for session sync).</summary>
    public string ClaudeAiBaseUrl { get; init; } = "https://claude.ai";

    /// <summary>ses-mcp auto-update manifest URL.</summary>
    public string SesMcpManifestUrl { get; init; } =
        "https://tmprodeus2data.blob.core.windows.net/artifacts/ses-mcp/latest.json";

    /// <summary>ses-local auto-update manifest URL.</summary>
    public string SesLocalManifestUrl { get; init; } =
        "https://tmprodeus2data.blob.core.windows.net/artifacts/ses-local/latest.json";

    /// <summary>ses-cloud MCP proxy URL.</summary>
    public string CloudMcpUrl { get; init; } = "https://mcp.tm.supereasysoftware.com/mcp";

    /// <summary>Documentation site base URL.</summary>
    public string DocsBaseUrl { get; init; } = "https://docs.supereasysoftware.com";

    /// <summary>Enable Claude Code JSONL session watcher. Requires developer scope.</summary>
    public bool EnableClaudeCodeSync { get; set; } = true;

    /// <summary>Enable Claude Desktop LevelDB UUID watcher.</summary>
    public bool EnableClaudeDesktopSync { get; set; } = true;

    /// <summary>Polling interval in seconds for file watchers.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// RSA public key PEM from the identity server, used for offline license key validation.
    /// Fetched from the JWKS endpoint on first activation and cached here for offline use.
    /// </summary>
    public string? LicensePublicKeyPem { get; set; }

    /// <summary>Days between online revocation checks. Default: 7.</summary>
    public int LicenseRevocationCheckDays { get; set; } = 7;

    /// <summary>Enable automatic CLAUDE.md generation in project roots after session activity.</summary>
    public bool EnableClaudeMdGeneration { get; set; } = true;

    /// <summary>Maximum age in days of activity to include in generated CLAUDE.md files.</summary>
    public int ClaudeMdMaxAgeDays { get; set; } = 7;

    /// <summary>Project paths to exclude from CLAUDE.md generation (substring match).</summary>
    public IReadOnlyList<string> ClaudeMdExcludePaths { get; set; } = [];

    // ── Vector Search (WI-989) ────────────────────────────────────────────────

    /// <summary>
    /// Enable local vector search using ONNX embeddings.
    /// When enabled, session summaries are embedded and searchable via cosine similarity.
    /// </summary>
    public bool EnableVectorSearch { get; set; }

    /// <summary>
    /// Path to the all-MiniLM-L6-v2 ONNX model file.
    /// Downloaded automatically on first use if not present.
    /// </summary>
    public string EmbeddingModelPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ses", "models", "all-MiniLM-L6-v2.onnx");

    // ── Telemetry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Enable OpenTelemetry metrics and tracing.
    /// When false, all OTel instruments are no-ops (no overhead).
    /// </summary>
    public bool EnableTelemetry { get; set; } = true;

    /// <summary>
    /// Telemetry exporter to use.
    /// Supported values: "console" (default), "otlp" (future).
    /// </summary>
    public string TelemetryExporter { get; set; } = "console";
}
