# WorkTrace board

The development board for [WorkTrace](https://github.com/crosenlund79/TimeAndTask): a small Kanban board that the
owner uses in the browser and Claude Code uses from the command line. Both edit the same data on GitHub.

**Open the board:** https://crosenlund79.github.io/TimeAndTask/

This `board` branch holds only the board: the app, its data and its tests. It is never merged into `main` or into
feature branches, so board edits never conflict with code changes.

| Path | What it is |
|---|---|
| `docs/board.json` | **The board.** The only authoritative copy. Validated against `schema/board.schema.json`. |
| `docs/BOARD.md` | Readable Markdown, generated from `board.json`. Never edit it by hand. |
| `index.html`, `app/` | The browser app: plain HTML/CSS/JS, no third-party code, served by GitHub Pages. |
| `app/board-core.js` | Shared rules (validation, operations, conflicts, Markdown), used by the app and the CLI. |
| `tools/board.mjs` | Command-line tool for Claude Code. |
| `tools/migrate.mjs`, `docs/migration/` | One-time migration from the hand-written BOARD.md (2026-10-28). |
| `tests/` | Data, sync and CLI tests (`npm test`) and browser tests (`npm run test:ui`). |

## Signing in (to edit)

Anyone can view the board. To save changes you need a GitHub fine-grained personal access token.

1. Open GitHub › Settings › Developer settings › Personal access tokens › **Fine-grained tokens** ›
   [Generate new token](https://github.com/settings/personal-access-tokens/new).
2. **Token name:** `WorkTrace board`. **Expiration:** 90 days.
3. **Repository access:** *Only select repositories* › `crosenlund79/TimeAndTask`.
4. **Repository permissions:** **Contents › Read and write**. Leave everything else at *No access*
   (Metadata: read-only is added automatically).
5. Generate the token and copy it. Paste it only into the board's **Sign in** dialog, never into chats or files.

The token is kept in this browser tab's session storage, or in local storage if you tick *Remember on this device*.
It is sent only to `api.github.com`, in the Authorization header. **Sign out** removes it. When it expires, the board
stays readable and your unsaved changes are kept; create a new token and sign in again. To revoke it early, delete
it on the same GitHub page.

A fine-grained token with Contents write can push to any branch of the repository. The app itself only writes
`docs/board.json` on the `board` branch. For extra safety, protect `main` with a ruleset (Settings › Rules).

## Using the board

- **Move cards:** drag them (on a phone, drag by the ⋮⋮ handle), use the *Status* selector in the card panel, or
  focus a card and press **Alt+←/→** (column) or **Alt+↑/↓** (order).
- **Done means accepted:** moving a card to Done asks you to confirm, and records your acceptance in its history.
- **Card panel:** click a card to edit title, status, category, priority, description, acceptance criteria,
  notes, evidence, links and dependencies. Add comments. See status history and timestamps.
- **Notes** save automatically as you type. **Comments** are append-only.
- **Dependencies:** *Blocked by* lists cards this one waits for; cycles and unknown ids are refused. Blocked cards
  are marked, and moving one to In progress asks first.
- **Archive:** archive a Done card from its panel, or *Archive all* in the Done column. Archived cards leave the
  board but are kept forever; open **Archive** in the toolbar to search, open and restore them.
- **Search and filters:** search id, title, description, notes, evidence, acceptance criteria and comments; filter
  by category, priority, status and archived state. Press `/` to search.
- **Undo** (toolbar or Ctrl+Z) reverses status changes, moves, reordering, field edits, archiving and restoring
  made in this browser tab since it was opened. The reversal is saved to GitHub like any other change. If the card
  was changed elsewhere since, Undo refuses rather than overwrite. Comments and new cards cannot be undone.

The indicator in the toolbar shows **Saved**, **Saving…**, **Unsaved changes**, **Save failed · Retry**,
**Conflict · Resolve**, or that you need to sign in. Problems also show a banner at the bottom left.

## How saving works

Every change is an operation ("set WT-010 notes", "move WT-011 to Review / Test", "add a comment") that records what
it expects to replace. Changes appear at once, are kept in this browser until saved, and are saved in small batches.

1. A save writes `docs/board.json` with GitHub's Contents API, naming the file version (blob SHA) it is based on.
   If anyone else saved since, GitHub refuses (HTTP 409) and nothing is overwritten.
2. The board then loads the latest version and re-applies the pending operations on top of it:
   - changes to different cards, or different fields of one card, merge;
   - comments are merged by their unique id, so neither side loses one and a retried save never duplicates one;
   - a change to something that was changed elsewhere in the meantime (the same field, or the same card's status)
     becomes a **conflict**. Both versions are shown, and you choose. Other changes keep saving meanwhile.
3. A failed save (offline, GitHub down, expired token) keeps the changes in this browser, survives a refresh, and is
   retried; nothing is lost silently. Reloading shows the last known board plus your unsaved changes.

The board checks for changes every minute while it is open, and whenever you return to the tab.
`docs/BOARD.md` is regenerated by the *Board* workflow after each browser save. The CLI writes both files in one commit.

## Command line (Claude Code)

```bash
git fetch origin board
git worktree add --detach /tmp/worktrace-board origin/board   # once per checkout
node /tmp/worktrace-board/tools/board.mjs help
```

Every command fetches the latest board first. Changes are committed and pushed without force; if the board changed
meanwhile, the change is re-applied on top, or reported as a conflict (exit code 3) without saving anything.

```bash
node tools/board.mjs show                      # columns and cards
node tools/board.mjs get WT-010                # one card: notes, comments, history
node tools/board.mjs move WT-010 doing         # start work
node tools/board.mjs move WT-010 review        # implemented and verified by CI
node tools/board.mjs evidence WT-010 "RealAppTests green, CI run 22"
node tools/board.mjs link WT-010 "CI run 22" https://github.com/crosenlund79/TimeAndTask/actions/runs/…
node tools/board.mjs comment WT-010 "Root cause: …"
node tools/board.mjs add --title "…" --category bug --priority high --status todo --accept "…"
node tools/board.mjs set WT-030 blockedBy '["WT-010"]'
node tools/board.mjs move WT-010 done --owner-accepted "Owner, 2026-11-02: works on my machine"
```

Feature and bug cards move to Done only with `--owner-accepted`, after the owner explicitly accepted the work.

## Data format

`docs/board.json` follows [schema/board.schema.json](schema/board.schema.json) (version 1): `nextId` and a list of
cards with `id` (never reused), `title`, `category`, `priority`, `status`, `rank` (order within a column),
`archived`/`archivedAt`, `description`, `acceptance`, `notes`, `evidence`, `blockedBy`, `links`, `comments`,
`history`, `createdAt`, `updatedAt` and `rev`. `validateBoard()` in `app/board-core.js` also checks unique ids,
existing dependencies and the absence of cycles.

## Development

```bash
npm ci
npm test            # data, sync and CLI tests (Node 22)
npm run test:ui     # browser tests (Playwright, Chromium)
```

Test against a scratch board with `?branch=board-test` (only `board` and `board-*` branches can be chosen).

## Hosting

GitHub Pages, *Deploy from a branch*: `board`, folder `/ (root)`. No server, database or paid service.
