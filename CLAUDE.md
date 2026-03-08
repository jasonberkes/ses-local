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
| `Ses.Local.Tray` | WinExe | Avalonia system tray app — menu-bar only (no Dock icon), supervises daemon via `DaemonSupervisor` |
| `Ses.Local.Workers` | Library | All background workers, sync services, HTTP clients, compression, embedding, telemetry |

### Daemon / Tray Architecture (WI-970)

Two independent binaries communicate over Unix domain socket IPC:

| Binary | Path | Purpose |
|--------|------|---------|
| `ses-local.app` | `~/.ses/ses-local.app` | Tray app — system tray icon, menu, UI. Starts on login via launchd. |
| `ses-local-daemon` | `~/.ses/bin/ses-local-daemon` | Headless daemon — all workers, Kestrel over UDS, no UI. Supervised by tray. |

**Lifecycle**: The tray app owns the daemon lifecycle via `DaemonSupervisor`:
- On tray startup, checks if daemon socket exists (already running) or launches the daemon binary
- Monitors daemon health via process `HasExited` / socket availability (every 10 s)
- On crash: exponential backoff restart — 5 s → 15 s → 45 s (max 3 attempts)
- Resets retry counter after 60 s of stable running
- On tray quit: sends `POST /api/shutdown`, waits 5 s, then kills process

**Tray UI — NativeMenu only**: A single `NativeMenu` handles all tray icon interactions. The DropdownPanel window approach was removed because `TrayIcon.Clicked` does not fire reliably on macOS when a `Menu` is present.
- One click on the tray icon shows ONE `NativeMenu` with all information and actions
- Menu is built in `TrayApp.OnFrameworkInitializationCompleted()` and updated every 5 s by `_statusTimer`
- Dynamic menu items (status, sync counts, model, hooks, components) are stored as fields and updated in `UpdateMenuItemsAsync()`
- "Import Conversations..." uses an invisible helper `Window` as a `TopLevel` for the native file picker
- "Manage MCP Servers..." and "Open settings.json" open `~/.claude/settings.json` in the system editor
- No `DropdownPanel`, no `DropdownPanelViewModel`, no `TrayIcon.Clicked` handler

**Menu structure**:
- Status (`● Connected` / `○ Not connected`) + Uptime
- Sync stats: Claude Desktop, Claude Code, Claude.ai, ChatGPT counts
- **CC Config** submenu: Model info, Change Model submenu, MCP server count, Manage MCP Servers..., Hooks status, Toggle Hooks, Open settings.json
- Import Conversations... + Last import age
- **Components** submenu: Daemon / ses-mcp / ses-hooks status + Check for Updates
- Sign In... (disabled when authenticated) / Stop Daemon / Quit

**launchd**: Only the tray app has a launchd plist (`com.supereasysoftware.ses-local-tray.plist`).
The old daemon plist (`com.supereasysoftware.ses-local.plist`) is deprecated but kept for existing installs.

### Browser Extension

`browser-extension/` — Chrome/Edge/Firefox Manifest V3 extension that syncs both Claude.ai and ChatGPT conversations. Communicates with daemon via `http://localhost:37780/`.

**Claude.ai sync** (`claude-api.js`): polls `/api/organizations/{orgId}/chat_conversations` every 5 min with session cookies.

**ChatGPT sync** (`chatgpt-api.js`): polls `chatgpt.com/backend-api/conversations` every 5 min with session cookies.
- API endpoints: `GET /backend-api/conversations?offset=N&limit=50&order=updated` (list) and `GET /backend-api/conversation/{id}` (detail)
- Timestamps are Unix epoch **seconds** — multiply by 1000 for JS `Date`
- Conversations use a tree structure (`mapping` dict with parent/children links) — `flattenMessages()` walks the tree BFS to produce a chronological message array
- Rate limit: 3 RPS (more conservative than Claude's 5 RPS)
- Auth: browser session cookies sent automatically (`credentials: 'include'`) — no token storage needed
- ChatGPT Desktop conversations are **encrypted** and cannot be read locally; browser extension is the primary sync path

**ChatGPT Desktop** (`ChatGptDesktopWatcher.cs`): detects installation at `~/Library/Application Support/com.openai.chat/` (macOS) or `%LOCALAPPDATA%/Programs/ChatGPT/` (Windows), counts conversation files, and logs guidance to use Settings > Export Data for manual import. Runs every 5 min.

### Test Projects

| Project | Tests | Coverage |
|---------|-------|----------|
| `Ses.Local.Core.Tests` | 48 | Options validation, model serialization, utility services |
| `Ses.Local.Workers.Tests` | 435 | Worker unit tests, service tests, telemetry, DaemonSupervisor |
| `Ses.Local.Integration.Tests` | 55 | SQLite CRUD, vector search, JSONL parsing (real temp DB) |

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
- Tables: `conv_sessions`, `conv_messages`, `conv_observations`, `conv_observation_links`, `conv_session_summaries`, `conv_embeddings`, `conv_relationships`, `conv_workitem_links`, `memory_observations`, `memory_summaries`, `sync_ledger`, `import_history`, `sync_metadata`
- FTS5 indexes: `conv_messages_fts`, `conv_observations_fts`, `conv_session_summaries_fts`

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

## Background Workers (12)

| Worker | Purpose |
|--------|---------|
| `ClaudeCodeWatcher` | Watches `~/.claude/projects/**/*.jsonl`, extracts observations, auto-links WorkItems |
| `LevelDbWatcher` | Monitors Claude Desktop LevelDB for UUID changes |
| `CoworkWatcher` | Cowork surface sync |
| `ClaudeDesktopSyncWorker` | Claude Desktop conversation sync |
| `ChatGptDesktopWatcher` | Detects ChatGPT Desktop installation, counts conversations, prompts manual export |
| `CloudSyncWorker` | Uploads pending sessions to DocumentService |
| `CloudPullWorker` | Downloads conversations from cloud (multi-device) |
| `BrowserExtensionListener` | HTTP listener for browser extension |
| `CompressionWorker` | Observation compression + embedding + cross-session linking |
| `AutoUpdateWorker` | Checks/downloads binary updates |
| `SesMcpManagerWorker` | ses-mcp auto-installation and updates |
| `HealthMonitorWorker` | Self-healing health monitor — checks all components every 30 s, auto-repairs |

### HealthMonitorWorker (OBS-4)

Monitors all daemon components every **30 seconds**. Exposes results at `GET /api/health`.

**Checks:**

| Check | Category | What it verifies |
|-------|----------|-----------------|
| `Auth` | Auth | OAuth token valid; triggers reauth if expired |
| `ses-mcp` | Config | ses-mcp binary exists at `~/.ses/bin/ses-mcp` |
| `ClaudeDesktop` | Config | Claude Desktop MCP config present and no drift |
| `CCHooks` | Config | ses-hooks registered correctly in `~/.claude/settings.json` |
| `Socket` | Infrastructure | IPC Unix socket file exists |
| `SQLite` | Storage | SQLite DB accessible (queries `GetSyncStatsAsync`) |

**Auto-repair:** When `ClaudeDesktop` or `ses-mcp` checks are degraded/unhealthy, calls `SesMcpManager.CheckAndRepairAsync()` with exponential backoff:
- Before attempt 2: wait 30 s
- Before attempt 3: wait 90 s
- After 3 attempts: blocked until 1-hour window resets

**GET /api/health response:**
```json
{
  "checkedAt": "2026-03-07T00:00:00Z",
  "status": "Healthy|Degraded|Unhealthy",
  "checks": [
    { "name": "Auth", "category": "Auth", "status": "Healthy", "message": "Authenticated", "repairAttempts": 0 }
  ]
}
```

**Registration:** Singleton + `AddHostedService` pattern so the `/api/health` endpoint shares the same instance.

### ChatGPT Desktop Storage Format (investigated 2026-03-06)

**Finding**: The ChatGPT macOS native app (`com.openai.chat`) stores conversations as encrypted
binary `.data` files in `~/Library/Application Support/com.openai.chat/conversations-v3-{user-id}/`.
The encryption key is held in the OS Keychain by the app process — **conversation content cannot be
read by third-party code**.

- `~/Library/Application Support/ChatGPT/` exists but is always empty (legacy/unused)
- `~/Library/Application Support/com.openai.chat/` is the real data directory
- Each conversation is `{uuid}.data` (encrypted binary, not plist/JSON)
- `ChatGptDesktopWatcher` detects installation and counts files; content sync is not possible
- Primary sync path remains manual export: Settings > Export Data in ChatGPT → import ZIP via ses-local
- `ChatGptDesktopPaths` handles macOS (`com.openai.chat`) and Windows (`ChatGPT`) detection

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
2. `dotnet test` — all 538 tests must pass
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
