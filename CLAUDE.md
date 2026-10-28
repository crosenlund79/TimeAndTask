# CLAUDE.md — WorkTrace (repo: TimeAndTask)

## Purpose
WorkTrace is a personal Windows tray utility: periodic check-ins ("what are you working on?"),
time reconstruction from those observations, and weekly timesheet preparation. Local data only.
Personal productivity tool — not employee surveillance.

## Repository identity
- GitHub: `crosenlund79/TimeAndTask` (repo name) — product name is **WorkTrace**. Keep them distinct.
- Default branch `main`. Implementation happens on feature/session branches; **never merge into `main`
  without the owner's approval**. Never force-push or rewrite shared history.
- Commit style: `feat:`, `fix:`, `test:`, `docs:`, `chore:`.

## Stack
C# / .NET 10 LTS · WPF (built-in Fluent `ThemeMode`) · MVVM (CommunityToolkit.Mvvm) · Generic Host
(DI, logging) · EF Core 10 + SQLite · Serilog file sink behind `ILogger<T>` · WinForms `NotifyIcon`
for the tray · xUnit v3 on Microsoft.Testing.Platform · `TimeProvider` / `FakeTimeProvider`.

## Solution layout
```
src/WorkTrace.Core            net10.0          entities, settings model, pure logic, service interfaces (no EF, no WPF)
src/WorkTrace.Infrastructure  net10.0          EF Core/SQLite, migrations, paths, JSON settings store, logging, DI
src/WorkTrace.App             net10.0-windows  WPF + tray, ViewModels, Windows-only code (Interop/, P/Invoke)
tests/WorkTrace.Tests         net10.0          unit + SQLite integration tests (run on Linux and Windows)
tests/WorkTrace.App.Tests     net10.0-windows  WPF smoke (binding errors) + view-model flow tests; Windows only
assets/icons                  SVG/ICO + generate_icons.py (Pillow)
docs/                         architecture.md, requirements.md, decisions.md
```
Windows-only capabilities (screen capture, power/lock events, hotkeys, Run key) live in the App
project behind Core interfaces so Core/Infrastructure stay testable cross-platform (ADR-003).

## Build & test
```
dotnet build TimeAndTask.slnx
dotnet test --solution TimeAndTask.slnx
dotnet tool restore && dotnet ef migrations add <Name> -p src/WorkTrace.Infrastructure -o Persistence/Migrations
```
- `EnableWindowsTargeting=true` lets the WPF project compile on Linux; it only **runs** on Windows.
- Test runner is Microsoft.Testing.Platform (`global.json` → `test.runner`); use `--project`/`--solution`.
- `WorkTrace.App.Tests` hosts a plain `Application` with App.xaml's resources — never `new App()`, which would
  run `OnStartup` (a full WorkTrace with engine, tray and hotkey) inside the test process.
- `WorkTrace.App.Tests` is skipped automatically on non-Windows hosts (its `Directory.Build.targets`);
  it runs in CI. It renders every page with the real theme and fails on WPF binding errors — keep it green
  and extend it whenever a view is added.
- CI: `.github/workflows/ci.yml` on `windows-latest` (build, test, publish artifact).
- `WORKTRACE_DATA_DIR` overrides the data directory (default `%LocalAppData%\WorkTrace`).

## Domain rules (do not violate)
- All persisted timestamps are UTC `DateTime` (Kind=Utc). `UtcTime.Ensure` rejects `Unspecified`;
  the EF converter enforces it on write and restores Kind on read. Convert to local only for display/day bucketing.
- An **Observation** is a point-in-time statement, never a period. `Original*` columns are immutable;
  corrections change the current columns only. Deletion is soft.
- Missed/dismissed/suppressed check-ins are stored (`CheckInPrompts`) and create **uncertainty**.
- No cascading deletes (all FKs Restrict). Clients/tasks are deactivated/archived, not deleted, once referenced.
- External task IDs are free-form strings and not unique.
- Settings live in `settings.json` (not the DB) so DB restore never rolls back settings.
- Never log task titles, notes or screenshot content.
- Saves of `IChangeTracked` entities append a `ChangeLog` batch automatically (same transaction).
  Usage bookkeeping (`ITaskService.MarkUsedAsync`) deliberately bypasses it via `ExecuteUpdate`.
- Name uniqueness (clients, lookups) is checked in .NET with `Text.SameName` (case- and accent-insensitive);
  SQLite `NOCASE` only folds ASCII.
- Tracking is owned by `TrackingEngine` (Infrastructure): one timer, state changes serialised by a gate,
  popups shown via `ICheckInPresenter` without holding the gate. Segments end at the moment tracking stops
  (pause/sleep/shutdown/clock jump); crash recovery closes them at the last heartbeat. Prompts are never
  replayed (next slot computed from now); unanswered prompts are stored Missed/Dismissed/Suppressed.
  Pause state lives in settings (`Tracking.Enabled`, `PausedUntilUtc`) so it survives restarts.
- Manual check-ins always activate the popup (escalating `ForceForeground`, focus the search box explicitly);
  scheduled popups in Unobtrusive mode must never take focus. `FocusTests` (incl. a real-keystroke test from
  another process) guard both — keep them green.
- Engine tests use `FakeTimeProvider` + a separate monotonic clock; `TrackingEngine.IdleAsync` makes them deterministic.
  Model sleep with `Sleep(...)` (no timers fire while asleep), not `AdvanceAsync`.
- Every check-in attempt has a `CheckInAttemptResult` that is logged and shown in the UI — never add a silent no-op path.
  State that depends on paired Windows notifications (resume after sleep, unlock) must self-heal (ADR-032).
- Fake-clock tests do not prove scheduling works: `RealAppTests` launches the built WorkTrace.exe (1-min interval) and
  `SchedulingTests` uses the real clock; keep both green. UI test contexts stop the engine before disposing.
- Services throw `DomainValidationException` (field errors), `OperationNotAllowedException`
  (e.g. deleting a referenced record) or `EntityNotFoundException`; view models map these to inline
  errors / notifications via `ViewModelBase.RunAsync`.

## Time-estimation invariants (Phase 4 engine; see docs/architecture.md)
1. Output is a flat, ordered, non-overlapping list of periods: Manual / Estimated / Unclassified / NotTracking.
2. Estimation never crosses tracking-segment boundaries (pause, sleep, shutdown, crash, clock jump).
3. Same task → estimated continuous; different tasks → estimated switch at the midpoint.
4. After a missed/dismissed prompt: unclassified from the prompt's scheduled time until the next observation.
5. Segment start → first observation: unclassified (no backward extrapolation).
6. Trailing estimate capped at `MaxEstimationHorizonMinutes`; still labelled Estimated, never Confirmed.
7. Locked periods are uncertain by default (`LockedTimeHandling.Unclassified`, grace 0 min).
8. Manual allocations overlay the estimate (latest created wins) and are never discarded by recalculation.
9. Deterministic: ties broken by CreatedAtUtc, then Id. Durations from UTC differences (DST-safe).

## Conventions
Nullable enabled (nullable warnings are errors) · file-scoped namespaces · `_camelCase` private fields ·
async + `CancellationToken` · `IDbContextFactory` with short-lived contexts (no generic repositories) ·
no business logic in code-behind (window behaviour only) · ViewModels never touch EF directly ·
never assign `Style` to standard controls without `BasedOn` a Fluent style (it drops the theme); use
`DynamicResource` for theme brushes · ComboBox keys use `PickerOption.NoneKey` (0) for "none", never null ·
editable ComboBoxes binding `SelectedValue` + `Text` must initialise the text (else type-ahead clears the selection) ·
resource URIs are assembly-qualified (`pack://application:,,,/WorkTrace;component/...`) ·
`TimeProvider` for all clock access · central package versions in `Directory.Packages.props`.

## Known limitations
- Development container is Linux: the WPF app cannot be run or UI-tested there; Windows behaviour is
  verified manually by the owner and built/tested by CI on `windows-latest`.
- Tray icon art is provisional (Phase 7 will hand-tune small frames).
- Fluent `ThemeMode` is experimental in .NET 10 (WPF0001 suppressed).

## Current phase
- Phase 1 — Foundation: complete.
- Phase 2 — Clients and tasks: complete.
- **Phase 3 — Check-ins: complete** (tracking engine, popup in focused/unobtrusive mode, global shortcut,
  pause/resume, sleep/lock/clock handling, Settings page, Today timeline; refinements: 1–240 min interval,
  reliable shortcut focus, Alt+1–4 clarity, "show check-in now"; round 2: scheduling self-heal, status/last attempt in UI, countdown, Alt+number at window
  level). Scheduled check-ins must be confirmed on the owner's machine before Phase 4. **Next: Phase 4 — estimation
  engine and activity log** (inputs: segments, lock periods, observations, prompts, manual allocations).
