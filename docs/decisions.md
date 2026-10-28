# Architectural decisions

## ADR-001 — WPF on .NET 10 with the built-in Fluent theme
**Decision:** WPF, .NET 10 LTS, `Application.ThemeMode` (Fluent, light/dark/system).
**Alternatives:** WinUI 3 (heavier deployment, weaker tray story), WPF-UI library (more controls, extra dependency).
**Reason:** native, no extra dependency, modern look. ThemeMode is experimental (WPF0001); WPF-UI remains the fallback.

## ADR-002 — EF Core + SQLite with `IDbContextFactory`
**Decision:** EF Core 10 with migrations; short-lived contexts from a factory; no generic repository.
**Alternatives:** Dapper + Microsoft.Data.Sqlite (lighter, manual migrations).
**Reason:** migrations, change tracking and transactions out of the box, as the specification prefers.
Idle memory cost (~20–40 MB) is acceptable; a switch would be contained in Infrastructure.

## ADR-003 — Windows-specific code lives in the App project
**Decision:** Core and Infrastructure target `net10.0`; screen capture, power/lock events, hotkeys and
startup registration are implemented in `WorkTrace.App` behind Core interfaces.
**Alternatives:** Infrastructure targeting `net10.0-windows` (as the original brief's layering implies).
**Reason:** keeps persistence/scheduling/retention logic testable on any OS (the development container is
Linux) and in CI. Screenshot path/metadata/retention logic still lives in Infrastructure.

## ADR-004 — UTC `DateTime` with an enforcing converter
**Decision:** store UTC `DateTime` (Kind=Utc) as ISO text; reject `Unspecified` kinds.
**Alternatives:** `DateTimeOffset` (EF Core SQLite cannot order/compare it in SQL), Unix ticks (unreadable).
**Reason:** correct ordering and range queries in SQL, readable data, no silent local/UTC mix-ups.

## ADR-005 — Observations are points; time is derived
**Decision:** observations are immutable evidence (with `Original*` columns); periods are computed by a
deterministic engine and never persisted. User corrections are `ManualAllocations` overlaid on the estimate.
**Alternatives:** persisting computed periods and editing them; full event sourcing.
**Reason:** reproducible reports, no stale derived data, manual corrections can't be lost by recalculation,
no double counting (flattening), far simpler than event sourcing.

## ADR-006 — Missed check-ins create uncertainty, not attribution
**Decision:** missed/dismissed/suppressed prompts are stored; time after the prompt's scheduled time is
Unclassified until the next observation. Trailing estimates are capped by `MaxEstimationHorizonMinutes`.
**Reason:** never fabricate work; specification requirement.

## ADR-007 — Locked time is uncertain by default
**Decision:** lock periods are stored; by default every locked interval is Unclassified and breaks
estimation (`LockGraceMinutes = 0`). `LockedTimeHandling.NotWorking` treats it as non-working instead.
**Reason:** owner decision — locked time must never be considered confirmed work.

## ADR-008 — Generic audit log for undo/history
**Decision:** a single append-only `ChangeLog` table (before/after JSON per entity, grouped by batch).
Undo applies the before-state as a new batch.
**Alternatives:** per-entity revision tables.
**Reason:** uniform undo across entity types with one small table; original observation values are
additionally kept in dedicated columns for querying.

## ADR-009 — Settings in JSON, not in the database
**Decision:** `settings.json` in the data folder, validated, atomically written; invalid files preserved.
**Reason:** restoring a database backup must not roll back machine settings; human-readable.
Lookup lists referenced by data (task systems, channels) are in the database.

## ADR-010 — Tray via Windows Forms `NotifyIcon`
**Decision:** `UseWindowsForms` for `NotifyIcon` + `ContextMenuStrip`; implicit WinForms usings removed.
**Alternatives:** H.NotifyIcon.Wpf.
**Reason:** mature, no extra dependency, as suggested in the specification.

## ADR-011 — Single instance via named mutex + event
**Decision:** `Local\` named mutex; a second launch sets a named event and exits after
`AllowSetForegroundWindow(ASFW_ANY)`.
**Reason:** no dependencies, per-session scope, simple activation.

## ADR-012 — Logging via Serilog file sink behind `ILogger<T>`
**Decision:** `Serilog.Extensions.Logging` + `Serilog.Sinks.File`; code uses Microsoft.Extensions.Logging only.
**Reason:** M.E.L has no file provider; Serilog's file sink is mature (rolling, retention, size caps).
CA1848/CA1873 suppressed: log volume is low.

## ADR-013 — xUnit v3 on Microsoft.Testing.Platform
**Decision:** xUnit v3 (4.x) with MTP runner configured in `global.json`; plain `Assert`.
**Reason:** current xUnit; .NET 10 `dotnet test` supports MTP natively. FluentAssertions avoided (commercial licence).

## ADR-014 — Screenshot format
**Decision:** PNG by default (readability); JPEG optional with configurable quality (default 85).
**Reason:** owner decision.

## ADR-015 — Communication channel on observations
**Decision:** the channel is stored per observation/allocation; tasks carry only a default channel.
**Reason:** a channel describes an interaction (a call, an email), which can differ for the same task.

## ADR-016 — Single time zone for reporting
**Decision:** UTC storage, reporting in the current system time zone.
**Reason:** simple and correct for a stationary user; travelling re-labels earlier days (accepted limitation).

## ADR-017 — Management services return read models; search ranks in memory
**Decision:** services return immutable summaries; task search filters in SQL and ranks in .NET with
`TaskSearch` (accent-insensitive, external-ID-first, deterministic).
**Alternatives:** SQLite `LIKE`/FTS5; returning EF entities to view models.
**Reason:** accent/case-insensitive matching (Danish letters, é) and ID normalisation are not possible
with `LIKE`; personal task lists are small; read models keep EF out of the UI.

## ADR-018 — Deactivate/archive/hide instead of delete for referenced data
**Decision:** referenced clients are deactivated, tasks archived, lookup items hidden; deletion is
offered only for unreferenced records, with confirmation.
**Reason:** history must stay reproducible; typos still need a clean way out.

## ADR-019 — Master-detail editing with explicit save and a locked list while dirty
**Decision:** edit a copy, explicit Save/Discard, list disabled while there are unsaved changes;
reversible operations act immediately with an Undo toast; lookup lists save inline immediately.
**Alternatives:** auto-save on selection change; modal edit dialogs; editable DataGrid.
**Reason:** never lose or silently persist half-typed edits, keep the UI calm and keyboard-friendly, and
avoid the "admin database" feel of grids and modal forms.

## ADR-020 — Windows-only WPF test project
**Decision:** `WorkTrace.App.Tests` (net10.0-windows) renders the real window/pages with the Fluent theme
and fails on WPF binding errors; also drives view-model flows. Skipped on non-Windows hosts, run in CI.
**Reason:** the development container cannot run WPF; binding errors are otherwise silent.

## ADR-021 — "None" is key 0 in pickers
**Decision:** ComboBox selections use `PickerOption.NoneKey` (0) for "none"/"all"; null writes from
ComboBoxes are ignored.
**Reason:** a ComboBox cannot display a selected null value, and it writes null back transiently while
its items are replaced, which would otherwise mark editors dirty.

## ADR-022 — Single-timer tracking engine in Infrastructure
**Decision:** `TrackingEngine` (net10.0) owns segments, schedule, pause and system-event reactions with one
`TimeProvider` timer and an async gate; the UI only presents popups through `ICheckInPresenter`.
**Alternatives:** DispatcherTimer logic in the App; polling every minute.
**Reason:** deterministic tests with a fake clock on any OS, no polling, one place for the invariants.

## ADR-023 — Clock-aligned check-ins with no backlog
**Decision:** slots aligned to the local clock (e.g. :00/:30), always computed from "now", at least half an
interval apart; one prompt after start/resume/unlock.
**Reason:** predictable for the user and for timesheets; never a burst of stale popups after sleep.

## ADR-024 — Pause state stored in settings
**Decision:** `Tracking.Enabled` / `PausedUntilUtc` in settings.json.
**Reason:** a pause must survive a restart (otherwise rebooting during a break would fabricate tracking),
and it is machine-local state, like other settings.

## ADR-025 — Unanswered prompts by kind
**Decision:** Missed (timeout, app exit), Dismissed (Esc), Suppressed (due while locked) are all stored;
late answers upgrade a prompt to Answered.
**Reason:** the estimator needs to know where uncertainty starts; the user's explicit answer always wins.

## ADR-026 — Popup interaction model
**Decision:** one search box over title/ID/client, Enter-to-repeat the previous task, Alt+1–4 recent tasks,
unknown text creates a task (client defaults to the chosen one, else the previous task's), details collapsed.
Countdown stops on interaction. Manual check-ins always take focus; scheduled ones honour the popup mode.
**Reason:** owner feedback — minimal keystrokes, reuse of the previous task must be fastest, no focus stealing.

## ADR-027 — Check-in interval 1–240 minutes
**Decision:** presets 1, 5, 10, 15, 30, 60 and a custom whole-minute value (1–240); default 30.
**Reason:** short intervals make scheduled behaviour testable; the engine already guarantees no backlog
and no duplicate popups, so short intervals are safe.

## ADR-028 — Escalating foreground activation for manual check-ins
**Decision:** see architecture "Popup → Activation". The synthetic-input step is a zero mouse event, never
key input (a synthetic Alt could trigger menus in the previously active application).
**Reason:** owner reported the shortcut popup not receiving focus; an end-to-end CI test with real keystrokes
from another application reproduced it and now guards the fix.

## ADR-029 — WPF UI tests isolate their own windows
**Decision:** tests identify the popup they opened (difference against a snapshot) and the test context closes
all windows on dispose.
**Reason:** one shared Application per process; a stray window from one test broke unrelated tests.
The host is a plain `Application` loading App.xaml's resources: constructing `App` ran `OnStartup`, i.e. a
complete WorkTrace (engine, tray, hotkey) inside the test process, whose start-up check-in appeared in random
tests and whose hotkey made Ctrl+Alt+Space look taken.
