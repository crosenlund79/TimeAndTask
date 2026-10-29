// Keeps the board in step with GitHub. Every change is an operation queued locally (and kept in browser
// storage, so a refresh or a lost connection loses nothing), applied to the screen at once and saved in
// batches. A save that GitHub rejects as stale is retried on top of the latest board; operations that no
// longer fit become conflicts for the user to resolve. Nothing is overwritten silently.

import {
  applyOp, clone, commitMessage, ConflictError, findCard, inverseOp, ops, rebase, remapOp, sameValue, validateBoard,
} from "./board-core.js";
import { StoreError } from "./github.js";

const STORAGE_VERSION = 1;
const MAX_SAVE_ATTEMPTS = 4;

export class SyncEngine {
  /**
   * @param store    GitHubStore (or a fake with load/save/canWrite)
   * @param storage  localStorage-like object for pending work
   * @param key      storage key (per repository and branch)
   */
  constructor({ store, storage, key, source = "board app", debounceMs = 1500, onUpdate = () => {}, timers = globalThis }) {
    Object.assign(this, { store, storage, key, source, debounceMs, onUpdate, timers });
    this.base = null; // { sha, commit, board } last version known to be on GitHub
    this.pending = []; // operations not yet saved, oldest first
    this.conflicts = []; // { op, reason, current }
    this.view = null; // base + pending: what the user sees
    this.undoStack = []; // operations made in this page session that can be undone
    this.error = null; // StoreError of the last failed load/save
    this.saving = null; // promise while a save runs
    this.inFlight = new Set();
    this.saveTimer = null;
    this.lastSavedAt = null;
    this.loaded = false;
  }

  // ---------------------------------------------------------------- state for the UI

  get status() {
    if (this.conflicts.length) return "conflict";
    if (this.saving) return "saving";
    if (this.pending.length && this.error) return "failed";
    if (this.pending.length) return "unsaved";
    if (this.error && !this.loaded) return "failed";
    return "saved";
  }

  /** Changes are waiting but cannot be saved until the user signs in (again). */
  get needsAuth() {
    return this.pending.length > 0 && !this.store.canWrite;
  }

  notify() {
    this.onUpdate(this);
  }

  // ---------------------------------------------------------------- loading

  /** Restores unsaved work from storage, then loads the latest board from GitHub. */
  async start() {
    const saved = this.readStorage();
    if (saved) {
      this.base = saved.base;
      this.pending = saved.pending ?? [];
      this.conflicts = saved.conflicts ?? [];
      this.recompute();
      this.notify();
    }
    await this.refresh();
  }

  /** Fetches the latest board; pending operations are re-applied on top of it. */
  async refresh() {
    if (this.saving) await this.saving;
    try {
      const latest = await this.store.load();
      this.error = null;
      this.loaded = true;
      if (!this.base || latest.sha !== this.base.sha) {
        this.base = latest;
        this.recompute();
        this.persist();
      }
      if (this.pending.length && this.store.canWrite) this.scheduleSave(0);
    } catch (e) {
      if (!(e instanceof StoreError)) throw e;
      this.error = e;
    }
    this.notify();
  }

  recompute() {
    if (!this.base) return;
    const result = rebase(this.base.board, this.pending);
    this.pending = result.applied;
    if (result.conflicts.length) this.conflicts.push(...result.conflicts);
    this.view = result.board;
  }

  // ---------------------------------------------------------------- changes

  /**
   * Applies an operation to the view and queues it for saving. Throws if it is not valid (for example a
   * dependency cycle); the board is then unchanged. With `coalesce`, consecutive edits of the same field
   * (typing in notes) become one operation.
   */
  enqueue(op, { undoable = true, coalesce = false } = {}) {
    if (!this.view) throw new Error("The board has not loaded yet.");
    const next = clone(this.view);
    applyOp(next, op);
    const errors = validateBoard(next);
    if (errors.length) throw new Error(errors[0]);

    const last = this.pending.at(-1);
    if (coalesce && last && last.type === "set" && op.type === "set" && last.id === op.id && last.field === op.field
      && !this.inFlight.has(last.opId)) {
      last.to = clone(op.to);
      last.at = op.at;
      if (sameValue(last.from, last.to)) {
        this.pending.pop();
        this.undoStack = this.undoStack.filter(e => e !== last);
      }
    } else {
      this.pending.push(op);
      if (undoable && inverseOp(op, op.at, op.by)) this.undoStack.push(op);
    }
    this.view = next;
    this.persist();
    this.notify();
    this.scheduleSave();
  }

  /**
   * Reverses the most recent undoable change made in this session. The reversal is saved like any other
   * change. If the card was changed elsewhere since, nothing is overwritten and false is returned.
   */
  undo(at, by) {
    const op = this.undoStack.pop();
    if (!op) return { ok: false, message: "Nothing to undo." };
    const inverse = inverseOp(op, at, by);
    try {
      this.enqueue(inverse, { undoable: false });
      return { ok: true, message: `Undone: ${describe(op)}.` };
    } catch (e) {
      this.notify();
      const why = e instanceof ConflictError ? "it was changed again since" : e.message;
      return { ok: false, message: `Could not undo ${describe(op)}: ${why}. Nothing was changed.` };
    }
  }

  get canUndo() {
    return this.undoStack.length > 0;
  }

  /**
   * Resolves a conflict. "theirs" keeps the version on GitHub and drops the local change; "mine" re-applies
   * the local change (optionally `value` for an edited field) on top of the current version.
   */
  resolveConflict(index, choice, at, by, value) {
    const [conflict] = this.conflicts.splice(index, 1);
    if (!conflict) return;
    if (choice === "mine") {
      const { op } = conflict;
      const card = findCard(this.view, op.id);
      if (card) {
        let fresh = null;
        if (op.type === "set") fresh = ops.set(this.view, op.id, op.field, value === undefined ? op.to : value, at, by);
        if (op.type === "move" && !card.archived) fresh = ops.move(this.view, op.id, op.to.status, Infinity, at, by, op.accepted);
        if (op.type === "archive" && card.status === "done") fresh = ops.archive(this.view, op.id, at, by);
        if (fresh) {
          try {
            this.enqueue(fresh);
          } catch (e) {
            this.conflicts.splice(index, 0, { ...conflict, reason: e.message });
          }
        }
      }
    }
    this.persist();
    this.notify();
    if (this.pending.length) this.scheduleSave(0);
  }

  // ---------------------------------------------------------------- saving

  scheduleSave(delay = this.debounceMs) {
    if (this.saveTimer) this.timers.clearTimeout(this.saveTimer);
    this.saveTimer = this.timers.setTimeout(() => {
      this.saveTimer = null;
      this.saveNow();
    }, delay);
  }

  /** Saves all pending operations now. Safe to call repeatedly; retries after a stale save. */
  saveNow() {
    if (this.saving) {
      this.saveAgain = true;
      return this.saving;
    }
    if (!this.pending.length || !this.store.canWrite || !this.base) {
      this.notify();
      return Promise.resolve();
    }
    this.saving = this.runSave().finally(() => {
      this.saving = null;
      this.inFlight.clear();
      this.notify();
      const again = this.saveAgain;
      this.saveAgain = false;
      if (this.pending.length && !this.error && this.store.canWrite) this.scheduleSave(again ? 0 : this.debounceMs);
    });
    this.notify();
    return this.saving;
  }

  async runSave() {
    try {
      for (let attempt = 0; attempt < MAX_SAVE_ATTEMPTS; attempt++) {
        const batch = this.pending.slice();
        batch.forEach(op => this.inFlight.add(op.opId));
        const result = rebase(this.base.board, batch);
        const rest = this.pending.slice(batch.length).map(op => remapOp(op, result.renamed));
        // Changes that no longer fit are set aside for the user; everything else is still saved.
        if (result.conflicts.length) this.conflicts.push(...result.conflicts);
        const errors = validateBoard(result.board);
        if (errors.length) throw new StoreError("invalid", `The board would become invalid, so it was not saved: ${errors[0]}`);
        try {
          const saved = await this.store.save(result.board, this.base.sha, commitMessage(result.applied, this.source));
          this.base = { sha: saved.sha, commit: saved.commit, board: result.board };
          this.pending = rest;
          this.recompute();
          this.error = null;
          this.lastSavedAt = new Date();
          this.persist();
          return;
        } catch (e) {
          if (!(e instanceof StoreError) || e.kind !== "stale") throw e;
          // Someone else saved first: take their version and re-apply ours on top (next loop).
          this.base = await this.store.load();
          this.pending = [...result.applied, ...rest];
          this.recompute();
          this.persist();
          this.notify();
          if (!this.pending.length) return;
        }
      }
      throw new StoreError("stale", "The board kept changing while saving. Your changes are kept; try again.");
    } catch (e) {
      if (!(e instanceof StoreError)) throw e;
      this.error = e;
      this.persist();
    }
  }

  /** Clears a failed save and tries again. */
  retry() {
    this.error = null;
    return this.saveNow();
  }

  // ---------------------------------------------------------------- local storage

  persist() {
    try {
      this.storage.setItem(this.key, JSON.stringify({ version: STORAGE_VERSION, base: this.base, pending: this.pending, conflicts: this.conflicts }));
    } catch {
      // Storage full or blocked: the board still works, but unsaved work would not survive a reload.
    }
  }

  readStorage() {
    try {
      const data = JSON.parse(this.storage.getItem(this.key) ?? "null");
      return data && data.version === STORAGE_VERSION ? data : null;
    } catch {
      return null;
    }
  }
}

function describe(op) {
  switch (op.type) {
    case "set": return `the ${op.field} change on ${op.id}`;
    case "move": return `the move of ${op.id}`;
    case "archive": return `archiving ${op.id}`;
    case "restore": return `restoring ${op.id}`;
    default: return `the change to ${op.id}`;
  }
}
