// A fake api.github.com for browser tests, plugged in with Playwright's page.route(). It implements the four
// endpoints the board uses with GitHub's real semantics: the save must name the current blob sha (else 409).
import { createHash } from "node:crypto";
import { clone, parseBoard, serializeBoard } from "../../app/board-core.js";

export const VALID_TOKEN = "fake-token-valid";

export class FakeApi {
  constructor(board, { branch = "board" } = {}) {
    this.branch = branch;
    this.saves = [];
    this.requests = [];
    this.next = []; // queued failures: { on: "save" | "load", kind: "abort" | number }
    this.validTokens = new Set([VALID_TOKEN]);
    this.commit = 0;
    this.write(serializeBoard(board));
  }

  write(text) {
    this.text = text;
    this.sha = createHash("sha1").update(`blob ${text}`).digest("hex");
    this.commit++;
  }

  get board() {
    return parseBoard(this.text);
  }

  /** Another editor (Claude Code, another browser) changes the board directly on "GitHub". */
  external(change) {
    const board = clone(this.board);
    change(board);
    this.write(serializeBoard(board));
  }

  async attach(context) {
    await context.route("https://api.github.com/**", route => this.handle(route));
  }

  async handle(route) {
    const request = route.request();
    const url = new URL(request.url());
    const auth = request.headers().authorization ?? null;
    this.requests.push({ method: request.method(), url: request.url(), auth });
    const json = (status, body) => route.fulfill({
      status, contentType: "application/json", body: JSON.stringify(body),
      headers: { "access-control-allow-origin": "*" },
    });
    const fail = this.takeFailure(request.method() === "PUT" ? "save" : "load");
    if (fail === "abort") return route.abort("internetdisconnected");
    if (typeof fail === "number") return json(fail, { message: `fake ${fail}` });

    if (auth && !this.validTokens.has(auth.replace(/^Bearer /, ""))) return json(401, { message: "Bad credentials" });
    const base = "/repos/crosenlund79/TimeAndTask";
    if (url.pathname === "/user") return auth ? json(200, { login: "crosenlund79" }) : json(401, { message: "Requires authentication" });
    if (url.pathname === `${base}/git/ref/heads/${this.branch}`) return json(200, { object: { sha: `commit${this.commit}` } });
    if (url.pathname === `${base}/contents/docs/board.json`) {
      if (request.method() === "GET") {
        return json(200, { sha: this.sha, content: Buffer.from(this.text, "utf8").toString("base64").replace(/(.{60})/g, "$1\n") });
      }
      if (request.method() === "PUT") {
        if (!auth) return json(401, { message: "Requires authentication" });
        const body = JSON.parse(request.postData());
        if (body.branch !== this.branch) return json(422, { message: "wrong branch" });
        if (body.sha !== this.sha) return json(409, { message: `docs/board.json does not match ${body.sha}` });
        this.write(Buffer.from(body.content, "base64").toString("utf8"));
        this.saves.push(body.message);
        return json(200, { content: { sha: this.sha }, commit: { sha: `commit${this.commit}` } });
      }
    }
    return json(404, { message: "Not Found" });
  }

  takeFailure(on) {
    const i = this.next.findIndex(f => f.on === on);
    return i < 0 ? null : this.next.splice(i, 1)[0].kind;
  }
}
