import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import {
  ALL_STATUSES, needsOwnerAcceptance, parseBoard, applyOp, CATEGORIES, clone, columnCards, ConflictError, findCard, inverseOp, isValidRank, openBlockers, ops, PRIORITIES,
  rankBetween, rebase, renderBoardMarkdown, serializeBoard, STATUSES, validateBoard, wouldCycle,
} from "../app/board-core.js";
import { AT, committedBoard, migratedBoard, ROOT } from "./helpers.mjs";

test("the committed board.json is valid (whatever its current cards)", () => {
  assert.deepEqual(validateBoard(committedBoard()), []);
});

test("the migrated board is valid and has the 22 cards", () => {
  const board = migratedBoard();
  assert.deepEqual(validateBoard(board), []);
  assert.equal(board.cards.length, 22);
  assert.equal(new Set(board.cards.map(c => c.id)).size, 22);
});

test("serialisation is stable", () => {
  const board = migratedBoard();
  const shuffled = clone(board);
  shuffled.cards.reverse();
  assert.equal(serializeBoard(shuffled), serializeBoard(board));
});

test("schema file lists the same values as the validator", () => {
  const schema = JSON.parse(readFileSync(path.join(ROOT, "schema/board.schema.json"), "utf8"));
  const card = schema.$defs.card.properties;
  assert.deepEqual(schema.$defs.status.enum, ALL_STATUSES);
  assert.deepEqual(card.category.oneOf[0].enum, CATEGORIES);
  assert.deepEqual(card.priority.enum, PRIORITIES);
  assert.deepEqual([...schema.$defs.card.required].sort(), Object.keys(migratedBoard().cards[0]).sort());
});

test("validation reports duplicate ids, bad status, unknown dependencies and cycles", () => {
  const board = migratedBoard();
  const bad = clone(board);
  bad.cards[1].id = bad.cards[0].id;
  bad.cards[2].status = "waiting";
  bad.cards[3].blockedBy = ["WT-999"];
  const errors = validateBoard(bad).join("\n");
  assert.match(errors, /duplicate id/);
  assert.match(errors, /status must be one of/);
  assert.match(errors, /unknown card WT-999/);

  const cyc = clone(board);
  findCard(cyc, "WT-010").blockedBy = ["WT-030"]; // WT-030 is already blocked by WT-010
  assert.match(validateBoard(cyc).join("\n"), /cycle/);
  const self = clone(board);
  findCard(self, "WT-010").blockedBy = ["WT-010"];
  assert.match(validateBoard(self).join("\n"), /itself/);

  const low = clone(board);
  low.nextId = 5;
  assert.match(validateBoard(low).join("\n"), /nextId/);
});

test("ranks: always room between two ranks, never ending in 0", () => {
  let ranks = [rankBetween(null, null)];
  for (let i = 0; i < 400; i++) {
    const pos = (i * 7919) % (ranks.length + 1);
    const r = rankBetween(ranks[pos - 1] ?? null, ranks[pos] ?? null);
    assert.ok(isValidRank(r), r);
    if (pos > 0) assert.ok(ranks[pos - 1] < r);
    if (pos < ranks.length) assert.ok(r < ranks[pos]);
    ranks.splice(pos, 0, r);
  }
  assert.deepEqual([...ranks].sort(), ranks);
  // Repeatedly inserting at the top stays short enough.
  let top = "i";
  for (let i = 0; i < 50; i++) top = rankBetween(null, top);
  assert.ok(isValidRank(top) && top.length < 30);
});

test("moving and reordering keep a stable order", () => {
  const board = migratedBoard();
  const [first, second, third] = columnCards(board, "review");
  applyOp(board, ops.move(board, third.id, "review", 0, AT, "owner"));
  assert.deepEqual(columnCards(board, "review").slice(0, 3).map(c => c.id), [third.id, first.id, second.id]);
  applyOp(board, ops.move(board, first.id, "doing", 0, AT, "owner"));
  assert.equal(findCard(board, first.id).status, "doing");
  assert.equal(findCard(board, first.id).history.at(-1).to, "doing");
  // A reorder within a column is not a status change.
  const historyLength = findCard(board, second.id).history.length;
  applyOp(board, ops.move(board, second.id, "review", 0, AT, "owner"));
  assert.equal(findCard(board, second.id).history.length, historyLength);
});

test("operations check what they replace", () => {
  const board = migratedBoard();
  const edit = ops.set(board, "WT-011", "notes", "mine", AT, "owner");
  const other = clone(board);
  findCard(other, "WT-011").notes = "theirs";
  assert.throws(() => applyOp(other, edit), ConflictError);
  // Applying twice (a retried save) is harmless.
  assert.equal(applyOp(board, edit), "applied");
  assert.equal(applyOp(board, edit), "noop");
});

test("creating cards uses new ids that are never reused", () => {
  const board = migratedBoard();
  const op = ops.create(board, { title: "Something new", category: "idea" }, AT, "owner");
  assert.equal(op.id, "WT-042");
  applyOp(board, op);
  assert.equal(board.nextId, 43);
  assert.equal(ops.create(board, { title: "Next" }, AT, "owner").id, "WT-043");
});

test("archiving only Done cards; restoring keeps notes, comments and history", () => {
  const board = migratedBoard();
  assert.throws(() => applyOp(board, ops.archive(board, "WT-010", AT, "owner")), ConflictError);
  applyOp(board, ops.comment(board, "WT-001", "keep me", AT, "owner"));
  applyOp(board, ops.set(board, "WT-001", "notes", "and me", AT, "owner"));
  applyOp(board, ops.archive(board, "WT-001", AT, "owner"));
  const archived = findCard(board, "WT-001");
  assert.equal(archived.archived, true);
  assert.equal(archived.archivedAt, AT);
  assert.ok(!columnCards(board, "done").some(c => c.id === "WT-001"));
  applyOp(board, ops.restore(board, "WT-001", AT, "owner"));
  const restored = findCard(board, "WT-001");
  assert.equal(restored.archived, false);
  assert.equal(restored.status, "done");
  assert.equal(restored.comments[0].text, "keep me");
  assert.equal(restored.notes, "and me");
  assert.deepEqual(restored.history.map(h => h.type), ["migrated", "archived", "restored"]);
  assert.deepEqual(validateBoard(board), []);
});

test("dependencies: blocked until the blocker is Done; cycles rejected", () => {
  const board = migratedBoard();
  assert.deepEqual(openBlockers(board, findCard(board, "WT-030")), ["WT-010"]);
  assert.ok(wouldCycle(board, "WT-010", ["WT-030"]));
  assert.throws(() => applyOp(board, ops.set(board, "WT-010", "blockedBy", ["WT-030"], AT, "owner")), /cycle/);
  applyOp(board, ops.move(board, "WT-010", "done", 0, AT, "owner", "accepted"));
  assert.deepEqual(openBlockers(board, findCard(board, "WT-030")), []);
});

test("rebase: changes to different cards and fields both survive", () => {
  const base = migratedBoard();
  const mine = [ops.set(base, "WT-010", "notes", "my note", AT, "owner"), ops.move(base, "WT-011", "doing", 0, AT, "owner")];
  const remote = clone(base);
  applyOp(remote, ops.set(remote, "WT-012", "title", "Their title", AT, "claude"));
  applyOp(remote, ops.set(remote, "WT-010", "priority", "high", AT, "claude"));
  const { board, conflicts } = rebase(remote, mine);
  assert.deepEqual(conflicts, []);
  assert.equal(findCard(board, "WT-010").notes, "my note");
  assert.equal(findCard(board, "WT-010").priority, "high");
  assert.equal(findCard(board, "WT-011").status, "doing");
  assert.equal(findCard(board, "WT-012").title, "Their title");
});

test("rebase: same field changed on both sides is a conflict, nothing overwritten", () => {
  const base = migratedBoard();
  const mine = [ops.set(base, "WT-010", "title", "Mine", AT, "owner"), ops.move(base, "WT-013", "done", 0, AT, "owner", "ok")];
  const remote = clone(base);
  applyOp(remote, ops.set(remote, "WT-010", "title", "Theirs", AT, "claude"));
  applyOp(remote, ops.move(remote, "WT-013", "doing", 0, AT, "claude"));
  const { board, conflicts } = rebase(remote, mine);
  assert.equal(conflicts.length, 2);
  assert.equal(findCard(board, "WT-010").title, "Theirs");
  assert.equal(findCard(board, "WT-013").status, "doing");
  assert.equal(conflicts[0].current, "Theirs");
});

test("rebase: concurrent comments are all kept, retried comments not duplicated", () => {
  const base = migratedBoard();
  const mine = ops.comment(base, "WT-010", "from the browser", AT, "owner");
  const remote = clone(base);
  applyOp(remote, ops.comment(remote, "WT-010", "from Claude", AT, "claude"));
  const first = rebase(remote, [mine]).board;
  const again = rebase(first, [mine]).board; // the same save retried
  assert.deepEqual(findCard(again, "WT-010").comments.map(c => c.text), ["from Claude", "from the browser"]);
});

test("rebase: a new card whose id was taken meanwhile is renumbered", () => {
  const base = migratedBoard();
  const mineCreate = ops.create(base, { title: "Mine" }, AT, "owner");
  const mineEdit = ops.set({ ...base, cards: [...base.cards, mineCreate.card] }, mineCreate.id, "notes", "n", AT, "owner");
  const remote = clone(base);
  applyOp(remote, ops.create(remote, { title: "Theirs" }, AT, "claude"));
  const { board, conflicts, renamed } = rebase(remote, [mineCreate, mineEdit]);
  assert.deepEqual(conflicts, []);
  assert.equal(findCard(board, "WT-042").title, "Theirs");
  assert.equal(findCard(board, "WT-043").title, "Mine");
  assert.equal(findCard(board, "WT-043").notes, "n");
  assert.equal(renamed.get("WT-042"), "WT-043");
  assert.deepEqual(validateBoard(board), []);
});

test("inverse operations undo moves, edits and archiving", () => {
  const board = migratedBoard();
  const before = serializeBoard(board);
  const done = [ops.move(board, "WT-011", "doing", 0, AT, "owner")];
  applyOp(board, done[0]);
  done.push(ops.set(board, "WT-011", "notes", "x", AT, "owner"));
  applyOp(board, done[1]);
  done.push(ops.archive(board, "WT-001", AT, "owner"));
  applyOp(board, done[2]);
  for (const op of done.reverse()) applyOp(board, inverseOp(op, AT, "owner"));
  const strip = b => JSON.parse(b, (k, v) => (["history", "rev", "updatedAt"].includes(k) ? undefined : v));
  assert.deepEqual(strip(serializeBoard(board)), strip(before));
  assert.equal(inverseOp(ops.comment(board, "WT-011", "c", AT, "owner"), AT, "owner"), null);
});

test("version 1 boards are upgraded on read and stay valid", () => {
  const v1 = JSON.parse(serializeBoard(migratedBoard()));
  v1.schemaVersion = 1;
  const upgraded = parseBoard(JSON.stringify(v1));
  assert.equal(upgraded.schemaVersion, 2);
  assert.deepEqual(validateBoard(upgraded), []);
});

test("Inbox: capture without a category, not a column, can be archived or put on the board", () => {
  const board = migratedBoard();
  const capture = ops.create(board, { title: "Call Peter about invoices", category: null, status: "inbox" }, AT, "owner");
  applyOp(board, capture);
  assert.deepEqual(validateBoard(board), []);
  for (const s of STATUSES) assert.ok(!columnCards(board, s).some(c => c.id === capture.id));
  assert.match(renderBoardMarkdown(board), /## Inbox \(1\)\n\n- \*\*WT-042\*\* · Call Peter about invoices/);
  assert.ok(needsOwnerAcceptance(findCard(board, "WT-042")), "uncategorised cards need acceptance before Done");

  const second = ops.create(board, { title: "Idea", category: null, status: "inbox" }, AT, "owner");
  applyOp(board, second);
  applyOp(board, ops.archive(board, second.id, AT, "owner"));
  assert.equal(findCard(board, second.id).archived, true);
  applyOp(board, ops.restore(board, second.id, AT, "owner"));
  assert.equal(findCard(board, second.id).status, "inbox");

  applyOp(board, ops.move(board, capture.id, "todo", 0, AT, "owner"));
  assert.equal(columnCards(board, "todo")[0].id, capture.id);
  assert.deepEqual(validateBoard(board), []);
});
