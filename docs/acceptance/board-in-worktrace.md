# Acceptance checklist – Board, Inbox and backups in WorkTrace

Use the `WorkTrace-win-x64` build artifact from CI (or a local publish). About 15 minutes.

**Migrate the browser board**
1. Download `docs/board.json` from the `board` branch (GitHub › branch `board` › docs › board.json › Download raw).
2. Board › Backup › Restore › Choose file… › pick it. The preview lists every card as **New**. Press Restore.
3. The columns show the same cards in the same order as the browser board. Open WT-047: comment and history are there;
   WT-049 shows *Blocked by* WT-047 and WT-048.
4. Restore the same file again: everything is **Unchanged**.

**Board**
5. Drag a card to another column and within a column; the accent line shows where it lands. `Ctrl+Z` puts it back.
6. Select a card, press `Alt+→` and `Alt+↓`. It moves; focus stays on it.
7. Drag a Feature card to Done: WorkTrace asks you to accept it. Cancel keeps it where it was.
8. Open a card, change the title and notes, wait a second: "Saved". Close and reopen: the text is there.
9. Add a link (try `javascript:x` – refused), Open and Copy it. Add a dependency; the card shows the blocked icon.
10. Add a comment. Search for a word from it: the card is found.
11. Archive a card (panel header). Board › Archive: search finds it; Restore brings it back.

**Screenshots**
12. Take a screenshot (Win+Shift+S), open a card, press `Ctrl+V`: a thumbnail appears. Drop an image file onto the panel;
    use Browse… Add a caption, click away, reopen the card: the caption is kept. Click a thumbnail: it opens.
13. Check `%LocalAppData%\WorkTrace\Attachments\` holds the files. Remove one in WorkTrace: the file is gone.

**Inbox**
14. Inbox (`Ctrl+7`): type a thought, Enter. Paste a link, Enter. Paste a screenshot into the capture box, Enter.
15. *To board* moves an item to the Backlog; `Ctrl+Z` returns it.

**Backup and restore**
16. Board › Backup › Complete backup… › save the ZIP. Open it in Explorer: README, manifest, cards, attachments, database.
17. Change a card, then Restore the ZIP: that card shows "Edited here since – kept". Tick *Overwrite my edits* and
    restore: the old version is back.
18. The month list shows "backed up …" after the backup, and "changed since backup" after you edit a card from that month.

**Time tracking still works**
19. A check-in appears on schedule; the Overview shows today's entries as before.
20. In a card, *Time tracking › Create task*: the task appears in check-ins and on the Tasks page.

Report anything that differs on the board card for this work.
