// WorkTrace board — shared core used by the browser app and the CLI (tools/board.mjs).
// Pure functions only: no DOM, no network, no Node APIs. Schema: schema/board.schema.json (version 1).

export const SCHEMA_VERSION = 2;
/** The five Kanban columns. */
export const STATUSES = ["backlog", "todo", "doing", "review", "done"];
/** Captured, not yet organised. A list beside the board, not a column. */
export const INBOX = "inbox";
export const ALL_STATUSES = [INBOX, ...STATUSES];
export const STATUS_LABELS = { inbox: "Inbox", backlog: "Backlog", todo: "To do", doing: "In progress", review: "Review / Test", done: "Done" };
export const CATEGORIES = ["feature", "bug", "architecture", "testing", "idea"];
export const CATEGORY_LABELS = { feature: "Feature", bug: "Bug", architecture: "Architecture", testing: "Testing", idea: "Idea" };
export const PRIORITIES = ["low", "normal", "high", "critical"];
export const PRIORITY_LABELS = { low: "Low", normal: "Normal", high: "High", critical: "Critical" };
/** Categories whose work the owner must accept before it is Done. */
export const USER_FACING = new Set(["feature", "bug"]);

/** Uncategorised cards might be user-facing too, so they also need the owner's acceptance. */
export function needsOwnerAcceptance(card) {
  return card.category === null || USER_FACING.has(card.category);
}
/** Fields a `set` operation may change. Status, order and archiving have their own operations. */
export const EDITABLE_FIELDS = ["title", "description", "category", "priority", "acceptance", "notes", "evidence", "blockedBy", "links"];
export const HISTORY_TYPES = ["created", "migrated", "status", "archived", "restored", "imported"];

const CARD_KEYS = ["id", "uid", "title", "category", "priority", "status", "rank", "archived", "archivedAt", "description",
  "acceptance", "notes", "evidence", "blockedBy", "links", "comments", "history", "createdAt", "updatedAt", "rev"];
const ID_RE = /^WT-(\d{3,})$/;
const RANK_RE = /^[0-9a-z]*[1-9a-z]$/;
const ISO_RE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/;
const URL_RE = /^https?:\/\/[^\s<>"']+$/;

export class ConflictError extends Error {
  constructor(op, reason, current) {
    super(reason);
    this.name = "ConflictError";
    this.op = op;
    this.reason = reason;
    this.current = current;
  }
}

// ------------------------------------------------------------------ ids, ranks, helpers

export function idNumber(id) {
  const m = ID_RE.exec(id ?? "");
  return m ? Number(m[1]) : NaN;
}

export function formatId(n) {
  return `WT-${String(n).padStart(3, "0")}`;
}

const DIGITS = "0123456789abcdefghijklmnopqrstuvwxyz";

/**
 * A rank strictly between `a` and `b` (either may be null for "start"/"end"). Ranks are base-36 fractions
 * written as strings that never end in "0", so there is always room between two ranks.
 */
export function rankBetween(a, b) {
  a = a ?? "";
  b = b ?? null;
  if (b !== null && a >= b) {
    throw new Error(`rankBetween: ${JSON.stringify(a)} is not before ${JSON.stringify(b)}`);
  }
  return midpoint(a, b);
}

function midpoint(a, b) {
  if (b !== null) {
    let n = 0;
    while ((a[n] ?? "0") === b[n]) n++;
    if (n > 0) return b.slice(0, n) + midpoint(a.slice(n), b.slice(n));
  }
  const da = a ? DIGITS.indexOf(a[0]) : 0;
  const db = b !== null ? DIGITS.indexOf(b[0]) : DIGITS.length;
  if (db - da > 1) return DIGITS[Math.round((da + db) / 2)];
  if (b !== null && b.length > 1) return b.slice(0, 1);
  return DIGITS[da] + midpoint(a.slice(1), null);
}

export function isValidRank(rank) {
  return typeof rank === "string" && RANK_RE.test(rank);
}

export function compareCards(a, b) {
  if (a.rank !== b.rank) return a.rank < b.rank ? -1 : 1;
  return idNumber(a.id) - idNumber(b.id);
}

export function clone(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

export function sameValue(a, b) {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

export function newUid() {
  return globalThis.crypto.randomUUID();
}

export function nowIso(date = new Date()) {
  return date.toISOString().replace(/\.\d{3}Z$/, "Z");
}

// ------------------------------------------------------------------ board queries

export function emptyBoard() {
  return { schemaVersion: SCHEMA_VERSION, nextId: 1, cards: [] };
}

export function findCard(board, id) {
  return board.cards.find(c => c.id === id) ?? null;
}

/** Cards of one status in display order. Archived cards are excluded unless asked for. */
export function columnCards(board, status, { archived = false } = {}) {
  return board.cards.filter(c => c.status === status && c.archived === archived).sort(compareCards);
}

/** Ids this card waits for that are not finished yet (Done, archived or not). */
export function openBlockers(board, card) {
  return (card.blockedBy ?? []).filter(id => findCard(board, id)?.status !== "done");
}

/** Rank for placing a card at `index` among `others` (the column without the moved card). */
export function rankAt(others, index) {
  const before = index > 0 ? others[index - 1].rank : null;
  const after = index < others.length ? others[index].rank : null;
  if (before !== null && after !== null && before >= after) {
    // Two cards share a rank (concurrent placement): place just after `before`.
    return rankBetween(before, null);
  }
  return rankBetween(before, after);
}

/** True if adding `blockedBy` to card `id` would create a self-reference or a cycle. */
export function wouldCycle(board, id, blockedBy) {
  const edges = new Map(board.cards.map(c => [c.id, c.id === id ? blockedBy : (c.blockedBy ?? [])]));
  const seen = new Set();
  const stack = [...blockedBy];
  while (stack.length) {
    const next = stack.pop();
    if (next === id) return true;
    if (seen.has(next)) continue;
    seen.add(next);
    stack.push(...(edges.get(next) ?? []));
  }
  return false;
}

// ------------------------------------------------------------------ validation

/** Returns a list of human-readable problems; empty when the board is valid. */
export function validateBoard(board) {
  const errors = [];
  const err = (path, message) => errors.push(`${path}: ${message}`);
  if (!board || typeof board !== "object" || Array.isArray(board)) return ["board: must be an object"];
  for (const key of Object.keys(board)) {
    if (!["schemaVersion", "nextId", "cards"].includes(key)) err("board", `unknown property "${key}"`);
  }
  if (board.schemaVersion !== SCHEMA_VERSION) err("schemaVersion", `must be ${SCHEMA_VERSION}`);
  if (!Number.isInteger(board.nextId) || board.nextId < 1) err("nextId", "must be a positive integer");
  if (!Array.isArray(board.cards)) return [...errors, "cards: must be an array"];

  const ids = new Set();
  const uids = new Set();
  let maxId = 0;
  board.cards.forEach((card, i) => {
    const p = `cards[${i}]${card && card.id ? ` (${card.id})` : ""}`;
    if (!card || typeof card !== "object") return err(p, "must be an object");
    for (const key of Object.keys(card)) if (!CARD_KEYS.includes(key)) err(p, `unknown property "${key}"`);
    for (const key of CARD_KEYS) if (!(key in card)) err(p, `missing "${key}"`);
    const n = idNumber(card.id);
    if (Number.isNaN(n)) err(p, "id must look like WT-001");
    else {
      if (ids.has(card.id)) err(p, `duplicate id ${card.id}`);
      ids.add(card.id);
      maxId = Math.max(maxId, n);
    }
    if (typeof card.uid !== "string" || !card.uid) err(p, "uid must be a non-empty string");
    else if (uids.has(card.uid)) err(p, "duplicate uid");
    else uids.add(card.uid);
    if (typeof card.title !== "string" || !card.title.trim()) err(p, "title must not be empty");
    else if (card.title.length > 200) err(p, "title is longer than 200 characters");
    if (card.category !== null && !CATEGORIES.includes(card.category)) err(p, `category must be one of ${CATEGORIES.join(", ")} or null`);
    if (!PRIORITIES.includes(card.priority)) err(p, `priority must be one of ${PRIORITIES.join(", ")}`);
    if (!ALL_STATUSES.includes(card.status)) err(p, `status must be one of ${ALL_STATUSES.join(", ")}`);
    if (!isValidRank(card.rank)) err(p, "rank must be a base-36 string not ending in 0");
    if (typeof card.archived !== "boolean") err(p, "archived must be true or false");
    if (card.archived) {
      if (card.status !== "done" && card.status !== INBOX) err(p, "only Done and Inbox cards can be archived");
      if (!ISO_RE.test(card.archivedAt ?? "")) err(p, "archivedAt must be a UTC timestamp when archived");
    } else if (card.archivedAt !== null) err(p, "archivedAt must be null when not archived");
    for (const f of ["description", "notes", "evidence"]) if (typeof card[f] !== "string") err(p, `${f} must be a string`);
    if (!Array.isArray(card.acceptance) || card.acceptance.some(a => typeof a !== "string" || !a.trim())) {
      err(p, "acceptance must be a list of non-empty strings");
    }
    if (!Array.isArray(card.links) || card.links.some(l => !l || typeof l.label !== "string" || !l.label.trim() || !URL_RE.test(l.url ?? ""))) {
      err(p, "links must be {label, url} with an http(s) url");
    }
    if (!Array.isArray(card.comments)) err(p, "comments must be a list");
    else {
      const cids = new Set();
      card.comments.forEach((c, j) => {
        const cp = `${p}.comments[${j}]`;
        if (!c || typeof c.id !== "string" || !c.id) return err(cp, "id is required");
        if (cids.has(c.id)) err(cp, "duplicate comment id");
        cids.add(c.id);
        if (typeof c.author !== "string" || !c.author) err(cp, "author is required");
        if (!ISO_RE.test(c.at ?? "")) err(cp, "at must be a UTC timestamp");
        if (typeof c.text !== "string" || !c.text.trim()) err(cp, "text must not be empty");
      });
    }
    if (!Array.isArray(card.history)) err(p, "history must be a list");
    else card.history.forEach((h, j) => {
      const hp = `${p}.history[${j}]`;
      if (!h || typeof h.id !== "string") return err(hp, "id is required");
      if (!HISTORY_TYPES.includes(h.type)) err(hp, `type must be one of ${HISTORY_TYPES.join(", ")}`);
      if (!ISO_RE.test(h.at ?? "")) err(hp, "at must be a UTC timestamp");
      if (typeof h.by !== "string" || !h.by) err(hp, "by is required");
    });
    if (!ISO_RE.test(card.createdAt ?? "")) err(p, "createdAt must be a UTC timestamp");
    if (!ISO_RE.test(card.updatedAt ?? "")) err(p, "updatedAt must be a UTC timestamp");
    if (!Number.isInteger(card.rev) || card.rev < 1) err(p, "rev must be a positive integer");
  });

  board.cards.forEach(card => {
    if (!Array.isArray(card?.blockedBy)) return err(card?.id ?? "card", "blockedBy must be a list");
    for (const dep of card.blockedBy) {
      if (dep === card.id) err(card.id, "cannot be blocked by itself");
      else if (!ids.has(dep)) err(card.id, `blocked by unknown card ${dep}`);
    }
    if (new Set(card.blockedBy).size !== card.blockedBy.length) err(card.id, "blockedBy lists a card twice");
  });
  if (errors.length === 0) {
    for (const card of board.cards) {
      if (card.blockedBy.length && wouldCycle(board, card.id, card.blockedBy)) err(card.id, "blocking relationships form a cycle");
    }
  }
  if (Number.isInteger(board.nextId) && board.nextId <= maxId) err("nextId", `must be greater than the highest card number (${maxId})`);
  return errors;
}

export function assertValid(board) {
  const errors = validateBoard(board);
  if (errors.length) throw new Error(`Invalid board:\n- ${errors.join("\n- ")}`);
  return board;
}

// ------------------------------------------------------------------ serialisation

function normalizeCard(card) {
  const out = {};
  for (const key of CARD_KEYS) out[key] = card[key];
  return out;
}

/** Stable, diff-friendly JSON (fixed key order, 2-space indent, trailing newline). */
export function serializeBoard(board) {
  const ordered = {
    schemaVersion: board.schemaVersion,
    nextId: board.nextId,
    cards: [...board.cards].sort((a, b) => idNumber(a.id) - idNumber(b.id)).map(normalizeCard),
  };
  return JSON.stringify(ordered, null, 2) + "\n";
}

/** Parses board.json, upgrading older versions in memory (they are written back as the current version). */
export function parseBoard(text) {
  return upgradeBoard(JSON.parse(text));
}

/** Version 1 → 2 added the Inbox status and optional categories; every version-1 board is valid as version 2. */
export function upgradeBoard(board) {
  if (board && board.schemaVersion === 1) return { ...board, schemaVersion: 2 };
  return board;
}

// ------------------------------------------------------------------ cards and operations

export function newCard({ id, title, category = "feature", priority = "normal", status = "backlog", rank, description = "",
  acceptance = [], notes = "", evidence = "", blockedBy = [], links = [] }, at, by) {
  return {
    id, uid: newUid(), title: title.trim(), category, priority, status, rank, archived: false, archivedAt: null,
    description, acceptance, notes, evidence, blockedBy, links, comments: [],
    history: [{ id: newUid(), type: "created", at, by, to: status }],
    createdAt: at, updatedAt: at, rev: 1,
  };
}

/** Builds operations. Every operation records what it expects to replace, so it can be checked after a rebase. */
export const ops = {
  create(board, fields, at, by) {
    const id = formatId(board.nextId);
    const status = fields.status ?? "backlog";
    const rank = rankBetween(columnCards(board, status).at(-1)?.rank ?? null, null);
    return { opId: newUid(), type: "create", id, at, by, card: newCard({ ...fields, id, status, rank }, at, by) };
  },
  set(board, id, field, value, at, by) {
    if (!EDITABLE_FIELDS.includes(field)) throw new Error(`${field} cannot be edited`);
    const card = mustFind(board, id);
    return { opId: newUid(), type: "set", id, at, by, field, from: clone(card[field]), to: clone(value) };
  },
  /** Moves a card to `status` at `index` within that column (index counts the other cards). */
  move(board, id, status, index, at, by, accepted = null) {
    const card = mustFind(board, id);
    const others = columnCards(board, status).filter(c => c.id !== id);
    const i = Math.max(0, Math.min(index ?? others.length, others.length));
    const rank = rankAt(others, i);
    return { opId: newUid(), type: "move", id, at, by, from: { status: card.status, rank: card.rank }, to: { status, rank }, accepted };
  },
  comment(board, id, text, at, by) {
    mustFind(board, id);
    return { opId: newUid(), type: "comment", id, at, by, comment: { id: newUid(), author: by, at, text: text.trim() } };
  },
  /** Restore `card` from a backup. `fromRev` is the board's revision of that card when the preview was shown. */
  restoreFromBackup(board, card, at, by, source) {
    const existing = board.cards.find(c => c.uid === card.uid);
    return { opId: newUid(), type: "import", id: existing?.id ?? card.id, at, by, source, fromRev: existing?.rev ?? null, card: clone(card) };
  },
  archive(board, id, at, by) {
    mustFind(board, id);
    return { opId: newUid(), type: "archive", id, at, by };
  },
  restore(board, id, at, by) {
    mustFind(board, id);
    return { opId: newUid(), type: "restore", id, at, by };
  },
};

function mustFind(board, id) {
  const card = findCard(board, id);
  if (!card) throw new Error(`No card ${id}`);
  return card;
}

function touch(card, op) {
  card.updatedAt = op.at;
  card.rev += 1;
}

/**
 * Applies one operation to `board` in place. Returns "applied" or "noop" (already applied, e.g. a retried
 * save). Throws ConflictError when the card changed in a way the operation did not expect.
 */
export function applyOp(board, op) {
  if (op.type === "import") return applyImport(board, op);
  if (op.type === "create") {
    const existing = findCard(board, op.id);
    if (existing && existing.uid === op.card.uid) return "noop";
    if (board.cards.some(c => c.uid === op.card.uid)) return "noop";
    if (existing) throw new ConflictError(op, "id-taken", existing);
    board.cards.push(clone(op.card));
    board.nextId = Math.max(board.nextId, idNumber(op.id) + 1);
    return "applied";
  }

  const card = findCard(board, op.id);
  if (!card) throw new ConflictError(op, `${op.id} no longer exists`, null);

  switch (op.type) {
    case "set": {
      const current = card[op.field];
      if (sameValue(current, op.to)) return "noop";
      if (!sameValue(current, op.from)) throw new ConflictError(op, `${op.field} was changed elsewhere`, clone(current));
      if (op.field === "blockedBy") {
        const missing = op.to.find(dep => !findCard(board, dep));
        if (missing) throw new ConflictError(op, `blocked by unknown card ${missing}`, clone(current));
        if (wouldCycle(board, card.id, op.to)) throw new ConflictError(op, "blocking relationships would form a cycle", clone(current));
      }
      card[op.field] = clone(op.to);
      touch(card, op);
      return "applied";
    }
    case "move": {
      if (card.archived) throw new ConflictError(op, `${op.id} was archived elsewhere`, { status: card.status, archived: true });
      if (card.status === op.to.status && card.rank === op.to.rank) return "noop";
      if (card.status !== op.from.status && card.status !== op.to.status) {
        throw new ConflictError(op, "status was changed elsewhere", { status: card.status, rank: card.rank });
      }
      const statusChanged = card.status !== op.to.status;
      const from = card.status;
      card.status = op.to.status;
      card.rank = op.to.rank;
      if (statusChanged) {
        const entry = { id: op.opId, type: "status", at: op.at, by: op.by, from, to: op.to.status };
        if (op.accepted) entry.accepted = op.accepted;
        card.history.push(entry);
      }
      touch(card, op);
      return "applied";
    }
    case "comment": {
      if (card.comments.some(c => c.id === op.comment.id)) return "noop";
      card.comments.push(clone(op.comment));
      touch(card, op);
      return "applied";
    }
    case "archive": {
      if (card.archived) return "noop";
      if (card.status !== "done" && card.status !== INBOX) throw new ConflictError(op, `${op.id} is no longer Done`, { status: card.status });
      card.archived = true;
      card.archivedAt = op.at;
      card.history.push({ id: op.opId, type: "archived", at: op.at, by: op.by });
      touch(card, op);
      return "applied";
    }
    case "restore": {
      if (!card.archived) return "noop";
      card.archived = false;
      card.archivedAt = null;
      card.history.push({ id: op.opId, type: "restored", at: op.at, by: op.by, to: card.status });
      touch(card, op);
      return "applied";
    }
    default:
      throw new Error(`Unknown operation ${op.type}`);
  }
}

/** Card fields a restore copies from the backup (identity, comments and history are merged separately). */
const RESTORED_FIELDS = ["title", "category", "priority", "status", "rank", "archived", "archivedAt", "description",
  "acceptance", "notes", "evidence", "blockedBy", "links"];

/**
 * Restores one card from a backup ("import" operation). A card already on the board (same uid) gets the backup's
 * fields, but only if it is still the revision the user previewed; comments and history are merged, never lost.
 * A card that is not on the board is added (renumbered if its id is taken by another card).
 */
function applyImport(board, op) {
  const backup = op.card;
  const existing = board.cards.find(c => c.uid === backup.uid);
  const entry = { id: op.opId, type: "imported", at: op.at, by: op.by, note: op.source ?? "backup" };
  const knownIds = new Set(board.cards.map(c => c.id));
  const deps = list => list.filter(id => knownIds.has(id) && id !== (existing?.id ?? op.id));

  if (existing) {
    if (op.fromRev !== null && existing.rev !== op.fromRev) {
      throw new ConflictError(op, `${existing.id} changed after the restore was previewed`, { rev: existing.rev });
    }
    const alreadyThere = RESTORED_FIELDS.every(f => sameValue(existing[f], backup[f]))
      && backup.comments.every(c => existing.comments.some(x => x.id === c.id));
    if (alreadyThere) return "noop";
    for (const f of RESTORED_FIELDS) existing[f] = clone(backup[f]);
    existing.blockedBy = deps(existing.blockedBy);
    if (wouldCycle(board, existing.id, existing.blockedBy)) existing.blockedBy = [];
    existing.comments = mergeById(existing.comments, backup.comments);
    existing.history = mergeById(existing.history, backup.history).concat(entry);
    touch(existing, op);
    return "applied";
  }

  if (findCard(board, op.id)) throw new ConflictError(op, "id-taken", findCard(board, op.id));
  const card = clone(backup);
  card.id = op.id;
  card.blockedBy = deps(card.blockedBy);
  card.history = [...card.history, entry];
  card.updatedAt = op.at;
  card.rev = (card.rev ?? 0) + 1;
  board.cards.push(card);
  board.nextId = Math.max(board.nextId, idNumber(op.id) + 1);
  return "applied";
}

function mergeById(current, incoming) {
  const seen = new Set(current.map(x => x.id));
  return [...current, ...incoming.filter(x => !seen.has(x.id))].sort((a, b) => (a.at < b.at ? -1 : a.at > b.at ? 1 : 0));
}

/** Renames card ids in an operation (used when a new card's id was taken by someone else meanwhile). */
export function remapOp(op, map) {
  const r = id => map.get(id) ?? id;
  const out = clone(op);
  out.id = r(out.id);
  if (out.type === "create" || out.type === "import") {
    out.card.id = out.id;
    out.card.blockedBy = out.card.blockedBy.map(r);
  }
  if (out.type === "set" && out.field === "blockedBy") {
    out.from = out.from.map(r);
    out.to = out.to.map(r);
  }
  return out;
}

/**
 * Applies pending operations on top of `base` (the latest saved board). Operations that no longer fit are
 * returned as conflicts and left out; nothing is overwritten silently. New cards whose id was taken are
 * renumbered. Returns { board, applied, conflicts, renamed }.
 */
export function rebase(base, pending) {
  const board = clone(base);
  const applied = [];
  const conflicts = [];
  const renamed = new Map();
  for (const original of pending) {
    let op = renamed.size ? remapOp(original, renamed) : original;
    try {
      applyOp(board, op);
      applied.push(op);
    } catch (e) {
      if (!(e instanceof ConflictError)) throw e;
      if (e.reason === "id-taken") {
        const newId = formatId(board.nextId);
        renamed.set(op.id, newId);
        op = remapOp(op, new Map([[op.id, newId]]));
        applyOp(board, op);
        applied.push(op);
      } else {
        conflicts.push({ op, reason: e.reason, current: e.current });
      }
    }
  }
  return { board, applied, conflicts, renamed };
}

/** The operation that reverses `op`, or null if it cannot be undone (comments and new cards). */
export function inverseOp(op, at, by) {
  const base = { opId: newUid(), id: op.id, at, by, undoOf: op.opId };
  switch (op.type) {
    case "set": return { ...base, type: "set", field: op.field, from: clone(op.to), to: clone(op.from) };
    case "move": return { ...base, type: "move", from: clone(op.to), to: clone(op.from), accepted: null };
    case "archive": return { ...base, type: "restore" };
    case "restore": return { ...base, type: "archive" };
    default: return null;
  }
}

/** One line describing an operation, for commit messages and the activity log. */
export function describeOp(op) {
  switch (op.type) {
    case "create": return `add ${op.id}`;
    case "set": return `edit ${op.id} ${op.field}`;
    case "move": return op.from.status === op.to.status ? `reorder ${op.id}` : `move ${op.id} to ${STATUS_LABELS[op.to.status]}`;
    case "comment": return `comment on ${op.id}`;
    case "archive": return `archive ${op.id}`;
    case "restore": return `restore ${op.id}`;
    case "import": return `restore ${op.id} from backup`;
    default: return op.type;
  }
}

export function commitMessage(opsList, source) {
  const parts = [...new Set(opsList.map(describeOp))];
  const summary = parts.length > 4 ? `${parts.slice(0, 4).join("; ")}; +${parts.length - 4} more` : parts.join("; ");
  return `board: ${summary || "update"} (${source})`;
}

// ------------------------------------------------------------------ Markdown view (docs/BOARD.md)

const MD_ORDER = ["doing", "review", "todo", "backlog", "done"];

/** Deterministic Markdown rendering of the board. Generated; never edited by hand. */
export function renderBoardMarkdown(board, { appUrl = "https://crosenlund79.github.io/TimeAndTask/" } = {}) {
  const active = board.cards.filter(c => !c.archived);
  const archived = board.cards.filter(c => c.archived).sort((a, b) => idNumber(a.id) - idNumber(b.id));
  const lines = [];
  lines.push("# WorkTrace board", "");
  lines.push(`> Generated from \`docs/board.json\` on the \`board\` branch. Do not edit this file: change cards on the`);
  lines.push(`> [interactive board](${appUrl}) or with \`node tools/board.mjs\`.`, "");
  lines.push(`| Inbox | ${STATUSES.map(s => STATUS_LABELS[s]).join(" | ")} | Archived |`);
  lines.push(`|---|${STATUSES.map(() => "---").join("|")}|---|`);
  lines.push(`| ${[INBOX, ...STATUSES].map(s => active.filter(c => c.status === s).length).join(" | ")} | ${archived.length} |`, "");

  const inbox = columnCards(board, INBOX).reverse();
  lines.push(`## Inbox (${inbox.length})`, "");
  if (inbox.length === 0) lines.push("_Empty._", "");
  else lines.push(...inbox.map(c => `- **${c.id}** · ${c.title} · captured ${c.createdAt.slice(0, 10)}`), "");

  for (const status of MD_ORDER) {
    const cards = columnCards(board, status);
    lines.push(`## ${STATUS_LABELS[status]}`, "");
    if (cards.length === 0) {
      lines.push("_None._", "");
      continue;
    }
    for (const card of cards) {
      if (status === "done") {
        lines.push(`- **${card.id}** · ${card.title}${card.category ? ` · \`${card.category}\`` : ""}`);
        continue;
      }
      lines.push(`### ${card.id} · ${card.title}`, "");
      const meta = [card.category ? `\`${card.category}\`` : "uncategorised", `priority ${PRIORITY_LABELS[card.priority]}`];
      if (card.blockedBy.length) meta.push(`blocked by ${card.blockedBy.join(", ")}`);
      lines.push(meta.join(" · "), "");
      if (card.description.trim()) lines.push(card.description.trim(), "");
      if (card.acceptance.length) {
        lines.push("**Acceptance criteria**", "", ...card.acceptance.map(a => `- ${a}`), "");
      }
      if (card.notes.trim()) lines.push("**Notes**", "", card.notes.trim(), "");
      if (card.evidence.trim()) lines.push("**Evidence**", "", card.evidence.trim(), "");
      if (card.links.length) lines.push("**Links**", "", ...card.links.map(l => `- [${l.label}](${l.url})`), "");
      if (card.comments.length) {
        lines.push(`**Comments (${card.comments.length})**`, "");
        for (const c of card.comments) {
          const [first, ...rest] = c.text.trim().split("\n");
          lines.push(`- ${c.at.slice(0, 16).replace("T", " ")} · ${c.author}: ${first}`, ...rest.map(l => `  ${l}`));
        }
        lines.push("");
      }
    }
    if (status === "done") lines.push("");
  }

  lines.push(`## Archived (${archived.length})`, "");
  if (archived.length === 0) lines.push("_None._", "");
  else lines.push(...archived.map(c => `- **${c.id}** · ${c.title} · archived ${c.archivedAt.slice(0, 10)}`), "");
  return lines.join("\n").replace(/\n{3,}/g, "\n\n").trimEnd() + "\n";
}
