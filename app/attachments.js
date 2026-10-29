// Screenshots, stored in this browser only (IndexedDB) until a private storage location is approved (WT-047).
// They are never uploaded: the repository and the board data are public. Backups (Backup & Export) include them.
// Keyed by the card's uid, which never changes (a card's WT number can change on restore).

const DB_NAME = "worktrace-board";
const STORE = "attachments";
export const MAX_IMAGE_BYTES = 20 * 1024 * 1024;

export class AttachmentStore {
  constructor({ onChange = () => {} } = {}) {
    this.onChange = onChange;
    this.index = new Map(); // cardUid → [meta]
    this.urls = new Map(); // attachment id → object URL
    this.available = typeof indexedDB !== "undefined";
    this.db = null;
  }

  async open() {
    if (!this.available) return;
    try {
      this.db = await new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(STORE, { keyPath: "id" }).createIndex("cardUid", "cardUid");
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
      const records = await this.tx("readonly", s => s.getAll());
      this.index.clear();
      for (const r of records) this.addToIndex(meta(r));
      this.onChange();
    } catch {
      this.available = false;
    }
  }

  tx(mode, run) {
    return new Promise((resolve, reject) => {
      const transaction = this.db.transaction(STORE, mode);
      const request = run(transaction.objectStore(STORE));
      transaction.oncomplete = () => resolve(request?.result);
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error ?? new Error("Storage refused the screenshot."));
    });
  }

  addToIndex(m) {
    const list = this.index.get(m.cardUid) ?? [];
    list.push(m);
    list.sort((a, b) => (a.createdAt < b.createdAt ? -1 : 1));
    this.index.set(m.cardUid, list);
  }

  forCard(cardUid) {
    return this.index.get(cardUid) ?? [];
  }

  get total() {
    let n = 0;
    for (const list of this.index.values()) n += list.length;
    return n;
  }

  /** Adds an image file to a card. Throws with a readable message for non-images, oversized files or full storage. */
  async add(cardUid, file, caption = "") {
    if (!this.db) throw new Error("Screenshots need browser storage, which is not available here (private window?).");
    if (!/^image\//.test(file.type)) throw new Error(`${file.name || "That file"} is not an image.`);
    if (file.size > MAX_IMAGE_BYTES) throw new Error(`${file.name || "The image"} is larger than 20 MB.`);
    const record = {
      id: globalThis.crypto.randomUUID(), cardUid, name: file.name || `screenshot-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.png`,
      type: file.type, size: file.size, caption, createdAt: new Date().toISOString(), blob: file,
    };
    await this.tx("readwrite", s => s.put(record));
    this.addToIndex(meta(record));
    this.onChange();
    return meta(record);
  }

  /** Restores a screenshot from a backup (skipped if this browser already has it). */
  async importAttachment(m, bytes) {
    if (!this.db) throw new Error("Screenshots need browser storage, which is not available here.");
    if (this.forCard(m.cardUid).some(x => x.id === m.id)) return false;
    const record = { ...m, size: bytes.length, blob: new Blob([bytes], { type: m.type }) };
    await this.tx("readwrite", s => s.put(record));
    this.addToIndex(meta(record));
    this.onChange();
    return true;
  }

  async url(id) {
    if (this.urls.has(id)) return this.urls.get(id);
    const record = await this.tx("readonly", s => s.get(id));
    if (!record) return null;
    const url = URL.createObjectURL(record.blob);
    this.urls.set(id, url);
    return url;
  }

  async setCaption(id, caption) {
    const record = await this.tx("readonly", s => s.get(id));
    if (!record) return;
    record.caption = caption;
    await this.tx("readwrite", s => s.put(record));
    const m = this.forCard(record.cardUid).find(x => x.id === id);
    if (m) m.caption = caption;
    this.onChange();
  }

  async remove(id) {
    const record = await this.tx("readonly", s => s.get(id));
    if (!record) return;
    await this.tx("readwrite", s => s.delete(id));
    this.index.set(record.cardUid, this.forCard(record.cardUid).filter(x => x.id !== id));
    if (this.urls.has(id)) URL.revokeObjectURL(this.urls.get(id));
    this.urls.delete(id);
    this.onChange();
  }

  /** Every screenshot with its bytes, for backups. */
  async all() {
    if (!this.db) return [];
    const records = await this.tx("readonly", s => s.getAll());
    return Promise.all(records.map(async r => ({ meta: meta(r), bytes: new Uint8Array(await r.blob.arrayBuffer()) })));
  }
}

function meta(r) {
  return { id: r.id, cardUid: r.cardUid, name: r.name, type: r.type, size: r.size, caption: r.caption ?? "", createdAt: r.createdAt };
}

/** Image files from a paste or drop event (ignores text and non-image files). */
export function imagesFrom(dataTransfer) {
  if (!dataTransfer) return [];
  const files = [...(dataTransfer.files ?? [])];
  if (!files.length) {
    for (const item of dataTransfer.items ?? []) {
      if (item.kind === "file") {
        const f = item.getAsFile();
        if (f) files.push(f);
      }
    }
  }
  return files.filter(f => /^image\//.test(f.type));
}
