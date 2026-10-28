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
| Basic WPF window (Fluent theme, status) | 🟡 | manual checklist |
| Tray icon, menu, double-click, close-to-tray, Exit | 🟡 | manual checklist |
| Single-instance operation with activation | 🟡 | manual checklist |
| Windows CI | ✅ | `.github/workflows/ci.yml` |

**Acceptance:** project builds ✅, starts 🟡, creates its database ✅ (tests) / 🟡 (app), operates from
the tray 🟡, passes tests ✅.

## Phase 2 — Clients and tasks ⬜
Client CRUD/deactivate, task systems & channels configuration, task CRUD/archive, search, autocomplete,
recent items, validation. Acceptance: entities created, edited, reused and persisted across restarts.

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
