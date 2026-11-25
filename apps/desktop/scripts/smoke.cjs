"use strict";
// Launches the real desktop app (needs a display: use xvfb-run on Linux) against a throwaway local server and checks what matters:
//   - the page loads and sees only the tiny preload API (no Node)
//   - a sign-in style popup (window.open about:blank, then navigate) can post back to the app
//   - other web links and navigations never replace the app window
//   - an unreachable server shows the offline page
// Usage: xvfb-run -a node scripts/smoke.cjs

const http = require("node:http");
const os = require("node:os");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const assert = require("node:assert/strict");

const electron = require("electron");
const root = path.join(__dirname, "..");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const PAGE = `<!doctype html><title>Smoke dashboard</title><body>hello<script>
  window.__popupResult = null;
  window.addEventListener("message", (e) => { if (e.origin === location.origin) window.__popupResult = e.data; });
  window.openSignIn = () => { const p = window.open("about:blank", "daksh-test-oauth", "popup=yes,width=560,height=720"); p.location.href = location.origin + "/popup"; return !!p; };
</script>`;
const POPUP = `<!doctype html><title>popup</title><script>
  window.opener.postMessage({ type: "daksh-app-oauth", key: "t", result: "connected" }, location.origin); window.close();
</script>`;

function startServer() {
  return new Promise((resolve) => {
    const s = http.createServer((req, res) => { res.setHeader("content-type", "text/html"); res.end(req.url.startsWith("/popup") ? POPUP : PAGE); });
    s.listen(0, "127.0.0.1", () => resolve(s));
  });
}

async function launch(appUrl, debugPort) {
  const userData = fs.mkdtempSync(path.join(os.tmpdir(), "daksha-smoke-"));
  const child = spawn(electron, [root, "--no-sandbox", "--disable-gpu", `--remote-debugging-port=${debugPort}`, `--user-data-dir=${userData}`],
    { env: { ...process.env, DAKSHA_APP_URL: appUrl, ELECTRON_ENABLE_LOGGING: "0" }, stdio: "ignore" });
  return { child, stop: () => { child.kill("SIGKILL"); } };
}

async function targets(port) {
  for (let i = 0; i < 80; i++) {
    try { const r = await fetch(`http://127.0.0.1:${port}/json/list`); const t = (await r.json()).filter((x) => x.type === "page"); if (t.length) return t; } catch { /* not up yet */ }
    await sleep(250);
  }
  throw new Error("the app did not open a window");
}

async function evaluate(wsUrl, expression) {
  const ws = new WebSocket(wsUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error("debugger connection failed")); });
  const result = await new Promise((res) => {
    ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id === 1) res(d.result); };
    ws.send(JSON.stringify({ id: 1, method: "Runtime.evaluate", params: { expression, returnByValue: true, awaitPromise: true } }));
  });
  ws.close();
  return result.result ? result.result.value : undefined;
}

async function waitFor(fn, what) {
  for (let i = 0; i < 60; i++) { const v = await fn(); if (v) return v; await sleep(250); }
  throw new Error(`timed out waiting for ${what}`);
}

(async () => {
  const server = await startServer();
  const appUrl = `http://localhost:${server.address().port}`;
  let run = await launch(appUrl, 9341);
  try {
    let pages = await targets(9341);
    const main = await waitFor(async () => (await targets(9341)).find((t) => t.title === "Smoke dashboard"), "the app page");
    assert.ok(main.url.startsWith(appUrl + "/dashboard"), `opened ${main.url}`);
    console.log("ok  the app page loads at /dashboard");

    const info = JSON.parse(await evaluate(main.webSocketDebuggerUrl, "JSON.stringify({ d: window.dakshaDesktop, r: typeof require, p: typeof process })"));
    assert.equal(info.d.isDesktop, true); assert.equal(info.r, "undefined"); assert.equal(info.p, "undefined");
    console.log(`ok  the page sees only { isDesktop, platform: ${info.d.platform} } and no Node`);

    await evaluate(main.webSocketDebuggerUrl, "window.openSignIn()");
    const posted = await waitFor(async () => evaluate(main.webSocketDebuggerUrl, "JSON.stringify(window.__popupResult)"), "the popup message");
    assert.deepEqual(JSON.parse(posted), { type: "daksh-app-oauth", key: "t", result: "connected" });
    console.log("ok  a sign-in popup can report back to the app and close");

    await evaluate(main.webSocketDebuggerUrl, "window.open('https://example.com/'); true");
    await evaluate(main.webSocketDebuggerUrl, "setTimeout(() => { location.href = 'https://example.org/'; }, 0); true");
    await sleep(1500);
    pages = await targets(9341);
    assert.ok(!pages.some((t) => /example\.(com|org)/.test(t.url)), "an external page replaced or joined the app window");
    assert.ok(pages.some((t) => t.title === "Smoke dashboard"), "the app window is gone");
    console.log("ok  external links and navigations do not open inside the app");
  } finally { run.stop(); }

  server.close();
  const dead = `http://localhost:${server.address ? 1 : 1}`; // nothing listens on port 1
  run = await launch(dead, 9342);
  try {
    const offline = await waitFor(async () => (await targets(9342)).find((t) => t.url.startsWith("file:") && t.url.includes("offline.html")), "the offline page");
    const text = await evaluate(offline.webSocketDebuggerUrl, "document.body.innerText");
    assert.match(text, /can't be reached/);
    console.log("ok  an unreachable server shows the offline page");
  } finally { run.stop(); }
  console.log("smoke test passed");
  process.exit(0);
})().catch((err) => { console.error("smoke test FAILED:", err.message); process.exit(1); });
