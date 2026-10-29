#!/usr/bin/env node
// One-time migration of the hand-written docs/BOARD.md (dev branch, 2026-10-28) into docs/board.json.
// Usage: node tools/migrate.mjs <old BOARD.md> <created-at ISO> [--write]
// Prints a report of every card and any information it could not map; with --write it creates
// docs/board.json and docs/BOARD.md. The parser is lossless by construction: it fails if the text of a
// card is not fully contained in the migrated fields.

import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import {
  assertValid, emptyBoard, formatId, idNumber, newUid, rankBetween, renderBoardMarkdown, serializeBoard, CATEGORIES,
} from "../app/board-core.js";

const SECTION_STATUS = { "Backlog": "backlog", "To do": "todo", "In progress": "doing", "Review / Test": "review", "Done": "done" };

export function migrate(markdown, createdAt) {
  const report = { cards: [], ignored: [], defaults: [] };
  const lines = markdown.replace(/\r\n?/g, "\n").split("\n");
  const parsed = [];
  let status = null;
  let current = null;

  const finish = () => {
    if (current) parsed.push(current);
    current = null;
  };

  for (const line of lines) {
    const section = /^## (.+)$/.exec(line);
    if (section) {
      finish();
      status = SECTION_STATUS[section[1].trim()] ?? null;
      if (!status) report.ignored.push(`section "${section[1].trim()}"`);
      continue;
    }
    if (!status) continue; // preamble: title, statuses, rules, last-updated line (now in CLAUDE.md / README)

    const heading = /^### (WT-\d{3,}) · (.+?) · `([a-z]+)`\s*$/.exec(line);
    if (heading) {
      finish();
      current = { id: heading[1], title: heading[2].trim(), category: heading[3], status, body: [] };
      continue;
    }
    const doneLine = /^- \*\*(WT-\d{3,})\*\* · (.+?) · `([a-z]+)`\s*$/.exec(line);
    if (doneLine) {
      finish();
      parsed.push({ id: doneLine[1], title: doneLine[2].trim(), category: doneLine[3], status, body: [] });
      continue;
    }
    if (current) current.body.push(line);
    else if (line.trim()) report.ignored.push(`text outside a card in ${status}: "${line.trim()}"`);
  }
  finish();

  const board = emptyBoard();
  let previousRank = { };
  for (const p of parsed) {
    if (!CATEGORIES.includes(p.category)) throw new Error(`${p.id}: unknown category ${p.category}`);
    const fields = splitBody(p.body.join("\n"));
    const blockedBy = [...fields.description.matchAll(/Blocked by (WT-\d{3,})/g)].map(m => m[1]);
    const rank = rankBetween(previousRank[p.status] ?? null, null);
    previousRank[p.status] = rank;
    board.cards.push({
      id: p.id, uid: newUid(), title: p.title, category: p.category, priority: "normal", status: p.status, rank,
      archived: false, archivedAt: null, description: fields.description, acceptance: fields.acceptance, notes: "",
      evidence: fields.evidence, blockedBy, links: [], comments: [],
      history: [{ id: newUid(), type: "migrated", at: createdAt, by: "migration", to: p.status }],
      createdAt, updatedAt: createdAt, rev: 1,
    });
    verifyLossless(p, fields);
    report.cards.push({ id: p.id, status: p.status, category: p.category, description: !!fields.description,
      acceptance: fields.acceptance.length, evidence: !!fields.evidence, blockedBy });
  }
  board.nextId = Math.max(0, ...board.cards.map(c => idNumber(c.id))) + 1;
  report.defaults.push("priority: not recorded in BOARD.md; every card set to Normal");
  report.defaults.push(`createdAt/updatedAt: not recorded per card; set to ${createdAt} (when BOARD.md was committed)`);
  report.defaults.push("notes, links, comments: none existed; empty");
  assertValid(board);
  return { board, report };
}

/** Splits a card body into description, acceptance criteria and evidence at the **Accept:** / **Evidence:** markers. */
export function splitBody(body) {
  const parts = body.split(/\*\*(Accept|Evidence):\*\*/);
  const out = { description: unwrap(parts[0]), acceptance: [], evidence: "" };
  for (let i = 1; i < parts.length; i += 2) {
    const text = parts[i + 1].trim();
    if (parts[i] === "Evidence") out.evidence = unwrap(text);
    else out.acceptance = splitCriteria(text);
  }
  return out;
}

/** The old file wrapped long lines at ~120 characters; a single line break there is a space (as in Markdown). */
function unwrap(text) {
  return text.trim().split(/\n\s*\n/).map(p => p.split("\n").map(l => l.trim()).join(" ")).join("\n\n");
}

function splitCriteria(text) {
  if (!/^- /m.test(text)) return text ? [unwrap(text)] : [];
  const items = [];
  for (const line of text.split("\n")) {
    if (line.startsWith("- ")) items.push(line.slice(2).trim());
    else if (line.trim() && items.length) items[items.length - 1] += ` ${line.trim()}`;
    else if (line.trim()) items.push(line.trim());
  }
  return items;
}

/** Every word of the original card must appear, in order, in the migrated fields. */
function verifyLossless(p, fields) {
  const squash = s => s.replace(/\*\*(Accept|Evidence):\*\*/g, "").replace(/^- /gm, "").replace(/\s+/g, "");
  const original = squash(p.body.join("\n"));
  const migrated = squash([fields.description, ...fields.acceptance, fields.evidence].join("\n"));
  if (original !== migrated) throw new Error(`${p.id}: migration would lose text`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [source, createdAt, flag] = process.argv.slice(2);
  if (!source || !createdAt) {
    console.error("Usage: node tools/migrate.mjs <old BOARD.md> <created-at ISO> [--write]");
    process.exit(2);
  }
  const { board, report } = migrate(readFileSync(source, "utf8"), createdAt);
  console.log(`Migrated ${board.cards.length} cards (next id ${formatId(board.nextId)}):`);
  for (const c of report.cards) {
    console.log(`  ${c.id}  ${c.status.padEnd(7)} ${c.category.padEnd(12)} description:${c.description ? "yes" : "no "} ` +
      `acceptance:${c.acceptance} evidence:${c.evidence ? "yes" : "no "}${c.blockedBy.length ? ` blockedBy:${c.blockedBy.join(",")}` : ""}`);
  }
  console.log("Not migrated (not card content):", report.ignored.length ? "" : "nothing");
  for (const i of report.ignored) console.log(`  - ${i}`);
  console.log("Defaults applied:");
  for (const d of report.defaults) console.log(`  - ${d}`);
  if (flag === "--write") {
    const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
    writeFileSync(path.join(root, "docs/board.json"), serializeBoard(board));
    writeFileSync(path.join(root, "docs/BOARD.md"), renderBoardMarkdown(board));
    console.log("Wrote docs/board.json and docs/BOARD.md");
  }
}
