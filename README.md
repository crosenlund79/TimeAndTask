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
check-in now" triggers a real scheduled check-in for testing and says why when it cannot (paused, a
check-in already open). Overview and Settings show the state, interval, next check-in and the last
check-in attempt with its outcome; scheduled popups show a small countdown (paused while the pointer is on it).
- **Enter** on an empty search repeats the previous task · **Alt+1…4** (top row or number pad) records the numbered recent task
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
| **Stakeholders** | Customers and internal work; deactivate instead of delete once tasks exist |
| **Lists** | Task systems and communication channels: rename inline, hide, reorder, add |
| **Settings** | Check-in interval, popup mode (focused/unobtrusive), position, auto-close, shortcut, theme, time registration reminders, automatic Board cards for new tasks |
| **Board** | Kanban (Backlog, To do, In progress, Review / Test, Done): drag cards, open one to edit notes, links, dependencies, comments and screenshots; Archive; Backup & restore |
| **Tasks › Inbox** | Quick capture of thoughts, links and pasted screenshots (`Ctrl+Shift+C` from anywhere); organise later ("To board", archive) |
| **Timesheet** | Monday–Sunday hours per task (estimated from check-ins), stakeholder and reference, corrections in the cells, unclassified hours; export to Excel or CSV |
| **Screenshots** | Automatic captures by day (opt-in in Settings), with the task you were on; open, delete |
| **Guides** | Your how-tos grouped by category; search titles and text; attached PDFs; added/modified dates, "Stale" after six months without changes |

Keyboard: `Ctrl+1…9` switch pages · Board: `Alt+←/→/↑/↓` move the selected card · `Ctrl+Z` undo ·
`Ctrl+Shift+C` capture · `Ctrl+V` pastes a screenshot into the open card · `Esc` closes it.
Lists: `Ctrl+F` search · `↓` from search into the list · `Enter` edit ·
`Ctrl+N` new · `Ctrl+S` save · `Esc` discard/clear · `Del` delete (offers archive/deactivate when in use).

## Install
Download `WorkTrace-Setup-<version>` from the latest successful **CI** run (Actions tab), unzip it and run
`WorkTrace-Setup.exe`. It installs per user (no admin prompt) to `%LocalAppData%\Programs\WorkTrace`, adds a Start
menu entry (desktop shortcut optional) and an entry in Settings › Apps › Installed apps. Running a newer setup
upgrades in place: it closes a running WorkTrace (also the tray-only one), replaces the program files and keeps the
install location. Your data in `%LocalAppData%\WorkTrace` is never touched, also not by uninstalling.
Requires the Microsoft .NET 10 Desktop Runtime (x64); setup offers the download page when it is missing.

The version (Major.Minor from `Directory.Build.props` + CI run number) is shown at the bottom of the navigation
rail and in Installed apps. A portable ZIP (`WorkTrace-win-x64`) is still produced by CI.

## Local storage
| What | Location |
|---|---|
| Database (SQLite, WAL) | `%LocalAppData%\WorkTrace\worktrace.db` |
| Settings | `%LocalAppData%\WorkTrace\settings.json` |
| Logs (daily, 14 kept) | `%LocalAppData%\WorkTrace\logs\` |
| Backups (incl. automatic pre-migration backups) | `%LocalAppData%\WorkTrace\backups\` |
| Screenshots (Phase 5, opt-in) | `%LocalAppData%\WorkTrace\Screenshots\yyyy\MM\dd\` |
| Board attachments (screenshots on cards) | `%LocalAppData%\WorkTrace\Attachments\yyyy\MM\` |

Set `WORKTRACE_DATA_DIR` to use a different data directory (useful for development).
None of these files are ever committed to Git.

## Documentation
- [CLAUDE.md](CLAUDE.md) — working notes for development sessions
- [docs/architecture.md](docs/architecture.md) — structure, database model, estimation algorithm, scheduling
- [docs/requirements.md](docs/requirements.md) — requirements, acceptance criteria, coverage
- [docs/decisions.md](docs/decisions.md) — architectural decisions
- [Development board](https://crosenlund79.github.io/TimeAndTask/) — Kanban board on the `board` branch (`docs/board.json`)
