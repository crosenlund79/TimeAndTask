# WorkTrace architecture

## Overview
WorkTrace is a single-process WPF desktop application that normally runs without a visible window,
hosted in the Windows notification area. A .NET Generic Host provides dependency injection, logging
and lifecycle management for background services; WPF owns the UI thread and message loop.

```
WorkTrace.App (net10.0-windows)
  App.xaml.cs        startup/shutdown orchestration, single instance, global exception handling
  Shell/             WindowManager, IAppLifetime, SingleInstanceGuard
  Tray/              TrayIconService (WinForms NotifyIcon)
  Views/ ViewModels/ MVVM (CommunityToolkit.Mvvm)
  Interop/           P/Invoke (LibraryImport)
        │
        ├──► WorkTrace.Infrastructure (net10.0)
        │      Persistence/   WorkTraceDbContext, configurations, migrations, initializer, seeder
        │      Settings/      JsonSettingsStore
        │      Storage/       AppPaths
        │      Logging/       Serilog file sink setup
        │           │
        └───────────┴──► WorkTrace.Core (net10.0)
                           Common/    UtcTime, UtcInterval
                           Entities/  domain entities (POCOs)
                           Settings/  WorkTraceSettings (+ validation), ISettingsStore
                           Services/  service interfaces consumed by ViewModels
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

## Scheduling design (implemented in Phase 3)
- No polling. The scheduler computes the next due instant (aligned to wall-clock multiples of the
  interval) and waits with a `TimeProvider` timer; it is re-evaluated on resume, unlock, time change
  and settings change.
- No backlog: after sleep/restart, overdue prompts are not replayed; at most one prompt is shown on resume.
- One prompt window at a time; a due prompt while one is open is recorded, not shown.
- TrackingSegments: opened on start/resume/unpause; closed on pause/suspend/shutdown. A heartbeat
  (every 5 min, a single UPDATE) bounds the end of a segment left open by a crash. Clock jumps are
  detected by comparing wall-clock and `Stopwatch` elapsed time and split the segment.

## Logging
`ILogger<T>` everywhere, Serilog daily rolling files (14 retained, 10 MB cap) in the logs folder.
Microsoft/System categories at Warning. Task names, notes and screenshot content are never logged.
