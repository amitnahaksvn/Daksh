import type { Page } from "playwright-core";
import { VerificationError, type Adapter, type CartSummary, type ItemResult } from "./types.js";

const HOME = "https://www.amazon.in/";

async function text(page: Page, selector: string, timeout = 1500): Promise<string> {
  return (await page.locator(selector).first().innerText({ timeout }).catch(() => "")).trim();
}

async function checkVerification(page: Page): Promise<void> {
  if (await page.locator("form[action*='validateCaptcha'], #captchacharacters").count()) throw new VerificationError("Amazon");
}

export const amazon: Adapter = {
  key: "amazon",
  name: "Amazon",
  home: HOME,
  cartUrl: HOME + "cart",
  loginUrl: HOME + "gp/sign-in.html",

  async loginDetected(page) {
    const url = page.url();
    if (!/amazon\.in/.test(url) || /\/ap\//.test(url)) return false;
    const hello = await text(page, "#nav-link-accountList-nav-line-1");
    return hello.length > 0 && !/sign in/i.test(hello);
  },

  async isLoggedIn(page) {
    await page.goto(HOME, { waitUntil: "domcontentloaded", timeout: 45000 });
    await checkVerification(page);
    return this.loginDetected(page);
  },

  async addItem(page, query): Promise<ItemResult> {
    await page.goto(`${HOME}s?k=${encodeURIComponent(query)}`, { waitUntil: "domcontentloaded", timeout: 45000 });
    await checkVerification(page);
    const cards = page.locator('div[data-component-type="s-search-result"]');
    const total = await cards.count();
    if (total === 0) return { query, status: "not_found", note: "No results." };

    // Candidates: real listings with a price, skipping paid placements, best match first (Amazon already ranks them).
    const candidates: { href: string; title: string; price: string }[] = [];
    for (let i = 0; i < Math.min(total, 12) && candidates.length < 3; i++) {
      const card = cards.nth(i);
      if (await card.locator(".puis-sponsored-label-text, [aria-label*='Sponsored' i]").count()) continue;
      const price = (await card.locator(".a-price .a-offscreen").first().innerText({ timeout: 800 }).catch(() => "")).trim();
      if (!price) continue;
      const link = card.locator("h2 a, a.a-link-normal.s-no-outline").first();
      const href = await link.getAttribute("href").catch(() => null);
      if (!href) continue;
      const title = (await card.locator("h2").first().innerText({ timeout: 800 }).catch(() => "")).trim();
      candidates.push({ href: href.startsWith("http") ? href : `https://www.amazon.in${href}`, title, price });
    }
    if (candidates.length === 0) return { query, status: "not_found", note: "No available listing found." };

    for (const c of candidates) {
      await page.goto(c.href, { waitUntil: "domcontentloaded", timeout: 45000 });
      await checkVerification(page);
      const add = page.locator("#add-to-cart-button");
      if (!(await add.count()) || !(await add.first().isVisible().catch(() => false))) continue; // out of stock or needs a seller choice
      await add.first().click({ timeout: 8000 });
      const done = page.locator("#NATC_SMART_WAGON_CONF_MSG_SUCCESS, #sw-atc-details-single-container, #attach-added-to-cart-message, #huc-v2-order-row-confirm-text");
      const ok = await Promise.race([
        done.first().waitFor({ state: "attached", timeout: 9000 }).then(() => true, () => false),
        page.waitForURL(/\/cart\/|smart-wagon/, { timeout: 9000 }).then(() => true, () => false),
      ]);
      if (!ok) continue;
      const title = (await text(page, "#productTitle", 800)) || c.title;
      return { query, status: "added", title: title.slice(0, 140), price: c.price };
    }
    return { query, status: "unavailable", note: "The matching products are out of stock or need a choice (size, seller) on Amazon." };
  },

  async readCart(page): Promise<CartSummary> {
    await page.goto(this.cartUrl, { waitUntil: "domcontentloaded", timeout: 45000 });
    await checkVerification(page);
    await page.waitForTimeout(1500);
    const rows = page.locator('[data-name="Active Items"] .sc-list-item, #activeCartViewForm .sc-list-item');
    const items: { title: string; price?: string }[] = [];
    for (let i = 0; i < Math.min(await rows.count(), 30); i++) {
      const row = rows.nth(i);
      const raw = await row.locator(".sc-product-title, .sc-product-link").first().innerText({ timeout: 800 }).catch(() => "");
      const title = raw.split("\n").map((l) => l.trim()).find((l) => l.length > 0 && !/^open$/i.test(l)) ?? "";
      if (!title) continue;
      items.push({ title: title.slice(0, 140), price: (await row.locator(".a-price .a-offscreen").first().innerText({ timeout: 500 }).catch(() => "")).trim() || undefined });
    }
    const count = Number((await text(page, "#nav-cart-count", 800)) || NaN);
    const shown = (await text(page, "#sc-subtotal-amount-activecart, #sc-subtotal-amount-buybox, #sc-subtotal-amount-activecart .a-price .a-offscreen", 800)).replace(/\s+/g, " ");
    // Fall back to adding up the listed prices when Amazon shows no subtotal box (it is hidden for some carts).
    const sum = items.reduce((n, i) => n + Number((i.price ?? "").replace(/[^0-9.]/g, "") || 0), 0);
    const total = /\d/.test(shown) ? shown : sum > 0 ? `\u20B9${sum.toLocaleString("en-IN", { maximumFractionDigits: 2 })}` : null;
    return { count: Number.isFinite(count) ? count : items.length, total, items, url: this.cartUrl };
  },
};
