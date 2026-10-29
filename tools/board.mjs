#!/usr/bin/env node
// Board CLI for Claude Code (and anyone with push access). Works in a checkout of the `board` branch:
// every command first fetches the latest board; changes are committed (board.json + generated BOARD.md)
// and pushed without force. If someone saved in between, the push is rejected and the change is re-applied
// on top of the new version, or reported as a conflict. Nothing is overwritten.
//
//   git fetch origin board && git worktree add --detach /tmp/worktrace-board origin/board   # once
//   node /tmp/worktrace-board/tools/board.mjs help

import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  CATEGORIES, PRIORITIES, STATUS_LABELS, STATUSES, USER_FACING, EDITABLE_FIELDS, columnCards, commitMessage, findCard,
  nowIso, openBlockers, ops, parseBoard, rebase, renderBoardMarkdown, serializeBoard, validateBoard,
} from "../app/board-core.js";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const JSON_PATH = path.join(ROOT, "docs/board.json");
const MD_PATH = path.join(ROOT, "docs/BOARD.md");
const MAX_ATTEMPTS = 4;

const HELP = `WorkTrace board CLI

Read (always fetches the latest board first):
  show [--status <s>] [--archived]       Cards per column
  get <id> [--json]                      One card with notes, comments and history
  validate                               Check docs/board.json in this checkout
  render [--check]                       Regenerate docs/BOARD.md (or check it is current)

Change (commit + push to the board branch; re-applied or reported if the board changed meanwhile):
  add --title <t> [--category c] [--priority p] [--status s] [--description d] [--accept a]... [--blocked-by id]...
  move <id> <status> [--top] [--owner-accepted "<the owner's words>"]
  set <id> <field> <value>               fields: ${EDITABLE_FIELDS.join(", ")} (lists as JSON arrays)
  comment <id> <text>
  evidence <id> <text>                   Append a line to the card's evidence
  link <id> <label> <url>
  archive <id> | restore <id>

Options: --branch <name> (default board), --by <author> (default claude), --offline (no fetch/push), --json
Statuses: ${STATUSES.join(", ")} · categories: ${CATEGORIES.join(", ")} · priorities: ${PRIORITIES.join(", ")}
User-facing cards (feature, bug) move to done only with --owner-accepted, after the owner explicitly accepted.`;

// ---------------------------------------------------------------- arguments

function parseArgs(argv) {
  const positional = [];
  const options = { accept: [], "blocked-by": [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) {
      positional.push(a);
      continue;
    }
    const name = a.slice(2);
    if (["archived", "check", "json", "offline", "top", "help"].includes(name)) options[name] = true;
    else {
      const value = argv[++i];
      if (value === undefined) fail(`--${name} needs a value`);
      if (Array.isArray(options[name])) options[name].push(value);
      else options[name] = value;
    }
  }
  return { positional, options };
}

function fail(message, code = 2) {
  console.error(`board: ${message}`);
  process.exit(code);
}

// ---------------------------------------------------------------- git

function git(args, { allowFail = false } = {}) {
  try {
    return execFileSync("git", ["-C", ROOT, ...args], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
  } catch (e) {
    if (allowFail) return null;
    fail(`git ${args[0]} failed: ${String(e.stderr || e.message).trim().split("\n").slice(-3).join(" ")}`, 4);
  }
}

function syncCheckout(branch) {
  if (git(["status", "--porcelain", "--", "docs"])) {
    fail("this checkout has uncommitted board changes; commit or discard them first", 4);
  }
  git(["fetch", "--quiet", "origin", branch]);
  git(["checkout", "--quiet", "--detach", `origin/${branch}`]);
}

function readBoard() {
  const board = parseBoard(readFileSync(JSON_PATH, "utf8"));
  const errors = validateBoard(board);
  if (errors.length) fail(`docs/board.json is invalid:\n  ${errors.join("\n  ")}`, 5);
  return board;
}

function writeBoard(board) {
  writeFileSync(JSON_PATH, serializeBoard(board));
  writeFileSync(MD_PATH, renderBoardMarkdown(board));
}

/**
 * Applies `makeOp(board)` to the latest board and pushes it. On a rejected push (someone saved first), fetches
 * again and re-applies the same operation, whose recorded "from" values detect real conflicts.
 */
function change(options, makeOp) {
  const branch = options.branch ?? "board";
  if (!options.offline) syncCheckout(branch);
  let op = makeOp(readBoard());
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    const board = readBoard();
    const result = rebase(board, [op]);
    if (result.conflicts.length) {
      const c = result.conflicts[0];
      console.error(`board: conflict on ${op.id}: ${c.reason}. Current value: ${JSON.stringify(c.current)}`);
      console.error("board: nothing was saved. Read the card again and decide what to do.");
      process.exit(3);
    }
    op = result.applied[0] ?? op;
    const errors = validateBoard(result.board);
    if (errors.length) fail(`the change would make the board invalid: ${errors[0]}`, 5);
    writeBoard(result.board);
    if (options.offline) {
      console.log(`${describe(op)} (written locally, not committed)`);
      return result.board;
    }
    if (!git(["status", "--porcelain", "--", "docs"])) {
      console.log(`${describe(op)} — already saved on ${branch}, nothing to do`);
      return result.board;
    }
    git(["add", "docs/board.json", "docs/BOARD.md"]);
    git(["-c", "user.name=Claude", "-c", "user.email=noreply@anthropic.com", "commit", "--quiet", "-m", commitMessage([op], "board CLI")]);
    if (process.env.BOARD_TEST_BEFORE_PUSH && attempt === 1) {
      // Tests only: let another editor save between our commit and our push.
      const { BOARD_TEST_BEFORE_PUSH: hook, ...env } = process.env;
      execFileSync("sh", ["-c", hook], { stdio: "ignore", env });
    }
    if (git(["push", "--quiet", "origin", `HEAD:refs/heads/${branch}`], { allowFail: true }) !== null) {
      console.log(`${describe(op)} — saved to ${branch} (${git(["rev-parse", "--short", "HEAD"])})`);
      return result.board;
    }
    console.error(`board: ${branch} changed while saving; re-applying on the latest version (attempt ${attempt})`);
    syncCheckout(branch);
  }
  fail("the board kept changing; nothing was saved", 4);
}

function describe(op) {
  switch (op.type) {
    case "create": return `Added ${op.id}`;
    case "move": return `Moved ${op.id} to ${STATUS_LABELS[op.to.status]}`;
    case "set": return `Updated ${op.id} ${op.field}`;
    case "comment": return `Commented on ${op.id}`;
    default: return `${op.type[0].toUpperCase()}${op.type.slice(1)}d ${op.id}`;
  }
}

// ---------------------------------------------------------------- output

function printCard(board, card) {
  const blockers = openBlockers(board, card);
  console.log(`${card.id} · ${card.title}`);
  console.log(`  ${STATUS_LABELS[card.status]}${card.archived ? " (archived)" : ""} · ${card.category} · priority ${card.priority}` +
    `${card.blockedBy.length ? ` · blocked by ${card.blockedBy.join(", ")}${blockers.length ? "" : " (all done)"}` : ""}`);
  console.log(`  created ${card.createdAt} · updated ${card.updatedAt} · rev ${card.rev}`);
  const block = (label, text) => {
    if (text && String(text).trim()) console.log(`\n  ${label}:\n${String(text).trim().split("\n").map(l => `    ${l}`).join("\n")}`);
  };
  block("Description", card.description);
  if (card.acceptance.length) block("Acceptance criteria", card.acceptance.map(a => `- ${a}`).join("\n"));
  block("Notes", card.notes);
  block("Evidence", card.evidence);
  if (card.links.length) block("Links", card.links.map(l => `- ${l.label}: ${l.url}`).join("\n"));
  if (card.comments.length) block(`Comments (${card.comments.length})`, card.comments.map(c => `${c.at} ${c.author}: ${c.text}`).join("\n"));
  block("History", card.history.map(h => `${h.at} ${h.by}: ${h.type}${h.from ? ` ${h.from} →` : ""}${h.to ? ` ${h.to}` : ""}${h.accepted ? ` (accepted: ${h.accepted})` : ""}`).join("\n"));
}

function parseListValue(field, value) {
  if (!["acceptance", "blockedBy", "links"].includes(field)) return value;
  try {
    const parsed = JSON.parse(value);
    if (!Array.isArray(parsed)) throw new Error();
    return parsed;
  } catch {
    fail(`${field} must be a JSON array, e.g. '["first","second"]'`);
  }
}

// ---------------------------------------------------------------- commands

function main() {
  const { positional, options } = parseArgs(process.argv.slice(2));
  const [command, ...args] = positional;
  const by = options.by ?? "claude";
  const at = nowIso();
  const branch = options.branch ?? "board";
  if (!command || command === "help" || options.help) return console.log(HELP);

  const need = (n, usage) => {
    if (args.length < n) fail(`usage: board ${usage}`);
  };
  const card = (board, id) => findCard(board, id) ?? fail(`no card ${id}`);

  switch (command) {
    case "show": {
      if (!options.offline) syncCheckout(branch);
      const board = readBoard();
      if (options.json) return console.log(serializeBoard(board));
      for (const status of options.status ? [options.status] : STATUSES) {
        const cards = columnCards(board, status, { archived: !!options.archived });
        console.log(`\n${STATUS_LABELS[status]} (${cards.length})`);
        for (const c of cards) {
          const blockers = openBlockers(board, c);
          console.log(`  ${c.id}  ${c.title}  [${c.category}${c.priority !== "normal" ? `, ${c.priority}` : ""}]` +
            `${blockers.length ? `  blocked by ${blockers.join(", ")}` : ""}${c.comments.length ? `  ${c.comments.length} comment(s)` : ""}`);
        }
      }
      const archived = board.cards.filter(c => c.archived).length;
      if (!options.archived) console.log(`\nArchived: ${archived} · next id WT-${String(board.nextId).padStart(3, "0")}`);
      return;
    }
    case "get": {
      need(1, "get <id>");
      if (!options.offline) syncCheckout(branch);
      const board = readBoard();
      const c = card(board, args[0]);
      return options.json ? console.log(JSON.stringify(c, null, 2)) : printCard(board, c);
    }
    case "validate": {
      const errors = validateBoard(parseBoard(readFileSync(JSON_PATH, "utf8")));
      if (errors.length) fail(`invalid:\n  ${errors.join("\n  ")}`, 5);
      return console.log("docs/board.json is valid");
    }
    case "render": {
      const board = readBoard();
      const markdown = renderBoardMarkdown(board);
      if (options.check) {
        if (readFileSync(MD_PATH, "utf8") !== markdown) fail("docs/BOARD.md is out of date; run: node tools/board.mjs render", 6);
        return console.log("docs/BOARD.md is up to date");
      }
      writeFileSync(MD_PATH, markdown);
      return console.log("Wrote docs/BOARD.md");
    }
    case "add": {
      if (!options.title) fail("usage: board add --title <t> [--category c] [--priority p] [--status s] ...");
      change(options, board => {
        const status = options.status ?? "backlog";
        if (status === "done") fail("new cards cannot start in done");
        return ops.create(board, {
          title: options.title, category: options.category ?? "feature", priority: options.priority ?? "normal", status,
          description: options.description ?? "", acceptance: options.accept, blockedBy: options["blocked-by"],
        }, at, by);
      });
      return;
    }
    case "move": {
      need(2, "move <id> <status> [--top] [--owner-accepted \"...\"]");
      const [id, status] = args;
      if (!STATUSES.includes(status)) fail(`status must be one of ${STATUSES.join(", ")}`);
      change(options, board => {
        const c = card(board, id);
        if (status === "done" && USER_FACING.has(c.category) && !options["owner-accepted"]) {
          fail(`${id} is user-facing (${c.category}): only the owner accepts it. Move it to review; use --owner-accepted "<their words>" only after explicit acceptance.`);
        }
        if (c.archived) fail(`${id} is archived; restore it first`);
        return ops.move(board, id, status, options.top ? 0 : Infinity, at, by, options["owner-accepted"] ?? null);
      });
      return;
    }
    case "set": {
      need(3, "set <id> <field> <value>");
      const [id, field, ...rest] = args;
      if (!EDITABLE_FIELDS.includes(field)) fail(`field must be one of ${EDITABLE_FIELDS.join(", ")}`);
      change(options, board => ops.set(board, card(board, id).id, field, parseListValue(field, rest.join(" ")), at, by));
      return;
    }
    case "comment": {
      need(2, "comment <id> <text>");
      change(options, board => ops.comment(board, card(board, args[0]).id, args.slice(1).join(" "), at, by));
      return;
    }
    case "evidence": {
      need(2, "evidence <id> <text>");
      change(options, board => {
        const c = card(board, args[0]);
        const line = args.slice(1).join(" ").trim();
        return ops.set(board, c.id, "evidence", c.evidence.trim() ? `${c.evidence.trim()}\n${line}` : line, at, by);
      });
      return;
    }
    case "link": {
      need(3, "link <id> <label> <url>");
      change(options, board => {
        const c = card(board, args[0]);
        return ops.set(board, c.id, "links", [...c.links, { label: args[1], url: args[2] }], at, by);
      });
      return;
    }
    case "archive":
    case "restore": {
      need(1, `${command} <id>`);
      change(options, board => ops[command](board, card(board, args[0]).id, at, by));
      return;
    }
    default:
      fail(`unknown command "${command}". Run: node tools/board.mjs help`);
  }
}

main();
