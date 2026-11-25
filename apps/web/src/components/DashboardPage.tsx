"use client";
import Link from "next/link";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { DashboardAppCard } from "@/components/integrations";
import { buttonClass, EmptyState, ErrorState, Loading } from "@/components/ui";
import { MicrosoftDashboardCard } from "@/components/MicrosoftDashboardCard";
import { GoogleWorkspaceDashboardCard } from "@/components/GoogleWorkspaceDashboardCard";
import { SlackDashboardCard } from "@/components/SlackDashboardCard";
import { OneAppDashboardCard } from "@/components/OneAppParts";
import { ONE_APPS, manageFor } from "@/components/oneAppConfigs";
import { isProviderAppKey, type OneAppKey } from "@/lib/api";
import { ProviderDashboardCard } from "@/components/ProviderApp";
import { GMAIL_CARD_HINT_KEY, GmailCardPlaceholder, GmailDashboardCard } from "@/components/GmailDashboardCard";
import { CommandCentre } from "@/components/CommandCentre";
import { api, type AvailableDashboardApp, type DashboardApp, type DashboardLayoutItem, type DashboardWidget } from "@/lib/api";
import { DashboardTile, getActiveTileDrag, getGrabOffsetX, type TileDrag } from "@/components/DashboardTile";
import { GRID_COLUMNS, applySize, colOf, effectiveStart, heightOf, moveTile, placeTile, sortTiles, spanOf, startFromPointer, toLayoutPayload } from "@/lib/dashboardLayout";
import { useApi } from "@/lib/useApi";

/** Messaging apps (WhatsApp, Telegram) live in the Chats sidebar on every page, so they are not tiles here. */
const MESSAGING = new Set(["whatsapp", "telegram"]);

/** A pop-up listing every connected app and account, ticked when it is already on the dashboard. Save adds the newly ticked and removes the unticked. */
function ChooseApps({ onChanged }: { onChanged: () => void }) {
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<AvailableDashboardApp[] | null>(null);
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [failed, setFailed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function show() {
    setOpen(true); setItems(null); setFailed(false); setError(null);
    api.availableDashboardApps().then(
      (a) => { setItems(a); setTicked(new Set(a.filter((x) => x.dashboardId).map((x) => x.connectionId))); },
      () => setFailed(true),
    );
  }
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  const toggle = (id: string) => setTicked((t) => { const n = new Set(t); if (n.has(id)) n.delete(id); else n.add(id); return n; });

  async function save() {
    if (!items) return;
    setSaving(true); setError(null);
    try {
      for (const a of items) {
        const on = ticked.has(a.connectionId);
        if (on && !a.dashboardId) await api.addToDashboard(a.connectionId);
        if (!on && a.dashboardId) await api.removeFromDashboard(a.dashboardId);
      }
      setOpen(false); onChanged();
    } catch { setError("Could not save your choices. Try again."); }
    finally { setSaving(false); }
  }

  return (
    <>
      <div className="mb-3 flex justify-end">
        <button type="button" onClick={show} className="rounded-full border border-line bg-surface px-4 py-2 text-sm font-semibold hover:border-accent">Choose apps</button>
      </div>
      {open && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
          <button type="button" aria-label="Close" onClick={() => setOpen(false)} className="absolute inset-0 cursor-default bg-black/50" />
          <div role="dialog" aria-modal="true" aria-labelledby="choose-apps-title" className="relative flex max-h-[85vh] w-full max-w-md flex-col rounded-2xl border border-line bg-surface p-5 text-text shadow-[var(--shadow)]">
            <h2 id="choose-apps-title" className="text-lg font-bold">Apps on your dashboard</h2>
            <p className="mt-1 text-sm text-muted">Tick the connected apps and accounts you want as tiles.</p>
            <div className="mt-3 min-h-0 flex-1 overflow-y-auto">
              {failed && <p role="alert" className="text-sm text-danger">Could not load your connected apps. Try again.</p>}
              {!failed && !items && <p role="status" className="text-sm text-muted">Loading…</p>}
              {items && items.length === 0 && (
                <p className="text-sm text-muted">No connected apps yet. <Link href="/apps" className="font-semibold text-accent hover:underline">Connect an app</Link></p>
              )}
              {items && items.length > 0 && (
                <ul className="divide-y divide-line">
                  {items.map((a) => (
                    <li key={a.connectionId}>
                      <label className="flex cursor-pointer items-center gap-3 py-3">
                        <input type="checkbox" checked={ticked.has(a.connectionId)} onChange={() => toggle(a.connectionId)} className="h-4 w-4" />
                        <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ background: a.color }} />
                        <span className="min-w-0 flex-1"><span className="font-semibold">{a.name}</span>{a.accountIdentifier && <span className="block truncate text-sm text-muted">{a.accountIdentifier}</span>}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              )}
            </div>
            {error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setOpen(false)} className="rounded-full border border-line px-4 py-2 text-sm font-semibold hover:bg-surface-2">Cancel</button>
              <button type="button" onClick={() => void save()} disabled={saving || !items} className="rounded-full bg-accent px-4 py-2 text-sm font-semibold text-accent-contrast hover:opacity-90 disabled:opacity-50">{saving ? "Saving…" : "Save"}</button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/** The layout save that is on its way to the server. Loading the tiles waits for it, so a reload (or another tab) can never read the old layout and show the resize as forgotten. */
let savingLayout: Promise<unknown> = Promise.resolve();
const saveLayout = (payload: DashboardLayoutItem[]) => { const p = api.saveDashboardLayout(payload); savingLayout = p.catch(() => undefined); return p; };

function DashboardBody({ apps, onRemove, onReload }: { apps: DashboardApp[]; onRemove: (id: string) => void; onReload: () => void }) {
  // The arrangement is local state seeded from the saved layout; every change is saved to the user's account after a short pause.
  const [tiles, setTiles] = useState(() => sortTiles(apps));
  const [seen, setSeen] = useState(apps);
  if (apps !== seen) { setSeen(apps); setTiles(sortTiles(apps)); } // the server list was reloaded: take it as the new truth
  const [drag, setDrag] = useState<TileDrag>({ dragId: null, overId: null });
  const [resizingNow, setResizingNow] = useState(false);
  const [moveGhost, setMoveGhost] = useState<{ left: number; top: number; width: number; height: number; name: string } | null>(null);
  const gridRef = useRef<HTMLUListElement>(null);
  const dropRef = useRef<{ col: number; atEnd: boolean } | null>(null);
  const guidesOn = drag.dragId !== null || resizingNow;

  // Dragging over empty grid space — or over the tile's own area — places the tile in the column under its left edge; below everything, at the end.
  const placementFor = (e: React.DragEvent) => {
    const dragId = getActiveTileDrag(); const grid = gridRef.current;
    if (!dragId || !grid) return null;
    const tile = tilesRef.current.find((t) => t.id === dragId);
    if (!tile) return null;
    const overTile = (e.target as HTMLElement).closest<HTMLElement>("[data-tile]");
    if (overTile && overTile.dataset.tile !== dragId) return null; // another tile handles it (reorder)
    const style = getComputedStyle(grid);
    const columns = style.gridTemplateColumns.split(" ").filter(Boolean).length || 1;
    if (columns < 2) return null;
    const gap = parseFloat(style.columnGap) || 0;
    const g = grid.getBoundingClientRect();
    const span = spanOf(tile);
    const col = startFromPointer(e.clientX - getGrabOffsetX(), g.left, g.width, gap, columns, span);
    const colW = (g.width - gap * (columns - 1)) / columns;
    const src = grid.querySelector<HTMLElement>(`[data-tile="${dragId}"]`)?.getBoundingClientRect();
    const others = [...grid.querySelectorAll<HTMLElement>("[data-tile]")].filter((el) => el.dataset.tile !== dragId).map((el) => el.getBoundingClientRect().bottom);
    const lastBottom = others.length ? Math.max(...others) : g.top;
    const atEnd = !overTile && others.length > 0 && e.clientY > lastBottom + 8;
    return { col, atEnd, ghost: { left: (col - 1) * (colW + gap), top: atEnd ? lastBottom - g.top + gap : (src?.top ?? g.top) - g.top, width: span * colW + (span - 1) * gap, height: src?.height ?? 200, name: tile.name } };
  };
  const onGridDragOver = (e: React.DragEvent) => {
    const p = placementFor(e);
    if (!p) return;
    e.preventDefault();
    dropRef.current = { col: p.col, atEnd: p.atEnd };
    setMoveGhost(p.ghost);
  };
  const onGridDrop = (e: React.DragEvent) => {
    const dragId = getActiveTileDrag();
    const p = placementFor(e);
    if (!p || !dragId) return;
    e.preventDefault();
    dropRef.current = null; setMoveGhost(null); setDrag({ dragId: null, overId: null });
    const name = tilesRef.current.find((t) => t.id === dragId)?.name ?? "Tile";
    commit(placeTile(tilesRef.current, dragId, p.col, p.atEnd), `${name} placed at column ${p.col}.`);
  };

  // Dragging: remember where the tile would land so a "Drop here" shadow can be shown there. Measured in the event handler, not during render.
  const onDragChange = (d: TileDrag) => {
    setDrag(d);
    const grid = gridRef.current;
    if (!grid || !d.dragId || !d.overId) { setMoveGhost(null); return; }
    const src = grid.querySelector<HTMLElement>(`[data-tile="${d.dragId}"]`);
    const dst = grid.querySelector<HTMLElement>(`[data-tile="${d.overId}"]`);
    if (!src || !dst) { setMoveGhost(null); return; }
    const g = grid.getBoundingClientRect(); const s = src.getBoundingClientRect(); const t = dst.getBoundingClientRect();
    const width = Math.min(s.width, g.width);
    setMoveGhost({ left: Math.min(t.left - g.left, g.width - width), top: t.top - g.top, width, height: s.height, name: tilesRef.current.find((x) => x.id === d.dragId)?.name ?? "" });
  };
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [announce, setAnnounce] = useState("");
  const tilesRef = useRef(tiles);
  const pending = useRef<{ timer: number; payload: DashboardLayoutItem[] } | null>(null);
  useEffect(() => { tilesRef.current = tiles; });

  const flush = useCallback(async () => {
    const p = pending.current;
    if (!p) return;
    window.clearTimeout(p.timer); pending.current = null;
    try { await saveLayout(p.payload); setSaveState("saved"); } catch { setSaveState("error"); }
  }, []);
  const schedule = useCallback((next: DashboardApp[]) => {
    if (pending.current) window.clearTimeout(pending.current.timer);
    setSaveState("saving");
    pending.current = { payload: toLayoutPayload(next), timer: window.setTimeout(() => void flush(), 400) };
  }, [flush]);
  // Leaving the page within the pause must not lose the change.
  useEffect(() => () => {
    const p = pending.current;
    if (!p) return;
    window.clearTimeout(p.timer); pending.current = null; // save once, now — not again when the timer would have fired
    void saveLayout(p.payload).catch(() => undefined);
  }, []);

  const commit = (next: DashboardApp[], message?: string) => { setTiles(next); schedule(next); if (message) setAnnounce(message); };
  const move = (id: string, to: number, name: string) => {
    const next = moveTile(tilesRef.current, id, to);
    if (next !== tilesRef.current) commit(next, `${name} moved to position ${to + 1} of ${next.length}.`);
  };

  const reloadAfterSave = () => { void flush().then(() => savingLayout).then(onReload); };

  return (
    <>
      {saveState === "error" && (
        <button type="button" onClick={() => schedule(tilesRef.current)} className="mb-3 rounded-full border border-danger px-4 py-2 text-sm font-semibold text-danger hover:bg-danger-bg">Couldn&apos;t save layout — retry</button>
      )}
      <p className="sr-only" role="status" aria-live="polite">{announce}{saveState === "saved" ? " Layout saved." : ""}</p>
      <div className="relative" onDragOver={onGridDragOver} onDrop={onGridDrop}>
        {/* While a tile is being dragged or resized, the grid's columns show as dashed shadows so the empty places and snap points are visible. */}
        {guidesOn && (
          <div aria-hidden className="pointer-events-none absolute inset-0 grid grid-cols-1 gap-4 lg:grid-cols-12">
            {Array.from({ length: GRID_COLUMNS }, (_, i) => (
              <div key={i} className={`rounded-xl border-2 border-dashed border-accent/35 bg-accent/5 ${i === 0 ? "" : "hidden lg:block"}`} />
            ))}
          </div>
        )}
        <ul ref={gridRef} className="relative grid grid-cols-1 items-start gap-4 lg:grid-cols-12">
          {tiles.map((app, index) => (
            <DashboardTile
              key={app.id} id={app.id} name={app.name} span={spanOf(app)} height={heightOf(app)} colStart={effectiveStart(spanOf(app), colOf(app))}
              drag={drag} onDragChange={onDragChange} onActiveChange={setResizingNow}
              onResizeCommit={(change) => commit(applySize(tilesRef.current, app.id, change), `${app.name} resized.`)}
              onDropOn={(fromId) => move(fromId, index, tilesRef.current.find((t) => t.id === fromId)?.name ?? "Tile")}
            >
              {app.integrationKey === "gmail"
                ? <GmailDashboardCard app={app} onChanged={reloadAfterSave} />
                : app.integrationKey === "microsoft-outlook"
                ? <MicrosoftDashboardCard app={app} onChanged={reloadAfterSave} />
                : app.integrationKey === "google-drive"
                ? <GoogleWorkspaceDashboardCard app={app} onChanged={reloadAfterSave} />
                : app.integrationKey === "slack"
                ? <SlackDashboardCard app={app} onChanged={reloadAfterSave} />
                : app.integrationKey in ONE_APPS
                ? <OneAppDashboardCard app={app} config={ONE_APPS[app.integrationKey as OneAppKey]} manage={manageFor(app.integrationKey as OneAppKey)} onChanged={reloadAfterSave} />
                : isProviderAppKey(app.integrationKey)
                ? <ProviderDashboardCard app={app} onChanged={reloadAfterSave} />
                : <DashboardAppCard app={app} onRemove={onRemove} />}
            </DashboardTile>
          ))}
        </ul>
        {/* Where the dragged tile will land, at its current size. */}
        {moveGhost && (
          <div aria-hidden style={{ left: moveGhost.left, top: moveGhost.top, width: moveGhost.width, height: moveGhost.height }}
            className="pointer-events-none absolute z-30 flex items-center justify-center rounded-[1.25rem] border-2 border-dashed border-accent bg-accent/15 shadow-[0_0_0_6px_color-mix(in_srgb,var(--accent)_16%,transparent)]">
            <span className="rounded-full bg-accent px-3 py-1 text-xs font-semibold text-accent-contrast shadow">Drop {moveGhost.name} here</span>
          </div>
        )}
      </div>
    </>
  );
}

const SECTIONS: { id: DashboardWidget; label: string }[] = [{ id: "command", label: "Command Centre" }, { id: "apps", label: "App tiles" }];

/** The blocks of one dashboard. Which blocks show is the dashboard's own choice (see DashboardTabs). */
export function DashboardContent({ widgets = ["command", "apps"] }: { widgets?: DashboardWidget[] }) {
  const { state: fresh, reload } = useApi(() => savingLayout.then(() => api.dashboardApps()));
  // A reload keeps showing the tiles as they were instead of blanking and rebuilding them.
  const [kept, setKept] = useState<DashboardApp[] | null>(null);
  if (fresh.status === "ready" && fresh.data !== kept) setKept(fresh.data);
  const state = fresh.status === "loading" && kept ? ({ status: "ready", data: kept } as const) : fresh;
  // Did this browser last see a Gmail card? If so, hold its space while loading instead of flashing a small spinner.
  const expectGmail = useSyncExternalStore(() => () => {}, () => { try { return localStorage.getItem(GMAIL_CARD_HINT_KEY) === "1"; } catch { return false; } }, () => false);
  useEffect(() => {
    if (fresh.status !== "ready") return;
    try { localStorage.setItem(GMAIL_CARD_HINT_KEY, fresh.data.some((a) => a.integrationKey === "gmail") ? "1" : "0"); } catch { /* storage unavailable */ }
  }, [fresh]);

  async function remove(id: string) {
    await api.removeFromDashboard(id);
    reload();
  }

  const [inner, setInner] = useState<DashboardWidget>("command");
  const available = SECTIONS.filter((x) => widgets.includes(x.id));
  const shown = available.some((x) => x.id === inner) ? inner : available[0]?.id;
  const showCommand = shown === "command";
  const showApps = shown === "apps";
  if (available.length === 0) return <EmptyState>Nothing on this dashboard yet. Use the ⋯ next to its name to choose what it shows.</EmptyState>;

  const content = (
    <>
      {state.status === "loading" && (showApps && expectGmail ? <GmailCardPlaceholder /> : <Loading />)}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (() => {
        const tiles = state.data.filter((a) => !MESSAGING.has(a.integrationKey));
        return (
          <>
            {/* Both stay mounted and the other is only hidden, so switching tabs never reloads the tiles from stale data (which looked like the resize was forgotten). */}
            {widgets.includes("command") && <div hidden={!showCommand}><CommandCentre apps={state.data} /></div>}
            {widgets.includes("apps") && <div hidden={!showApps}><ChooseApps onChanged={reload} />{(tiles.length === 0 ? (
              <EmptyState>
                Nothing here yet. Your WhatsApp and Telegram chats are always in the Chats sidebar on the right. Use Choose apps above to add a connected app, or connect more apps.{" "}
                <Link href="/apps" className={`${buttonClass} mt-3 inline-flex`}>Browse apps</Link>
              </EmptyState>
            ) : (
              <DashboardBody apps={tiles} onRemove={remove} onReload={reload} />
            ))}</div>}
          </>
        );
      })()}
    </>
  );

  // One block: just show it. Both: connected folder tabs, the active tab merging into the content box.
  if (available.length < 2) return content;
  return (
    <div>
      <div role="tablist" aria-label="Dashboard sections" className="grid grid-cols-2">
        {available.map((x, i) => {
          const active = shown === x.id;
          return (
            <button key={x.id} type="button" role="tab" id={`section-${x.id}`} aria-selected={active} aria-controls="section-panel" onClick={() => setInner(x.id)}
              className={`border border-line px-3 py-3.5 text-sm transition-colors ${i === 0 ? "rounded-tl-2xl" : "border-l-0 rounded-tr-2xl"} ${active ? "border-b-surface bg-surface font-bold text-text shadow-[inset_0_3px_0_var(--accent)]" : "bg-surface-2 font-medium text-muted hover:text-text"}`}>
              {x.label}
            </button>
          );
        })}
      </div>
      <div role="tabpanel" id="section-panel" aria-labelledby={`section-${shown}`} className="-mt-px flex flex-col gap-4 rounded-b-2xl border border-t-0 border-line bg-surface p-4 md:p-6">{content}</div>
    </div>
  );
}
