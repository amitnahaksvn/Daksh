"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { app, BrowserWindow, Menu, session, shell, screen } = require("electron");
const { resolveAppUrl } = require("./config.cjs");
const { classifyNavigation, classifyWindowOpen, deepLinkToPath, pageUrl } = require("./policy.cjs");
const { restoreBounds } = require("./windowState.cjs");
const { buildMenuTemplate } = require("./menu.cjs");

// Daksha for Windows and macOS: the Daksha web app in a native window. Everything the web app can do works here (all connected apps, the
// Manage popups, notifications), plus: one window per person, daksha:// links, remembered window size, a native menu and an offline page.

const isMac = process.platform === "darwin";
const isDev = !app.isPackaged;
const PROTOCOL = "daksha";

let appConfig;
let mainWindow = null;
let pendingPath = null;
const popups = new WeakSet();

function readJson(file) { try { return JSON.parse(fs.readFileSync(file, "utf8")); } catch { return null; } }
const stateFile = () => path.join(app.getPath("userData"), "window-state.json");
const settingsFile = () => path.join(app.getPath("userData"), "config.json");

function loadConfig() {
  appConfig = resolveAppUrl({ env: process.env, fileConfig: readJson(settingsFile()), packaged: readJson(path.join(__dirname, "app-config.json")) });
  if (appConfig.warning) console.warn(`[daksha] ${appConfig.warning} Using ${appConfig.url}.`);
}

function saveState(win) {
  try {
    const maximized = win.isMaximized();
    const b = maximized ? win.getNormalBounds() : win.getBounds();
    fs.writeFileSync(stateFile(), JSON.stringify({ ...b, maximized }));
  } catch { /* losing the window position is harmless */ }
}

function openExternal(url) { shell.openExternal(url).catch(() => {}); }

/** Applies the navigation and popup rules to every window, including sign-in popups. */
function guard(contents) {
  contents.on("will-attach-webview", (e) => e.preventDefault());
  contents.setWindowOpenHandler(({ url }) => {
    const kind = classifyWindowOpen(url, appConfig.origin);
    if (kind === "popup") {
      return { action: "allow", overrideBrowserWindowOptions: { width: 560, height: 720, autoHideMenuBar: true, webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false } } };
    }
    if (kind === "external") openExternal(url);
    return { action: "deny" };
  });
  contents.on("did-create-window", (child) => { popups.add(child.webContents); });
  const onNavigate = (event, url) => {
    const verdict = classifyNavigation(url, appConfig.origin, popups.has(contents));
    if (verdict === "allow") return;
    event.preventDefault();
    if (verdict === "external") openExternal(url);
  };
  contents.on("will-navigate", onNavigate);
  contents.on("will-redirect", onNavigate);
}

function createWindow(startPath = "/dashboard") {
  const state = restoreBounds(readJson(stateFile()), screen.getAllDisplays());
  const win = new BrowserWindow({
    width: state.width, height: state.height, ...(state.x !== undefined ? { x: state.x, y: state.y } : {}),
    minWidth: 420, minHeight: 560, show: false, title: "Daksha", backgroundColor: "#0b0b10",
    webPreferences: { preload: path.join(__dirname, "preload.cjs"), contextIsolation: true, sandbox: true, nodeIntegration: false, spellcheck: true },
  });
  if (state.maximized) win.maximize();
  win.once("ready-to-show", () => win.show());
  win.on("close", () => saveState(win));
  win.webContents.on("did-fail-load", (_e, code, _desc, url, isMainFrame) => {
    if (!isMainFrame || code === -3) return; // -3 = cancelled by a newer navigation
    win.loadFile(path.join(__dirname, "offline.html"), { query: { target: url.startsWith("http") ? url : appConfig.url } });
  });
  win.loadURL(pageUrl(appConfig.url, startPath));
  if (!mainWindow) mainWindow = win;
  win.on("closed", () => { if (mainWindow === win) mainWindow = null; });
  return win;
}

function goTo(pagePath, { newWindow = false } = {}) {
  if (newWindow || !mainWindow) { createWindow(pagePath); return; }
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.focus();
  mainWindow.loadURL(pageUrl(appConfig.url, pagePath));
}

function handleDeepLink(raw) {
  const p = deepLinkToPath(raw);
  if (!p) return;
  if (app.isReady() && mainWindow) goTo(p); else pendingPath = p;
}

function setPermissions() {
  // Notifications and the clipboard are what the web app uses; the camera, microphone, location and the rest are refused.
  const allowed = new Set(["notifications", "clipboard-sanitized-write", "fullscreen"]);
  session.defaultSession.setPermissionRequestHandler((wc, permission, callback) => {
    let ok = false;
    try { ok = allowed.has(permission) && new URL(wc.getURL()).origin === appConfig.origin; } catch { /* about:blank */ }
    callback(ok);
  });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", (_e, argv) => {
    const link = argv.find((a) => a.startsWith(`${PROTOCOL}://`));
    if (link) handleDeepLink(link);
    else if (mainWindow) { if (mainWindow.isMinimized()) mainWindow.restore(); mainWindow.focus(); }
  });
  app.on("open-url", (e, url) => { e.preventDefault(); handleDeepLink(url); }); // macOS delivers daksha:// links here
  app.on("web-contents-created", (_e, contents) => guard(contents));

  app.whenReady().then(() => {
    loadConfig();
    if (process.defaultApp && process.argv.length >= 2) app.setAsDefaultProtocolClient(PROTOCOL, process.execPath, [path.resolve(process.argv[1])]);
    else app.setAsDefaultProtocolClient(PROTOCOL);
    setPermissions();
    Menu.setApplicationMenu(Menu.buildFromTemplate(buildMenuTemplate({ isMac, isDev, go: goTo, openExternal, appName: app.name, appUrl: appConfig.url })));
    const launchLink = process.argv.find((a) => a.startsWith(`${PROTOCOL}://`));
    if (launchLink && !pendingPath) pendingPath = deepLinkToPath(launchLink);
    createWindow(pendingPath || "/dashboard");
    pendingPath = null;
    app.on("activate", () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
  });

  app.on("window-all-closed", () => { if (!isMac) app.quit(); });
}
