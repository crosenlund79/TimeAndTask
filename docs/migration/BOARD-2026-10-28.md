# WorkTrace board

A lightweight board for planning and tracking WorkTrace. It is the single list of what is planned, in progress,
waiting for the owner, and accepted. No sprints, estimates or ceremonies.

**Statuses**
- **Backlog**: ideas and possible future features, not yet agreed.
- **To do**: agreed requirements, ready to implement.
- **In progress**: being designed or implemented now.
- **Review / Test**: implemented, waiting for the owner to verify or accept.
- **Done**: reviewed and accepted by the owner.

**Rules**
- A card has an ID (`WT-nnn`, never reused), a title, a category (`feature`, `bug`, `architecture`, `testing`, `idea`),
  a short description, and acceptance criteria where they matter. "Evidence" records what was verified, and how.
- Passing automated tests is not enough for user-facing work: it moves to **Review / Test** and reaches **Done** only
  when the owner confirms it. Purely internal work (tests, refactoring) may go to Done when CI is green.
- Owner feedback, bugs and test results become new cards or update existing ones. A failed review moves the card back
  to In progress, with the finding noted.
- Update the board in the same commit as the work it describes. Keep it short: edit cards, don't log history.
  Done cards may be trimmed to one line once the phase is accepted.

_Last updated: 2026-10-28 · branch `claude/keen-mccarthy-qr7q0p` · CI run 21 green (211/211 tests)_

---

## In progress

_Nothing. Waiting for the owner's review of Phase 3 refinements (round 2)._

## Review / Test

### WT-010 · Scheduled check-ins appear reliably · `bug`
Scheduled check-ins did not appear on the owner's machine (Unobtrusive, Focused, auto-dismiss, "show now").
Likely cause: a wake reported only as an automatic resume left tracking "asleep"; also guards a lock without unlock.
**Accept:**
- With a 1-minute interval, check-ins appear in Unobtrusive mode (no focus taken) and in Focused mode (takes focus).
- They appear while the main window is closed to the tray, and after sleep/wake (within about a minute).
- An ignored check-in closes after its countdown and shows as "missed" in Overview.

**Evidence:** `RealAppTests` (real WorkTrace.exe in CI), `SchedulingTests`, engine tests. Not yet confirmed on the owner's machine.

### WT-011 · Tracking status and last check-in visible · `feature`
Overview and Settings show Active / Paused / check-ins off / error, interval, next check-in, the last attempt with
its outcome, and a warning when check-ins are held back or failing.
**Accept:** the status matches what happens (pause, resume, missed, answered); nothing fails without a visible reason.
**Evidence:** `TrackingStateTests`, `SchedulingTests`.

### WT-012 · "Show a scheduled check-in now" explains itself · `feature`
Uses the real scheduled path; when it cannot show one it says why (paused, popup already open, session locked).
**Accept:** shows a popup with countdown when tracking; shows a clear message when paused or a popup is open.
**Evidence:** `ShowNow_…` tests.

### WT-013 · Popup countdown · `feature`
Compact mm:ss countdown on scheduled popups; paused while the pointer is over the popup; stops on interaction;
closes and records a missed check-in on expiry. No focus stealing, tick stops when not needed.
**Accept:** behaves as described in both popup modes.
**Evidence:** `Countdown_…` tests, `RealAppTests`.

### WT-014 · Alt+1–4 records a recent task · `bug`
Handled at window level (top row and number pad); never types into the search box; does nothing without recents.
**Accept:** with the popup active and search empty, Alt+1 records recent task 1 and closes the popup, no characters typed.
**Evidence:** `FocusTests` (incl. real keystrokes from another process).

### WT-005 · Check-in interval presets and custom value · `feature`
Presets 1/5/10/15/30/60 minutes plus custom; applied immediately, no duplicate or backlog prompts.
**Accept:** changing the interval moves the next check-in in Overview and the popups follow it.
**Evidence:** engine interval tests, `SettingsTests`. Could not be verified while WT-010 blocked check-ins.

### WT-015 · Custom interval validation (R08) · `testing`
Whole minutes 1–240 only. **Accept:** 0, 241, 1.5 rejected with a message; 7 accepted and applied.
**Evidence:** `CustomInterval_AcceptsOnlyWholeMinutesFrom1To240`.

### WT-016 · Pause and resume in tray and Overview (R09) · `feature`
**Accept:** timed and indefinite pause show in tray menu and Overview; Resume appears only while paused; a timed pause
resumes by itself. **Evidence:** `TrackingStateTests`. Tray menu appearance is a manual check.

### WT-017 · Close to tray, reopen, single instance (R10) · `feature`
**Accept:** closing the window keeps WorkTrace running in the tray; starting it again opens the existing window.
**Evidence:** `RealAppTests`.

### WT-018 · WorkTrace logo in the popup header · `feature`
Small vector logo. **Accept:** looks right in light and dark themes.

## To do

### WT-030 · Estimation engine · `architecture`
Phase 4. Reconstruct time from segments, lock periods, observations, prompts and manual allocations.
Blocked by WT-010 (scheduled check-ins must work on the owner's machine first).
**Accept:** the nine time-estimation invariants in CLAUDE.md hold, each covered by tests; estimates are labelled
Estimated, never Confirmed; recalculation never discards manual allocations.

### WT-031 · Activity log · `feature`
Phase 4. Editable day view of observations and estimated periods: filters, auto-save, undo (ChangeLog), manual
allocations, split and merge.
**Accept:** owner can correct a day's time without losing original observations; undo restores the previous state.

### WT-032 · Screenshots · `feature`
Phase 5. Opt-in primary-monitor capture, PNG default or JPEG with quality, date folders, viewer, retention and
deletion, no capture while locked, notification excluded from the capture.

### WT-033 · Weekly timesheets · `feature`
Phase 6. Week view, grouping and totals, unclassified time, adjustments with reconciliation, rounding,
CSV (Danish-friendly, injection-safe) and JSON export.

### WT-034 · Polish and release · `feature`
Phase 7. Start with Windows, theme, hand-tuned tray icons, backup/restore UI, packaging, user documentation.

## Backlog

### WT-040 · Tray notification when check-ins are held back · `idea`
Today the warning is shown only in the main window. A tray balloon could tell the owner sooner.

### WT-041 · Automated check of the tray menu's appearance · `idea`
Tray menu layout is verified manually today.

## Done

- **WT-001** · Foundation: solution, SQLite + migrations, settings file, logging, tray, Windows CI · `architecture`
- **WT-002** · Clients, tasks and lists management UI, incl. Phase 2 refinements · `feature`
- **WT-003** · Check-in popup: record, reuse previous task, create task, notes, details · `feature`
- **WT-004** · Tracking engine: segments, pause/resume, sleep/lock/clock handling, Today timeline · `architecture`
- **WT-006** · Global shortcut opens a focused check-in from any app · `bug`
