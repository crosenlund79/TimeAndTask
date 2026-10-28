# WorkTrace

WorkTrace is a lightweight Windows tray utility for logging what you work on, reconstructing
working time from periodic check-ins, and preparing weekly timesheets. Everything is stored
locally; nothing is uploaded.

- Repository: `crosenlund79/TimeAndTask` · Product: **WorkTrace**
- Status: **Phase 1 (foundation)** — the app starts in the tray, creates its database and shows a
  status window. Check-ins, activity log, screenshots and timesheets arrive in later phases
  (see [docs/requirements.md](docs/requirements.md)).

## Requirements
- Windows 10 (1809+) or Windows 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run;
  .NET 10 SDK (10.0.100+) to build
- Optional: Visual Studio 2026 / Rider, or VS Code with C# Dev Kit

## Build and test
```powershell
dotnet build TimeAndTask.slnx
dotnet test --solution TimeAndTask.slnx
```
The test project uses xUnit v3 on Microsoft.Testing.Platform (configured in `global.json`).
Core and Infrastructure tests also run on Linux/macOS; the WPF app compiles there but runs only on Windows.

## Run
```powershell
dotnet run --project src/WorkTrace.App
```
WorkTrace starts in the notification area. Double-click the tray icon to open the main window;
closing the window returns to the tray; use **Exit** in the tray menu to quit. Starting a second
instance brings the running instance's window to the front. `--show` opens the window on start.

## Publish
```powershell
dotnet publish src/WorkTrace.App/WorkTrace.App.csproj -c Release -r win-x64 --self-contained false -o publish/WorkTrace
```
Run `publish/WorkTrace/WorkTrace.exe`. CI (`.github/workflows/ci.yml`) produces the same output as the
`WorkTrace-win-x64` build artifact on every push. An installer is planned for Phase 7.

## Local storage
| What | Location |
|---|---|
| Database (SQLite, WAL) | `%LocalAppData%\WorkTrace\worktrace.db` |
| Settings | `%LocalAppData%\WorkTrace\settings.json` |
| Logs (daily, 14 kept) | `%LocalAppData%\WorkTrace\logs\` |
| Backups (incl. automatic pre-migration backups) | `%LocalAppData%\WorkTrace\backups\` |
| Screenshots (Phase 5, opt-in) | `%LocalAppData%\WorkTrace\Screenshots\yyyy\MM\dd\` |

Set `WORKTRACE_DATA_DIR` to use a different data directory (useful for development).
None of these files are ever committed to Git.

## Documentation
- [CLAUDE.md](CLAUDE.md) — working notes for development sessions
- [docs/architecture.md](docs/architecture.md) — structure, database model, estimation algorithm, scheduling
- [docs/requirements.md](docs/requirements.md) — requirements, acceptance criteria, coverage
- [docs/decisions.md](docs/decisions.md) — architectural decisions
