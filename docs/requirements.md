# WorkTrace requirements and coverage

The full product specification is maintained by the owner (initial project brief). This document
tracks the requirements per phase, their acceptance criteria and implementation coverage.

Legend: ✅ implemented & verified · 🟡 implemented, Windows manual verification pending · ⬜ not started

## Phase 1 — Foundation
| Requirement | Status | Verification |
|---|---|---|
| Repository initialised (`main`, .gitignore, central packages, slnx) | ✅ | repository |
| Solution structure Core / Infrastructure / App / Tests | ✅ | builds |
| Dependency injection (Generic Host) | ✅ | `DatabaseInitializerTests` (DI graph), app startup |
| Logging (file, rolling, privacy-conscious) | 🟡 | manual: log file appears |
| Domain model (all entities incl. revision columns, prompts, segments, locks, allocations, adjustments, screenshots, change log) | ✅ | `ObservationTests`, `PersistenceTests` |
| SQLite initialisation + migrations, WAL, FKs, seed data | ✅ | `DatabaseInitializerTests` |
| Pre-migration backup | 🟡 | code path only runs with a second migration |
| Per-user data directory, never the exe folder | ✅ | `AppPathsTests` |
| Strongly typed, validated settings (JSON, atomic write) | ✅ | `SettingsValidationTests`, `JsonSettingsStoreTests` |
| Basic WPF window (Fluent theme, status) | ✅ | owner verified start on Windows |
| Tray icon, menu, double-click, close-to-tray, Exit | 🟡 | manual checklist |
| Single-instance operation with activation | 🟡 | manual checklist |
| Windows CI | ✅ | `.github/workflows/ci.yml` |

**Acceptance:** project builds ✅, starts 🟡, creates its database ✅ (tests) / 🟡 (app), operates from
the tray 🟡, passes tests ✅.

## Phase 2 — Clients and tasks
| Requirement | Status | Verification |
|---|---|---|
| Client create / edit / rename / deactivate; delete only when unreferenced | ✅ | `ClientServiceTests`, `ViewModelFlowTests` |
| Default "Internal" client; multiple internal clients; keep ≥1 active internal | ✅ | `DatabaseInitializerTests`, `ClientServiceTests` |
| Recent-client selection (recently used first) and autocomplete (editable picker, type-ahead) | ✅ / 🟡 | `ClientServiceTests` (ordering); manual (type-ahead) |
| Configurable task systems and communication channels (add, rename, hide, reorder, delete unused) | ✅ | `LookupServiceTests`, `ViewModelFlowTests` |
| Task create / edit / archive / restore; delete only when unreferenced | ✅ | `TaskServiceTests`, `ViewModelFlowTests` |
| External task IDs as free strings, not unique (duplicate warning) | ✅ | `TaskServiceTests` |
| Task search, autocomplete ranking, recent-task reuse | ✅ | `TaskSearchTests`, `TaskServiceTests` |
| Validation with inline field errors; nothing persisted on failure | ✅ | `ManagementValidatorTests`, service + VM tests |
| Relationship integrity (no orphaned/broken history) | ✅ | FK + service tests |
| Persistence across restarts | ✅ | `Tasks_PersistAcrossRestart`, Phase 1 tests |
| Change history for edits (basis for Phase 4 undo) | ✅ | `Edits_AreWrittenToChangeLog_AsOneBatchPerSave` |
| Management UI renders without binding/XAML errors | ✅ (CI) | `UiSmokeTests` on windows-latest |
| Usability (keyboard flow, look & feel) | 🟡 | manual checklist |
| Create tasks from the check-in popup | ⬜ | Phase 3 (service + create-client flow are ready) |

**Acceptance:** entities can be created, edited, reused and persisted across restarts ✅.

## Phase 3 — Check-ins
| Requirement | Status | Verification |
|---|---|---|
| Compact popup: client, task, optional note; details expandable (time/date, channel, task system/ID for new task) | ✅ / 🟡 | `CheckInTests` (render, flows); manual look & feel |
| Keyboard: Enter saves, Esc dismisses, Tab/Shift+Tab, arrows through suggestions, Alt+1–4 recent, Ctrl+Enter | ✅ / 🟡 | VM tests; manual keyboard pass |
| Fast reuse of the previous task (Enter on empty search / one click) | ✅ | `Enter_OnEmptySearch_RepeatsThePreviousTask` |
| Create tasks from the popup | ✅ | `UnknownText_CreatesTheTask_ForThePreviousClient` |
| Focused and unobtrusive (no focus stealing) modes | ✅ | `ScheduledPopup_InUnobtrusiveMode_DoesNotTakeFocus`, `…InFocusedMode_TakesFocus` |
| Configurable global shortcut | ✅ | `SettingsTests`; `GlobalShortcut_FromAnotherApplication_…` (real keystrokes, CI) |
| Manual check-ins always receive keyboard focus (main window active, minimized, hidden; other app active) | ✅ | `FocusTests` (CI on windows-latest) |
| Recent tasks via Alt+1–4 (top row or number pad), safe no-op without recents or while searching, never types into search | ✅ | `FocusTests` (`AltNumber_IsHandledAtWindowLevel_…`, no-op, real Alt+1 from another app) |
| Interval presets 1/5/10/15/30/60 min + custom 1–240, applied live without backlog/duplicates | ✅ | `Interval_…`, `CustomInterval_AcceptsOnlyWholeMinutesFrom1To240` (0, 7, 241, 1.5 …), engine interval tests |
| Scheduled check-ins appear in the real app (1-min interval, hidden in tray, after restart) | ✅ (CI) / 🟡 owner machine | `RealAppTests` (launches WorkTrace.exe), `SchedulingTests` (real clock + presenter) |
| Tracking status visible: Active/Paused/Error, interval, next check-in, last attempt and outcome, warnings | ✅ | `TrackingStateTests`, `SchedulingTests` |
| "Show a scheduled check-in now" uses the real path and explains when it cannot run | ✅ | `ShowNow_…` tests |
| Countdown mm:ss, paused on hover, stops on interaction, closes as missed | ✅ | `Countdown_…` tests, `RealAppTests` |
| Single instance; close-to-tray and reopen | ✅ (CI) | `RealAppTests` |
| Scheduler aligned to interval (default 30 min), DST-safe | ✅ | `CheckInScheduleTests`, engine tests |
| Auto-dismiss after timeout; missed check-in stored; time after it uncertain | ✅ | `UnansweredPrompt_IsStoredAsMissed_FromItsScheduledTime` |
| No duplicate popups; no backlog after sleep/restart | ✅ | `OpenPopup_PreventsDuplicatePrompts`, `SleepEndsTheSegment_AndNoBacklogIsShownAfterResume` |
| Pause/resume (timed and indefinite), persisted | ✅ | `TimedPause_…`, `IndefinitePause_SurvivesRestart_UntilResumed` |
| Sleep/resume, shutdown, crash, clock change never fabricate tracking | ✅ | engine tests |
| Locked time recorded as uncertain; prompts suppressed while locked | ✅ | `LockedSession_…` |
| Settings: popup mode, interval, position, timeout, sound, remember last task, enable prompts | ✅ | `SettingsTests` |
| Tray reflects tracking state; recording and pause at the top | ✅ / 🟡 | `TrackingStateTests` (status, Pause/Resume items); menu look manual |
| Observations persist with original values and origin (scheduled/manual) | ✅ | `CheckInServiceTests` |

**Acceptance:** check-ins persist correctly ✅; focus modes behave as configured 🟡 (manual); ignored
check-ins create uncertainty rather than fabricated work ✅.

## Phase 4 — Activity log and estimation ⬜
Estimation engine (see architecture.md), editable grid, filters, auto-save, undo (ChangeLog),
manual allocations, split/merge.

## Phase 5 — Screenshots ⬜
Opt-in capture of primary monitor, PNG (default) or JPEG with configurable quality, date-organised
storage, metadata, notification excluded from capture, viewer, retention/deletion, no capture while locked.

## Phase 6 — Weekly timesheets ⬜
Week view, grouping, totals, unclassified time, adjustments with reconciliation, rounding,
CSV (Danish-friendly, injection-safe) and JSON export.

## Phase 7 — Polish and release ⬜
Settings UI, start with Windows, theme, icon refinement, backup/restore UI, packaging, documentation.

## Owner-confirmed decisions affecting requirements
- Locked time is never confirmed work; uncertain by default, configurable (`LockedTimeHandling`, `LockGraceMinutes`).
- Screenshots: PNG default, JPEG optional with configurable quality.
- Observations stay distinguishable from inferred allocations; trailing estimates remain labelled Estimated.
- Manual allocations survive recalculation.
