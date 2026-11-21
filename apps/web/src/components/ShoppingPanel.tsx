"use client";
import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { BrandTile } from "@/components/integrations";
import { RemoteBrowser } from "@/components/RemoteBrowser";
import { buttonClass, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass } from "@/components/ui";
import { api, ApiError, type AddToCartResult, type ListItem, type ShoppingStore } from "@/lib/api";
import { useApi } from "@/lib/useApi";

const LIST = "Shopping";
const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const searchLink = (store: ShoppingStore, item: string) => store.searchUrl.replace("{0}", encodeURIComponent(item));
const cartLink = (store: ShoppingStore) => store.home + (store.key === "amazon" ? "cart" : "viewcart");

/** "milk, 2 usb c cable and a steel bottle" → ["milk", "2 usb c cable", "a steel bottle"]. */
export function splitItems(text: string): string[] {
  return text.split(/[,;\n]+|\s+and\s+/i).map((i) => i.trim()).filter(Boolean).slice(0, 10);
}

/**
 * One shopping app. None of these stores lets another app act on a buyer's account. For Amazon and Flipkart, Daksha's helper on this computer uses your own
 * Chrome: you sign in once in a real window (Daksha never sees the login), then Daksha adds items to your cart. It never opens checkout or payment: you review and
 * pay on the store yourself. The other stores keep your shopping list and open each item on the store.
 */
export function ShoppingPanel({ storeKey, icon, color, onChanged }: { storeKey: string; icon: string; color: string; onChanged?: () => void }) {
  const { state, reload } = useApi(() => api.shoppingStores());
  const [items, setItems] = useState<ListItem[] | null>(null);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<AddToCartResult | null>(null);
  // Cloud sign-in: a live view of the server's browser, where the user signs in.
  const [streamUrl, setStreamUrl] = useState<string | null>(null);

  const loadItems = () => api.lists().then((all) => setItems(all.filter((i) => i.list.toLowerCase() === LIST.toLowerCase() && !i.done)), () => setItems([]));
  useEffect(() => { const t = setTimeout(() => void loadItems(), 0); return () => clearTimeout(t); }, []);

  // While the sign-in window is open on this computer, keep checking until it finishes.
  const signingIn = state.status === "ready" && state.data.find((s) => s.key === storeKey)?.session === "signing_in";
  // The panel was reopened (or the page reloaded) while a sign-in is open: ask for a fresh live-view address.
  useEffect(() => {
    if (!signingIn || streamUrl) return;
    let live = true;
    api.shoppingSession(storeKey).then((s) => { if (live && s.streamUrl) setStreamUrl(s.streamUrl); }, () => undefined);
    return () => { live = false; };
  }, [signingIn, streamUrl, storeKey]);
  useEffect(() => {
    if (!signingIn) return;
    const id = window.setInterval(() => { reload(); onChanged?.(); }, 3000);
    return () => window.clearInterval(id);
  }, [signingIn, reload, onChanged]);

  if (state.status === "loading") return <Loading label="Loading" />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const store = state.data.find((s) => s.key === storeKey);
  if (!store) return <p role="alert" className="text-sm text-danger">That store is not available.</p>;

  async function run(action: () => Promise<unknown>, fallback: string) {
    setBusy(true); setError(null);
    try { await action(); onChanged?.(); reload(); }
    catch (err) { setError(errorText(err, fallback)); }
    finally { setBusy(false); }
  }
  async function addList(e: FormEvent) {
    e.preventDefault();
    if (!text.trim()) return;
    await run(async () => { await api.addListItem(LIST, text.trim()); setText(""); await loadItems(); }, "Could not add that item.");
  }
  async function done(item: ListItem) { await run(async () => { await api.toggleListItem(item.id); await loadItems(); }, "Could not update the list."); }
  async function fillCart(names: string[]) {
    if (names.length === 0) return;
    setBusy(true); setError(null); setOutcome(null);
    try { setOutcome(await api.addToCart(store!.key, names)); setText(""); }
    catch (err) {
      setError(errorText(err, "Could not add to the cart."));
      if (err instanceof ApiError && err.code === "not_signed_in") reload();
    } finally { setBusy(false); }
  }

  const cartMode = store.cart;
  const helperDown = cartMode && store.session === "unavailable";
  const needSignIn = cartMode && !helperDown && store.session !== "connected";

  return (
    <div>
      <header className="flex flex-wrap items-center gap-3 pr-10">
        <BrandTile icon={icon} color={color} size={44} />
        <div className="min-w-0">
          <h2 className="text-xl font-bold">{store.name}</h2>
          <p className="text-xs text-muted">{store.connected ? "Connected" : "Not connected"} · Daksha never places the order: you review and pay in {store.name}</p>
        </div>
        <div className="ml-auto flex flex-wrap gap-2">
          <a href={store.home} target="_blank" rel="noreferrer" className={secondaryButtonClass}>Open {store.name}</a>
          {cartMode && store.session === "connected" && (
            <button type="button" disabled={busy} onClick={() => { if (window.confirm(`Sign out of ${store.name} in Daksha? Your ${store.name} account itself is not affected.`)) void run(() => api.endShoppingSession(store.key), "Could not sign out."); }}
              className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg">Disconnect</button>
          )}
          {!cartMode && (store.connected
            ? <button type="button" disabled={busy} onClick={() => void run(() => api.disableShopping(store.key), "Could not turn it off.")} className="rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg">Turn off</button>
            : <button type="button" disabled={busy} onClick={() => void run(() => api.enableShopping(store.key), "Could not turn it on.")} className={`${buttonClass} px-4`}>Turn on</button>)}
        </div>
      </header>

      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}

      {helperDown && (
        <div role="alert" className="mt-4 rounded-xl border border-line bg-surface-2 p-3 text-sm">
          <p className="font-semibold">Daksha&apos;s shopping helper is not available right now.</p>
          <p className="mt-1 text-muted">It fills your cart in a real browser. Try again in a minute. If you run Daksha yourself, start the helper in <code className="ai-mono">services/shopping</code>.</p>
          <button type="button" onClick={reload} className={`${secondaryButtonClass} mt-2`}>Check again</button>
        </div>
      )}

      {needSignIn && (
        <div className="mt-4 rounded-xl border border-line p-4 text-sm">
          {store.session === "signing_in" ? (
            <>
              <p className="font-semibold">Sign in to {store.name}</p>
              {streamUrl ? (
                <div className="mt-2"><RemoteBrowser url={streamUrl} onDone={() => { setStreamUrl(null); reload(); onChanged?.(); }} /></div>
              ) : (
                <p className="mt-1 text-muted">A Chrome window opened on this computer. Sign in there, including any code or captcha. It closes by itself once you are in. Daksha never sees your password.</p>
              )}
            </>
          ) : (
            <>
              <p className="font-semibold">Connect your {store.name} account</p>
              <p className="mt-1 text-muted">Daksha opens a private browser where you sign in yourself. After that you can say what you want and Daksha adds it to your {store.name} cart. It stops there: you open {store.name} to check and pay.</p>
              <button type="button" disabled={busy} onClick={() => void run(async () => { const s = await api.startShoppingSession(store.key); if (s.streamUrl) setStreamUrl(s.streamUrl); }, "Could not open the sign-in window.")} className={`${buttonClass} mt-3 px-5`}>Sign in to {store.name}</button>
            </>
          )}
        </div>
      )}

      {cartMode && store.session === "connected" && (
        <section className="mt-5">
          <h3 className="text-base font-bold">Add to your {store.name} cart</h3>
          <form onSubmit={(e) => { e.preventDefault(); void fillCart(splitItems(text)); }} className="mt-2 flex gap-2">
            <input value={text} onChange={(e) => setText(e.target.value)} maxLength={400} disabled={busy} placeholder="e.g. milk, usb c cable, steel water bottle" aria-label={`Items to add to your ${store.name} cart`} className={`${inputClass} min-w-0 flex-1`} />
            <button type="submit" disabled={busy || splitItems(text).length === 0} className={`${buttonClass} px-5`}>{busy ? "Adding…" : "Add to cart"}</button>
          </form>
          {busy && <p role="status" className="mt-2 text-xs text-muted">Daksha is adding your items in {store.name}. This takes about 15 seconds per item.</p>}
          {items && items.length > 0 && !busy && (
            <button type="button" onClick={() => void fillCart(items.map((i) => i.text))} className={`${secondaryButtonClass} mt-2`}>Add my whole shopping list ({items.length})</button>
          )}

          {outcome && (
            <div className="mt-4 rounded-xl border border-line p-3">
              <ul className="space-y-1.5 text-sm">
                {outcome.results.map((r) => (
                  <li key={r.query} className="flex gap-2">
                    <span aria-hidden>{r.status === "added" ? "✅" : "❌"}</span>
                    <span className="min-w-0"><span className="block truncate">{r.status === "added" ? (r.title ?? r.query) : r.query}</span>
                      <span className="block text-xs text-muted">{r.status === "added" ? (r.price ?? "") : (r.note ?? r.status)}</span></span>
                  </li>
                ))}
              </ul>
              {outcome.results.some((r) => r.status === "added") && (
                <div className="mt-3 flex flex-wrap items-center gap-3 border-t border-line pt-3">
                  <span className="text-sm font-semibold">Ready to buy{outcome.cart ? ` · ${outcome.cart.count ?? outcome.cart.items.length} in cart${outcome.cart.total ? ` · ${outcome.cart.total}` : ""}` : ""}</span>
                  <a href={cartLink(store)} target="_blank" rel="noreferrer" className={`${buttonClass} ml-auto px-4`}>Open cart on {store.name}</a>
                </div>
              )}
              <p className="mt-2 text-xs text-muted">Check the products above are what you meant, then place the order on {store.name} yourself.</p>
            </div>
          )}
        </section>
      )}

      {!cartMode && (
        <p className="mt-4 rounded-xl border border-line bg-surface-2 p-3 text-sm text-muted">
          Daksha cannot fill a {store.name} cart from here. It keeps your shopping list and opens each item on {store.name}, where you add it and check out yourself.
        </p>
      )}

      <section className="mt-5">
        <h3 className="text-base font-bold">Your shopping list</h3>
        <form onSubmit={(e) => void addList(e)} className="mt-2 flex gap-2">
          <input value={cartMode ? "" : text} onChange={(e) => setText(e.target.value)} disabled={cartMode} maxLength={200} placeholder={cartMode ? "Add list items from chat or the Lists page" : "Add an item, e.g. basmati rice 5 kg"} aria-label="Add a shopping item" className={`${inputClass} min-w-0 flex-1`} />
          {!cartMode && <button type="submit" disabled={busy || !text.trim()} className={`${buttonClass} px-5`}>Add</button>}
        </form>
        {items === null ? <Loading label="Loading list" /> : items.length === 0 ? <EmptyState>Your shopping list is empty.</EmptyState> : (
          <ul className="mt-3 divide-y divide-line rounded-xl border border-line">
            {items.map((i) => (
              <li key={i.id} className="flex items-center gap-3 px-3 py-2">
                <input type="checkbox" aria-label={`Mark ${i.text} as bought`} onChange={() => void done(i)} />
                <span className="min-w-0 flex-1 truncate text-sm">{i.text}</span>
                <a href={searchLink(store, i.text)} target="_blank" rel="noreferrer" className="shrink-0 rounded-full border border-line px-3 py-1 text-xs font-semibold text-accent hover:bg-surface-2">Find on {store.name}</a>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
