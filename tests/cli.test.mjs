// The CLI against a real Git remote (a local bare repository): concurrent saves, conflicts, the Done rule.
import { test, before } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { cpSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { findCard, parseBoard, renderBoardMarkdown } from "../app/board-core.js";
import { ROOT } from "./helpers.mjs";

let remote;
let a;
let b;

const git = (cwd, ...args) => execFileSync("git", ["-C", cwd, "-c", "user.name=t", "-c", "user.email=t@t", ...args], { encoding: "utf8" }).trim();
const cli = (clone, args, env = {}) => spawnSync("node", [path.join(clone, "tools/board.mjs"), ...args], {
  encoding: "utf8", env: { ...process.env, ...env },
});
const remoteBoard = () => parseBoard(execFileSync("git", ["-C", remote, "show", "board:docs/board.json"], { encoding: "utf8" }));

before(() => {
  const dir = mkdtempSync(path.join(tmpdir(), "board-cli-"));
  remote = path.join(dir, "remote.git");
  execFileSync("git", ["init", "--quiet", "--bare", remote]);
  const seed = path.join(dir, "seed");
  cpSync(ROOT, seed, { recursive: true, filter: src => !/node_modules|\.git$|\/\.git\//.test(src) });
  execFileSync("git", ["init", "--quiet", "-b", "board", seed]);
  git(seed, "add", "-A");
  git(seed, "commit", "--quiet", "-m", "seed");
  git(seed, "push", "--quiet", remote, "board");
  a = path.join(dir, "a");
  b = path.join(dir, "b");
  execFileSync("git", ["clone", "--quiet", "-b", "board", remote, a]);
  execFileSync("git", ["clone", "--quiet", "-b", "board", remote, b]);
});

test("show and get read the latest board", () => {
  const out = cli(a, ["show"]);
  assert.equal(out.status, 0, out.stderr);
  assert.match(out.stdout, /Review \/ Test \(10\)/);
  assert.match(out.stdout, /WT-030 {2}Estimation engine {2}\[architecture\] {2}blocked by WT-010/);
  const card = cli(a, ["get", "WT-010"]);
  assert.match(card.stdout, /Acceptance criteria:/);
});

test("a change is committed with a regenerated BOARD.md and seen by the other editor", () => {
  const out = cli(a, ["comment", "WT-010", "Checked on CI run 22"]);
  assert.equal(out.status, 0, out.stderr);
  const board = remoteBoard();
  assert.equal(findCard(board, "WT-010").comments.at(-1).author, "claude");
  const md = execFileSync("git", ["-C", remote, "show", "board:docs/BOARD.md"], { encoding: "utf8" });
  assert.equal(md, renderBoardMarkdown(board));
  assert.match(cli(b, ["get", "WT-010"]).stdout, /claude: Checked on CI run 22/);
});

test("someone saves in between: changes to different cards are both kept", () => {
  const hook = `node ${path.join(b, "tools/board.mjs")} set WT-012 notes "B was first"`;
  const out = cli(a, ["set", "WT-011", "notes", "A second"], { BOARD_TEST_BEFORE_PUSH: hook });
  assert.equal(out.status, 0, out.stderr);
  assert.match(out.stderr, /re-applying/);
  const board = remoteBoard();
  assert.equal(findCard(board, "WT-011").notes, "A second");
  assert.equal(findCard(board, "WT-012").notes, "B was first");
});

test("someone changes the same field in between: conflict, their value kept, nothing saved", () => {
  const hook = `node ${path.join(b, "tools/board.mjs")} set WT-013 title "B's title"`;
  const out = cli(a, ["set", "WT-013", "title", "A's title"], { BOARD_TEST_BEFORE_PUSH: hook });
  assert.equal(out.status, 3);
  assert.match(out.stderr, /conflict on WT-013: title was changed elsewhere/);
  assert.equal(findCard(remoteBoard(), "WT-013").title, "B's title");
});

test("concurrent comments on one card are both kept", () => {
  const hook = `node ${path.join(b, "tools/board.mjs")} comment WT-014 "comment from B"`;
  const out = cli(a, ["comment", "WT-014", "comment from A"], { BOARD_TEST_BEFORE_PUSH: hook });
  assert.equal(out.status, 0, out.stderr);
  assert.deepEqual(findCard(remoteBoard(), "WT-014").comments.map(c => c.text), ["comment from B", "comment from A"]);
});

test("user-facing work reaches Done only with the owner's acceptance", () => {
  const refused = cli(a, ["move", "WT-011", "done"]);
  assert.equal(refused.status, 2);
  assert.match(refused.stderr, /only the owner accepts it/);
  assert.equal(findCard(remoteBoard(), "WT-011").status, "review");
  const accepted = cli(a, ["move", "WT-011", "done", "--owner-accepted", "Owner: WT-011 works, accepted"]);
  assert.equal(accepted.status, 0, accepted.stderr);
  const card = findCard(remoteBoard(), "WT-011");
  assert.equal(card.status, "done");
  assert.equal(card.history.at(-1).accepted, "Owner: WT-011 works, accepted");
  // Internal work may go to Done without that.
  assert.equal(cli(a, ["move", "WT-015", "done"]).status, 0);
});

test("adding cards uses the next id; invalid changes are refused", () => {
  const out = cli(a, ["add", "--title", "Found while testing", "--category", "bug", "--priority", "high", "--status", "todo", "--accept", "No crash"]);
  assert.equal(out.status, 0, out.stderr);
  assert.match(out.stdout, /Added WT-042/);
  const card = findCard(remoteBoard(), "WT-042");
  assert.equal(card.priority, "high");
  assert.deepEqual(card.acceptance, ["No crash"]);
  const cycle = cli(a, ["set", "WT-010", "blockedBy", '["WT-030"]']);
  assert.equal(cycle.status, 3);
  assert.match(cycle.stderr, /cycle/);
  assert.equal(cli(a, ["validate"]).status, 0);
  assert.equal(cli(a, ["render", "--check"]).status, 0);
});
