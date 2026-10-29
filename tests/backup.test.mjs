import { test } from "node:test";
import assert from "node:assert/strict";
import { applyOp, clone, emptyBoard, findCard, ops, validateBoard } from "../app/board-core.js";
import { buildBackup, contentHash, monthsOf, parseBackup, planRestore, restoreOps, selectCards } from "../app/backup.js";
import { createZip, readZip } from "../app/zip.js";
import { AT, migratedBoard } from "./helpers.mjs";

/** The migrated board plus cards created in November, one with a screenshot, a comment and a dependency. */
function boardWithMonths() {
  const board = migratedBoard();
  const nov = "2026-11-03T09:00:00Z";
  applyOp(board, ops.create(board, { title: "November thought", category: null, status: "inbox" }, nov, "owner"));
  applyOp(board, ops.create(board, { title: "November task", status: "todo", blockedBy: ["WT-010"] }, nov, "owner"));
  applyOp(board, ops.comment(board, "WT-043", "remember this", nov, "owner"));
  return board;
}

const image = (cardUid, id = "img-1") => ({
  meta: { id, cardUid, name: "screen shot.png", type: "image/png", size: 4, caption: "the error", createdAt: AT },
  bytes: new Uint8Array([137, 80, 78, 71]),
});

test("cards are grouped by the month they were created in", () => {
  const months = monthsOf(boardWithMonths());
  assert.deepEqual(months.map(m => [m.month, m.cards.length]), [["2026-11", 2], ["2026-10", 22]]);
  assert.deepEqual(selectCards(boardWithMonths(), { kind: "cards", ids: ["WT-043", "WT-001"] }).map(c => c.id), ["WT-001", "WT-043"]);
});

test("a month archive is complete and restores on its own into an empty board", async () => {
  const board = boardWithMonths();
  const shot = image(findCard(board, "WT-042").uid);
  const { bytes, fileName, manifest } = await buildBackup({ board, scope: { kind: "month", month: "2026-11" }, attachments: [shot], date: new Date(AT) });
  assert.equal(fileName, "worktrace-backup-2026-11.zip");
  assert.deepEqual(manifest.cards.map(c => c.id), ["WT-042", "WT-043"]);
  assert.equal(manifest.attachments.length, 1);
  const files = await readZip(bytes);
  assert.ok(files.has("README.txt") && files.has("cards.md") && files.has("attachments/WT-042/img-1-screen_shot.png"));

  const backup = await parseBackup(bytes);
  assert.deepEqual(backup.problems, []);
  assert.deepEqual(backup.attachments[0].bytes, shot.bytes);
  assert.equal(backup.attachments[0].meta.caption, "the error");

  // Restore into a board that has none of these cards.
  const empty = emptyBoard();
  const plan = planRestore(empty, backup.cards);
  assert.deepEqual(plan.map(p => p.state), ["new", "new"]);
  for (const op of restoreOps(empty, plan, AT, "owner", fileName)) applyOp(empty, op);
  assert.deepEqual(validateBoard(empty), []);
  const restored = findCard(empty, "WT-043");
  assert.equal(restored.status, "todo");
  assert.equal(findCard(empty, "WT-042").status, "inbox");
  assert.equal(restored.comments[0].text, "remember this");
  assert.deepEqual(restored.blockedBy, [], "a dependency on a card outside the archive is dropped");
  assert.equal(restored.history.at(-1).type, "imported");
});

test("the content hash changes only when the month's content changes", async () => {
  const board = boardWithMonths();
  const nov = selectCards(board, { kind: "month", month: "2026-11" });
  const h1 = await contentHash(nov);
  assert.equal(await contentHash(clone(nov).reverse()), h1, "independent of order");
  applyOp(board, ops.set(board, "WT-043", "notes", "changed", AT, "owner"));
  assert.notEqual(await contentHash(selectCards(board, { kind: "month", month: "2026-11" })), h1);
  assert.notEqual(await contentHash(nov, ["abc"]), h1, "a new screenshot changes it");
});

test("restore preview: same, changed (with board-newer warning), new with a taken id", async () => {
  const board = boardWithMonths();
  const { bytes } = await buildBackup({ board, scope: { kind: "all" } });
  const backup = await parseBackup(bytes);

  const later = clone(board);
  applyOp(later, ops.set(later, "WT-012", "title", "Renamed after the backup", "2026-12-01T00:00:00Z", "owner"));
  applyOp(later, ops.comment(later, "WT-012", "comment after the backup", "2026-12-01T00:00:00Z", "owner"));
  // WT-043 disappeared (another board) and its number is used by a different card.
  later.cards = later.cards.filter(c => c.id !== "WT-043");
  applyOp(later, { ...ops.create(later, { title: "Someone else's card" }, AT, "owner"), id: "WT-043" });
  later.cards.at(-1).id = "WT-043";

  const plan = planRestore(later, backup.cards);
  const byId = id => plan.find(p => p.card.id === id);
  assert.equal(byId("WT-001").state, "same");
  assert.equal(byId("WT-012").state, "changed");
  assert.deepEqual(byId("WT-012").fields, ["title"]);
  assert.equal(byId("WT-012").boardNewer, true);
  assert.equal(byId("WT-043").state, "new");
  assert.equal(byId("WT-043").idTaken, true);

  for (const op of restoreOps(later, [byId("WT-012"), byId("WT-043")], AT, "owner", "test")) applyOp(later, op);
  assert.deepEqual(validateBoard(later), []);
  const wt12 = findCard(later, "WT-012");
  assert.equal(wt12.title, findCard(board, "WT-012").title);
  assert.deepEqual(wt12.comments.map(c => c.text), ["comment after the backup"], "restoring never removes comments");
  assert.equal(findCard(later, "WT-043").title, "Someone else's card");
  assert.equal(later.nextId, 45);
  assert.ok(later.cards.some(c => c.title === "November task" && c.id === "WT-044"), "renumbered to the next free id");
});

test("restoring is checked against the revision that was previewed", () => {
  const board = boardWithMonths();
  const backupCard = clone(findCard(board, "WT-011"));
  backupCard.title = "Old title";
  const op = ops.restoreFromBackup(board, backupCard, AT, "owner", "x");
  applyOp(board, ops.set(board, "WT-011", "notes", "edited meanwhile", AT, "claude"));
  assert.throws(() => applyOp(board, op), /changed after the restore was previewed/);
});

test("damaged or edited backups are detected", async () => {
  const board = boardWithMonths();
  const { bytes } = await buildBackup({ board, scope: { kind: "all" }, attachments: [image(findCard(board, "WT-042").uid)] });
  const files = await readZip(bytes);
  files.set("attachments/WT-042/img-1-screen_shot.png", new Uint8Array([1, 2, 3]));
  const tampered = createZip([...files].map(([path, data]) => ({ path, data })));
  const result = await parseBackup(tampered);
  assert.match(result.problems[0], /damaged/);
  assert.equal(result.attachments.length, 0);

  const noManifest = createZip([{ path: "cards.json", data: "{}" }]);
  await assert.rejects(parseBackup(noManifest), /not a WorkTrace board backup/);
  const badCards = createZip([...files].map(([path, data]) => ({ path, data: path === "cards.json" ? '{"schemaVersion":2,"cards":[{"id":"x"}]}' : data })));
  await assert.rejects(parseBackup(badCards), /not valid/);
});
