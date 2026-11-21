import fs from "node:fs/promises";
import path from "node:path";
import { chromium, type BrowserContext, type Page } from "playwright-core";
import { adapters, NotSignedInError, type CartSummary, type ItemResult } from "./adapters/index.js";
import { config } from "./config.js";

export type SessionState = "none" | "signing_in" | "connected";

const signingIn = new Map<string, { startedAt: number }>();
const locks = new Map<string, Promise<unknown>>();

const safe = (s: string) => s.replace(/[^a-zA-Z0-9_-]/g, "");
const keyOf = (userId: string, store: string) => `${safe(userId)}:${safe(store)}`;
export const profileDir = (userId: string, store: string) => path.join(config.profilesDir, safe(userId), safe(store));
const markerFile = (userId: string, store: string) => path.join(profileDir(userId, store), "daksha-connected.json");

async function exists(p: string): Promise<boolean> {
  return fs.access(p).then(() => true, () => false);
}

export async function getState(userId: string, store: string): Promise<SessionState> {
  if (signingIn.has(keyOf(userId, store))) return "signing_in";
  return (await exists(markerFile(userId, store))) ? "connected" : "none";
}

/** One browser may use a profile at a time, so everything that opens it runs one after another. */
async function exclusive<T>(userId: string, store: string, fn: () => Promise<T>): Promise<T> {
  const k = keyOf(userId, store);
  const run = (locks.get(k) ?? Promise.resolve()).catch(() => undefined).then(fn);
  locks.set(k, run.catch(() => undefined));
  return run;
}

function launch(userId: string, store: string, headless: boolean): Promise<BrowserContext> {
  return chromium.launchPersistentContext(profileDir(userId, store), {
    channel: config.browserChannel || undefined,
    headless,
    viewport: headless ? { width: 1280, height: 900 } : null,
    locale: "en-IN",
    args: headless ? [] : ["--no-first-run", "--no-default-browser-check"],
  });
}

/**
 * Opens a visible Chrome window on this computer at the store's sign-in page. The user signs in themselves (password, codes, captchas all happen in
 * that window); Daksha never sees the credentials. When the page shows a signed-in account the window closes and the profile is kept for later.
 */
export async function startSignIn(userId: string, store: string): Promise<SessionState> {
  const adapter = adapters[store];
  if (!adapter) throw new Error("Unknown store.");
  const k = keyOf(userId, store);
  if (signingIn.has(k)) return "signing_in";
  signingIn.set(k, { startedAt: Date.now() });
  void exclusive(userId, store, async () => {
    let ctx: BrowserContext | null = null;
    try {
      await fs.mkdir(profileDir(userId, store), { recursive: true });
      ctx = await launch(userId, store, false);
      const page = ctx.pages()[0] ?? (await ctx.newPage());
      await page.goto(adapter.loginUrl, { waitUntil: "domcontentloaded", timeout: 60000 }).catch(() => undefined);
      const deadline = Date.now() + config.signInTimeoutMs;
      let closed = false;
      ctx.on("close", () => { closed = true; });
      while (!closed && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 2500));
        const live: Page | undefined = ctx.pages().find((p) => !p.isClosed());
        if (!live) break;
        if (await adapter.loginDetected(live).catch(() => false)) {
          await fs.writeFile(markerFile(userId, store), JSON.stringify({ at: new Date().toISOString() }));
          break;
        }
      }
    } catch { /* the window was closed or the browser could not start: the state below stays "none" */ }
    finally {
      await ctx?.close().catch(() => undefined);
      signingIn.delete(k);
    }
  });
  return "signing_in";
}

export async function signOut(userId: string, store: string): Promise<void> {
  if (signingIn.has(keyOf(userId, store))) throw new Error("Finish or close the sign-in window first.");
  await exclusive(userId, store, () => fs.rm(profileDir(userId, store), { recursive: true, force: true }));
}

async function withPage<T>(userId: string, store: string, fn: (page: Page) => Promise<T>): Promise<T> {
  const adapter = adapters[store];
  if (!adapter) throw new Error("Unknown store.");
  if (signingIn.has(keyOf(userId, store))) throw new Error("The sign-in window is still open. Finish signing in first.");
  if (!config.allowGuest && !(await exists(markerFile(userId, store)))) throw new NotSignedInError(adapter.name);
  return exclusive(userId, store, async () => {
    await fs.mkdir(profileDir(userId, store), { recursive: true });
    const ctx = await launch(userId, store, true);
    try {
      const page = ctx.pages()[0] ?? (await ctx.newPage());
      if (!config.allowGuest && !(await adapter.isLoggedIn(page))) {
        await fs.rm(markerFile(userId, store), { force: true });
        throw new NotSignedInError(adapter.name);
      }
      return await fn(page);
    } finally {
      await ctx.close().catch(() => undefined);
    }
  });
}

/** Adds each item to the user's cart, one after another, with a short pause between. Never opens checkout. */
export async function addToCart(userId: string, store: string, items: string[]): Promise<{ results: ItemResult[]; cart: CartSummary | null }> {
  const adapter = adapters[store]!;
  return withPage(userId, store, async (page) => {
    const results: ItemResult[] = [];
    for (const query of items) {
      try { results.push(await adapter.addItem(page, query)); }
      catch (err) {
        if (err instanceof NotSignedInError) throw err;
        results.push({ query, status: "failed", note: err instanceof Error ? err.message : "Could not add this item." });
      }
      await page.waitForTimeout(800);
    }
    const cart = await adapter.readCart(page).catch(() => null);
    return { results, cart };
  });
}

export async function readCart(userId: string, store: string): Promise<CartSummary> {
  const adapter = adapters[store]!;
  return withPage(userId, store, (page) => adapter.readCart(page));
}
