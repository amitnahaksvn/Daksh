import type { Page } from "playwright-core";
import { NotSignedInError, type Adapter, type CartSummary, type ItemResult } from "./types.js";

const HOME = "https://www.flipkart.com/";

/**
 * Flipkart's cart page is plain text, not links: each item reads "Qty: n | (stock note) | Title | Description | rating | price(s) ... | Save for later | Remove",
 * and the "Items you may have missed" upsell follows the real items. Pure so it can be tested on its own.
 */
export function parseCartText(body: string): { title: string; price?: string }[] {
  const end = body.search(/items you may have missed/i);
  const lines = (end > 0 ? body.slice(0, end) : body).split("\n").map((l) => l.trim()).filter(Boolean);
  const items: { title: string; price?: string }[] = [];
  for (let i = 0; i < lines.length; i++) {
    if (!/^qty:\s*\d+/i.test(lines[i]!)) continue;
    let j = i + 1;
    while (j < lines.length && (lines[j]!.length < 16 || /^only\b|left$|^zoom$/i.test(lines[j]!))) j++;
    const title = lines[j];
    if (!title) continue;
    let price: string | undefined;
    for (let k = j + 1; k < lines.length && !/^save for later$/i.test(lines[k]!) && !/^qty:/i.test(lines[k]!); k++) {
      if (/^\u20B9\s?[\d,]+$/.test(lines[k]!)) price = lines[k]; // the last plain price before "Save for later" is the one being charged
    }
    items.push({ title: title.replace(/\.{3}\s*more$/i, "").slice(0, 140), price });
  }
  return items;
}

export const flipkart: Adapter = {
  key: "flipkart",
  name: "Flipkart",
  home: HOME,
  cartUrl: HOME + "viewcart",
  loginUrl: HOME + "account/login?ret=%2F",

  async loginDetected(page) {
    const url = page.url();
    if (!/flipkart\.com/.test(url) || /account\/login/.test(url)) return false;
    // Signed out, the header shows a "Login" entry; signed in it shows the account menu instead.
    const loginLinks = await page.locator("a[href*='account/login']").count();
    const body = (await page.locator("body").innerText({ timeout: 1500 }).catch(() => "")).slice(0, 1500);
    return loginLinks === 0 && !/^\s*login\s*$/im.test(body) && body.length > 0;
  },

  async isLoggedIn(page) {
    await page.goto(HOME, { waitUntil: "domcontentloaded", timeout: 45000 });
    await page.waitForTimeout(2000);
    return this.loginDetected(page);
  },

  async addItem(page, query): Promise<ItemResult> {
    await page.goto(`${HOME}search?q=${encodeURIComponent(query)}`, { waitUntil: "domcontentloaded", timeout: 45000 });
    await page.waitForTimeout(2000);
    const links = page.locator("a[href*='/p/itm']");
    if ((await links.count()) === 0) return { query, status: "not_found", note: "No results." };

    const hrefs: string[] = [];
    for (let i = 0; i < Math.min(await links.count(), 12) && hrefs.length < 3; i++) {
      const href = await links.nth(i).getAttribute("href").catch(() => null);
      if (href && !hrefs.includes(href)) hrefs.push(href);
    }

    for (const href of hrefs) {
      await page.goto(href.startsWith("http") ? href : `https://www.flipkart.com${href}`, { waitUntil: "domcontentloaded", timeout: 45000 });
      await page.waitForTimeout(2500);
      const title = ((await page.locator("h1").first().innerText({ timeout: 1000 }).catch(() => "")) || (await page.title())).replace(/\.{3}\s*more$/i, "").slice(0, 140);
      if (await page.locator("text=/sold out|currently unavailable|out of stock/i").count()) continue;
      const add = page.getByText(/^\s*add to cart\s*$/i).first();
      if (!(await add.isVisible().catch(() => false))) continue;
      await add.click({ timeout: 8000 });
      await page.waitForTimeout(1500);
      // A login prompt after pressing the button means this browser profile is not signed in.
      if (/account\/login/.test(page.url()) || (await page.locator("text=/login to continue|enter email\\/mobile/i").count())) throw new NotSignedInError("Flipkart");
      const ok = await Promise.race([
        page.waitForURL(/viewcart/, { timeout: 8000 }).then(() => true, () => false),
        page.getByText(/go to cart/i).first().waitFor({ state: "visible", timeout: 8000 }).then(() => true, () => false),
      ]);
      if (!ok) continue;
      return { query, status: "added", title };
    }
    return { query, status: "unavailable", note: "The matching products are out of stock or need a choice (size, colour) on Flipkart." };
  },

  async readCart(page): Promise<CartSummary> {
    await page.goto(this.cartUrl, { waitUntil: "domcontentloaded", timeout: 45000 });
    await page.waitForTimeout(2500);
    if (/account\/login/.test(page.url())) throw new NotSignedInError("Flipkart");
    const body = await page.locator("body").innerText({ timeout: 2000 }).catch(() => "");
    const items = parseCartText(body);
    const sum = items.reduce((n, i) => n + Number((i.price ?? "").replace(/[^0-9.]/g, "") || 0), 0);
    const total = /total amount\s*[\s\S]{0,20}?(\u20B9\s?[\d,]+)/i.exec(body)?.[1] ?? (sum > 0 ? `\u20B9${sum.toLocaleString("en-IN")}` : null);
    return { count: items.length, total, items, url: this.cartUrl };
  },
};
