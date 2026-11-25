"use strict";
// Where the Daksha web app lives. Order: DAKSHA_APP_URL, then the person's config.json in the app data folder, then the address built into this
// release (src/app-config.json). A bad value never stops the app from starting: it falls back and says why.

const FALLBACK_URL = "https://qa.daksha.ampalab.com";
const LOOPBACK = new Set(["localhost", "127.0.0.1", "[::1]", "::1"]);

/** Returns a clean absolute address (no trailing slash, no credentials) or throws. https is required except for localhost. */
function normalizeAppUrl(raw) {
  if (typeof raw !== "string" || raw.trim() === "") throw new Error("empty address");
  const u = new URL(raw.trim());
  if (u.username || u.password) throw new Error("the address must not contain a user name or password");
  const local = LOOPBACK.has(u.hostname);
  if (u.protocol !== "https:" && !(local && u.protocol === "http:")) throw new Error("use an https:// address (http:// is only allowed for localhost)");
  u.hash = "";
  u.search = "";
  return u.toString().replace(/\/+$/, "");
}

/** @returns {{ url: string, origin: string, source: string, warning?: string }} */
function resolveAppUrl({ env = {}, fileConfig = null, packaged = null } = {}) {
  const candidates = [
    ["environment", env.DAKSHA_APP_URL],
    ["settings file", fileConfig && fileConfig.appUrl],
    ["this release", packaged && packaged.appUrl],
  ].filter(([, v]) => v !== undefined && v !== null && v !== "");
  let warning;
  for (const [source, raw] of candidates) {
    try {
      const url = normalizeAppUrl(raw);
      return { url, origin: new URL(url).origin, source, ...(warning ? { warning } : {}) };
    } catch (err) {
      warning = `Ignored the address from the ${source}: ${err.message}.`;
    }
  }
  return { url: FALLBACK_URL, origin: new URL(FALLBACK_URL).origin, source: "default", ...(warning ? { warning } : {}) };
}

module.exports = { FALLBACK_URL, normalizeAppUrl, resolveAppUrl };
