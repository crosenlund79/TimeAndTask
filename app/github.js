// GitHub storage for the board: reads and writes docs/board.json on one branch through the REST API.
// Saves are version-checked with the file's blob SHA (Contents API): GitHub rejects a save based on an
// old version with 409, so nothing is overwritten. Error messages never contain the token.

import { parseBoard, serializeBoard } from "./board-core.js";

export const API = "https://api.github.com";

export class StoreError extends Error {
  /** kind: stale | auth | forbidden | notfound | rate | network | http | invalid */
  constructor(kind, message, status = 0) {
    super(message);
    this.name = "StoreError";
    this.kind = kind;
    this.status = status;
  }
}

export function encodeBase64Utf8(text) {
  const bytes = new TextEncoder().encode(text);
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

export function decodeBase64Utf8(b64) {
  const binary = atob(b64.replace(/\s/g, ""));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new TextDecoder().decode(bytes);
}

export class GitHubStore {
  constructor({ owner, repo, branch, path = "docs/board.json", token = null, fetchImpl = globalThis.fetch.bind(globalThis) }) {
    Object.assign(this, { owner, repo, branch, path, token });
    this.fetch = fetchImpl;
  }

  /** False when there is no token, or GitHub rejected it (expired or revoked): changes then wait locally. */
  get canWrite() {
    return !!this.token && !this.tokenRejected;
  }

  setToken(token) {
    this.token = token;
    this.tokenRejected = false;
  }

  async request(method, url, body, { anonymous = false } = {}) {
    const headers = { Accept: "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28" };
    if (this.token && !anonymous) headers.Authorization = `Bearer ${this.token}`;
    if (body) headers["Content-Type"] = "application/json";
    let res;
    try {
      res = await this.fetch(`${API}${url}`, { method, headers, body: body ? JSON.stringify(body) : undefined, cache: "no-store" });
    } catch {
      throw new StoreError("network", "Could not reach GitHub. Check your connection; your changes are kept and will be saved later.");
    }
    if (res.ok) return res.json();
    let detail = "";
    try {
      detail = String((await res.json()).message ?? "").slice(0, 200);
    } catch {
      // not JSON
    }
    if (res.status === 409 || (res.status === 422 && /sha|match/i.test(detail))) {
      throw new StoreError("stale", "The board was changed by someone else since it was loaded.", res.status);
    }
    if (res.status === 401) throw new StoreError("auth", "GitHub did not accept the token (expired or revoked). Sign in again; your changes are kept.", 401);
    if (res.status === 403 || res.status === 429) {
      if (res.headers.get("x-ratelimit-remaining") === "0" || res.status === 429) {
        throw new StoreError("rate", "GitHub's rate limit was reached. Saving resumes automatically in a few minutes.", res.status);
      }
      throw new StoreError("forbidden", "The token may not write to this repository. It needs Contents: Read and write on crosenlund79/TimeAndTask.", 403);
    }
    if (res.status === 404) {
      throw new StoreError("notfound", `Not found on GitHub (${this.branch}/${this.path}). If you are signed in, check that the token covers this repository.`, 404);
    }
    throw new StoreError("http", `GitHub returned ${res.status}${detail ? `: ${detail}` : ""}.`, res.status);
  }

  /** The signed-in GitHub user's login. */
  async whoami() {
    const user = await this.request("GET", "/user");
    return user.login;
  }

  /**
   * Latest board: the branch head first, then the file at that exact commit, so a cached copy of an older
   * version can never be mistaken for the current one.
   */
  async load() {
    try {
      return await this.loadWith({ anonymous: this.tokenRejected });
    } catch (e) {
      if (e.kind !== "auth" || !this.token || this.tokenRejected) throw e;
      // The token expired or was revoked. The repository is public, so keep showing the board read-only;
      // changes wait in this browser until the user signs in again.
      this.tokenRejected = true;
      return this.loadWith({ anonymous: true });
    }
  }

  async loadWith(options) {
    const ref = await this.request("GET", `/repos/${this.owner}/${this.repo}/git/ref/heads/${encodeURIComponent(this.branch)}`, null, options);
    const commit = ref.object.sha;
    const file = await this.request("GET", `/repos/${this.owner}/${this.repo}/contents/${this.path}?ref=${commit}`, null, options);
    let board;
    try {
      board = parseBoard(decodeBase64Utf8(file.content));
    } catch {
      throw new StoreError("invalid", "docs/board.json on GitHub is not valid JSON. Fix it in the repository; nothing was changed.");
    }
    return { sha: file.sha, commit, board };
  }

  /** Saves `board` if the file on GitHub is still version `sha`. Returns the new { sha, commit }. */
  async save(board, sha, message) {
    if (!this.token) throw new StoreError("auth", "Sign in to save changes to GitHub.");
    let res;
    try {
      res = await this.request("PUT", `/repos/${this.owner}/${this.repo}/contents/${this.path}`, {
        message, content: encodeBase64Utf8(serializeBoard(board)), sha, branch: this.branch,
      });
    } catch (e) {
      if (e.kind === "auth") this.tokenRejected = true;
      throw e;
    }
    return { sha: res.content.sha, commit: res.commit.sha };
  }
}
