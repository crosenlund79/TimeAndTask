// Browser test harness: serves the app from this checkout and opens it in Chromium against FakeApi.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { chromium } from "playwright";
import { ROOT } from "../helpers.mjs";
import { FakeApi, VALID_TOKEN } from "./fake-api.mjs";

const TYPES = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".css": "text/css", ".json": "application/json", ".svg": "image/svg+xml" };

export async function startServer() {
  const server = createServer(async (req, res) => {
    const pathname = decodeURIComponent(new URL(req.url, "http://x").pathname);
    const file = path.join(ROOT, pathname === "/" ? "index.html" : pathname);
    if (!file.startsWith(ROOT)) return res.writeHead(403).end();
    try {
      const body = await readFile(file);
      res.writeHead(200, { "content-type": TYPES[path.extname(file)] ?? "application/octet-stream", "cache-control": "no-store" }).end(body);
    } catch {
      res.writeHead(404).end();
    }
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  return { server, url: `http://127.0.0.1:${server.address().port}/` };
}

let browser;
export async function getBrowser() {
  const options = process.env.PLAYWRIGHT_CHROMIUM_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH } : {};
  browser ??= await chromium.launch(options);
  return browser;
}

export async function closeBrowser() {
  await browser?.close();
  browser = null;
}

/**
 * Opens the board. `signedIn` puts a valid token in sessionStorage first (as the sign-in dialog does).
 * Returns helpers bound to the page.
 */
export async function openBoard(url, board, { signedIn = true, viewport = { width: 1440, height: 900 }, api = null, context = null } = {}) {
  api ??= new FakeApi(board);
  const ctx = context ?? await (await getBrowser()).newContext({ viewport });
  if (!context) await api.attach(ctx);
  const errors = [];
  const external = [];
  const page = await ctx.newPage();
  page.on("pageerror", e => errors.push(e.message));
  page.on("console", m => m.type() === "error" && !/Failed to load resource|ERR_INTERNET_DISCONNECTED/.test(m.text()) && errors.push(m.text()));
  page.on("request", r => {
    const u = new URL(r.url());
    if (u.hostname !== "127.0.0.1" && u.hostname !== "api.github.com") external.push(r.url());
  });
  if (signedIn && !context) {
    await ctx.addInitScript(token => {
      if (!sessionStorage.getItem("wtb.token")) sessionStorage.setItem("wtb.token", token);
    }, VALID_TOKEN);
  }
  await page.goto(url);
  await page.waitForSelector(".lane .card", { state: "attached" });
  return { page, api, ctx, errors, external, ...helpers(page, api) };
}

function helpers(page, api) {
  return {
    lane: status => page.locator(`.lane[data-status="${status}"]`),
    cardIds: async status => page.locator(`.lane[data-status="${status}"] .card`).evaluateAll(els => els.map(e => e.dataset.id)),
    /** Saves now instead of waiting for the debounce, then waits until everything is saved. */
    async flush() {
      await page.evaluate(() => window.__board.saveNow());
      await page.waitForFunction(() => window.__board.pending() === 0 && window.__board.status() === "saved");
    },
    async drag(id, targetStatus, beforeId = null) {
      const card = page.locator(`.card[data-id="${id}"]`);
      const from = await card.boundingBox();
      await page.mouse.move(from.x + from.width / 2, from.y + 12);
      await page.mouse.down();
      await page.mouse.move(from.x + from.width / 2 + 10, from.y + 20, { steps: 3 });
      let to;
      if (beforeId) {
        const box = await page.locator(`.card[data-id="${beforeId}"]`).boundingBox();
        to = { x: box.x + box.width / 2, y: box.y + 4 };
      } else {
        const box = await page.locator(`.lane[data-status="${targetStatus}"] .lane-cards`).boundingBox();
        to = { x: box.x + box.width / 2, y: box.y + box.height - 4 };
      }
      await page.mouse.move(to.x, to.y, { steps: 12 });
      await page.mouse.up();
      await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
    },
    async openCard(id) {
      await page.locator(`.card[data-id="${id}"]`).click();
      await page.waitForSelector("#panel:not([hidden]) .title-input");
    },
    apiBoardCard: id => api.board.cards.find(c => c.id === id),
  };
}
