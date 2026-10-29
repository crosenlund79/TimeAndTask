import { test } from "node:test";
import assert from "node:assert/strict";
import { findCard } from "../app/board-core.js";
import { migrate, splitBody } from "../tools/migrate.mjs";
import { committedBoard, LEGACY_MD, MIGRATED_AT } from "./helpers.mjs";

const EXPECTED = {
  review: ["WT-010", "WT-011", "WT-012", "WT-013", "WT-014", "WT-005", "WT-015", "WT-016", "WT-017", "WT-018"],
  todo: ["WT-030", "WT-031", "WT-032", "WT-033", "WT-034"],
  backlog: ["WT-040", "WT-041"],
  done: ["WT-001", "WT-002", "WT-003", "WT-004", "WT-006"],
};

test("all 22 cards are migrated with their ids, statuses and categories", () => {
  const { board } = migrate(LEGACY_MD, MIGRATED_AT);
  for (const [status, ids] of Object.entries(EXPECTED)) {
    assert.deepEqual(board.cards.filter(c => c.status === status).map(c => c.id), ids, status);
  }
  assert.equal(board.cards.length, 22);
  assert.equal(findCard(board, "WT-030").category, "architecture");
  assert.equal(findCard(board, "WT-015").category, "testing");
  assert.equal(findCard(board, "WT-040").category, "idea");
  assert.equal(board.nextId, 42);
});

test("descriptions, acceptance criteria and evidence are preserved word for word", () => {
  const { board } = migrate(LEGACY_MD, MIGRATED_AT);
  const wt10 = findCard(board, "WT-010");
  assert.match(wt10.description, /^Scheduled check-ins did not appear on the owner's machine/);
  assert.equal(wt10.acceptance.length, 3);
  assert.match(wt10.acceptance[1], /closed to the tray, and after sleep\/wake \(within about a minute\)\.$/);
  assert.match(wt10.evidence, /Not yet confirmed on the owner's machine\.$/);
  const wt16 = findCard(board, "WT-016");
  assert.match(wt16.acceptance[0], /^timed and indefinite pause show in tray menu and Overview; Resume appears only while paused; a timed pause resumes by itself\.$/);
  assert.match(wt16.evidence, /Tray menu appearance is a manual check\.$/);
  assert.equal(findCard(board, "WT-015").description, "Whole minutes 1–240 only.");
  assert.equal(findCard(board, "WT-002").title, "Clients, tasks and lists management UI, incl. Phase 2 refinements");
});

test("blocking relationships written in text become dependencies", () => {
  const { board } = migrate(LEGACY_MD, MIGRATED_AT);
  assert.deepEqual(findCard(board, "WT-030").blockedBy, ["WT-010"]);
  assert.ok(board.cards.filter(c => c.id !== "WT-030").every(c => c.blockedBy.length === 0));
});

test("only non-card text is left out, and it is reported", () => {
  const { report } = migrate(LEGACY_MD, MIGRATED_AT);
  assert.deepEqual(report.ignored, ["text outside a card in doing: \"_Nothing. Waiting for the owner's review of Phase 3 refinements (round 2)._\""]);
  assert.ok(report.defaults.some(d => d.startsWith("priority")));
});

test("the committed board.json matches the migration (apart from random uids)", () => {
  const strip = b => [...b.cards].sort((x, y) => x.id.localeCompare(y.id)).map(({ uid, history, ...rest }) => ({ ...rest, history: history.map(({ id, ...h }) => h) }));
  assert.deepEqual(strip(committedBoard()), strip(migrate(LEGACY_MD, MIGRATED_AT).board));
});

test("inline Accept/Evidence markers are split correctly", () => {
  assert.deepEqual(splitBody("Desc.\n**Accept:** a; b. **Evidence:** tests."), { description: "Desc.", acceptance: ["a; b."], evidence: "tests." });
  assert.deepEqual(splitBody("**Accept:**\n- one\n- two\n  more\n\n**Evidence:** e"), { description: "", acceptance: ["one", "two more"], evidence: "e" });
});
