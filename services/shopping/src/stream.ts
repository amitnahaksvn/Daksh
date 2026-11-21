import crypto from "node:crypto";
import type { Server } from "node:http";
import type { CDPSession, Page } from "playwright-core";
import { WebSocketServer, type WebSocket } from "ws";
import { config } from "./config.js";
import { getRemotePage } from "./sessions.js";

/**
 * A live view of the sign-in browser, for cloud mode. The API hands the user a short-lived signed token; the web page opens a WebSocket here with it,
 * receives JPEG frames of the browser, and sends back clicks, keys and scrolls. The user types their own password, codes and captchas into it.
 * Only the one browser that is signing in is shown, only to the user the token was made for, for a few minutes.
 */

const b64u = (b: Buffer | string): string => (typeof b === "string" ? Buffer.from(b, "utf8") : b).toString("base64url");
const sign = (payload: string) => b64u(crypto.createHmac("sha256", config.internalServiceKey).update(payload).digest());

/** Token format (also made by the API): base64url(JSON {u: userId, s: store, e: expiry unix seconds}) + "." + base64url(HMAC-SHA256(key, first part)). */
export function verifyToken(token: string): { userId: string; store: string } | null {
  const [payload, mac] = token.split(".");
  if (!payload || !mac) return null;
  const expected = Buffer.from(sign(payload)), given = Buffer.from(mac);
  if (expected.length !== given.length || !crypto.timingSafeEqual(expected, given)) return null;
  try {
    const { u, s, e } = JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as { u?: string; s?: string; e?: number };
    if (!u || !s || typeof e !== "number" || e < Date.now() / 1000) return null;
    return { userId: u, store: s };
  } catch { return null; }
}

type Msg =
  | { type: "mouse"; kind: "move" | "down" | "up"; x: number; y: number; button?: "left" | "right" | "middle" }
  | { type: "wheel"; x: number; y: number; dx: number; dy: number }
  | { type: "key"; kind: "down" | "up"; key: string }
  | { type: "text"; text: string };

const num = (v: unknown, lo: number, hi: number): number | null => (typeof v === "number" && Number.isFinite(v) && v >= lo && v <= hi ? v : null);

async function apply(page: Page, m: Msg): Promise<void> {
  const { width, height } = config.streamViewport;
  if (m.type === "mouse") {
    const x = num(m.x, 0, width), y = num(m.y, 0, height);
    if (x === null || y === null) return;
    await page.mouse.move(x, y);
    if (m.kind === "down") await page.mouse.down({ button: m.button ?? "left" });
    if (m.kind === "up") await page.mouse.up({ button: m.button ?? "left" });
  } else if (m.type === "wheel") {
    const x = num(m.x, 0, width), y = num(m.y, 0, height), dx = num(m.dx, -2000, 2000), dy = num(m.dy, -2000, 2000);
    if (x === null || y === null || dx === null || dy === null) return;
    await page.mouse.move(x, y);
    await page.mouse.wheel(dx, dy);
  } else if (m.type === "key" && typeof m.key === "string" && m.key.length <= 20) {
    if (m.kind === "down") await page.keyboard.down(m.key);
    else await page.keyboard.up(m.key);
  } else if (m.type === "text" && typeof m.text === "string" && m.text.length <= 200) {
    await page.keyboard.insertText(m.text);
  }
}

export function attachStream(server: Server): void {
  const wss = new WebSocketServer({ server, path: "/stream", maxPayload: 4096 });
  wss.on("connection", (ws: WebSocket, req) => {
    const token = new URL(req.url ?? "", "http://x").searchParams.get("t") ?? "";
    const who = verifyToken(token);
    if (!who) { ws.close(4401, "Invalid or expired link"); return; }
    void begin(ws, who);
  });

  async function begin(ws: WebSocket, who: { userId: string; store: string }): Promise<void> {
    // The browser may still be opening when the page connects: wait a little for it.
    let page: Page | null = null;
    for (let i = 0; i < 20 && !page; i++) {
      page = getRemotePage(who.userId, who.store);
      if (!page) await new Promise((r) => setTimeout(r, 500));
    }
    if (!page || ws.readyState !== ws.OPEN) { ws.close(4404, "No sign-in in progress"); return; }
    run(ws, who, page);
  }

  function run(ws: WebSocket, who: { userId: string; store: string }, page: Page): void {
    let cdp: CDPSession | null = null;
    let closed = false;
    void (async () => {
      try {
        cdp = await page.context().newCDPSession(page);
        cdp.on("Page.screencastFrame", (frame: { data: string; sessionId: number }) => {
          if (ws.readyState === ws.OPEN && ws.bufferedAmount < 2_000_000) ws.send(JSON.stringify({ type: "frame", data: frame.data }));
          void cdp?.send("Page.screencastFrameAck", { sessionId: frame.sessionId }).catch(() => undefined);
        });
        await cdp.send("Page.startScreencast", { format: "jpeg", quality: 55, maxWidth: config.streamViewport.width, maxHeight: config.streamViewport.height, everyNthFrame: 2 });
        ws.send(JSON.stringify({ type: "ready", width: config.streamViewport.width, height: config.streamViewport.height }));
      } catch { ws.close(1011, "Could not start the view"); }
    })();

    // When sign-in finishes (or the browser closes) the session disappears: tell the page.
    const watch = setInterval(() => {
      if (!getRemotePage(who.userId, who.store) && ws.readyState === ws.OPEN) { ws.send(JSON.stringify({ type: "done" })); ws.close(1000, "done"); }
    }, 1500);

    ws.on("message", (raw) => {
      let m: Msg;
      try { m = JSON.parse(raw.toString()) as Msg; } catch { return; }
      void apply(page, m).catch(() => undefined);
    });
    const cleanup = () => {
      if (closed) return;
      closed = true;
      clearInterval(watch);
      void cdp?.send("Page.stopScreencast").catch(() => undefined);
      void cdp?.detach().catch(() => undefined);
    };
    ws.on("close", cleanup);
    ws.on("error", cleanup);
  }
}
