// Browser tests: the real app in Chromium against FakeApi (GitHub's version-checked Contents API behaviour).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { applyOp, findCard, ops } from "../../app/board-core.js";
import { committedBoard } from "../helpers.mjs";
import { FakeApi, VALID_TOKEN } from "./fake-api.mjs";
import { closeBrowser, getBrowser, openBoard, startServer } from "./harness.mjs";

let server;
let url;
before(async () => ({ server, url } = await startServer()));
after(async () => {
  await closeBrowser();
  server.close();
});

const AT = "2026-10-29T10:00:00Z";

async function withBoard(options, run) {
  const b = await openBoard(url, committedBoard(), options);
  try {
    await run(b);
    assert.deepEqual(b.errors, [], "no script errors or CSP violations");
    assert.deepEqual(b.external, [], "no requests to third parties");
  } finally {
    await b.ctx.close();
  }
}

test("loads all cards into five columns with counts and indicators", () => withBoard({}, async b => {
  assert.deepEqual(await b.page.locator(".lane h2").allTextContents(), ["Backlog", "To do", "In progress", "Review / Test", "Done"]);
  assert.equal((await b.cardIds("review")).length, 10);
  assert.equal((await b.cardIds("done")).length, 5);
  assert.match(await b.page.locator('.card[data-id="WT-030"]').textContent(), /Blocked by WT-010/);
  assert.equal(await b.page.locator("#sync-text").textContent(), "Saved");
}));

test("drag a card to another column: saved to GitHub and still there after a refresh", () => withBoard({}, async b => {
  await b.drag("WT-040", "todo");
  assert.ok((await b.cardIds("todo")).includes("WT-040"));
  await b.flush();
  assert.equal(b.apiBoardCard("WT-040").status, "todo");
  assert.match(b.api.saves.at(-1), /move WT-040 to To do/);
  await b.page.reload();
  await b.page.waitForSelector('.lane[data-status="todo"] .card[data-id="WT-040"]');
}));

test("reorder within a column", () => withBoard({}, async b => {
  const before = await b.cardIds("todo");
  await b.drag(before[3], "todo", before[0]);
  const after = await b.cardIds("todo");
  assert.equal(after[0], before[3]);
  await b.flush();
  await b.page.reload();
  await b.page.waitForSelector(".card");
  assert.deepEqual(await b.cardIds("todo"), after);
}));

test("dragging to Done asks for acceptance; cancel changes nothing", () => withBoard({}, async b => {
  await b.drag("WT-013", "done");
  await b.page.getByRole("dialog").getByText("Accept WT-013?").waitFor();
  await b.page.getByRole("button", { name: "Cancel" }).click();
  assert.ok((await b.cardIds("review")).includes("WT-013"));
  await b.drag("WT-013", "done");
  await b.page.getByRole("button", { name: "Accept and move to Done" }).click();
  await b.flush();
  const card = b.apiBoardCard("WT-013");
  assert.equal(card.status, "done");
  assert.match(card.history.at(-1).accepted, /Accepted by crosenlund79/);
}));

test("keyboard moves (Alt+arrow) as an alternative to dragging", () => withBoard({}, async b => {
  await b.page.locator('.card[data-id="WT-041"]').focus();
  await b.page.keyboard.press("Alt+ArrowRight");
  await b.page.waitForSelector('.lane[data-status="todo"] .card[data-id="WT-041"]');
  await b.flush();
  assert.equal(b.apiBoardCard("WT-041").status, "todo");
}));

test("edit title, priority, notes, acceptance and dependencies in the card panel", () => withBoard({}, async b => {
  await b.openCard("WT-031");
  const panel = b.page.locator("#panel");
  await panel.locator("#card-title").fill("Activity log (day view)");
  await panel.locator("#card-title").press("Enter");
  await panel.locator("#card-priority").selectOption("high");
  await panel.locator("#card-notes").fill("Start with the day view.\nThen filters.");
  await panel.locator("#card-notes").blur();
  await panel.locator(".panel-section", { hasText: "Acceptance criteria" }).getByRole("button", { name: "Edit" }).click();
  await panel.locator("#card-acceptance").fill("Corrections keep originals\nUndo works");
  await panel.getByRole("button", { name: "Save" }).click();
  await panel.locator("#dep-input").fill("WT-030");
  await panel.getByRole("button", { name: "Add", exact: true }).first().click();
  // A self-dependency and a cycle are refused with a message.
  await panel.locator("#dep-input").fill("WT-031");
  await panel.getByRole("button", { name: "Add", exact: true }).first().click();
  await panel.getByText("A card cannot wait for itself.").waitFor();
  await b.flush();
  const card = b.apiBoardCard("WT-031");
  assert.equal(card.title, "Activity log (day view)");
  assert.equal(card.priority, "high");
  assert.equal(card.notes, "Start with the day view.\nThen filters.");
  assert.deepEqual(card.acceptance, ["Corrections keep originals", "Undo works"]);
  assert.deepEqual(card.blockedBy, ["WT-030"]);
  await b.page.reload();
  await b.openCard("WT-031");
  assert.equal(await b.page.locator("#card-notes").inputValue(), "Start with the day view.\nThen filters.");
  // Clicking a dependency opens that card.
  await b.page.locator("#panel .dep-chip .link-btn").click();
  await b.page.locator("#panel .card-id", { hasText: "WT-030" }).waitFor();
}));

test("create a card", () => withBoard({}, async b => {
  await b.page.getByRole("button", { name: "New card" }).click();
  await b.page.locator("#new-title").fill("Board works on my phone");
  await b.page.locator("#new-category").selectOption("testing");
  await b.page.locator("#new-status").selectOption("todo");
  await b.page.getByRole("button", { name: "Add card" }).click();
  await b.page.waitForSelector('.lane[data-status="todo"] .card[data-id="WT-042"]');
  await b.flush();
  assert.equal(b.apiBoardCard("WT-042").title, "Board works on my phone");
  assert.equal(b.api.board.nextId, 43);
}));

test("comments: timestamped, kept after refresh, shown as text (no script injection)", () => withBoard({}, async b => {
  await b.openCard("WT-010");
  const attack = "Checked <img src=x onerror=\"window.__xss=1\"> and [x](javascript:window.__xss=2) **ok**";
  await b.page.locator("#comment-text").fill(attack);
  await b.page.getByRole("button", { name: "Comment", exact: true }).click();
  await b.flush();
  const comment = b.apiBoardCard("WT-010").comments[0];
  assert.equal(comment.text, attack);
  assert.equal(comment.author, "crosenlund79");
  assert.match(comment.at, /^\d{4}-\d\d-\d\dT/);
  await b.page.reload();
  await b.openCard("WT-010");
  const html = await b.page.locator("#panel .comment .md").innerHTML();
  assert.match(html, /&lt;img/);
  assert.match(html, /<strong>ok<\/strong>/);
  assert.equal(await b.page.evaluate(() => window.__xss), undefined);
  assert.match(await b.page.locator("#panel .comment-meta").textContent(), /crosenlund79/);
}));

test("search and filters", () => withBoard({}, async b => {
  await b.page.locator("#search").fill("Alt+1");
  assert.deepEqual(await b.page.locator(".card").evaluateAll(e => e.map(x => x.dataset.id)), ["WT-014"]);
  await b.page.locator("#search").fill("wt-030");
  assert.deepEqual(await b.page.locator(".card").evaluateAll(e => e.map(x => x.dataset.id)), ["WT-030"]);
  await b.page.locator("#search").fill("");
  await b.page.locator("#filter-category").selectOption("bug");
  assert.deepEqual((await b.page.locator(".card").evaluateAll(e => e.map(x => x.dataset.id))).sort(), ["WT-006", "WT-010", "WT-014"]);
  await b.page.locator("#filter-category").selectOption("");
  await b.page.locator("#filter-status").selectOption("backlog");
  assert.equal(await b.page.locator(".lane").count(), 1);
}));

test("archive one Done card, browse and search the archive, restore it; archive all Done", () => withBoard({}, async b => {
  await b.openCard("WT-001");
  await b.page.locator("#panel").getByRole("button", { name: "Archive" }).click();
  await b.page.waitForFunction(() => !document.querySelector('.lane[data-status="done"] .card[data-id="WT-001"]'));
  await b.flush();
  assert.equal(b.apiBoardCard("WT-001").archived, true);
  assert.equal(await b.page.locator("#archive-count").textContent(), "1");

  await b.page.getByRole("button", { name: /^Archive/ }).first().click();
  await b.page.locator("#archive-search").fill("foundation");
  assert.equal(await b.page.locator(".archive-item").count(), 1);
  await b.page.locator("#archive-search").fill("nothing like this");
  assert.equal(await b.page.locator(".archive-item").count(), 0);
  await b.page.locator("#archive-search").fill("WT-001");
  await b.page.locator(".archive-item").getByRole("button", { name: "Restore" }).click();
  await b.flush();
  assert.equal(b.apiBoardCard("WT-001").archived, false);
  assert.deepEqual(b.apiBoardCard("WT-001").history.map(h => h.type), ["migrated", "archived", "restored"]);
  await b.page.keyboard.press("Escape");

  await b.page.getByRole("button", { name: "Archive all" }).click();
  await b.page.getByRole("button", { name: "Archive all" }).last().click();
  await b.flush();
  assert.equal(b.api.board.cards.filter(c => c.archived).length, 5);
  assert.equal((await b.cardIds("done")).length, 0);
  // Archived cards are still on the board file, with everything they had.
  assert.equal(b.api.board.cards.length, 22);
}));

test("undo reverses the saved change (move and field edit)", () => withBoard({}, async b => {
  await b.drag("WT-040", "todo");
  await b.flush();
  await b.page.locator("#undo").click();
  await b.flush();
  assert.equal(b.apiBoardCard("WT-040").status, "backlog");
  await b.openCard("WT-033");
  await b.page.locator("#card-priority").selectOption("critical");
  await b.flush();
  await b.page.keyboard.press("Escape");
  await b.page.keyboard.press("Control+z");
  await b.flush();
  assert.equal(b.apiBoardCard("WT-033").priority, "normal");
}));

test("someone else saved meanwhile (stale version): both changes kept", () => withBoard({}, async b => {
  b.api.external(board => applyOp(board, ops.set(board, "WT-032", "notes", "edited by Claude Code", AT, "claude")));
  await b.drag("WT-033", "doing");
  await b.flush();
  assert.equal(b.apiBoardCard("WT-033").status, "doing");
  assert.equal(b.apiBoardCard("WT-032").notes, "edited by Claude Code");
  await b.page.evaluate(() => window.__board.refresh());
  await b.openCard("WT-032");
  assert.equal(await b.page.locator("#card-notes").inputValue(), "edited by Claude Code");
}));

test("same field changed elsewhere: conflict shown, nothing overwritten, resolved by choice", () => withBoard({}, async b => {
  b.api.external(board => applyOp(board, ops.set(board, "WT-012", "title", "Claude's title", AT, "claude")));
  await b.openCard("WT-012");
  await b.page.locator("#card-title").fill("My title");
  await b.page.locator("#card-title").press("Enter");
  await b.page.evaluate(() => window.__board.saveNow());
  await b.page.locator("#sync-text", { hasText: "Conflict" }).waitFor();
  assert.equal(b.apiBoardCard("WT-012").title, "Claude's title");
  await b.page.locator("#banner").getByRole("button", { name: "Resolve" }).click();
  const dialog = b.page.getByRole("dialog");
  await dialog.locator("pre", { hasText: "Claude's title" }).waitFor();
  await dialog.locator("textarea").fill("Merged title");
  await dialog.getByRole("button", { name: "Use my text" }).click();
  await b.flush();
  assert.equal(b.apiBoardCard("WT-012").title, "Merged title");
}));

test("concurrent comments on the same card are both kept", () => withBoard({}, async b => {
  await b.openCard("WT-014");
  b.api.external(board => applyOp(board, ops.comment(board, "WT-014", "comment from Claude", AT, "claude")));
  await b.page.locator("#comment-text").fill("comment from the browser");
  await b.page.getByRole("button", { name: "Comment", exact: true }).click();
  await b.flush();
  assert.deepEqual(b.apiBoardCard("WT-014").comments.map(c => c.text), ["comment from Claude", "comment from the browser"]);
  await b.page.locator("#panel .comment", { hasText: "comment from Claude" }).waitFor();
}));

test("network failure: shown clearly, change kept, retry saves it", () => withBoard({}, async b => {
  b.api.next.push({ on: "save", kind: "abort" });
  await b.drag("WT-041", "todo");
  await b.page.evaluate(() => window.__board.saveNow());
  await b.page.locator("#sync-text", { hasText: "Save failed" }).waitFor();
  assert.ok(await b.page.locator("#banner").isVisible());
  assert.equal(b.apiBoardCard("WT-041").status, "backlog");
  await b.page.locator("#banner").getByRole("button", { name: "Retry" }).click();
  await b.flush();
  assert.equal(b.apiBoardCard("WT-041").status, "todo");
}));

test("expired token: edits kept across a refresh until signing in again", async () => {
  const api = new FakeApi(committedBoard());
  const context = await (await getBrowser()).newContext();
  await api.attach(context);
  await context.addInitScript(() => sessionStorage.getItem("wtb.token") || sessionStorage.setItem("wtb.token", "fake-token-expired"));
  const b = await openBoard(url, null, { api, context });
  try {
    await b.drag("WT-040", "todo");
    await b.page.evaluate(() => window.__board.saveNow());
    await b.page.locator("#sync-text", { hasText: "Sign in to save" }).waitFor();
    await b.page.reload();
    await b.page.waitForSelector('.lane[data-status="todo"] .card[data-id="WT-040"]'); // restored from this browser
    assert.equal(api.board.cards.find(c => c.id === "WT-040").status, "backlog");
    await b.page.locator("#banner").getByRole("button", { name: "Sign in" }).click();
    await b.page.locator("#token").fill(VALID_TOKEN);
    await b.page.getByRole("button", { name: "Sign in" }).last().click();
    await b.flush();
    assert.equal(api.board.cards.find(c => c.id === "WT-040").status, "todo");
  } finally {
    await context.close();
  }
});

test("signed out: read-only indicator; changes survive refresh; sign in saves them; token handling", async () => {
  const b = await openBoard(url, committedBoard(), { signedIn: false });
  try {
    await b.page.locator("#sync-text", { hasText: "Read-only" }).waitFor();
    await b.drag("WT-033", "doing");
    await b.page.locator("#sync-text", { hasText: "Sign in to save" }).waitFor();
    await b.page.reload();
    await b.page.waitForSelector('.lane[data-status="doing"] .card[data-id="WT-033"]');

    await b.page.getByRole("button", { name: "Sign in" }).first().click();
    await b.page.locator("#token").fill("wrong-token");
    await b.page.getByRole("button", { name: "Sign in" }).last().click();
    await b.page.getByText("GitHub did not accept this token").waitFor();
    await b.page.locator("#token").fill(VALID_TOKEN);
    await b.page.getByRole("button", { name: "Sign in" }).last().click();
    await b.flush();
    assert.equal(b.apiBoardCard("WT-033").status, "doing");

    const stored = await b.page.evaluate(() => ({ session: sessionStorage.getItem("wtb.token"), local: localStorage.getItem("wtb.token") }));
    assert.deepEqual(stored, { session: VALID_TOKEN, local: null }, "session storage only, by default");
    const localDump = await b.page.evaluate(() => JSON.stringify({ ...localStorage }));
    assert.ok(!localDump.includes(VALID_TOKEN), "token not in the saved board state");
    for (const r of b.api.requests) assert.ok(!r.url.includes(VALID_TOKEN), "token never in a URL");
    assert.ok(!(await b.page.content()).includes(VALID_TOKEN), "token not in the page");

    await b.page.getByRole("button", { name: /Sign out/ }).click();
    const cleared = await b.page.evaluate(() => [sessionStorage.getItem("wtb.token"), localStorage.getItem("wtb.token")]);
    assert.deepEqual(cleared, [null, null]);
    assert.deepEqual(b.errors, []);
  } finally {
    await b.ctx.close();
  }
});

test("phone layout: one column at a time, status selector instead of dragging", () => withBoard({ viewport: { width: 390, height: 844 } }, async b => {
  assert.ok(await b.page.locator("#tabs").isVisible());
  const visibleLanes = await b.page.locator(".lane").evaluateAll(els => els.filter(e => getComputedStyle(e).display !== "none").length);
  assert.equal(visibleLanes, 1);
  assert.equal(await b.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, "no sideways scrolling");
  await b.page.getByRole("button", { name: /^To do/ }).click();
  await b.page.locator('.lane[data-status="todo"] .card[data-id="WT-034"]').click();
  await b.page.locator("#card-status").selectOption("doing");
  await b.flush();
  assert.equal(b.apiBoardCard("WT-034").status, "doing");
  await b.page.keyboard.press("Escape");
  await b.page.getByRole("button", { name: /^In progress/ }).click();
  await b.page.locator('.lane[data-status="doing"] .card[data-id="WT-034"]').waitFor();
  // Archive drawer works on a phone too.
  await b.page.locator("#archive-open").click();
  assert.ok(await b.page.locator("#archive").isVisible());
}));

test("blocked work is flagged before it is started", () => withBoard({}, async b => {
  await b.drag("WT-030", "doing");
  await b.page.getByRole("dialog").getByText("WT-030 is blocked").waitFor();
  await b.page.getByRole("button", { name: "Cancel" }).click();
  assert.ok((await b.cardIds("todo")).includes("WT-030"));
  assert.equal(findCard(b.api.board, "WT-030").status, "todo");
}));
