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
