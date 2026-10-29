import { test } from "node:test";
import assert from "node:assert/strict";
import { serializeBoard } from "../app/board-core.js";
import { decodeBase64Utf8, encodeBase64Utf8, GitHubStore } from "../app/github.js";
import { migratedBoard } from "./helpers.mjs";

function fakeFetch(responses) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init });
    const next = responses.shift();
    if (next instanceof Error) throw next;
    return {
      ok: next.status >= 200 && next.status < 300,
      status: next.status,
      headers: { get: h => next.headers?.[h.toLowerCase()] ?? null },
      json: async () => next.body,
    };
  };
  fn.calls = calls;
  return fn;
}

const TOKEN = "fake-token-secret-123";
const store = fetchImpl => new GitHubStore({ owner: "o", repo: "r", branch: "board", token: TOKEN, fetchImpl });

test("UTF-8 survives base64", () => {
  const text = "Æblegrød – “quotes” ✓ 🙂";
  assert.equal(decodeBase64Utf8(encodeBase64Utf8(text)), text);
});

test("load reads the branch head, then the file at that exact commit", async () => {
  const board = migratedBoard();
  const fetchImpl = fakeFetch([
    { status: 200, body: { object: { sha: "commit1" } } },
    { status: 200, body: { sha: "blob1", content: encodeBase64Utf8(serializeBoard(board)) } },
  ]);
  const loaded = await store(fetchImpl).load();
  assert.equal(loaded.sha, "blob1");
  assert.equal(loaded.board.cards.length, 22);
  assert.match(fetchImpl.calls[0].url, /\/repos\/o\/r\/git\/ref\/heads\/board$/);
  assert.match(fetchImpl.calls[1].url, /contents\/docs\/board\.json\?ref=commit1$/);
  assert.equal(fetchImpl.calls[0].init.cache, "no-store");
});

test("save sends the version it is based on, to the board branch only", async () => {
  const fetchImpl = fakeFetch([{ status: 200, body: { content: { sha: "blob2" }, commit: { sha: "c2" } } }]);
  const result = await store(fetchImpl).save(migratedBoard(), "blob1", "board: test");
  assert.deepEqual(result, { sha: "blob2", commit: "c2" });
  const body = JSON.parse(fetchImpl.calls[0].init.body);
  assert.equal(body.sha, "blob1");
  assert.equal(body.branch, "board");
  assert.equal(fetchImpl.calls[0].init.method, "PUT");
});

test("errors are classified and never contain the token", async () => {
  const cases = [
    [{ status: 409, body: { message: "docs/board.json does not match blob1" } }, "stale"],
    [{ status: 401, body: { message: "Bad credentials" } }, "auth"],
    [{ status: 403, body: { message: "Resource not accessible by personal access token" } }, "forbidden"],
    [{ status: 403, headers: { "x-ratelimit-remaining": "0" }, body: {} }, "rate"],
    [{ status: 404, body: {} }, "notfound"],
    [{ status: 500, body: { message: `oops ${TOKEN}` } }, "http"],
    [new TypeError("Failed to fetch"), "network"],
  ];
  for (const [response, kind] of cases) {
    await assert.rejects(store(fakeFetch([response])).save(migratedBoard(), "s", "m"), e => {
      assert.equal(e.kind, kind);
      return true;
    });
  }
  // A server message that echoes the token must not leak it (500 case is passed through, so check it).
  await assert.rejects(store(fakeFetch([{ status: 500, body: { message: "nothing secret" } }])).save(migratedBoard(), "s", "m"),
    e => !e.message.includes(TOKEN));
});

test("the token is sent only in the Authorization header", async () => {
  const fetchImpl = fakeFetch([
    { status: 200, body: { object: { sha: "c" } } },
    { status: 200, body: { sha: "b", content: encodeBase64Utf8(serializeBoard(migratedBoard())) } },
  ]);
  await store(fetchImpl).load();
  for (const call of fetchImpl.calls) {
    assert.ok(!call.url.includes(TOKEN));
    assert.equal(call.init.headers.Authorization, `Bearer ${TOKEN}`);
  }
});
