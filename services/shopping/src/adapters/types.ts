import type { Page } from "playwright-core";

export interface ItemResult {
  query: string;
  status: "added" | "unavailable" | "not_found" | "failed";
  title?: string;
  price?: string;
  note?: string;
}

export interface CartSummary {
  count: number | null;
  total: string | null;
  items: { title: string; price?: string }[];
  url: string;
}

/**
 * One shopping site. Adapters only ever search, open a product and press "Add to cart", and read the cart.
 * They have no checkout, order or payment code, by design.
 */
export interface Adapter {
  key: string;
  name: string;
  home: string;
  cartUrl: string;
  /** Where the sign-in window opens. */
  loginUrl: string;
  /** Looks at the page as it is (no navigation) and says whether the user has finished signing in. */
  loginDetected(page: Page): Promise<boolean>;
  /** Opens the home page and says whether this browser profile is signed in. */
  isLoggedIn(page: Page): Promise<boolean>;
  addItem(page: Page, query: string): Promise<ItemResult>;
  readCart(page: Page): Promise<CartSummary>;
}

export class NotSignedInError extends Error {
  constructor(store: string) { super(`Not signed in to ${store}. Sign in again from Daksha.`); }
}
export class VerificationError extends Error {
  constructor(store: string) { super(`${store} asked for a verification (captcha). Open ${store} in Chrome, complete it, then try again.`); }
}
