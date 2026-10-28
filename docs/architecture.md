# WorkTrace architecture

## Overview
WorkTrace is a single-process WPF desktop application that normally runs without a visible window,
hosted in the Windows notification area. A .NET Generic Host provides dependency injection, logging
and lifecycle management for background services; WPF owns the UI thread and message loop.

```
WorkTrace.App (net10.0-windows)
  App.xaml.cs        startup/shutdown orchestration, single instance, global exception handling
  AppServices.cs     DI registrations for shell, view models and windows (shared with UI tests)
  Shell/             WindowManager, IAppLifetime, SingleInstanceGuard, Navigation/Notification/Dialog services
  Tray/              TrayIconService (WinForms NotifyIcon)
  Themes/Styles.xaml shared typography, cards, pills, icon buttons, list rows (on top of Fluent)
  Views/ ViewModels/ MVVM (CommunityToolkit.Mvvm): Home, Tasks, Clients, Lists pages
  Interop/           P/Invoke (LibraryImport)
        │
        ├──► WorkTrace.Infrastructure (net10.0)
        │      Management/    ClientService, TaskService, LookupService
        │      Persistence/   WorkTraceDbContext (+ ChangeLogWriter), configurations, migrations, initializer, seeder
        │      Settings/      JsonSettingsStore
        │      Storage/       AppPaths
        │      Logging/       Serilog file sink setup
        │           │
        └───────────┴──► WorkTrace.Core (net10.0)
                           Common/    UtcTime, UtcInterval
                           Entities/  domain entities (POCOs)
                           Management/ service contracts, inputs/read models, validator, TaskSearch ranking
                           Validation/ ValidationErrors + domain exceptions, Text (normalisation, comparison)
                           Settings/  WorkTraceSettings (+ validation), ISettingsStore
                           Services/  IAppStatusService
```

**Dependency rules**
- Core references nothing but the BCL. No EF Core attributes, no WPF types.
- Infrastructure implements Core interfaces; it is cross-platform so it can be tested on any OS.
- Windows-only implementations (screen capture, power/lock notifications, global hotkey, Run-key
  startup registration) live in the App project and are exposed through Core interfaces.
- ViewModels depend on Core interfaces only; they never create a `DbContext`.

## Startup and shutdown
1. `SingleInstanceGuard.TryAcquire()` — named mutex `Local\WorkTrace.SingleInstance.*`. A second
   instance signals a named event (after `AllowSetForegroundWindow`) and exits; the first instance
   shows its main window.
2. `AppPaths.Resolve()` → `%LocalAppData%\WorkTrace` (or `WORKTRACE_DATA_DIR`), directories created.
3. Host built: file logging, Infrastructure services, shell services.
4. `DatabaseInitializer`: pre-migration backup (only when an existing DB has pending migrations),
   `Migrate()`, `PRAGMA journal_mode=WAL`, idempotent seeding.
5. Settings loaded (invalid files preserved as `settings.invalid-*.json`, defaults used), theme applied.
6. Tray icon created; main window shown on first run, when `StartMinimized=false`, or with `--show`.

Shutdown (`IAppLifetime.RequestExit`, Windows session end, or close with close-to-tray disabled) closes
windows, removes the tray icon, stops the host on a pool thread (5 s timeout), releases the mutex and
calls `Application.Shutdown`. It is synchronous so it also completes inside `OnSessionEnding`.

## Database model
SQLite via EF Core 10. Connection: `Foreign Keys=True`, pooling, WAL. All foreign keys are
`ON DELETE RESTRICT`. Timestamps are UTC `DateTime` stored as ISO-8601 text (sorts chronologically).

| Table | Purpose | Key constraints |
|---|---|---|
| Clients | Customers and internal cost bearers | unique `Name COLLATE NOCASE`; seeded "Internal" |
| TaskSystems | D365FO, CRM, Azure DevOps, Other, None | unique name; `IsBuiltIn` |
| CommunicationChannels | Phone, Teams, Email, Verbal, Other, Unspecified | unique name; `IsBuiltIn` |
| Tasks | Reusable work items | FK Client (required), TaskSystem?, DefaultChannel?; non-unique index (ClientId, TaskSystemId, ExternalId) |
| Observations | Point-in-time "working on X" statements | FK Task?, Channel?; immutable `Original*` columns; soft delete |
| CheckInPrompts | Scheduled prompts and their outcome | Outcome: Pending/Answered/Missed/Dismissed/Suppressed |
| TrackingSegments | Periods with tracking active | `EndUtc ≥ StartUtc`; EndReason; heartbeat |
| LockPeriods | Windows session locked | `EndUtc ≥ StartUtc` |
| ManualAllocations | User-asserted period allocations (overlay) | `EndUtc > StartUtc`; Task kind ⇔ TaskId not null |
| TimesheetAdjustments | Per day × task reported-minute overrides | unique (LocalDate, TaskId); stores `BaselineMinutes` |
| Screenshots | Screenshot file metadata (no blobs) | unique RelativePath |
| ChangeLog | Append-only audit / undo log | BatchId groups one user action |

Seeding fills each reference table only while it is empty, so renamed or deactivated seed rows are respected.

## Management layer (Phase 2)

**Services** (`IClientService`, `ITaskService`, `ILookupService`) are singletons that create a short-lived
`DbContext` per call and return immutable read models (`ClientSummary`, `TaskSummary`, `LookupSummary`).
View models never see EF entities.

**Rules**
- Input is trimmed; empty optional text becomes null; external IDs keep case and leading zeros.
- Client and lookup names are unique per list, compared case- and accent-insensitively in .NET.
- New tasks need an existing, *active* client / task system / channel. Editing a task may keep a
  reference that has since been deactivated, so old tasks stay editable.
- Referenced records are never deleted: clients with tasks are deactivated, tasks with time records
  (current or original observation task, allocations, adjustments) are archived, used/built-in lookup items
  are hidden. Unreferenced records can be deleted (fixing typos).
- Deactivated clients' tasks drop out of default search and pickers; restoring an archived task under an
  inactive client is refused. The last active internal client cannot be deleted.
- Duplicate external IDs are allowed (`FindSimilarAsync` only warns).

**Search** (`TaskSearch.Rank`, pure and deterministic). All terms must match some field, ignoring case
and accents. Per-term score: external ID exact 1000 / normalised exact 900 (`42` = `0042` = `BUG-0042`) /
prefix 600 / contains 400; title prefix 300 / word start 200 / contains 100; client 150/120/60; task system
80/60/30. Recency boost (≤1 d 40, ≤7 d 25, ≤30 d 10), archived −50. Ties: last used, modified, title, id.
Empty query = most recently used first. Candidates are filtered in SQL (client, archived, active client)
and ranked in memory — a personal task list is small, and SQLite `LIKE` cannot do accent-insensitive matching.

**Change log.** `WorkTraceDbContext.SaveChangesAsync` snapshots added/modified/deleted `IChangeTracked`
entities, saves, then writes one `ChangeLog` batch (before/after JSON) inside the same transaction
(or the caller's). Usage bookkeeping (`LastUsedAtUtc`) uses `ExecuteUpdate` and is not logged.

**UI pattern.** Each management page is a list + editor ("master-detail"). The editor edits a copy and
tracks `IsDirty` against a snapshot; while dirty, the list is disabled so a selection change can never
discard edits; Save (Ctrl+S) / Discard (Esc) are explicit. Reversible actions (archive, deactivate) are
immediate with an Undo toast; irreversible deletes use a themed confirmation dialog with Cancel focused.
Lookup lists are edited inline and saved immediately. Navigation between pages keeps page state
(view models are singletons).

## Time-estimation algorithm (implemented in Phase 4)
The engine is a pure, deterministic function in Core:

`Estimate(segments, lockPeriods, observations, prompts, allocations, settings) → IReadOnlyList<TimelinePeriod>`

Each output period has a classification:

| Classification | Meaning |
|---|---|
| Manual | Explicitly asserted by the user (ManualAllocation). The only "confirmed" time. |
| Estimated | Inferred from observations. Always shown as estimated. |
| Unclassified | Tracking was active but there is no supportable evidence. |
| NotTracking | Paused, asleep, shut down, crashed. Never filled. |

Observations themselves are evidence points, displayed as markers — never as periods.

Steps per tracking segment `[S, E)` (E = now for the open segment):
1. Collect active observations and uncertainty markers inside the segment: prompts with outcome
   Missed/Dismissed/Suppressed at their *scheduled* time, and lock periods (longer than
   `LockGraceMinutes`) as uncertain intervals. Order by time, then CreatedAtUtc, then Id.
2. `S → first observation`: Unclassified (no backward extrapolation; a prompt is shown at
   tracking start/resume to keep this short).
3. Between consecutive observations O₁(A) and O₂(B) without markers in between:
   - A = B → Estimated A for the whole gap.
   - A ≠ B → Estimated A until the midpoint, Estimated B from the midpoint. The boundary is flagged estimated.
4. A marker M after O(A): Estimated A until M, then Unclassified until the next observation.
   Lock intervals become Unclassified (or NotTracking with `LockedTimeHandling.NotWorking`).
5. After the last observation: Estimated, capped at `MaxEstimationHorizonMinutes` (default 60) and the
   segment end, then Unclassified. A trailing estimate stays Estimated even if no check-in ever follows.
6. Overlay ManualAllocations (non-deleted) in CreatedAtUtc order; later allocations win where they overlap.
   They apply inside and outside tracking segments (historical additions).
7. Flatten into non-overlapping half-open intervals. Gaps not covered by any segment are NotTracking.

Reporting splits periods at local midnight via `TimeZoneInfo` (current system zone); durations are UTC
differences so DST days have 23/25 hours. Rounding (Phase 6) applies to reported cells only.

**Recalculation.** Nothing derived is persisted; the timeline is recomputed from stored facts, so
historical edits are reflected automatically and manual allocations are never lost.
**Timesheet reconciliation.** If the computed value of an adjusted cell differs from its
`BaselineMinutes`, the cell is flagged "underlying data changed"; the adjusted value stays in effect
until the user accepts the new value or re-confirms.

## Tracking and scheduling (Phase 3)

```
SystemEvents (power, session, clock) ─► SystemEventsBridge ─┐
Tray / Overview / Settings ─► TrackingController ───────────┤
                                                            ▼
                        TrackingEngine (Infrastructure) ──► ITrackingStore (SQLite)
                                 │  ▲
             ShowPromptAsync     ▼  │ outcome
                        CheckInPresenter (App) ─► CheckInWindow + CheckInViewModel ─► ICheckInService
GlobalHotkey ─► CheckInPresenter.ShowManual
```

**Engine.** One `ITimer` armed for the earliest of: next check-in slot, an extra prompt (start/resume/
welcome back), the next heartbeat (5 min), the end of a timed pause. No polling. Every state change runs
under one async gate; popups are awaited outside it, and their outcome is written when they close.

**Schedule.** `CheckInSchedule.NextSlotUtc` returns the earliest wall-clock-aligned slot after a moment
(local time, slots restart at midnight, DST gaps skipped, repeated hours in real-time order). The next slot
is always computed from *now*, so nothing is replayed after sleep or a restart. Prompts are kept at least
half an interval apart (a start-up prompt at 09:28 skips 09:30).

**Prompts.** Due prompt → `CheckInPrompts` row (Pending) → popup. Answer → the check-in service stores the
observation and marks the prompt Answered (also when answered late). Esc → Dismissed; timeout or app exit →
Missed; due while locked → Suppressed (not shown) and one "welcome back" prompt after unlock; due while a
popup is open → skipped (no duplicates). All three unanswered outcomes create uncertainty from the prompt's
*scheduled* time.

**Segments.** Opened on start, resume from sleep and unpause; closed at the moment of pause, sleep,
shutdown or a detected clock jump. Clock jumps are detected by comparing wall-clock progress with monotonic
elapsed time (tolerance 2 min); the old segment ends at the last moment known on the old clock. After an
unclean exit, `RecoverAsync` closes open segments at their last heartbeat (reason Crash), turns pending
prompts into Missed and closes open lock periods. Lock periods are recorded only while tracking.
A backward clock jump can produce segments that overlap in UTC; the Phase 4 estimator must flatten them.

**Pause.** Stored in settings (`Tracking.Enabled = false` for "until resumed", `PausedUntilUtc` for a timed
pause) so it survives restarts; the engine resumes by itself when a timed pause ends.

**Popup.** Unobtrusive mode shows the window without activation (`ShowActivated = false`); clicking it or the
global shortcut activates it. Focused mode activates it and uses the attach-thread-input technique when Windows
blocks `SetForegroundWindow`. The window is a tool window (not in Alt+Tab), topmost, Windows 11 rounded
corners, positioned in the work area. The auto-dismiss countdown pauses on hover and stops on any input.

## Logging
`ILogger<T>` everywhere, Serilog daily rolling files (14 retained, 10 MB cap) in the logs folder.
Microsoft/System categories at Warning. Task names, notes and screenshot content are never logged.
