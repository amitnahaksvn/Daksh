import os from "node:os";
import path from "node:path";

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

export const config = {
  port: Number(process.env.PORT ?? 4300),
  internalServiceKey: required("INTERNAL_SERVICE_KEY"),
  /** Where each user's browser profile (their store logins) is kept: on this computer only, never in the database. */
  profilesDir: process.env.SHOPPING_PROFILES_DIR ?? path.join(os.homedir(), ".daksh", "shopping"),
  /** "chrome" drives the installed Google Chrome (best: it is the user's own browser). Set to "msedge" or "" for Playwright's bundled Chromium. */
  browserChannel: process.env.SHOPPING_BROWSER_CHANNEL ?? "chrome",
  /** Testing only: skip the signed-in check so the add-to-cart path can be tried with a throw-away guest cart. */
  allowGuest: process.env.SHOPPING_ALLOW_GUEST === "1",
  /** The most items one request may add. */
  maxItems: 10,
  signInTimeoutMs: 10 * 60 * 1000,
};
