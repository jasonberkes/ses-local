# ses-local

Super Easy Software Local — AI conversation sync, memory, and Claude Code intelligence for Mac and Windows.

ses-local runs as a background tray application, automatically syncing AI conversations across all surfaces (claude.ai, Claude Desktop, Claude Code, Cowork) and giving Claude Code intelligent memory from your full history.

**Download:** Available from TaskMaster UI > Settings > Apps. Requires a TaskMaster account.

## Project Structure

| Project | Purpose |
|---|---|
| `Ses.Local.Core` | Shared models, interfaces, enums — foundation for all other projects |
| `Ses.Local.Workers` | Background workers: LevelDB watcher, JSONL watcher, cloud sync, browser extension listener |
| `Ses.Local.Tray` | Avalonia cross-platform tray application (Mac + Windows) |
| `Ses.Local.Hooks` | AOT-compiled Claude Code hooks binary (ses-hooks) |
| `Ses.Local.Core.Tests` | Unit tests for Core |
| `Ses.Local.Workers.Tests` | Unit tests for Workers |

## Build

```
dotnet build ses-local.sln
dotnet test ses-local.sln
```

Requires .NET 10 SDK. NuGet packages sourced from tm-packages feed (authenticated via AZURE_DEVOPS_PAT in CI, or az keyvault locally).
