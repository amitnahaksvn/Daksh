"use strict";
// What the desktop window may open or navigate to. The web app is trusted; everything else is handed to the person's browser.

const EXTERNAL_PROTOCOLS = new Set(["http:", "https:", "mailto:", "tel:"]);

function parse(url) { try { return new URL(url); } catch { return null; } }

function isSameOrigin(url, appOrigin) { const u = parse(url); return u !== null && u.origin === appOrigin; }

/** Links that may be sent to the system (browser, mail app, dialer). Never file:, javascript:, data: or custom schemes. */
function isSafeExternal(url) { const u = parse(url); return u !== null && EXTERNAL_PROTOCOLS.has(u.protocol); }

/**
 * window.open(): the web app opens its sign-in popups as about:blank and then points them at the provider, so those are allowed
 * (same-origin windows too). Any other web link opens in the person's own browser.
 * @returns {"popup"|"external"|"deny"}
 */
function classifyWindowOpen(url, appOrigin) {
  if (url === "about:blank" || url === "" || isSameOrigin(url, appOrigin)) return "popup";
  return isSafeExternal(url) ? "external" : "deny";
}

/**
 * A navigation inside a window. The main window stays on the app (other web links open externally). A sign-in popup may go to any https
 * page, because providers redirect through their own login and single-sign-on pages before coming back to the app.
 * @returns {"allow"|"external"|"deny"}
 */
function classifyNavigation(url, appOrigin, isPopup) {
  const u = parse(url);
  if (u === null) return "deny";
  if (u.protocol === "file:" || u.protocol === "about:") return "allow"; // the offline page and blank popups are ours
  if (u.origin === appOrigin) return "allow";
  if (isPopup) return u.protocol === "https:" ? "allow" : "deny";
  return isSafeExternal(url) ? "external" : "deny";
}

/** daksha://apps/youtube?tab=x  ->  /apps/youtube?tab=x  (null when the link is not one of ours or looks unsafe). */
function deepLinkToPath(raw) {
  const u = parse(raw);
  if (u === null || u.protocol !== "daksha:") return null;
  const path = `/${u.hostname}${u.pathname}`.replace(/\/+$/, "") || "/";
  if (!/^\/[A-Za-z0-9_\-./\[\]]*$/.test(path) || path.includes("..") || path.includes("//")) return null;
  return path + u.search;
}

/** The address of an app page for a path like "/apps/youtube". */
function pageUrl(appUrl, path) {
  const base = appUrl.replace(/\/+$/, "");
  return path.startsWith("/") ? base + path : `${base}/${path}`;
}

module.exports = { isSameOrigin, isSafeExternal, classifyWindowOpen, classifyNavigation, deepLinkToPath, pageUrl };
