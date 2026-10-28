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

## Phase 3 — Check-ins ⬜
Popup (focused/unobtrusive), keyboard handling, scheduler, manual check-ins, auto-dismiss, missed
check-ins, pause/resume, tracking segments, lock periods, global shortcut.

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
