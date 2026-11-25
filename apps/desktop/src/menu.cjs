"use strict";
// The native menu. Navigation items load pages of the web app in the main window.

const PAGES = [
  ["Dashboard", "/dashboard", "1"], ["Assistant", "/assistant", "2"], ["Apps", "/apps", "3"], ["Calendar", "/calendar", "4"], ["Email", "/email", "5"],
  ["Tasks", "/tasks", "6"], ["Lists", "/lists", "7"], ["Reminders", "/reminders", "8"], ["Notifications", "/notifications", "9"],
];

/** @param {{ isMac: boolean, isDev: boolean, go: (path: string) => void, openExternal: (url: string) => void, appName: string, appUrl: string }} o */
function buildMenuTemplate({ isMac, isDev, go, openExternal, appName, appUrl }) {
  const view = [
    ...PAGES.map(([label, path, key]) => ({ label, accelerator: `CmdOrCtrl+${key}`, click: () => go(path) })),
    { type: "separator" }, { label: "Profile", click: () => go("/profile") }, { label: "Activity", click: () => go("/activity") },
    { type: "separator" }, { role: "reload" }, { role: "forceReload" }, { type: "separator" },
    { role: "resetZoom" }, { role: "zoomIn" }, { role: "zoomOut" }, { type: "separator" }, { role: "togglefullscreen" },
    ...(isDev ? [{ type: "separator" }, { role: "toggleDevTools" }] : []),
  ];
  return [
    ...(isMac ? [{ label: appName, submenu: [{ role: "about" }, { type: "separator" }, { role: "services" }, { type: "separator" }, { role: "hide" }, { role: "hideOthers" }, { role: "unhide" }, { type: "separator" }, { role: "quit" }] }] : []),
    { label: "File", submenu: [{ label: "New window", accelerator: "CmdOrCtrl+Shift+N", click: () => go("/dashboard", { newWindow: true }) }, { type: "separator" }, isMac ? { role: "close" } : { role: "quit" }] },
    { label: "Edit", submenu: [{ role: "undo" }, { role: "redo" }, { type: "separator" }, { role: "cut" }, { role: "copy" }, { role: "paste" }, { role: "selectAll" }] },
    { label: "View", submenu: view },
    { label: "Window", submenu: isMac ? [{ role: "minimize" }, { role: "zoom" }, { type: "separator" }, { role: "front" }] : [{ role: "minimize" }, { role: "close" }] },
    { role: "help", submenu: [{ label: "Privacy", click: () => go("/privacy") }, { label: "Terms", click: () => go("/legal/terms") }, { label: "Daksha on the web", click: () => openExternal(appUrl) }] },
  ];
}

module.exports = { PAGES, buildMenuTemplate };
