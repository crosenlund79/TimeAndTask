// Browser tests for quick capture, the Inbox, screenshots (this browser only), links and Backup & Export.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { readFile, writeFile, mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { parseBackup } from "../../app/backup.js";
import { migratedBoard } from "../helpers.mjs";
import { closeBrowser, openBoard, startServer } from "./harness.mjs";

let server;
let url;
let dir;
before(async () => {
  ({ server, url } = await startServer());
  dir = await mkdtemp(path.join(tmpdir(), "board-ui-"));
});
after(async () => {
  await closeBrowser();
  server.close();
});

// 1×1 PNG
const PNG_B64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

async function withBoard(options, run) {
  const b = await openBoard(url, migratedBoard(), options);
  try {
    await run(b);
    assert.deepEqual(b.errors, [], "no script errors or CSP violations");
    assert.deepEqual(b.external, [], "no requests to third parties");
    for (const r of b.api.requests) assert.ok(!r.url.includes("iVBOR"), "no image data sent to GitHub");
    assert.ok(!b.api.text.includes("iVBOR") && !b.api.text.includes("data:image"), "no image data in board.json");
  } finally {
    await b.ctx.close();
  }
}

/** Dispatches a paste of a PNG at the focused element, like Ctrl+V with a screenshot on the clipboard. */
async function pasteImage(page) {
  await page.evaluate(b64 => {
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    const dt = new DataTransfer();
    dt.items.add(new File([bytes], "image.png", { type: "image/png" }));
    (document.activeElement ?? document.body).dispatchEvent(new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true }));
  }, PNG_B64);
}

async function dropImage(page, selector) {
  await page.evaluate(([b64, sel]) => {
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    const dt = new DataTransfer();
    dt.items.add(new File([bytes], "dropped.png", { type: "image/png" }));
    const target = document.querySelector(sel);
    target.dispatchEvent(new DragEvent("dragover", { dataTransfer: dt, bubbles: true, cancelable: true }));
    target.dispatchEvent(new DragEvent("drop", { dataTransfer: dt, bubbles: true, cancelable: true }));
  }, [PNG_B64, selector]);
}

async function pngFile() {
  const file = path.join(dir, "screenshot.png");
  await writeFile(file, Buffer.from(PNG_B64, "base64"));
  return file;
}

const lastCard = api => api.board.cards.reduce((a, c) => (Number(c.id.slice(3)) > Number(a.id.slice(3)) ? c : a));

test("capture: a title and Enter puts it in the Inbox, not on the board", () => withBoard({}, async b => {
  const before = await b.page.locator(".lane .card").count();
  await b.page.locator("#capture-title").fill("Renew the board token in January");
  await b.page.locator("#capture-title").press("Enter");
  await b.page.locator("#toast", { hasText: "Captured to Inbox (WT-042)" }).waitFor();
  assert.equal(await b.page.locator("#capture-title").inputValue(), "");
  assert.ok(await b.page.locator("#capture-title").evaluate(el => el === document.activeElement), "ready for the next thought");
  assert.equal(await b.page.locator("#inbox-count").textContent(), "1");
  assert.equal(await b.page.locator(".lane .card").count(), before, "the five columns are unchanged");
  await b.flush();
  const card = b.apiBoardCard("WT-042");
  assert.equal(card.status, "inbox");
  assert.equal(card.category, null);
  assert.equal(card.title, "Renew the board token in January");
}));

test("capture: a pasted link becomes the title and a link; c focuses the capture box", () => withBoard({}, async b => {
  await b.page.locator("body").press("c");
  assert.ok(await b.page.locator("#capture-title").evaluate(el => el === document.activeElement));
  await b.page.keyboard.type("https://github.com/crosenlund79/TimeAndTask/pull/7");
  await b.page.keyboard.press("Enter");
  await b.flush();
  const card = b.apiBoardCard("WT-042");
  assert.equal(card.title, "github.com/crosenlund79/TimeAndTask/pull/7");
  assert.deepEqual(card.links, [{ label: "github.com/crosenlund79/TimeAndTask/pull/7", url: "https://github.com/crosenlund79/TimeAndTask/pull/7" }]);
}));

test("capture: paste a screenshot, press Enter; the image stays in this browser", () => withBoard({}, async b => {
  await b.page.locator("#capture-title").focus();
  await pasteImage(b.page);
  await b.page.locator("#capture-images", { hasText: "1 image" }).waitFor();
  await b.page.locator("#capture-title").press("Enter");
  await b.flush();
  const card = lastCard(b.api);
  assert.match(card.title, /^Screenshot /);
  await b.page.locator("#inbox-open").click();
  await b.page.locator("#inbox .inbox-item", { hasText: "1 image" }).waitFor();
  await b.page.locator("#inbox .inbox-item .archive-open").first().click();
  await b.page.locator("#panel .thumb img[src^='blob:']").waitFor();
  assert.match(await b.page.locator("#panel .local-note").textContent(), /this browser only/);
}));

test("Inbox: search, put an item on the board, archive another", () => withBoard({}, async b => {
  for (const t of ["Order a new headset", "Idea: weekly review on Fridays"]) {
    await b.page.locator("#capture-title").fill(t);
    await b.page.locator("#capture-title").press("Enter");
  }
  await b.page.locator("#inbox-open").click();
  assert.deepEqual(await b.page.locator("#inbox .inbox-title").allTextContents(), ["Idea: weekly review on Fridays", "Order a new headset"], "newest first");
  await b.page.locator("#inbox-search").fill("headset");
  assert.equal(await b.page.locator("#inbox .inbox-item").count(), 1);
  await b.page.locator("#inbox .inbox-move").selectOption("todo");
  await b.page.locator('.lane[data-status="todo"] .card', { hasText: "Order a new headset" }).waitFor({ state: "attached" });
  await b.page.locator("#inbox-search").fill("");
  await b.page.locator("#inbox .inbox-item").getByRole("button", { name: "Archive" }).click();
  await b.flush();
  const cards = b.api.board.cards;
  assert.equal(cards.find(c => c.title === "Order a new headset").status, "todo");
  assert.equal(cards.find(c => c.title.startsWith("Idea:")).archived, true);
  assert.equal(await b.page.locator("#inbox-count").textContent(), "0");
}));

test("screenshots on a card: browse, drop, paste, enlarge, caption, remove; kept after reload", () => withBoard({}, async b => {
  await b.openCard("WT-010");
  await b.page.locator("#card-files").setInputFiles(await pngFile());
  await b.page.locator("#panel .thumb").first().waitFor();
  await dropImage(b.page, "#panel .drop-zone");
  await b.page.locator("#panel").click({ position: { x: 5, y: 5 } }); // focus inside the panel, not a text field
  await pasteImage(b.page);
  await b.page.waitForFunction(() => document.querySelectorAll("#panel .thumb").length === 3);

  await b.page.locator("#panel .thumb-open").first().click();
  await b.page.locator(".dialog .lightbox-img[src^='blob:']").waitFor();
  await b.page.locator(".dialog input").fill("Error dialog after resume");
  await b.page.getByRole("button", { name: "Done" }).click();
  await b.page.locator("#panel figcaption", { hasText: "Error dialog after resume" }).waitFor();

  await b.page.reload();
  await b.openCard("WT-010");
  await b.page.waitForFunction(() => document.querySelectorAll("#panel .thumb").length === 3);
  assert.match(await b.page.locator(".card[data-id='WT-010'] .card-meta").textContent(), /3 images/);

  await b.page.locator("#panel .thumb-open").first().click();
  await b.page.getByRole("button", { name: "Remove" }).click();
  await b.page.getByRole("button", { name: "Remove" }).last().click(); // confirm
  await b.page.waitForFunction(() => document.querySelectorAll("#panel .thumb").length === 2);
}));

test("links: paste only the URL, copy and open with one click; bare URLs in notes are links", () => withBoard({}, async b => {
  await b.ctx.grantPermissions(["clipboard-read", "clipboard-write"], { origin: url });
  await b.openCard("WT-011");
  await b.page.locator("#link-url").fill("https://learn.microsoft.com/dotnet/api/system.timeprovider");
  await b.page.locator("#link-url").press("Enter");
  await b.page.locator("#panel .link-list a", { hasText: "learn.microsoft.com/dotnet/api/system.timeprovider" }).first().waitFor();
  await b.page.locator("#panel .link-list").getByRole("button", { name: /^Copy link/ }).click();
  assert.equal(await b.page.evaluate(() => navigator.clipboard.readText()), "https://learn.microsoft.com/dotnet/api/system.timeprovider");
  const open = b.page.locator("#panel .link-list a.link-action");
  assert.equal(await open.getAttribute("target"), "_blank");
  assert.equal(await open.getAttribute("rel"), "noopener noreferrer");

  await b.page.locator("#card-notes").fill("Docs: https://example.com/guide. Ask Peter.");
  await b.page.locator("#card-notes").blur();
  await b.page.locator("#panel .panel-section", { hasText: "Notes" }).getByRole("button", { name: "Preview" }).click();
  assert.equal(await b.page.locator("#panel .md a[href='https://example.com/guide']").count(), 1);
  await b.flush();
  assert.equal(b.apiBoardCard("WT-011").links.length, 1);
}));

test("Backup & Export: download a ZIP, then restore it with a preview", () => withBoard({}, async b => {
  await b.openCard("WT-010");
  await b.page.locator("#card-files").setInputFiles(await pngFile());
  await b.page.locator("#comment-text").fill("Before the backup");
  await b.page.getByRole("button", { name: "Comment", exact: true }).click();
  await b.flush();
  await b.page.keyboard.press("Escape");

  await b.page.locator("#backup-open").click();
  const [download] = await Promise.all([b.page.waitForEvent("download"), b.page.getByRole("button", { name: "Download ZIP" }).click()]);
  assert.match(download.suggestedFilename(), /^worktrace-backup-all-\d{4}-\d\d-\d\d\.zip$/);
  const zipPath = path.join(dir, download.suggestedFilename());
  await download.saveAs(zipPath);
  const backup = await parseBackup(new Uint8Array(await readFile(zipPath)));
  assert.equal(backup.cards.length, 22);
  assert.equal(backup.attachments.length, 1);
  assert.equal(backup.cards.find(c => c.id === "WT-010").comments[0].text, "Before the backup");
  await b.page.getByRole("button", { name: "Close" }).click();

  // Lose things: rename a card, remove the screenshot.
  await b.openCard("WT-012");
  await b.page.locator("#card-title").fill("Renamed by mistake");
  await b.page.locator("#card-title").press("Enter");
  await b.flush();
  await b.page.keyboard.press("Escape");
  await b.openCard("WT-010");
  await b.page.locator("#panel .thumb-open").first().click();
  await b.page.getByRole("button", { name: "Remove" }).click();
  await b.page.getByRole("button", { name: "Remove" }).last().click();
  await b.page.keyboard.press("Escape");

  await b.page.locator("#backup-open").click();
  await b.page.locator("#restore-file").setInputFiles(zipPath);
  const dialog = b.page.getByRole("dialog");
  await dialog.getByText(/22 cards \(0 new, 1 changed, 21 unchanged\)/).waitFor();
  const row = dialog.locator(".restore-item", { hasText: "WT-012" });
  assert.match(await row.textContent(), /Changes: title · the board was edited after this backup/);
  assert.equal(await row.locator("input").isChecked(), false, "edits newer than the backup are not overwritten by default");
  await row.locator("input").check();
  await dialog.getByRole("button", { name: "Restore selected" }).click();
  await b.page.locator("#toast", { hasText: "Restored 1 card and 1 screenshot" }).waitFor();
  await b.flush();
  assert.equal(b.apiBoardCard("WT-012").title, '"Show a scheduled check-in now" explains itself');
  assert.equal(b.apiBoardCard("WT-012").history.at(-1).type, "imported");
  await b.openCard("WT-010");
  await b.page.locator("#panel .thumb").first().waitFor();
}));

test("phone: capture and the Inbox work at phone width", () => withBoard({ viewport: { width: 390, height: 844 } }, async b => {
  await b.page.locator("#capture-title").fill("Buy milk");
  await b.page.locator("#capture-title").press("Enter");
  await b.page.locator("#inbox-open").click();
  await b.page.locator("#inbox .inbox-item", { hasText: "Buy milk" }).waitFor();
  assert.equal(await b.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
}));
