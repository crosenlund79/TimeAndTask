// Backup & Export: builds self-contained ZIP archives of cards (and screenshots) and restores them.
// Pure logic (no DOM, no IndexedDB, no network) so the browser and a future scheduled backup job share it.
//
// Archive layout (format "worktrace-board-backup", version 1):
//   README.txt        what the archive is and how to restore it
//   manifest.json     scope, card list, screenshot list with SHA-256, content hash
//   cards.json        the complete cards ({ schemaVersion, cards })
//   cards.md          the same cards, readable
//   attachments/<card id>/<attachment id>-<file name>
// Every archive restores on its own; dependencies on cards outside it are dropped on restore (and noted).

import { clone, findCard, formatId, idNumber, ops, renderBoardMarkdown, sameValue, SCHEMA_VERSION, validateBoard } from "./board-core.js";
import { createZip, readZip } from "./zip.js";

export const BACKUP_FORMAT = "worktrace-board-backup";
export const BACKUP_VERSION = 1;

/** "2026-10": the UTC month a card was created in (backups are grouped by it). */
export function creationMonth(card) {
  return card.createdAt.slice(0, 7);
}

/** Months that have cards, newest first, with their cards. */
export function monthsOf(board) {
  const months = new Map();
  for (const card of board.cards) {
    const m = creationMonth(card);
    if (!months.has(m)) months.set(m, []);
    months.get(m).push(card);
  }
  return [...months.entries()].sort((a, b) => (a[0] < b[0] ? 1 : -1)).map(([month, cards]) => ({ month, cards }));
}

/** scope: { kind: "all" } | { kind: "month", month: "2026-10" } | { kind: "cards", ids: [...] } */
export function selectCards(board, scope) {
  const cards = scope.kind === "month" ? board.cards.filter(c => creationMonth(c) === scope.month)
    : scope.kind === "cards" ? board.cards.filter(c => scope.ids.includes(c.id))
      : board.cards;
  return [...cards].sort((a, b) => idNumber(a.id) - idNumber(b.id));
}

export function backupFileName(scope, date = new Date()) {
  const day = date.toISOString().slice(0, 10);
  if (scope.kind === "month") return `worktrace-backup-${scope.month}.zip`;
  if (scope.kind === "cards") return `worktrace-backup-${scope.ids.length}-cards-${day}.zip`;
  return `worktrace-backup-all-${day}.zip`;
}

export async function sha256Hex(data) {
  const bytes = typeof data === "string" ? new TextEncoder().encode(data) : data;
  const hash = await globalThis.crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(hash)].map(b => b.toString(16).padStart(2, "0")).join("");
}

/**
 * Hash of everything a backup of these cards contains. Unchanged hash = nothing to back up again
 * (used to decide which months changed since the last successful backup).
 */
export async function contentHash(cards, attachmentHashes = []) {
  const canonical = JSON.stringify({ cards: [...cards].sort((a, b) => idNumber(a.id) - idNumber(b.id)), attachments: [...attachmentHashes].sort() });
  return sha256Hex(canonical);
}

function safeName(name) {
  return (name || "image").replace(/[^\w.-]+/g, "_").slice(0, 60);
}

/**
 * Builds a backup archive.
 * @param board        the board (cards are selected from it)
 * @param scope        see selectCards()
 * @param attachments  [{ meta: { id, cardUid, name, type, size, caption, createdAt }, bytes: Uint8Array }]
 * @returns { fileName, bytes, manifest }
 */
export async function buildBackup({ board, scope, attachments = [], date = new Date(), appUrl = "https://crosenlund79.github.io/TimeAndTask/" }) {
  const cards = selectCards(board, scope).map(clone);
  const byUid = new Map(cards.map(c => [c.uid, c]));
  const files = [];
  const attachmentEntries = [];
  for (const { meta, bytes } of attachments) {
    const card = byUid.get(meta.cardUid);
    if (!card) continue;
    const path = `attachments/${card.id}/${meta.id}-${safeName(meta.name)}`;
    attachmentEntries.push({
      id: meta.id, cardId: card.id, cardUid: meta.cardUid, path, name: meta.name, type: meta.type, bytes: bytes.length,
      caption: meta.caption ?? "", createdAt: meta.createdAt, sha256: await sha256Hex(bytes),
    });
    files.push({ path, data: bytes });
  }
  const manifest = {
    format: BACKUP_FORMAT,
    formatVersion: BACKUP_VERSION,
    createdAt: date.toISOString(),
    scope,
    board: { schemaVersion: SCHEMA_VERSION, nextId: board.nextId },
    cards: cards.map(c => ({ id: c.id, uid: c.uid, title: c.title, rev: c.rev, createdAt: c.createdAt, updatedAt: c.updatedAt, archived: c.archived })),
    attachments: attachmentEntries,
    contentHash: await contentHash(cards, attachmentEntries.map(a => a.sha256)),
  };
  const subset = { schemaVersion: SCHEMA_VERSION, nextId: board.nextId, cards };
  const scopeText = scope.kind === "month" ? `cards created in ${scope.month} (UTC)` : scope.kind === "cards" ? `${cards.length} selected cards` : "the complete board";
  files.unshift(
    { path: "README.txt", data: readme(scopeText, cards.length, attachmentEntries.length, date, appUrl) },
    { path: "manifest.json", data: JSON.stringify(manifest, null, 2) + "\n" },
    { path: "cards.json", data: JSON.stringify(subset, null, 2) + "\n" },
    { path: "cards.md", data: renderBoardMarkdown(subset, { appUrl }) },
  );
  return { fileName: backupFileName(scope, date), bytes: createZip(files, date), manifest };
}

function readme(scopeText, cardCount, imageCount, date, appUrl) {
  return [
    "WorkTrace board backup",
    "======================",
    "",
    `Contains ${scopeText}: ${cardCount} card(s) and ${imageCount} screenshot(s), as of ${date.toISOString()}.`,
    "Every card is complete: description, acceptance criteria, notes, evidence, links, comments, history,",
    "archived state and screenshots.",
    "",
    "Restore: open the board (" + appUrl + "), choose Backup & Export > Restore, and select this ZIP.",
    "The board shows what would change before anything is restored. Restoring never deletes cards or comments.",
    "",
    "Files: cards.json (data), cards.md (readable), attachments/ (screenshots), manifest.json (checksums).",
    "",
  ].join("\r\n");
}

/**
 * Reads and checks a backup archive. Returns { manifest, cards, attachments: [{ meta, bytes }], problems }.
 * Throws when the file is not a usable backup; `problems` lists issues that do not prevent restoring.
 */
export async function parseBackup(bytes) {
  const files = await readZip(bytes);
  const json = name => {
    const data = files.get(name);
    if (!data) throw new Error(`This ZIP is not a WorkTrace board backup (${name} is missing).`);
    try {
      return JSON.parse(new TextDecoder().decode(data));
    } catch {
      throw new Error(`${name} in the backup is not valid JSON.`);
    }
  };
  const manifest = json("manifest.json");
  if (manifest.format !== BACKUP_FORMAT) throw new Error("This ZIP is not a WorkTrace board backup.");
  if (manifest.formatVersion > BACKUP_VERSION) throw new Error("This backup was made by a newer version of the board. Reload the board and try again.");
  const data = json("cards.json");
  const cards = Array.isArray(data.cards) ? data.cards : [];
  if (data.schemaVersion !== 1 && data.schemaVersion !== SCHEMA_VERSION) throw new Error(`Unknown card format (schema ${data.schemaVersion}).`);

  // Validate the cards as a board of their own; dependencies on cards outside the archive are allowed.
  const ids = new Set(cards.map(c => c.id));
  const checkable = { schemaVersion: SCHEMA_VERSION, nextId: Math.max(0, ...cards.map(c => idNumber(c.id))) + 1,
    cards: cards.map(c => ({ ...c, blockedBy: (c.blockedBy ?? []).filter(id => ids.has(id)) })) };
  const errors = validateBoard(checkable);
  if (errors.length) throw new Error(`The cards in this backup are not valid: ${errors.slice(0, 3).join("; ")}`);

  const problems = [];
  const attachments = [];
  for (const meta of manifest.attachments ?? []) {
    const fileBytes = files.get(meta.path);
    if (!fileBytes) {
      problems.push(`Screenshot ${meta.name} of ${meta.cardId} is missing from the ZIP.`);
      continue;
    }
    if (await sha256Hex(fileBytes) !== meta.sha256) {
      problems.push(`Screenshot ${meta.name} of ${meta.cardId} is damaged (checksum mismatch); it will not be restored.`);
      continue;
    }
    attachments.push({ meta: { id: meta.id, cardUid: meta.cardUid, name: meta.name, type: meta.type, size: meta.bytes, caption: meta.caption, createdAt: meta.createdAt }, bytes: fileBytes });
  }
  const listed = new Set((manifest.cards ?? []).map(c => c.uid));
  if (cards.some(c => !listed.has(c.uid))) problems.push("The manifest does not list every card; the backup may have been edited.");
  return { manifest, cards, attachments, problems };
}

const COMPARED = ["title", "category", "priority", "status", "rank", "archived", "description", "acceptance", "notes", "evidence", "blockedBy", "links"];
const FIELD_LABELS = { title: "title", category: "category", priority: "priority", status: "status", rank: "order", archived: "archived",
  description: "description", acceptance: "acceptance criteria", notes: "notes", evidence: "evidence", blockedBy: "dependencies", links: "links" };

/**
 * What restoring each backup card would do:
 *   new       not on the board → added (renumbered if its id is used by another card)
 *   same      identical → nothing to do
 *   changed   differs → `fields` lists what would change; `boardNewer` warns that the board was edited after the backup
 * Comments missing on the board count as a change (they are added; none are removed).
 */
export function planRestore(board, cards) {
  return cards.map(card => {
    const current = board.cards.find(c => c.uid === card.uid) ?? null;
    if (!current) {
      const taken = findCard(board, card.id);
      return { card, current: null, state: "new", fields: [], idTaken: !!taken, boardNewer: false };
    }
    const fields = COMPARED.filter(f => f !== "rank" && !sameValue(current[f], card[f])).map(f => FIELD_LABELS[f]);
    const missingComments = card.comments.filter(c => !current.comments.some(x => x.id === c.id)).length;
    if (missingComments) fields.push(`${missingComments} comment(s)`);
    return {
      card, current, state: fields.length ? "changed" : "same", fields, idTaken: false,
      boardNewer: fields.length > 0 && current.updatedAt > card.updatedAt,
    };
  });
}

/** Operations that restore the chosen plan items (see planRestore). */
export function restoreOps(board, items, at, by, source) {
  let next = board.nextId;
  return items.filter(i => i.state !== "same").map(i => {
    const op = ops.restoreFromBackup(board, i.card, at, by, source);
    if (i.state === "new" && i.idTaken) {
      // Its number is used by another card now: give it the next free number (ids are never reused).
      op.id = formatId(next++);
      op.card.id = op.id;
    } else if (i.state === "new") next = Math.max(next, idNumber(op.id) + 1);
    return op;
  });
}
