// WorkTrace board — browser app. Data: docs/board.json on the board branch (see README.md).
// Security: the token is kept in sessionStorage (or localStorage when "remember" is chosen), sent only to
// api.github.com in the Authorization header, and never shown, logged or put in a URL. All card text is
// inserted as text; Markdown goes through app/markdown.js, which escapes everything first.

import {
  ALL_STATUSES, CATEGORIES, CATEGORY_LABELS, INBOX, PRIORITIES, PRIORITY_LABELS, STATUSES, STATUS_LABELS, columnCards, findCard,
  nowIso, openBlockers, ops, wouldCycle,
} from "./board-core.js";
import { AttachmentStore, imagesFrom } from "./attachments.js";
import { buildBackup, monthsOf, parseBackup, planRestore, restoreOps } from "./backup.js";
import { GitHubStore } from "./github.js";
import { renderMarkdown } from "./markdown.js";
import { SyncEngine } from "./sync.js";

const OWNER = "crosenlund79";
const REPO = "TimeAndTask";
const BRANCH = pickBranch();
const TOKEN_KEY = "wtb.token";
const LOGIN_KEY = "wtb.login";
const POLL_SIGNED_IN_MS = 60_000;
const POLL_SIGNED_OUT_MS = 300_000;

/** Only board branches may be chosen (?branch=board-test for testing); never main or a code branch. */
function pickBranch() {
  const requested = new URLSearchParams(location.search).get("branch");
  return requested && /^board(-[a-z0-9-]{1,40})?$/.test(requested) ? requested : "board";
}

// ---------------------------------------------------------------- credentials

const credentials = {
  get token() {
    return safeGet(sessionStorage, TOKEN_KEY) ?? safeGet(localStorage, TOKEN_KEY);
  },
  save(token, remember) {
    this.clear();
    safeSet(remember ? localStorage : sessionStorage, TOKEN_KEY, token);
  },
  clear() {
    safeRemove(sessionStorage, TOKEN_KEY);
    safeRemove(localStorage, TOKEN_KEY);
  },
};

function safeGet(storage, key) {
  try {
    return storage.getItem(key);
  } catch {
    return null;
  }
}
function safeSet(storage, key, value) {
  try {
    storage.setItem(key, value);
  } catch {
    // storage blocked: the token then lasts only while the page is open
  }
}
function safeRemove(storage, key) {
  try {
    storage.removeItem(key);
  } catch {
    // ignore
  }
}

// ---------------------------------------------------------------- state

const store = new GitHubStore({ owner: OWNER, repo: REPO, branch: BRANCH, token: credentials.token });
const engine = new SyncEngine({
  store, storage: localStorage, key: `wtb:${OWNER}/${REPO}@${BRANCH}`, onUpdate: () => scheduleRender(),
});
const attachments = new AttachmentStore({ onChange: () => scheduleRender() });
const ui = {
  search: "", category: "", priority: "", status: "", scope: "active",
  openCardId: null, mobileColumn: "review", archiveOpen: false, archiveSearch: "",
  inboxOpen: false, inboxSearch: "", pendingImages: [],
  editing: new Set(), // panel sections switched to edit mode
  login: safeGet(localStorage, LOGIN_KEY),
};

const $ = id => document.getElementById(id);
const author = () => ui.login || "owner";
const now = () => nowIso();
const board = () => engine.view;

// ---------------------------------------------------------------- DOM helpers

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === false || value === null || value === undefined) continue;
    if (key === "class") el.className = value;
    else if (key === "dataset") Object.assign(el.dataset, value);
    else if (key.startsWith("on")) el.addEventListener(key.slice(2), value);
    else if (key === "html") el.innerHTML = value; // only ever renderMarkdown() output
    else if (key in el && typeof value !== "string") el[key] = value;
    else el.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

function option(value, label, selected) {
  return h("option", { value, selected: value === selected }, label);
}

function formatTime(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  return d.toLocaleString(undefined, { day: "numeric", month: "short", year: d.getFullYear() === new Date().getFullYear() ? undefined : "numeric", hour: "2-digit", minute: "2-digit" });
}

// ---------------------------------------------------------------- changes

/** Queues a change; shows why if it was refused. Returns true when applied. */
function change(op, { coalesce = false, toast = null, undoable = true } = {}) {
  try {
    engine.enqueue(op, { coalesce, undoable });
    if (toast) showToast(toast, undoable ? { label: "Undo", run: undo } : null);
    return true;
  } catch (e) {
    showToast(e.message.replace(/^.*?: /, ""), null, true);
    return false;
  }
}

function undo() {
  const result = engine.undo(now(), author());
  showToast(result.message, null, !result.ok);
}

async function moveCard(id, status, index) {
  const card = findCard(board(), id);
  if (!card) return;
  if (card.status === status && index === undefined) return;
  let accepted = null;
  if (status === "done" && card.status !== "done") {
    const ok = await confirmDialog({
      title: `Accept ${id}?`,
      message: `Moving "${card.title}" to Done records that you have reviewed and accepted it.`,
      confirm: "Accept and move to Done",
    });
    if (!ok) return cancelled();
    accepted = `Accepted by ${author()} on the board`;
  }
  if (status === "doing" && card.status !== "doing" && openBlockers(board(), card).length) {
    const ok = await confirmDialog({
      title: `${id} is blocked`,
      message: `It waits for ${openBlockers(board(), card).join(", ")}. Start it anyway?`,
      confirm: "Move to In progress",
    });
    if (!ok) return cancelled();
  }
  const op = ops.move(board(), id, status, index, now(), author(), accepted);
  const label = op.from.status === op.to.status ? `Reordered ${id}` : `Moved ${id} to ${STATUS_LABELS[status]}`;
  change(op, { toast: label });
}

function cancelled() {
  if (ui.openCardId) renderPanel(true);
  render();
}

function setField(id, field, value, options = {}) {
  const card = findCard(board(), id);
  if (!card || JSON.stringify(card[field]) === JSON.stringify(value)) return true;
  return change(ops.set(board(), id, field, value, now(), author()), options);
}

// ---------------------------------------------------------------- filtering

function matches(card) {
  if (ui.category && card.category !== ui.category) return false;
  if (ui.priority && card.priority !== ui.priority) return false;
  if (ui.scope === "active" && card.archived) return false;
  if (ui.scope === "archived" && !card.archived) return false;
  return textMatches(card, ui.search);
}

function textMatches(card, query) {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  const haystack = [card.id, card.title, card.description, card.notes, card.evidence, ...card.acceptance,
    ...card.comments.map(c => c.text), ...card.links.map(l => l.label)].join("\n").toLowerCase();
  return q.split(/\s+/).every(term => haystack.includes(term));
}

function visibleCards(status) {
  const all = [...columnCards(board(), status), ...(ui.scope === "active" ? [] : columnCards(board(), status, { archived: true }))];
  return all.filter(matches);
}

// ---------------------------------------------------------------- rendering

let renderQueued = false;
function scheduleRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => {
    renderQueued = false;
    render();
  });
}

function render() {
  renderSync();
  if (!board()) {
    $("board").replaceChildren(h("p", { class: "empty-state" }, engine.error ? engine.error.message : "Loading the board from GitHub…"));
    return;
  }
  renderToolbar();
  renderBoard();
  renderTabs();
  if (ui.openCardId) renderPanel();
  if (ui.archiveOpen) renderArchive();
  if (ui.inboxOpen) renderInbox();
}

function renderToolbar() {
  $("undo").disabled = !engine.canUndo;
  $("archive-count").textContent = String(board().cards.filter(c => c.archived).length);
  const inbox = columnCards(board(), INBOX);
  const inboxMatches = ui.search.trim() ? inbox.filter(c => textMatches(c, ui.search)).length : 0;
  $("inbox-count").textContent = inboxMatches ? `${inbox.length} · ${inboxMatches} match` : String(inbox.length);
  const chip = $("capture-images");
  chip.hidden = ui.pendingImages.length === 0;
  chip.replaceChildren(`${ui.pendingImages.length} image${ui.pendingImages.length === 1 ? "" : "s"}`,
    h("button", { type: "button", class: "chip-remove", "aria-label": "Remove the images from this capture", onclick: () => { ui.pendingImages = []; render(); } }, "✕"));
  const account = $("account");
  account.textContent = store.token && !store.tokenRejected ? (ui.login ? `${ui.login} · Sign out` : "Sign out") : "Sign in";
  account.title = store.token ? "Remove the GitHub token from this browser" : "Sign in with a GitHub token to save changes";
}

function renderSync() {
  const el = $("sync");
  let state = engine.status;
  let text = { saved: "Saved", saving: "Saving…", unsaved: "Unsaved changes", failed: "Save failed · Retry", conflict: "Conflict · Resolve" }[state];
  if (!engine.loaded && !engine.error) {
    state = "loading";
    text = "Loading…";
  }
  if (engine.needsAuth) {
    state = "auth";
    text = `${engine.pending.length} unsaved · Sign in to save`;
  } else if (store.tokenRejected) {
    state = "auth";
    text = "Token expired · Sign in again";
  } else if (state === "failed" && !engine.pending.length) {
    text = "Could not load · Retry";
  } else if (state === "saved" && !store.token) {
    state = "readonly";
    text = "Read-only · Sign in to edit";
  }
  if (!navigator.onLine && engine.pending.length) {
    state = "failed";
    text = "Offline · changes kept";
  }
  el.dataset.state = state;
  $("sync-text").textContent = text;
  const details = [];
  if (engine.lastSavedAt) details.push(`Last saved ${engine.lastSavedAt.toLocaleTimeString()}`);
  if (engine.pending.length) details.push(`${engine.pending.length} change(s) not saved yet`);
  if (engine.error) details.push(engine.error.message);
  details.push(`Branch ${BRANCH}`);
  el.title = details.join("\n");
  const banner = $("banner");
  if (state === "auth") {
    banner.replaceChildren(h("span", {}, store.tokenRejected
      ? "GitHub no longer accepts your token (expired or revoked). Your changes are kept in this browser; sign in again to save them."
      : "Sign in to save your changes to GitHub. They are kept in this browser until then."), " ",
    h("button", { type: "button", class: "btn", onclick: signInDialog }, "Sign in"));
    banner.hidden = false;
  } else if (engine.error && state === "failed") {
    banner.replaceChildren(h("span", {}, engine.error.message), " ",
      h("button", { type: "button", class: "btn", onclick: () => engine.retry() }, "Retry"));
    banner.hidden = false;
  } else if (engine.conflicts.length) {
    banner.replaceChildren(h("span", {}, `${engine.conflicts.length} change(s) conflict with edits made elsewhere. Nothing was overwritten.`), " ",
      h("button", { type: "button", class: "btn", onclick: conflictDialog }, "Resolve"));
    banner.hidden = false;
  } else banner.hidden = true;
}

function renderTabs() {
  const tabs = $("tabs");
  tabs.replaceChildren(...STATUSES.map(status => h("button", {
    type: "button", class: "tab", "aria-pressed": String(ui.mobileColumn === status),
    onclick: () => {
      ui.mobileColumn = status;
      render();
    },
  }, STATUS_LABELS[status], h("span", { class: "count-pill" }, String(visibleCards(status).length)))));
}

function renderBoard() {
  const statuses = ui.status ? [ui.status] : STATUSES;
  const lanes = statuses.map(status => {
    const cards = visibleCards(status);
    const header = h("div", { class: "lane-header" },
      h("h2", {}, STATUS_LABELS[status]), h("span", { class: "count-pill", "aria-label": `${cards.length} cards` }, String(cards.length)),
      status === "done" && ui.scope !== "archived" && cards.some(c => !c.archived)
        ? h("button", { type: "button", class: "link-btn", onclick: archiveAllDone }, "Archive all") : null);
    const list = h("div", { class: "lane-cards", dataset: { status } },
      cards.length ? cards.map(cardElement) : h("p", { class: "lane-empty" }, ui.search || ui.category || ui.priority ? "No matching cards" : "No cards"));
    return h("section", {
      class: "lane", dataset: { status }, "data-mobile-active": String(ui.mobileColumn === status), "aria-label": STATUS_LABELS[status],
    }, header, list);
  });
  const boardEl = $("board");
  boardEl.dataset.columns = String(statuses.length);
  boardEl.replaceChildren(...lanes);
}

function cardElement(card) {
  const blockers = openBlockers(board(), card);
  const el = h("article", {
    class: `card prio-${card.priority}${card.archived ? " is-archived" : ""}${card.id === ui.openCardId ? " is-open" : ""}`,
    tabindex: "0", dataset: { id: card.id },
    "aria-label": `${card.id} ${card.title}${card.category ? `, ${CATEGORY_LABELS[card.category]}` : ""}, ${PRIORITY_LABELS[card.priority]} priority${blockers.length ? ", blocked" : ""}`,
    onclick: e => {
      if (drag.suppressClick) return;
      if (!e.target.closest("button")) openCard(card.id);
    },
    onkeydown: e => cardKeys(e, card),
  },
  h("div", { class: "card-top" },
    h("span", { class: "card-id" }, card.id),
    card.category ? h("span", { class: `cat cat-${card.category}` }, CATEGORY_LABELS[card.category]) : null,
    card.priority === "high" || card.priority === "critical" ? h("span", { class: `prio-badge prio-badge-${card.priority}` }, PRIORITY_LABELS[card.priority]) : null,
    card.archived ? h("span", { class: "archived-badge" }, "Archived") : null,
    h("button", { type: "button", class: "handle", "aria-label": `Drag ${card.id}`, title: "Drag to move", tabindex: "-1" }, "⋮⋮")),
  h("div", { class: "card-title" }, card.title),
  cardMeta(card, blockers));
  return el;
}

/** Small indicators under a card title: blocked, comments, links, screenshots. */
function cardMeta(card, blockers = openBlockers(board(), card)) {
  const images = attachments.forCard(card.uid).length;
  const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;
  const bits = [
    blockers.length ? h("span", { class: "blocked" }, `Blocked by ${blockers.join(", ")}`) : null,
    card.comments.length ? h("span", {}, plural(card.comments.length, "comment")) : null,
    card.links.length ? h("span", {}, plural(card.links.length, "link")) : null,
    images ? h("span", {}, plural(images, "image")) : null,
  ].filter(Boolean);
  return bits.length ? h("div", { class: "card-meta" }, ...bits) : null;
}

/** Keyboard moves: Alt+←/→ changes column, Alt+↑/↓ reorders; Enter opens. */
function cardKeys(e, card) {
  if (e.key === "Enter" || e.key === " ") {
    e.preventDefault();
    openCard(card.id);
    return;
  }
  if (!e.altKey || card.archived) return;
  const i = STATUSES.indexOf(card.status);
  const column = columnCards(board(), card.status);
  const pos = column.findIndex(c => c.id === card.id);
  const refocus = () => requestAnimationFrame(() => document.querySelector(`.card[data-id="${card.id}"]`)?.focus());
  if (e.key === "ArrowRight" && i < STATUSES.length - 1) moveCard(card.id, STATUSES[i + 1]).then(refocus);
  else if (e.key === "ArrowLeft" && i > 0) moveCard(card.id, STATUSES[i - 1]).then(refocus);
  else if (e.key === "ArrowUp" && pos > 0) moveCard(card.id, card.status, pos - 1).then(refocus);
  else if (e.key === "ArrowDown" && pos < column.length - 1) moveCard(card.id, card.status, pos + 1).then(refocus);
  else return;
  e.preventDefault();
}

// ---------------------------------------------------------------- drag and drop (mouse, pen, touch via the handle)

const drag = { active: null, suppressClick: false };

function onPointerDown(e) {
  const cardEl = e.target.closest(".card");
  if (!cardEl || e.button !== 0 || !board()) return;
  if (e.target.closest("button:not(.handle), a, input, select, textarea")) return;
  if (e.pointerType !== "mouse" && !e.target.closest(".handle")) return; // touch: scroll unless using the handle
  const card = findCard(board(), cardEl.dataset.id);
  if (!card || card.archived) return;
  drag.active = { id: card.id, el: cardEl, startX: e.clientX, startY: e.clientY, started: false, pointerId: e.pointerId };
  window.addEventListener("pointermove", onPointerMove);
  window.addEventListener("pointerup", onPointerUp);
  window.addEventListener("pointercancel", cancelDrag);
  if (e.target.closest(".handle")) e.preventDefault();
}

function onPointerMove(e) {
  const d = drag.active;
  if (!d || e.pointerId !== d.pointerId) return;
  if (!d.started) {
    if (Math.hypot(e.clientX - d.startX, e.clientY - d.startY) < 6) return;
    startDrag(d);
  }
  e.preventDefault();
  d.ghost.style.transform = `translate(${e.clientX - d.offsetX}px, ${e.clientY - d.offsetY}px)`;
  autoScroll(e);
  const under = document.elementsFromPoint(e.clientX, e.clientY).find(el => el.classList?.contains("lane"));
  document.querySelectorAll(".lane.drop-target").forEach(l => l !== under && l.classList.remove("drop-target"));
  if (!under) return;
  under.classList.add("drop-target");
  const list = under.querySelector(".lane-cards");
  const cards = [...list.querySelectorAll(".card:not(.dragging)")];
  const next = cards.find(c => {
    const r = c.getBoundingClientRect();
    return e.clientY < r.top + r.height / 2;
  });
  list.querySelector(".lane-empty")?.remove();
  if (next) list.insertBefore(d.placeholder, next);
  else list.append(d.placeholder);
}

function startDrag(d) {
  d.started = true;
  const rect = d.el.getBoundingClientRect();
  d.offsetX = d.startX - rect.left;
  d.offsetY = d.startY - rect.top;
  d.ghost = d.el.cloneNode(true);
  d.ghost.classList.add("drag-ghost");
  d.ghost.style.width = `${rect.width}px`;
  d.ghost.style.transform = `translate(${rect.left}px, ${rect.top}px)`;
  d.placeholder = h("div", { class: "drop-placeholder" });
  d.placeholder.style.height = `${rect.height}px`;
  d.el.after(d.placeholder);
  d.el.classList.add("dragging");
  document.body.append(d.ghost);
  document.body.classList.add("is-dragging");
}

function autoScroll(e) {
  const boardEl = $("board");
  const edge = 48;
  if (e.clientX < edge) boardEl.scrollLeft -= 16;
  else if (e.clientX > innerWidth - edge) boardEl.scrollLeft += 16;
  if (e.clientY < edge) scrollBy(0, -16);
  else if (e.clientY > innerHeight - edge) scrollBy(0, 16);
}

function onPointerUp(e) {
  const d = drag.active;
  if (!d || e.pointerId !== d.pointerId) return;
  const target = d.started ? dropTarget(d) : null;
  cleanupDrag();
  if (d.started) {
    drag.suppressClick = true;
    setTimeout(() => (drag.suppressClick = false), 0);
  }
  if (target) moveCard(d.id, target.status, target.index);
  else if (d.started) render(); // dropped where it was: put the card back (a plain click must not re-render)
}

/** Status and position from where the placeholder is, mapped onto the full (unfiltered) column. */
function dropTarget(d) {
  const list = d.placeholder.closest(".lane-cards");
  if (!list) return null;
  const status = list.dataset.status;
  const siblings = [...list.children].filter(el => el.classList.contains("card") && !el.classList.contains("dragging"));
  const visibleIds = siblings.map(el => el.dataset.id);
  const placeholderIndex = [...list.children].filter(el => el === d.placeholder || (el.classList.contains("card") && !el.classList.contains("dragging"))).indexOf(d.placeholder);
  const others = columnCards(board(), status).filter(c => c.id !== d.id);
  const nextId = visibleIds[placeholderIndex];
  const prevId = visibleIds[placeholderIndex - 1];
  let index = others.length;
  if (nextId) index = others.findIndex(c => c.id === nextId);
  else if (prevId) index = others.findIndex(c => c.id === prevId) + 1;
  const card = findCard(board(), d.id);
  const currentIndex = columnCards(board(), status).findIndex(c => c.id === d.id);
  if (card.status === status && currentIndex === index) return null; // dropped where it was
  return { status, index };
}

function cancelDrag() {
  cleanupDrag();
  render();
}

function cleanupDrag() {
  const d = drag.active;
  drag.active = null;
  window.removeEventListener("pointermove", onPointerMove);
  window.removeEventListener("pointerup", onPointerUp);
  window.removeEventListener("pointercancel", cancelDrag);
  if (!d?.started) return;
  d.ghost.remove();
  d.placeholder.remove();
  d.el.classList.remove("dragging");
  document.body.classList.remove("is-dragging");
  document.querySelectorAll(".lane.drop-target").forEach(l => l.classList.remove("drop-target"));
}

// ---------------------------------------------------------------- card panel

function openCard(id) {
  if (ui.openCardId !== id) ui.editing.clear();
  ui.openCardId = id;
  $("panel").hidden = false;
  $("scrim").hidden = false;
  renderPanel(true);
  render();
  requestAnimationFrame(() => $("panel").querySelector(".panel-close")?.focus());
}

function closePanel() {
  const id = ui.openCardId;
  ui.openCardId = null;
  ui.editing.clear();
  $("panel").hidden = true;
  $("scrim").hidden = !ui.archiveOpen && !ui.inboxOpen;
  render();
  if (id) document.querySelector(`.card[data-id="${id}"]`)?.focus();
}

let panelCardRev = null;
function renderPanel(force = false) {
  const panel = $("panel");
  const card = findCard(board(), ui.openCardId);
  if (!card) {
    panel.replaceChildren(h("p", { class: "empty-state" }, `${ui.openCardId} is not on the board.`));
    return;
  }
  // Rebuild only when the card changed, and never under the user's cursor.
  const shots = attachments.forCard(card.uid).map(m => `${m.id}${m.caption}`).join();
  const key = `${card.id}:${card.rev}:${card.status}:${card.archived}:${[...ui.editing].join()}:${shots}`;
  if (!force && key === panelCardRev) return;
  if (!force && panel.contains(document.activeElement) && document.activeElement.matches("input, textarea")) {
    updatePanelLive(card);
    return;
  }
  panelCardRev = key;
  const scroll = panel.scrollTop;
  panel.replaceChildren(...panelContent(card));
  panel.scrollTop = scroll;
}

/** While the user types, refresh only the read-only parts (comments, history) so input is never disturbed. */
function updatePanelLive(card) {
  const comments = $("panel").querySelector(".comment-list");
  if (comments) comments.replaceChildren(...commentItems(card));
  const history = $("panel").querySelector(".history-list");
  if (history) history.replaceChildren(...historyItems(card));
}

function panelContent(card) {
  const readOnly = card.archived;
  const section = (title, ...children) => h("section", { class: "panel-section" }, h("h3", {}, title), ...children);
  const select = (field, values, labels) => h("select", {
    id: `card-${field}`, disabled: readOnly, "aria-label": field[0].toUpperCase() + field.slice(1),
    onchange: e => (field === "status" ? moveCard(card.id, e.target.value)
      : setField(card.id, field, e.target.value === "" ? null : e.target.value, { toast: `Changed ${card.id} ${field}` })),
  }, values.map(v => option(v, labels[v], card[field] ?? "")));
  // Inbox items are quick notes: show only what is filled in, plus notes, links and screenshots.
  const brief = card.status === INBOX;
  const optional = (empty, build) => (brief && empty ? null : build());

  return [
    h("header", { class: "panel-header" },
      h("span", { class: "card-id" }, card.id),
      card.archived ? h("span", { class: "archived-badge" }, `Archived ${formatTime(card.archivedAt)}`) : null,
      h("button", { type: "button", class: "btn icon-btn panel-close", "aria-label": "Close", onclick: closePanel }, "✕")),
    h("input", {
      id: "card-title", class: "title-input", value: card.title, maxlength: "200", disabled: readOnly, "aria-label": "Title",
      onchange: e => {
        if (!setField(card.id, "title", e.target.value.trim(), { toast: `Renamed ${card.id}` })) e.target.value = card.title;
      },
      onkeydown: e => e.key === "Enter" && e.target.blur(),
    }),
    h("div", { class: "field-row" },
      h("label", {}, "Status", select("status", ALL_STATUSES, STATUS_LABELS)),
      h("label", {}, "Category", select("category", ["", ...CATEGORIES], { "": "None", ...CATEGORY_LABELS })),
      h("label", {}, "Priority", select("priority", PRIORITIES, PRIORITY_LABELS))),
    notesSection(card, readOnly),
    linksSection(card, readOnly),
    screenshotsSection(card, readOnly),
    optional(!card.description.trim(), () => markdownSection(card, "description", "Description", readOnly)),
    optional(!card.acceptance.length, () => acceptanceSection(card, readOnly)),
    optional(!card.blockedBy.length, () => dependencySection(card, readOnly)),
    optional(!card.evidence.trim(), () => markdownSection(card, "evidence", "Evidence", readOnly)),
    brief ? h("p", { class: "muted" }, "Inbox item. Choose a Status to put it on the board; more fields appear there.") : null,
    section(`Comments (${card.comments.length})`,
      h("ol", { class: "comment-list" }, ...commentItems(card)),
      commentForm(card)),
    section("History", h("ol", { class: "history-list" }, ...historyItems(card))),
    h("footer", { class: "panel-footer" },
      h("span", {}, `Created ${formatTime(card.createdAt)} · updated ${formatTime(card.updatedAt)} · revision ${card.rev}`),
      card.archived
        ? h("button", { type: "button", class: "btn", onclick: () => restoreCard(card.id) }, card.status === INBOX ? "Restore to Inbox" : "Restore to Done")
        : card.status === "done" || card.status === INBOX ? h("button", { type: "button", class: "btn", onclick: () => archiveCard(card.id) }, "Archive") : null),
  ];
}

function dependencySection(card, readOnly) {
  const chips = card.blockedBy.map(id => {
    const dep = findCard(board(), id);
    const done = dep?.status === "done";
    return h("li", { class: `dep-chip${done ? " is-done" : ""}` },
      h("button", { type: "button", class: "link-btn", onclick: () => openCard(id), title: dep ? dep.title : "Unknown card" },
        `${id}${dep ? ` · ${dep.title}` : ""}`),
      done ? h("span", { class: "dep-state" }, "done") : h("span", { class: "dep-state blocked" }, "open"),
      readOnly ? null : h("button", {
        type: "button", class: "chip-remove", "aria-label": `Remove dependency on ${id}`,
        onclick: () => setField(card.id, "blockedBy", card.blockedBy.filter(x => x !== id), { toast: `Removed dependency on ${id}` }),
      }, "✕"));
  });
  const blocking = board().cards.filter(c => c.blockedBy.includes(card.id));
  const input = h("input", { id: "dep-input", placeholder: "WT-010", list: "card-ids", "aria-label": "Add a card this one waits for", autocomplete: "off" });
  const error = h("p", { class: "field-error", role: "alert" });
  const add = () => {
    const id = input.value.trim().toUpperCase();
    if (!id) return;
    if (!findCard(board(), id)) return (error.textContent = `There is no card ${id}.`);
    if (id === card.id) return (error.textContent = "A card cannot wait for itself.");
    if (card.blockedBy.includes(id)) return (error.textContent = `Already waiting for ${id}.`);
    if (wouldCycle(board(), card.id, [...card.blockedBy, id])) return (error.textContent = `${id} already waits for ${card.id}; that would be a cycle.`);
    if (setField(card.id, "blockedBy", [...card.blockedBy, id], { toast: `${card.id} now waits for ${id}` })) renderPanel(true);
  };
  return h("section", { class: "panel-section" },
    h("h3", {}, "Blocked by"),
    chips.length ? h("ul", { class: "dep-list" }, ...chips) : h("p", { class: "muted" }, "Not blocked by other cards."),
    readOnly ? null : h("div", { class: "inline-form" }, input,
      h("datalist", { id: "card-ids" }, board().cards.filter(c => c.id !== card.id).map(c => h("option", { value: c.id }, c.title))),
      h("button", { type: "button", class: "btn", onclick: add }, "Add")),
    error,
    blocking.length ? h("p", { class: "muted" }, "Blocks: ", ...blocking.flatMap((c, i) => [i ? ", " : "",
      h("button", { type: "button", class: "link-btn", onclick: () => openCard(c.id) }, c.id)])) : null);
}

function markdownSection(card, field, title, readOnly) {
  const editing = ui.editing.has(field);
  const finish = value => {
    ui.editing.delete(field);
    if (!setField(card.id, field, value, { toast: `Saved ${title.toLowerCase()} of ${card.id}` })) return;
    renderPanel(true);
  };
  if (editing) {
    const area = h("textarea", { id: `card-${field}`, rows: "6", "aria-label": title, onkeydown: e => e.key === "Enter" && (e.ctrlKey || e.metaKey) && finish(area.value) }, card[field]);
    requestAnimationFrame(() => area.focus());
    return h("section", { class: "panel-section" },
      h("h3", {}, title), area,
      h("div", { class: "form-actions" },
        h("span", { class: "muted" }, "Markdown: **bold**, *italic*, - lists, `code`, [link](https://…)"),
        h("button", { type: "button", class: "btn", onclick: () => { ui.editing.delete(field); renderPanel(true); } }, "Cancel"),
        h("button", { type: "button", class: "btn btn-primary", onclick: () => finish(area.value) }, "Save")));
  }
  return h("section", { class: "panel-section" },
    h("h3", {}, title, readOnly ? null : h("button", { type: "button", class: "link-btn edit-btn", onclick: () => { ui.editing.add(field); renderPanel(true); } }, "Edit")),
    card[field].trim() ? h("div", { class: "md", html: renderMarkdown(card[field]) }) : h("p", { class: "muted" }, `No ${title.toLowerCase()}.`));
}

function acceptanceSection(card, readOnly) {
  const editing = ui.editing.has("acceptance");
  if (editing) {
    const area = h("textarea", { id: "card-acceptance", rows: "5", "aria-label": "Acceptance criteria, one per line" }, card.acceptance.join("\n"));
    requestAnimationFrame(() => area.focus());
    const save = () => {
      ui.editing.delete("acceptance");
      setField(card.id, "acceptance", area.value.split("\n").map(l => l.replace(/^\s*[-*]\s*/, "").trim()).filter(Boolean),
        { toast: `Saved acceptance criteria of ${card.id}` });
      renderPanel(true);
    };
    return h("section", { class: "panel-section" }, h("h3", {}, "Acceptance criteria"),
      area, h("div", { class: "form-actions" }, h("span", { class: "muted" }, "One criterion per line."),
        h("button", { type: "button", class: "btn", onclick: () => { ui.editing.delete("acceptance"); renderPanel(true); } }, "Cancel"),
        h("button", { type: "button", class: "btn btn-primary", onclick: save }, "Save")));
  }
  return h("section", { class: "panel-section" },
    h("h3", {}, "Acceptance criteria", readOnly ? null : h("button", { type: "button", class: "link-btn edit-btn", onclick: () => { ui.editing.add("acceptance"); renderPanel(true); } }, "Edit")),
    card.acceptance.length ? h("ul", { class: "acceptance" }, card.acceptance.map(a => h("li", { html: renderMarkdown(a).replace(/^<p>|<\/p>$/g, "") })))
      : h("p", { class: "muted" }, "No acceptance criteria."));
}

let notesTimer = null;
function notesSection(card, readOnly) {
  const preview = ui.editing.has("notes-preview");
  const status = h("span", { class: "muted notes-status" }, "Saved automatically");
  const area = h("textarea", {
    id: "card-notes", rows: "5", disabled: readOnly, "aria-label": "Notes", placeholder: "Current situation, reminders, context…",
    oninput: () => {
      status.textContent = "Typing…";
      clearTimeout(notesTimer);
      notesTimer = setTimeout(() => {
        if (setField(card.id, "notes", area.value, { coalesce: true })) status.textContent = "Saved automatically";
      }, 500);
    },
    onblur: () => {
      clearTimeout(notesTimer);
      setField(card.id, "notes", area.value, { coalesce: true });
    },
  }, card.notes);
  return h("section", { class: "panel-section" },
    h("h3", {}, "Notes", h("button", {
      type: "button", class: "link-btn edit-btn",
      onclick: () => {
        if (preview) ui.editing.delete("notes-preview");
        else ui.editing.add("notes-preview");
        renderPanel(true);
      },
    }, preview ? "Edit" : "Preview")),
    preview ? (card.notes.trim() ? h("div", { class: "md", html: renderMarkdown(card.notes) }) : h("p", { class: "muted" }, "No notes."))
      : [area, status]);
}

/** "github.com/owner/repo/pull/12" from a URL: the default label when none is given. */
function linkLabel(url) {
  try {
    const u = new URL(url);
    return (u.hostname.replace(/^www\./, "") + u.pathname.replace(/\/$/, "")).slice(0, 80);
  } catch {
    return url.slice(0, 80);
  }
}

async function copyText(text, what = "Link") {
  try {
    await navigator.clipboard.writeText(text);
    showToast(`${what} copied.`);
  } catch {
    showToast(`Could not copy automatically. ${what}: ${text}`, null, true);
  }
}

function linksSection(card, readOnly) {
  const url = h("input", { id: "link-url", placeholder: "Paste a link (https://…)", type: "url", "aria-label": "Link URL" });
  const label = h("input", { id: "link-label", placeholder: "Label (optional)", "aria-label": "Link label" });
  const error = h("p", { class: "field-error", role: "alert" });
  const add = () => {
    const href = url.value.trim();
    if (!/^https?:\/\/\S+$/.test(href)) return (error.textContent = "Paste a link that starts with https://");
    if (card.links.some(l => l.url === href)) return (error.textContent = "That link is already on this card.");
    if (setField(card.id, "links", [...card.links, { label: label.value.trim() || linkLabel(href), url: href }], { toast: `Added link to ${card.id}` })) {
      renderPanel(true); // show it now, even though the cursor is in the link box
      $("link-url")?.focus();
    }
  };
  url.addEventListener("keydown", e => e.key === "Enter" && add());
  label.addEventListener("keydown", e => e.key === "Enter" && add());
  return h("section", { class: "panel-section" }, h("h3", {}, `Links${card.links.length ? ` (${card.links.length})` : ""}`),
    card.links.length ? h("ul", { class: "link-list" }, card.links.map((l, i) => h("li", {},
      h("div", { class: "link-text" },
        h("a", { href: l.url, target: "_blank", rel: "noopener noreferrer", title: l.url }, l.label),
        h("span", { class: "link-url" }, l.url)),
      h("button", { type: "button", class: "btn link-action", onclick: () => copyText(l.url), "aria-label": `Copy link ${l.label}` }, "Copy"),
      h("a", { class: "btn link-action", href: l.url, target: "_blank", rel: "noopener noreferrer", "aria-label": `Open link ${l.label}` }, "Open ↗"),
      readOnly ? null : h("button", {
        type: "button", class: "chip-remove", "aria-label": `Remove link ${l.label}`,
        onclick: () => setField(card.id, "links", card.links.filter((_, j) => j !== i), { toast: `Removed link from ${card.id}` }),
      }, "✕")))) : null,
    readOnly ? null : h("div", { class: "inline-form" }, url, label, h("button", { type: "button", class: "btn", onclick: add }, "Add")),
    error);
}

// ---------------------------------------------------------------- screenshots (this browser only, see attachments.js)

async function attachImages(card, files) {
  let added = 0;
  for (const file of files) {
    try {
      await attachments.add(card.uid, file);
      added++;
    } catch (e) {
      showToast(e.message, null, true);
    }
  }
  if (added) showToast(`Added ${added} screenshot${added === 1 ? "" : "s"} to ${card.id} (stored in this browser only).`);
}

function screenshotsSection(card, readOnly) {
  const list = attachments.forCard(card.uid);
  const picker = h("input", { type: "file", accept: "image/*", multiple: true, hidden: true, id: "card-files",
    onchange: e => attachImages(card, [...e.target.files]) });
  const drop = h("div", {
    class: "drop-zone", tabindex: "0", "aria-label": "Add screenshots: paste, drop or browse",
    ondragover: e => { e.preventDefault(); drop.classList.add("is-over"); },
    ondragleave: () => drop.classList.remove("is-over"),
    ondrop: e => { e.preventDefault(); e.stopPropagation(); drop.classList.remove("is-over"); attachImages(card, imagesFrom(e.dataTransfer)); },
  }, "Paste (Ctrl+V), drop images here, or ", h("button", { type: "button", class: "link-btn", onclick: () => picker.click() }, "browse"), ".");
  const thumbs = list.map(m => {
    const img = h("img", { alt: m.caption || m.name, loading: "lazy" });
    attachments.url(m.id).then(u => u && (img.src = u));
    return h("figure", { class: "thumb" },
      h("button", { type: "button", class: "thumb-open", "aria-label": `Enlarge ${m.caption || m.name}`, onclick: () => imageDialog(card, m) }, img),
      h("figcaption", {}, m.caption || h("span", { class: "muted" }, m.name)));
  });
  return h("section", { class: "panel-section" },
    h("h3", {}, `Screenshots${list.length ? ` (${list.length})` : ""}`),
    !attachments.available ? h("p", { class: "muted" }, "This browser does not allow storing screenshots here (private window?).")
      : [thumbs.length ? h("div", { class: "thumbs" }, ...thumbs) : null,
        readOnly ? null : [drop, picker],
        h("p", { class: "muted local-note" }, "Stored in this browser only; never uploaded to the public repository. Include them in a backup (Backup) to keep them safe.")]);
}

function imageDialog(card, m) {
  showDialog(close => {
    const img = h("img", { class: "lightbox-img", alt: m.caption || m.name });
    attachments.url(m.id).then(u => u && (img.src = u));
    const caption = h("input", { value: m.caption, placeholder: "Caption (optional)", "aria-label": "Caption", maxlength: "200" });
    const saveCaption = () => caption.value !== m.caption && attachments.setCaption(m.id, caption.value.trim());
    caption.addEventListener("keydown", e => e.key === "Enter" && (saveCaption(), close()));
    return [
      h("h2", {}, `${card.id} · ${m.name}`),
      img,
      h("label", { class: "stack" }, "Caption", caption),
      h("p", { class: "muted" }, `${Math.round(m.size / 1024)} KB · added ${formatTime(m.createdAt)} · stored in this browser only`),
      h("div", { class: "form-actions" },
        h("button", { type: "button", class: "btn", onclick: async () => {
          close(); // one dialog at a time
          if (await confirmDialog({ title: "Remove this screenshot?", message: "It is deleted from this browser. A backup that contains it can bring it back.", confirm: "Remove" })) {
            await attachments.remove(m.id);
            showToast("Screenshot removed.");
          }
        } }, "Remove"),
        h("button", { type: "button", class: "btn btn-primary", onclick: () => { saveCaption(); close(); } }, "Done")),
    ];
  });
}

function commentItems(card) {
  if (!card.comments.length) return [h("li", { class: "muted" }, "No comments yet.")];
  return card.comments.map(c => h("li", { class: "comment" },
    h("div", { class: "comment-meta" }, h("strong", {}, c.author), " · ", h("time", { datetime: c.at }, formatTime(c.at))),
    h("div", { class: "md", html: renderMarkdown(c.text) })));
}

function commentForm(card) {
  const area = h("textarea", { id: "comment-text", rows: "3", placeholder: "Add a comment…", "aria-label": "New comment" });
  const post = () => {
    if (!area.value.trim()) return;
    if (change(ops.comment(board(), card.id, area.value, now(), author()), { toast: `Comment added to ${card.id}`, undoable: false })) area.value = "";
  };
  area.addEventListener("keydown", e => e.key === "Enter" && (e.ctrlKey || e.metaKey) && post());
  return h("div", { class: "comment-form" }, area,
    h("div", { class: "form-actions" }, h("span", { class: "muted" }, "Ctrl+Enter to post. Comments cannot be edited."),
      h("button", { type: "button", class: "btn btn-primary", onclick: post }, "Comment")));
}

function historyItems(card) {
  return [...card.history].reverse().map(e => {
    const what = {
      created: `Created in ${STATUS_LABELS[e.to] ?? ""}`,
      migrated: `Migrated from BOARD.md (${STATUS_LABELS[e.to] ?? ""})`,
      status: `${STATUS_LABELS[e.from]} → ${STATUS_LABELS[e.to]}`,
      archived: "Archived",
      restored: "Restored to Done",
    }[e.type] ?? e.type;
    return h("li", {}, h("time", { datetime: e.at }, formatTime(e.at)), ` · ${e.by} · ${what}`,
      e.accepted ? h("span", { class: "accepted" }, ` · ${e.accepted}`) : null);
  });
}

// ---------------------------------------------------------------- archive

function archiveCard(id) {
  change(ops.archive(board(), id, now(), author()), { toast: `Archived ${id}` });
  if (ui.openCardId === id && !ui.archiveOpen) closePanel();
}

function restoreCard(id) {
  const to = findCard(board(), id)?.status === INBOX ? "the Inbox" : "Done";
  change(ops.restore(board(), id, now(), author()), { toast: `Restored ${id} to ${to}` });
}

async function archiveAllDone() {
  const done = columnCards(board(), "done");
  if (!done.length) return;
  const ok = await confirmDialog({
    title: `Archive ${done.length} Done card${done.length === 1 ? "" : "s"}?`,
    message: `${done.map(c => c.id).join(", ")} will leave the board. They stay searchable in the Archive and can be restored.`,
    confirm: "Archive all",
  });
  if (!ok) return;
  let archived = 0;
  for (const card of done) if (change(ops.archive(board(), card.id, now(), author()))) archived++;
  showToast(`Archived ${archived} card${archived === 1 ? "" : "s"}`, {
    label: "Undo", run: () => { for (let i = 0; i < archived; i++) engine.undo(now(), author()); },
  });
}

function openArchive() {
  ui.archiveOpen = true;
  $("archive").hidden = false;
  $("scrim").hidden = false;
  renderArchive(true);
  requestAnimationFrame(() => $("archive-search")?.focus());
}

function closeArchive() {
  ui.archiveOpen = false;
  $("archive").hidden = true;
  $("scrim").hidden = !ui.openCardId && !ui.inboxOpen;
  $("archive-open").focus();
}

function renderArchive(force = false) {
  const drawer = $("archive");
  const archived = board().cards.filter(c => c.archived).sort((a, b) => (b.archivedAt ?? "").localeCompare(a.archivedAt ?? ""));
  const list = archived.filter(c => textMatches(c, ui.archiveSearch));
  const items = list.length ? list.map(c => h("li", { class: "archive-item" },
    h("button", { type: "button", class: "archive-open", onclick: () => openCard(c.id) },
      h("span", { class: "card-id" }, c.id), " ", h("span", {}, c.title),
      h("span", { class: "muted" }, ` · archived ${formatTime(c.archivedAt)}`)),
    h("button", { type: "button", class: "btn", onclick: () => restoreCard(c.id) }, "Restore")))
    : [h("li", { class: "muted" }, archived.length ? "No archived cards match." : "Nothing archived yet. Done cards can be archived from the card or the Done column.")];
  if (!force && drawer.querySelector(".archive-list")) {
    drawer.querySelector(".archive-list").replaceChildren(...items);
    drawer.querySelector(".archive-title").textContent = `Archive (${archived.length})`;
    return;
  }
  drawer.replaceChildren(
    h("header", { class: "panel-header" }, h("h2", { class: "archive-title" }, `Archive (${archived.length})`),
      h("button", { type: "button", class: "btn icon-btn", "aria-label": "Close archive", onclick: closeArchive }, "✕")),
    h("p", { class: "muted" }, "Archived cards are kept for good, with their notes, comments and history."),
    h("input", {
      type: "search", id: "archive-search", placeholder: "Search archived cards…", "aria-label": "Search archived cards", value: ui.archiveSearch,
      oninput: e => {
        ui.archiveSearch = e.target.value;
        renderArchive();
      },
    }),
    h("ul", { class: "archive-list" }, ...items));
}

// ---------------------------------------------------------------- quick capture and inbox

/**
 * Captures a thought into the Inbox: a title is all that is needed (a pasted link or screenshot is enough too).
 * Stays in the capture box so the next thought can follow immediately.
 */
async function capture(e) {
  e?.preventDefault();
  const input = $("capture-title");
  let title = input.value.trim();
  const images = ui.pendingImages;
  if (!title && !images.length) return;
  if (!board()) return showToast("The board has not loaded yet; try again in a moment.", null, true);
  const links = [];
  if (/^https?:\/\/\S+$/.test(title)) {
    links.push({ label: linkLabel(title), url: title });
    title = linkLabel(title);
  }
  if (!title) title = `Screenshot ${formatTime(now())}`;
  const op = ops.create(board(), { title, category: null, status: INBOX, links }, now(), author());
  if (!change(op, { undoable: false })) return;
  input.value = "";
  ui.pendingImages = [];
  for (const file of images) {
    try {
      await attachments.add(op.card.uid, file);
    } catch (err) {
      showToast(err.message, null, true);
    }
  }
  render();
  showToast(`Captured to Inbox (${op.id}).`, { label: "Add details", run: () => openCard(op.id) });
}

function addPendingImages(files) {
  if (!files.length) return;
  ui.pendingImages = [...ui.pendingImages, ...files];
  render();
  $("capture-title").focus();
  showToast(`${files.length} image${files.length === 1 ? "" : "s"} ready. Type a title (optional) and press Enter.`);
}

/** Ctrl+V with an image: to the open card, otherwise into a new capture. Text pastes are left alone. */
function onPaste(e) {
  const files = imagesFrom(e.clipboardData);
  if (!files.length) return;
  const card = ui.openCardId && findCard(board(), ui.openCardId);
  e.preventDefault();
  if (card && !card.archived) attachImages(card, files);
  else addPendingImages(files);
}

/** Dropping image files anywhere outside a card's drop zone starts a capture with them. */
function onFileDrop(e) {
  if (![...(e.dataTransfer?.types ?? [])].includes("Files")) return;
  e.preventDefault();
  if (e.type !== "drop") return;
  const files = imagesFrom(e.dataTransfer);
  const card = ui.openCardId && findCard(board(), ui.openCardId);
  if (card && !card.archived) attachImages(card, files);
  else addPendingImages(files);
}

function openInbox() {
  ui.inboxOpen = true;
  $("inbox").hidden = false;
  $("scrim").hidden = false;
  renderInbox(true);
}

function closeInbox() {
  ui.inboxOpen = false;
  $("inbox").hidden = true;
  $("scrim").hidden = !ui.openCardId && !ui.archiveOpen;
  $("inbox-open").focus();
}

function ago(iso) {
  const minutes = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 24 * 60) return `${Math.round(minutes / 60)} h ago`;
  return formatTime(iso);
}

function renderInbox(force = false) {
  const drawer = $("inbox");
  const items = columnCards(board(), INBOX).reverse(); // newest first
  const list = items.filter(c => textMatches(c, ui.inboxSearch));
  const rows = list.length ? list.map(c => h("li", { class: "inbox-item", dataset: { id: c.id } },
    h("button", { type: "button", class: "archive-open", onclick: () => openCard(c.id) },
      h("span", { class: "inbox-title" }, c.title),
      h("span", { class: "muted" }, ` · ${ago(c.createdAt)}`),
      cardMeta(c)),
    h("select", {
      class: "inbox-move", "aria-label": `Put ${c.id} on the board`,
      onchange: e => e.target.value && moveCard(c.id, e.target.value),
    }, option("", "To board…", ""), ...STATUSES.filter(st => st !== "done").map(st => option(st, STATUS_LABELS[st], ""))),
    h("button", { type: "button", class: "btn", title: "Archive (keeps it, out of the way)", onclick: () => archiveCard(c.id) }, "Archive")))
    : [h("li", { class: "muted" }, items.length ? "No Inbox items match." : "The Inbox is empty. Capture thoughts with the box at the top (Enter saves).")];
  if (!force && drawer.querySelector(".inbox-list")) {
    drawer.querySelector(".inbox-list").replaceChildren(...rows);
    drawer.querySelector(".inbox-heading").textContent = `Inbox (${items.length})`;
    return;
  }
  drawer.replaceChildren(
    h("header", { class: "panel-header" }, h("h2", { class: "inbox-heading" }, `Inbox (${items.length})`),
      h("button", { type: "button", class: "btn icon-btn", "aria-label": "Close inbox", onclick: closeInbox }, "✕")),
    h("p", { class: "muted" }, "Captured thoughts, not yet on the board. Put them on the board when you are ready, or archive them."),
    h("input", {
      type: "search", id: "inbox-search", placeholder: "Search the Inbox…", "aria-label": "Search the Inbox", value: ui.inboxSearch,
      oninput: e => {
        ui.inboxSearch = e.target.value;
        renderInbox();
      },
    }),
    h("ul", { class: "inbox-list" }, ...rows));
}

// ---------------------------------------------------------------- backup and export

function download(fileName, bytes) {
  const url = URL.createObjectURL(new Blob([bytes], { type: "application/zip" }));
  const a = h("a", { href: url, download: fileName, hidden: true });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

function backupDialog() {
  if (!board()) return;
  showDialog(close => {
    const months = monthsOf(board());
    const scope = h("select", { id: "backup-scope", "aria-label": "What to back up" },
      option("all", `Everything (${board().cards.length} cards)`, "all"),
      ...months.map(m => option(`month:${m.month}`, `Created in ${m.month} (${m.cards.length} cards)`, "")),
      option("cards", "Selected cards…", ""));
    const picker = h("div", { class: "backup-picker", hidden: true },
      ...board().cards.slice().sort((a, b) => a.id.localeCompare(b.id)).map(c => h("label", { class: "check" },
        h("input", { type: "checkbox", value: c.id }), ` ${c.id} · ${c.title}${c.archived ? " (archived)" : ""}`)));
    scope.addEventListener("change", () => (picker.hidden = scope.value !== "cards"));
    const withImages = h("input", { type: "checkbox", id: "backup-images", checked: true });
    const status = h("p", { class: "muted", role: "status" });
    const downloadButton = h("button", { type: "button", class: "btn btn-primary" }, "Download ZIP");
    downloadButton.addEventListener("click", async () => {
      const value = scope.value;
      const selected = [...picker.querySelectorAll("input:checked")].map(i => i.value);
      if (value === "cards" && !selected.length) return (status.textContent = "Tick at least one card.");
      const chosen = value === "all" ? { kind: "all" } : value === "cards" ? { kind: "cards", ids: selected } : { kind: "month", month: value.slice(6) };
      downloadButton.disabled = true;
      status.textContent = "Preparing the backup…";
      try {
        const images = withImages.checked ? await attachments.all() : [];
        const result = await buildBackup({ board: board(), scope: chosen, attachments: images });
        download(result.fileName, result.bytes);
        status.textContent = `Downloaded ${result.fileName}: ${result.manifest.cards.length} cards, ${result.manifest.attachments.length} screenshots.`;
      } catch (e) {
        status.textContent = `The backup could not be created: ${e.message}`;
      } finally {
        downloadButton.disabled = false;
      }
    });
    const file = h("input", { type: "file", id: "restore-file", accept: ".zip,application/zip", "aria-label": "Backup file to restore" });
    file.addEventListener("change", () => file.files[0] && (close(), restorePreview(file.files[0])));
    return [
      h("h2", {}, "Backup & Export"),
      h("section", { class: "dialog-section" },
        h("h3", {}, "Download a backup"),
        h("label", { class: "stack" }, "What", scope), picker,
        h("label", { class: "check" }, withImages, ` Include screenshots stored in this browser (${attachments.total})`),
        h("p", { class: "muted" }, "Each ZIP restores on its own: cards with notes, comments, links, history and screenshots."),
        h("div", { class: "form-actions" }, status, downloadButton)),
      h("section", { class: "dialog-section" },
        h("h3", {}, "Restore from a backup"),
        h("p", { class: "muted" }, "You will see what changes before anything is restored. Restoring never deletes cards or comments."),
        file),
      h("section", { class: "dialog-section" },
        h("h3", {}, "Email backup"),
        h("p", { class: "muted" }, "Daily backups to Outlook are not set up yet: they wait for your decision on screenshot storage and the email service (card WT-047).")),
      h("div", { class: "form-actions" }, h("button", { type: "button", class: "btn", onclick: () => close() }, "Close")),
    ];
  });
}

async function restorePreview(fileObj) {
  let backup;
  try {
    backup = await parseBackup(new Uint8Array(await fileObj.arrayBuffer()));
  } catch (e) {
    return showToast(`Cannot restore ${fileObj.name}: ${e.message}`, null, true);
  }
  const plan = planRestore(board(), backup.cards);
  const counts = { new: 0, changed: 0, same: 0 };
  plan.forEach(p => counts[p.state]++);
  showDialog(close => {
    const rows = plan.map((p, i) => {
      const box = h("input", { type: "checkbox", value: String(i), checked: p.state === "new" || (p.state === "changed" && !p.boardNewer), disabled: p.state === "same" });
      const what = p.state === "new" ? (p.idTaken ? `New (gets a new number: ${p.card.id} is used by another card)` : "New")
        : p.state === "same" ? "Unchanged" : `Changes: ${p.fields.join(", ")}${p.boardNewer ? " · the board was edited after this backup" : ""}`;
      return h("li", { class: `restore-item restore-${p.state}` }, h("label", { class: "check" }, box, ` ${p.card.id} · ${p.card.title}`), h("span", { class: "muted" }, what));
    });
    const list = h("ul", { class: "restore-list" }, ...rows);
    const apply = h("button", { type: "button", class: "btn btn-primary" }, "Restore selected");
    apply.addEventListener("click", async () => {
      const chosen = [...list.querySelectorAll("input:checked")].map(b => plan[Number(b.value)]);
      if (!chosen.length) return close();
      const uids = new Set(chosen.map(p => p.card.uid));
      let applied = 0;
      for (const op of restoreOps(board(), chosen, now(), author(), fileObj.name)) if (change(op, { undoable: false })) applied++;
      let images = 0;
      for (const a of backup.attachments) {
        if (!uids.has(a.meta.cardUid) && !board().cards.some(c => c.uid === a.meta.cardUid)) continue;
        try {
          if (await attachments.importAttachment(a.meta, a.bytes)) images++;
        } catch (e) {
          showToast(e.message, null, true);
        }
      }
      close();
      showToast(`Restored ${applied} card${applied === 1 ? "" : "s"} and ${images} screenshot${images === 1 ? "" : "s"} from ${fileObj.name}.`);
    });
    return [
      h("h2", {}, `Restore ${fileObj.name}`),
      h("p", {}, `Backup from ${formatTime(backup.manifest.createdAt)}: ${backup.cards.length} cards (${counts.new} new, ${counts.changed} changed, ${counts.same} unchanged), ${backup.attachments.length} screenshots.`),
      backup.problems.length ? h("ul", { class: "field-error" }, ...backup.problems.map(p => h("li", {}, p))) : null,
      list,
      h("p", { class: "muted" }, "Checked cards get the backup's content; comments are merged, none are removed. Cards edited on the board after the backup are unchecked. Restoring cannot be undone with Undo; you can restore an older backup instead."),
      h("div", { class: "form-actions" },
        h("button", { type: "button", class: "btn", onclick: () => close() }, "Cancel"), apply),
    ];
  });
}

// ---------------------------------------------------------------- dialogs and toasts

function showDialog(build) {
  const root = $("dialog");
  const previous = document.activeElement;
  return new Promise(resolve => {
    const close = value => {
      root.hidden = true;
      root.replaceChildren();
      document.removeEventListener("keydown", onKey, true);
      previous?.focus?.();
      resolve(value);
    };
    const onKey = e => {
      if (e.key === "Escape") {
        e.stopPropagation();
        close(null);
      }
    };
    document.addEventListener("keydown", onKey, true);
    const box = h("div", { class: "dialog", role: "dialog", "aria-modal": "true" }, ...build(close));
    root.replaceChildren(h("div", { class: "dialog-backdrop", onclick: () => close(null) }), box);
    root.hidden = false;
    requestAnimationFrame(() => box.querySelector("[autofocus], input, textarea, .btn-primary")?.focus());
  });
}

function confirmDialog({ title, message, confirm }) {
  return showDialog(close => [
    h("h2", {}, title), h("p", {}, message),
    h("div", { class: "form-actions" },
      h("button", { type: "button", class: "btn", onclick: () => close(false) }, "Cancel"),
      h("button", { type: "button", class: "btn btn-primary", onclick: () => close(true) }, confirm)),
  ]).then(v => v === true);
}

function newCardDialog() {
  if (!board()) return;
  showDialog(close => {
    const title = h("input", { id: "new-title", maxlength: "200", required: true, "aria-label": "Title", autofocus: true });
    const category = h("select", { id: "new-category", "aria-label": "Category" }, CATEGORIES.map(c => option(c, CATEGORY_LABELS[c], "feature")));
    const priority = h("select", { id: "new-priority", "aria-label": "Priority" }, PRIORITIES.map(p => option(p, PRIORITY_LABELS[p], "normal")));
    const status = h("select", { id: "new-status", "aria-label": "Status" }, STATUSES.filter(s => s !== "done").map(s => option(s, STATUS_LABELS[s], "backlog")));
    const description = h("textarea", { id: "new-description", rows: "4", "aria-label": "Description" });
    const error = h("p", { class: "field-error", role: "alert" });
    const create = () => {
      if (!title.value.trim()) return (error.textContent = "Give the card a title.");
      const op = ops.create(board(), {
        title: title.value, category: category.value, priority: priority.value, status: status.value, description: description.value.trim(),
      }, now(), author());
      if (change(op, { toast: `Added ${op.id}`, undoable: false })) {
        close(true);
        openCard(op.id);
      }
    };
    title.addEventListener("keydown", e => e.key === "Enter" && create());
    return [
      h("h2", {}, "New card"),
      h("label", { class: "stack" }, "Title", title),
      h("div", { class: "field-row" }, h("label", {}, "Category", category), h("label", {}, "Priority", priority), h("label", {}, "Status", status)),
      h("label", { class: "stack" }, "Description (Markdown, optional)", description),
      error,
      h("div", { class: "form-actions" },
        h("button", { type: "button", class: "btn", onclick: () => close(false) }, "Cancel"),
        h("button", { type: "button", class: "btn btn-primary", onclick: create }, "Add card")),
    ];
  });
}

function signInDialog() {
  showDialog(close => {
    const token = h("input", { id: "token", type: "password", autocomplete: "off", spellcheck: "false", "aria-label": "GitHub token", autofocus: true });
    const remember = h("input", { id: "remember", type: "checkbox" });
    const error = h("p", { class: "field-error", role: "alert" });
    const submit = h("button", { type: "button", class: "btn btn-primary" }, "Sign in");
    const go = async () => {
      const value = token.value.trim();
      if (!value) return (error.textContent = "Paste your token first.");
      submit.disabled = true;
      error.textContent = "";
      const candidate = new GitHubStore({ owner: OWNER, repo: REPO, branch: BRANCH, token: value });
      try {
        const login = await candidate.whoami();
        await candidate.load();
        credentials.save(value, remember.checked);
        store.setToken(value);
        ui.login = login;
        safeSet(localStorage, LOGIN_KEY, login);
        token.value = "";
        close(true);
        showToast(`Signed in as ${login}. Changes now save to GitHub.`);
        engine.retry();
        render();
      } catch (e) {
        error.textContent = e.kind === "auth" ? "GitHub did not accept this token. Check that it is copied completely and has not expired."
          : e.kind === "notfound" ? "The token works but cannot see crosenlund79/TimeAndTask. Give it access to that repository."
            : e.message;
        submit.disabled = false;
      }
    };
    submit.addEventListener("click", go);
    token.addEventListener("keydown", e => e.key === "Enter" && go());
    return [
      h("h2", {}, "Sign in with a GitHub token"),
      h("p", {}, "Changes are saved to docs/board.json on the ", h("code", {}, BRANCH), " branch of crosenlund79/TimeAndTask."),
      h("ol", { class: "steps" },
        h("li", {}, "Open ", h("a", { href: "https://github.com/settings/personal-access-tokens/new", target: "_blank", rel: "noopener noreferrer" }, "GitHub › Fine-grained tokens › Generate new token"), "."),
        h("li", {}, "Repository access: Only select repositories › crosenlund79/TimeAndTask. Expiration: 90 days."),
        h("li", {}, "Repository permissions: Contents › Read and write. Nothing else."),
        h("li", {}, "Generate, copy, and paste it here.")),
      h("label", { class: "stack" }, "Token", token),
      h("label", { class: "check" }, remember, " Remember on this device (otherwise only until this tab is closed)"),
      h("p", { class: "muted" }, "The token stays in this browser and is sent only to api.github.com. Sign out removes it."),
      error,
      h("div", { class: "form-actions" }, h("button", { type: "button", class: "btn", onclick: () => close(false) }, "Cancel"), submit),
    ];
  });
}

function signOut() {
  credentials.clear();
  store.setToken(null);
  ui.login = null;
  safeRemove(localStorage, LOGIN_KEY);
  showToast(engine.pending.length ? "Signed out. Unsaved changes are kept in this browser until you sign in again." : "Signed out. The token was removed from this browser.");
  render();
}

function conflictDialog() {
  showDialog(close => {
    const items = engine.conflicts.map((c, index) => {
      const card = findCard(board(), c.op.id);
      const label = `${c.op.id}${card ? ` · ${card.title}` : ""}`;
      const describeValue = v => (v === null || v === undefined ? "(none)" : typeof v === "string" ? v : c.op.type === "move" ? STATUS_LABELS[v.status] : JSON.stringify(v, null, 1));
      const mine = c.op.type === "set" ? c.op.to : c.op.type === "move" ? c.op.to : c.op.type;
      const editable = c.op.type === "set" && typeof c.op.to === "string";
      const mineInput = editable ? h("textarea", { rows: "4", "aria-label": `Your version of ${c.op.field}` }, c.op.to) : null;
      return h("li", { class: "conflict" },
        h("strong", {}, label), h("p", { class: "muted" }, `${c.reason}.`),
        h("div", { class: "conflict-versions" },
          h("div", {}, h("h4", {}, "On GitHub now"), h("pre", {}, describeValue(c.current))),
          h("div", {}, h("h4", {}, "Your change"), mineInput ?? h("pre", {}, describeValue(mine)))),
        h("div", { class: "form-actions" },
          h("button", { type: "button", class: "btn", onclick: () => { engine.resolveConflict(index, "theirs", now(), author()); close(); conflictsLeft(); } }, "Keep GitHub's version"),
          h("button", { type: "button", class: "btn btn-primary", onclick: () => { engine.resolveConflict(index, "mine", now(), author(), mineInput ? mineInput.value : undefined); close(); conflictsLeft(); } },
            mineInput ? "Use my text" : "Apply my change again")));
    });
    return [h("h2", {}, "Resolve conflicts"),
      h("p", {}, "These changes were made here while the same thing changed elsewhere. Nothing has been overwritten. Choose for each:"),
      h("ol", { class: "conflict-list" }, ...items),
      h("div", { class: "form-actions" }, h("button", { type: "button", class: "btn", onclick: () => close() }, "Decide later"))];
  });
}

function conflictsLeft() {
  if (engine.conflicts.length) conflictDialog();
}

let toastTimer = null;
function showToast(message, action = null, isError = false) {
  const toast = $("toast");
  toast.replaceChildren(h("span", {}, message),
    action ? h("button", { type: "button", class: "link-btn", onclick: () => { toast.hidden = true; action.run(); } }, action.label) : null);
  toast.dataset.error = String(isError);
  toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (toast.hidden = true), action ? 8000 : 5000);
}

// ---------------------------------------------------------------- wiring

function wire() {
  for (const c of CATEGORIES) $("filter-category").append(option(c, CATEGORY_LABELS[c], ""));
  for (const p of PRIORITIES) $("filter-priority").append(option(p, PRIORITY_LABELS[p], ""));
  for (const s of STATUSES) $("filter-status").append(option(s, STATUS_LABELS[s], ""));
  if (BRANCH !== "board") {
    $("branch-label").textContent = BRANCH;
    $("branch-label").hidden = false;
  }
  $("search").addEventListener("input", e => {
    ui.search = e.target.value;
    render();
  });
  for (const [id, key] of [["filter-category", "category"], ["filter-priority", "priority"], ["filter-status", "status"], ["filter-scope", "scope"]]) {
    $(id).addEventListener("change", e => {
      ui[key] = e.target.value;
      if (key === "status" && e.target.value) ui.mobileColumn = e.target.value;
      render();
    });
  }
  $("undo").addEventListener("click", undo);
  $("new-card").addEventListener("click", () => (store.token || engine.view ? newCardDialog() : signInDialog()));
  $("archive-open").addEventListener("click", openArchive);
  $("inbox-open").addEventListener("click", openInbox);
  $("backup-open").addEventListener("click", backupDialog);
  $("capture").addEventListener("submit", capture);
  $("capture-files").addEventListener("change", e => {
    addPendingImages([...e.target.files].filter(f => /^image\//.test(f.type)));
    e.target.value = "";
  });
  document.addEventListener("paste", onPaste);
  document.addEventListener("dragover", onFileDrop);
  document.addEventListener("drop", onFileDrop);
  $("refresh").addEventListener("click", async () => {
    await engine.refresh();
    showToast(engine.error ? engine.error.message : "Board is up to date with GitHub.", null, !!engine.error);
  });
  $("account").addEventListener("click", () => (store.token && !store.tokenRejected ? signOut() : signInDialog()));
  $("sync").addEventListener("click", () => {
    if (engine.needsAuth) signInDialog();
    else if (engine.conflicts.length) conflictDialog();
    else if (engine.error) engine.retry();
    else engine.refresh();
  });
  $("scrim").addEventListener("click", () => {
    if (ui.openCardId) closePanel();
    else if (ui.inboxOpen) closeInbox();
    else if (ui.archiveOpen) closeArchive();
  });
  document.addEventListener("pointerdown", onPointerDown);
  document.addEventListener("keydown", e => {
    const typing = e.target.closest?.("input, textarea, select");
    if (e.key === "Escape") {
      if (drag.active) return cancelDrag();
      if (ui.openCardId) return closePanel();
      if (ui.inboxOpen) return closeInbox();
      if (ui.archiveOpen) return closeArchive();
    }
    if (!typing && !e.ctrlKey && !e.metaKey && !e.altKey && e.key.toLowerCase() === "c") {
      e.preventDefault();
      $("capture-title").focus();
    }
    if (!typing && (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z" && !e.shiftKey) {
      e.preventDefault();
      undo();
    }
    if (!typing && e.key === "/" && !e.ctrlKey) {
      e.preventDefault();
      $("search").focus();
    }
  });
  addEventListener("online", () => engine.pending.length && engine.retry());
  addEventListener("offline", () => render());
  document.addEventListener("visibilitychange", () => document.visibilityState === "visible" && engine.refresh());
  const poll = () => {
    setTimeout(async () => {
      if (document.visibilityState === "visible" && !drag.active) await engine.refresh();
      poll();
    }, store.token ? POLL_SIGNED_IN_MS : POLL_SIGNED_OUT_MS);
  };
  poll();
  // Phones: show the column with the most to review first.
  if (matchMedia("(max-width: 760px)").matches) ui.mobileColumn = "review";
}

wire();
render();
attachments.open();
engine.start().then(() => {
  if (store.token && !ui.login) store.whoami().then(login => {
    ui.login = login;
    safeSet(localStorage, LOGIN_KEY, login);
    render();
  }).catch(() => {});
  render();
});

// Read-only hooks for the automated browser tests. They expose board data only, never the token.
window.__board = {
  view: () => engine.view,
  status: () => engine.status,
  pending: () => engine.pending.length,
  conflicts: () => engine.conflicts.length,
  saveNow: () => engine.saveNow(),
  refresh: () => engine.refresh(),
};
