// Test helpers: the migrated board, an in-memory GitHub with real version checking, and storage.
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { clone, parseBoard, serializeBoard } from "../app/board-core.js";
import { StoreError } from "../app/github.js";
import { migrate } from "../tools/migrate.mjs";

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const LEGACY_MD = readFileSync(path.join(ROOT, "docs/migration/BOARD-2026-10-28.md"), "utf8");
export const MIGRATED_AT = "2026-10-28T22:47:05Z";

export function migratedBoard() {
  return migrate(LEGACY_MD, MIGRATED_AT).board;
}

export function committedBoard() {
  return parseBoard(readFileSync(path.join(ROOT, "docs/board.json"), "utf8"));
}

/** Behaves like the Contents API for one file: a save must name the current blob sha. */
export class FakeGitHub {
  constructor(board) {
    this.commits = 0;
    this.saves = [];
    this.failNext = [];
    this.put(serializeBoard(board));
  }

  put(text) {
    this.text = text;
    this.sha = createHash("sha1").update(text).digest("hex");
    this.commits++;
  }

  get board() {
    return parseBoard(this.text);
  }

  /** A client of this fake GitHub (one browser tab, signed in or not). */
  client({ token = "test-token" } = {}) {
    const gh = this;
    return {
      get canWrite() {
        return !!this.token && !this.tokenRejected;
      },
      setToken(value) {
        this.token = value;
        this.tokenRejected = false;
      },
      token,
      tokenRejected: false,
      async load() {
        gh.maybeFail("load");
        return { sha: gh.sha, commit: `c${gh.commits}`, board: parseBoard(gh.text) };
      },
      async save(board, sha, message) {
        try {
          gh.maybeFail("save");
        } catch (e) {
          if (e.kind === "auth") this.tokenRejected = true;
          throw e;
        }
        if (sha !== gh.sha) throw new StoreError("stale", "stale", 409);
        gh.put(serializeBoard(board));
        gh.saves.push(message);
        if (gh.loseNextResponse) {
          gh.loseNextResponse = false;
          throw new StoreError("network", "connection lost after the commit");
        }
        return { sha: gh.sha, commit: `c${gh.commits}` };
      },
    };
  }

  maybeFail(what) {
    const i = this.failNext.findIndex(f => f.on === what);
    if (i >= 0) {
      const [f] = this.failNext.splice(i, 1);
      throw new StoreError(f.kind, f.kind, f.status ?? 0);
    }
  }

  /** Someone else edits the file directly (e.g. Claude Code via the CLI). */
  external(change) {
    const board = clone(this.board);
    change(board);
    this.put(serializeBoard(board));
  }
}

export function memoryStorage() {
  const data = new Map();
  return {
    getItem: k => (data.has(k) ? data.get(k) : null),
    setItem: (k, v) => data.set(k, String(v)),
    removeItem: k => data.delete(k),
    data,
  };
}

/** Timers that never fire on their own: tests call saveNow() explicitly. */
export const manualTimers = { setTimeout: () => 1, clearTimeout: () => {} };

export const AT = "2026-10-29T10:00:00Z";
