import { test } from "node:test";
import assert from "node:assert/strict";
import { applyOp, findCard, ops } from "../app/board-core.js";
import { SyncEngine } from "../app/sync.js";
import { AT, FakeGitHub, manualTimers, memoryStorage, migratedBoard } from "./helpers.mjs";

async function engine(gh, { storage = memoryStorage(), token = "t" } = {}) {
  const e = new SyncEngine({ store: gh.client({ token }), storage, key: "k", timers: manualTimers });
  await e.start();
  return e;
}

test("a change is shown at once and saved to GitHub", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  e.enqueue(ops.move(e.view, "WT-011", "doing", 0, AT, "owner"));
  assert.equal(findCard(e.view, "WT-011").status, "doing");
  assert.equal(e.status, "unsaved");
  await e.saveNow();
  assert.equal(e.status, "saved");
  assert.equal(findCard(gh.board, "WT-011").status, "doing");
  assert.match(gh.saves[0], /move WT-011 to In progress/);
});

test("stale save: edits to different cards by two editors are both kept", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const a = await engine(gh);
  const b = await engine(gh);
  a.enqueue(ops.set(a.view, "WT-010", "notes", "A's note", AT, "a"));
  b.enqueue(ops.set(b.view, "WT-012", "notes", "B's note", AT, "b"));
  await a.saveNow();
  await b.saveNow(); // rejected as stale, then re-applied on A's version
  assert.equal(b.status, "saved");
  assert.equal(findCard(gh.board, "WT-010").notes, "A's note");
  assert.equal(findCard(gh.board, "WT-012").notes, "B's note");
});

test("same field edited by two editors: conflict shown, nothing overwritten, other changes still saved", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const a = await engine(gh);
  const b = await engine(gh);
  a.enqueue(ops.set(a.view, "WT-010", "title", "A title", AT, "a"));
  b.enqueue(ops.set(b.view, "WT-010", "title", "B title", AT, "b"));
  b.enqueue(ops.set(b.view, "WT-011", "priority", "high", AT, "b"));
  await a.saveNow();
  await b.saveNow();
  assert.equal(b.status, "conflict");
  assert.equal(b.conflicts.length, 1);
  assert.equal(b.conflicts[0].current, "A title");
  assert.equal(findCard(gh.board, "WT-010").title, "A title");
  assert.equal(findCard(gh.board, "WT-011").priority, "high");

  // The user decides: keep theirs, or keep (an edited version of) mine.
  b.resolveConflict(0, "mine", AT, "b", "Merged title");
  await b.saveNow();
  assert.equal(b.status, "saved");
  assert.equal(findCard(gh.board, "WT-010").title, "Merged title");
});

test("concurrent comments are neither lost nor duplicated, even when a save is retried", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const a = await engine(gh);
  const b = await engine(gh);
  a.enqueue(ops.comment(a.view, "WT-010", "comment A", AT, "a"));
  b.enqueue(ops.comment(b.view, "WT-010", "comment B", AT, "b"));
  gh.loseNextResponse = true; // A's commit succeeds but the response never arrives
  await a.saveNow();
  assert.equal(a.status, "failed");
  await b.saveNow();
  await a.retry();
  assert.equal(a.status, "saved");
  assert.deepEqual(findCard(gh.board, "WT-010").comments.map(c => c.text), ["comment A", "comment B"]);
});

test("network failure: change kept, shown as failed, saved on retry", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  e.enqueue(ops.set(e.view, "WT-013", "priority", "critical", AT, "owner"));
  gh.failNext.push({ on: "save", kind: "network" });
  await e.saveNow();
  assert.equal(e.status, "failed");
  assert.equal(e.error.kind, "network");
  assert.equal(findCard(e.view, "WT-013").priority, "critical");
  await e.retry();
  assert.equal(e.status, "saved");
  assert.equal(findCard(gh.board, "WT-013").priority, "critical");
});

test("expired token: change kept until signing in again", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  e.enqueue(ops.set(e.view, "WT-013", "notes", "keep", AT, "owner"));
  gh.failNext.push({ on: "save", kind: "auth", status: 401 });
  await e.saveNow();
  assert.equal(e.status, "failed");
  assert.ok(e.needsAuth);
  e.store.setToken("new-token");
  await e.retry();
  assert.equal(findCard(gh.board, "WT-013").notes, "keep");
});

test("unsaved changes survive a page reload and are saved afterwards", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const storage = memoryStorage();
  const first = await engine(gh, { storage, token: null }); // signed out: cannot save
  first.enqueue(ops.move(first.view, "WT-040", "todo", 0, AT, "owner"));
  assert.ok(first.needsAuth);
  gh.external(board => applyOp(board, ops.set(board, "WT-041", "notes", "meanwhile", AT, "claude")));
  const reloaded = await engine(gh, { storage }); // new page, signed in
  assert.equal(findCard(reloaded.view, "WT-040").status, "todo");
  assert.equal(findCard(reloaded.view, "WT-041").notes, "meanwhile");
  await reloaded.saveNow();
  assert.equal(findCard(gh.board, "WT-040").status, "todo");
  assert.equal(findCard(gh.board, "WT-041").notes, "meanwhile");
});

test("refresh picks up changes made elsewhere (e.g. by Claude Code)", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  gh.external(board => applyOp(board, ops.comment(board, "WT-010", "from the CLI", AT, "claude")));
  await e.refresh();
  assert.equal(findCard(e.view, "WT-010").comments[0].text, "from the CLI");
});

test("typing in notes becomes one saved change", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  for (const text of ["h", "he", "hel", "hello"]) e.enqueue(ops.set(e.view, "WT-010", "notes", text, AT, "owner"), { coalesce: true });
  assert.equal(e.pending.length, 1);
  await e.saveNow();
  assert.equal(gh.saves.length, 1);
  assert.equal(findCard(gh.board, "WT-010").notes, "hello");
});

test("undo reverses the saved change; refuses if changed elsewhere since", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  e.enqueue(ops.move(e.view, "WT-011", "doing", 0, AT, "owner"));
  e.enqueue(ops.set(e.view, "WT-012", "priority", "high", AT, "owner"));
  await e.saveNow();

  assert.ok(e.undo(AT, "owner").ok); // priority back
  await e.saveNow();
  assert.equal(findCard(gh.board, "WT-012").priority, "normal");

  gh.external(board => applyOp(board, ops.move(board, "WT-011", "todo", 0, AT, "claude")));
  await e.refresh();
  const result = e.undo(AT, "owner"); // the move: card has moved again remotely
  assert.equal(result.ok, false);
  assert.match(result.message, /changed again/);
  assert.equal(findCard(e.view, "WT-011").status, "todo");
});

test("invalid changes are refused before they are queued", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const e = await engine(gh);
  assert.throws(() => e.enqueue(ops.set(e.view, "WT-010", "blockedBy", ["WT-030"], AT, "owner")), /cycle/);
  assert.throws(() => e.enqueue(ops.set(e.view, "WT-010", "title", "  ", AT, "owner")), /title/);
  assert.equal(e.pending.length, 0);
});

test("a failed load keeps the last known board and pending work", async () => {
  const gh = new FakeGitHub(migratedBoard());
  const storage = memoryStorage();
  const first = await engine(gh, { storage, token: null });
  first.enqueue(ops.set(first.view, "WT-010", "notes", "offline note", AT, "owner"));
  gh.failNext.push({ on: "load", kind: "network" });
  const offline = await engine(gh, { storage });
  assert.equal(offline.error.kind, "network");
  assert.equal(findCard(offline.view, "WT-010").notes, "offline note");
  assert.equal(offline.status, "failed");
});
