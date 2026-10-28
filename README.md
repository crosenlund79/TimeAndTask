# WorkTrace

WorkTrace is a lightweight Windows tray utility for logging what you work on, reconstructing
working time from periodic check-ins, and preparing weekly timesheets. Everything is stored
locally; nothing is uploaded.

- Repository: `crosenlund79/TimeAndTask` · Product: **WorkTrace**
- Status: **Phase 3** — tray app with periodic check-ins, pause/resume, and management of clients, tasks,
  task systems and communication channels. Activity log, screenshots and timesheets arrive in later phases
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
The test projects use xUnit v3 on Microsoft.Testing.Platform (configured in `global.json`).
`WorkTrace.Tests` (Core/Infrastructure) runs anywhere; `WorkTrace.App.Tests` (WPF rendering and
view-model flows) runs on Windows only and is skipped elsewhere.

## Run
```powershell
dotnet run --project src/WorkTrace.App
```
WorkTrace starts in the notification area. Double-click the tray icon to open the main window;
closing the window returns to the tray; use **Exit** in the tray menu to quit. Starting a second
instance brings the running instance's window to the front. `--show` opens the window on start.

## Check-ins
Every 30 minutes (on the hour and half hour) a small popup asks what you are working on. The interval
can be 1, 5, 10, 15, 30 or 60 minutes, or any whole number of minutes from 1 to 240 (Settings → Check-in
interval → Custom). Changes apply immediately; missed slots are never replayed. Settings → "Show a scheduled
check-in now" triggers a real scheduled check-in for testing.
- **Enter** on an empty search repeats the previous task · **Alt+1…4** records the numbered recent task
  (shown with its key while the search box is empty; does nothing when there are no recent tasks)
- Type to search (title, ticket ID or client), **↑/↓** to choose, **Enter** to record; unknown text creates a task
- **Esc** skips; an unanswered check-in closes itself and the time after it is marked uncertain
- **Ctrl+Alt+Space** (configurable) opens a check-in at any time, with the cursor in the search box, even
  when another application is active. The popup border turns accent-coloured while it has keyboard focus.
- Pause from the tray (15 min … until resumed). Sleep, shutdown and pauses are never counted as work;
  locked time is uncertain by default.

## Using WorkTrace
| Page | What it's for |
|---|---|
| **Overview** | Tracking status, record/pause, today's check-ins and gaps, data folder |
| **Tasks** | Search-as-you-type by title, ticket ID (`42` finds `BUG-0042`) or client; edit on the right; archive tasks you no longer use |
| **Clients** | Customers and internal work; deactivate instead of delete once tasks exist |
| **Lists** | Task systems and communication channels: rename inline, hide, reorder, add |
| **Settings** | Check-in interval, popup mode (focused/unobtrusive), position, auto-close, shortcut, theme |

Keyboard: `Ctrl+1…5` switch pages · `Ctrl+F` search · `↓` from search into the list · `Enter` edit ·
`Ctrl+N` new · `Ctrl+S` save · `Esc` discard/clear · `Del` delete (offers archive/deactivate when in use).

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
