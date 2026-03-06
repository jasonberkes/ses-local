# ses-local

## Project Overview

ses-local is the Super Easy Software Local application — a cross-platform desktop app (macOS + Windows) that syncs AI conversations from Claude Desktop, Claude Code, Claude.ai, Cowork, ChatGPT, and Gemini into TaskMaster's cloud platform. It provides local SQLite storage, vector search via ONNX embeddings, observation compression, and MCP server management.

## Architecture

**Target Framework:** .NET 10.0 (`net10.0`, with `net10.0-windows10.0.19041.0` for WinRT)

### Source Projects

| Project | Type | Purpose |
|---------|------|---------|
| `Ses.Local.Core` | Library | Shared models, interfaces, options (`SesLocalOptions`), enums, events |
| `Ses.Local.Daemon` | Exe (`ses-local-daemon`) | Headless background service over Unix domain socket (`~/.ses/local.sock`), hosts all workers, OpenTelemetry, single-instance mutex |
| `Ses.Local.Hooks` | Exe (`ses-hooks`, AOT) | Claude Code lifecycle hooks binary — fast 3s timeout, direct SQLite fallback if daemon unavailable |
| `Ses.Local.Tray` | WinExe | Avalonia system tray app — feature flag UI, license activation, MCP server provisioning |
| `Ses.Local.Workers` | Library | All background workers, sync services, HTTP clients, compression, embedding, telemetry |

### Browser Extension

`browser-extension/` — Chrome/Edge/Firefox Manifest V3 extension for claude.ai conversation sync. Communicates with daemon via `http://localhost:37780/`.

### Test Projects

| Project | Tests | Coverage |
|---------|-------|----------|
| `Ses.Local.Core.Tests` | 49 | Options validation, model serialization, utility services |
| `Ses.Local.Workers.Tests` | 341 | Worker unit tests, service tests, telemetry, ViewModels |
| `Ses.Local.Integration.Tests` | 33 | SQLite CRUD, vector search, JSONL parsing (real temp DB) |

## Key Patterns

### Configuration
- All config in `IOptions<SesLocalOptions>` — no hardcoded URLs
- Validated at startup via `SesLocalOptionsValidator` (HTTPS URLs enforced)
- Bound from `appsettings.json` section `"SesLocal"`

### HTTP Resilience
- All HTTP clients registered in `DependencyInjection.cs` with `AddStandardResilienceHandler` (Polly)
- Configurable retry counts, attempt timeouts, and total timeouts per client
- Named clients: `DocumentService`, `CloudMemory`, `SesMcpInstall`, `ClaudeAi`, `Identity`, `License`, etc.

### Async & Cancellation
- All async methods accept `CancellationToken ct = default`
- Workers use `PeriodicTimer` with `stoppingToken`
- Hooks enforce 3s timeout

### Telemetry
- OpenTelemetry metrics + traces via `SesLocalMetrics` (meter: `ses-local` v1.0)
- Counters: `ses.watcher.sessions_processed`, `ses.sync.uploads_*`, `ses.compression.*`, `ses.auth.*`
- Histogram: `ses.db.query_duration_ms`
- Conditionally enabled via `EnableTelemetry` option

### Data Storage
- SQLite at `~/.ses/local.db` via `ILocalDbService`
- Tables: `conv_sessions`, `conv_messages`, `conv_observations`, `observation_links`, `conv_session_summaries`, `conv_embeddings`, `conv_relationships`, `conv_workitem_links`, `sync_metadata`
- FTS5 index: `conv_messages_fts`

### Vector Search
- ONNX model: `all-MiniLM-L6-v2` (384-dim, auto-downloaded to `~/.ses/models/`)
- `ILocalEmbeddingService` for inference, `IVectorSearchService` for cosine similarity
- Gated by `EnableVectorSearch` option

### Compression Pipeline
- Layer 1 (`RuleBasedCompressor`): always runs — tool counts, error classification, file refs, category
- Layers 2–3: pluggable via `IObservationCompressor` for future LLM-based summarization
- `CompressionWorker` polls every 60s, processes 10 sessions/batch

### License System
- Online activation via identity server → RSA public key for offline validation
- Keys stored in OS keychain (`ICredentialStore`: macOS Keychain / Windows Credential Vault)
- Revocation checks every 7 days
- Tiers: 0 (no license), 1 (local-only), 2 (OAuth + cloud sync)

### Privacy Controls
- `<private>...</private>` tags stripped before storage (`PrivateTagStripper`)
- Session exclusion via `ExcludedProjectPaths`
- Import filtering for ChatGPT/Gemini exports

### Credential Store
- Platform-specific: `MacCredentialStore` (macOS) / `WindowsCredentialStore` (Windows) / `InMemoryCredentialStore` (Linux/CI)

## Background Workers (10)

| Worker | Purpose |
|--------|---------|
| `ClaudeCodeWatcher` | Watches `~/.claude/projects/**/*.jsonl`, extracts observations, auto-links WorkItems |
| `LevelDbWatcher` | Monitors Claude Desktop LevelDB for UUID changes |
| `CoworkWatcher` | Cowork surface sync |
| `ClaudeDesktopSyncWorker` | Claude Desktop conversation sync |
| `CloudSyncWorker` | Uploads pending sessions to DocumentService |
| `CloudPullWorker` | Downloads conversations from cloud (multi-device) |
| `BrowserExtensionListener` | HTTP listener for browser extension |
| `CompressionWorker` | Observation compression + embedding + cross-session linking |
| `AutoUpdateWorker` | Checks/downloads binary updates |
| `SesMcpManagerWorker` | ses-mcp auto-installation and updates |

## Build & Test

```bash
# Restore (private Azure DevOps NuGet feed)
dotnet restore

# Build
dotnet build

# Test
dotnet test

# Publish (macOS ARM64)
dotnet publish src/Ses.Local.Daemon -c Release -r osx-arm64 -p:PublishSingleFile=true --self-contained

# Publish hooks (AOT)
dotnet publish src/Ses.Local.Hooks -c Release -r osx-arm64 -p:PublishSingleFile=true -p:PublishAot=true
```

### NuGet Feed

Private feed `tm-packages` at Azure DevOps. Auth: `az` CLI locally, `AZURE_DEVOPS_PAT` in CI.

Central package versioning via `Directory.Packages.props`.

### CI/CD

- `.github/workflows/ci.yml` — PR validation (build + test on ubuntu-latest)
- `.github/workflows/release.yml` — Production release (build → publish osx-arm64/win-x64 → Azure Blob → GitHub Release)

## Key Dependencies

| Package | Purpose |
|---------|---------|
| `Avalonia` 11.3.x | Cross-platform UI framework (tray app) |
| `Microsoft.Data.Sqlite` 10.0.0 | Local SQLite database |
| `Microsoft.Extensions.Http.Resilience` 10.3.0 | Polly-based HTTP resilience |
| `Microsoft.ML.OnnxRuntime` 1.24.2 | ONNX embedding inference |
| `Microsoft.ML.Tokenizers` 2.0.0 | Tokenization for embeddings |
| `System.IdentityModel.Tokens.Jwt` 8.4.0 | OAuth JWT token parsing |
| `OpenTelemetry.*` 1.11.2 | Metrics, tracing, structured logging |
| `TaskMaster.DocumentService.Client` 1.2.0 | Cloud sync client (private feed) |

## Common Tasks

### Add a New Worker
1. Create class in `src/Ses.Local.Workers/Workers/` inheriting `BackgroundService`
2. Override `ExecuteAsync(CancellationToken stoppingToken)` with `PeriodicTimer`
3. Inject `IOptions<SesLocalOptions>`, `ILogger<T>`, `ILocalDbService`, etc.
4. Register in `Ses.Local.Daemon/Program.cs`: `builder.Services.AddHostedService<MyWorker>()`
5. Add unit tests in `tests/Ses.Local.Workers.Tests/Workers/`

### Add a New MCP Tool
1. MCP tools are managed by `SesMcpManagerWorker` (auto-installs `ses-mcp` binary)
2. MCP config managed via `IMcpConfigManager` — reads/writes host config files for Claude Desktop, claude-app, codeium
3. Config paths determined per `McpHost` enum

### Add a New HTTP Client
1. Register in `src/Ses.Local.Workers/DependencyInjection.cs`
2. Configure base address from `SesLocalOptions` (never hardcode URLs)
3. Add `AddStandardResilienceHandler()` with appropriate retry/timeout
4. Create service class that accepts `IHttpClientFactory`

### Test Locally
1. `dotnet build` — ensure 0 errors, 0 warnings
2. `dotnet test` — all 423 tests must pass
3. Run daemon: `dotnet run --project src/Ses.Local.Daemon`
4. Daemon listens on `~/.ses/local.sock` (Unix socket)
5. Integration tests use real temp SQLite instances

## File Paths

| Path | Purpose |
|------|---------|
| `~/.ses/local.db` | SQLite database |
| `~/.ses/local.sock` | Daemon Unix domain socket |
| `~/.ses/models/` | ONNX model directory |
| `~/.ses/watcher-positions.json` | Incremental JSONL parsing state |
| `~/.claude/projects/**/*.jsonl` | Claude Code conversation logs |

## Coding Standards

- No `NoWarn` suppressions in .csproj files
- All URLs must come from `SesLocalOptions` (validated HTTPS)
- All async methods must accept `CancellationToken`
- All HTTP clients must use Polly resilience policies
- Secrets stored in OS keychain, never in config files
- `<private>` tags stripped before any storage or sync
