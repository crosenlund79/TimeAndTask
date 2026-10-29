# Backups and restore

WorkTrace keeps everything on this PC (`%LocalAppData%\WorkTrace`). Backups are ZIP files you save wherever you
like (OneDrive, a USB stick). Open **Board › Backup**.

| Backup | Contains |
|---|---|
| **Complete** | All cards with notes, links, comments, history and screenshots, **plus** a consistent snapshot of the whole database (time tracking, clients, tasks) and `settings.json` |
| **All cards** | All cards and their screenshots |
| **One month** | Cards created in that month (UTC) and their screenshots. The list shows which months changed since their last backup |
| **Selected cards** | The ticked cards and their screenshots |

Every ZIP holds `README.txt`, `manifest.json` (card list, SHA-256 of every screenshot, content hash), `cards.json`,
`cards.md` (readable) and `attachments/<card>/<id>-<name>`; a complete backup adds `database/worktrace.db` and
`settings/settings.json`. The format is the browser board's (`worktrace-board-backup` v1), so its backups restore in
WorkTrace and the other way round.

## Restore cards
**Board › Backup › Restore › Choose file…** accepts a WorkTrace backup, a browser-board backup or the browser
board's `board.json`. WorkTrace checks the file (format, card data, screenshot checksums) and shows, per card:

- **New** – added with its original number (or a new number if that number is taken by a different card);
- **Will be updated** – the backup differs and the card was not edited here since;
- **Edited here since – kept** – tick *Overwrite my edits* to take the backup's version;
- **Unchanged**.

Comments, history and screenshots are merged by id and never deleted. Nothing is written until you press **Restore**.

## Restore the database snapshot (manual)
Restoring `database/worktrace.db` replaces **all** time tracking, clients and tasks with the backup's, so WorkTrace
does not do it automatically (pending the owner's decision on an in-app flow):

1. Quit WorkTrace (tray icon › Exit).
2. Rename `%LocalAppData%\WorkTrace\worktrace.db` (and any `worktrace.db-wal` / `-shm`) to keep them.
3. Copy `database/worktrace.db` from the ZIP to `%LocalAppData%\WorkTrace\worktrace.db`; optionally copy
   `settings/settings.json` over `settings.json`.
4. Start WorkTrace. Attachments are restored with *Restore cards* above.

## Email backups (prepared, not active)
Each written backup records a content hash per month, so a daily job can send only months that changed. Sending
is not implemented: the delivery method and where any credentials live are the owner's decision (board card
WT-047). Until then nothing leaves this PC.
