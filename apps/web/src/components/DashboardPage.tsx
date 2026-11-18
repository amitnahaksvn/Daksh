"use client";
import Link from "next/link";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { DashboardAppCard } from "@/components/integrations";
import { buttonClass, EmptyState, ErrorState, Loading, PageHeader } from "@/components/ui";
import { GMAIL_CARD_HINT_KEY, GmailCardPlaceholder, GmailDashboardCard } from "@/components/GmailDashboardCard";
import { DashboardHero } from "@/components/DashboardWidgets";
import { api, type DashboardApp, type DashboardLayoutItem } from "@/lib/api";
import { DashboardTile, getActiveTileDrag, getGrabOffsetX, type TileDrag } from "@/components/DashboardTile";
import { GRID_COLUMNS, applySize, colOf, effectiveStart, heightOf, isCustomized, moveTile, placeTile, setTileCol, sortTiles, spanOf, startFromPointer, toLayoutPayload } from "@/lib/dashboardLayout";
import { useApi } from "@/lib/useApi";

/** Messaging apps (WhatsApp, Telegram) live in the Chats sidebar on every page, so they are not tiles here. */
const MESSAGING = new Set(["whatsapp", "telegram"]);

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
    try { await api.saveDashboardLayout(p.payload); setSaveState("saved"); } catch { setSaveState("error"); }
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
    void api.saveDashboardLayout(p.payload).catch(() => undefined);
  }, []);

  const commit = (next: DashboardApp[], message?: string) => { setTiles(next); schedule(next); if (message) setAnnounce(message); };
  const move = (id: string, to: number, name: string) => {
    const next = moveTile(tilesRef.current, id, to);
    if (next !== tilesRef.current) commit(next, `${name} moved to position ${to + 1} of ${next.length}.`);
  };

  async function reset() {
    if (pending.current) window.clearTimeout(pending.current.timer);
    pending.current = null;
    setSaveState("saving");
    try { await api.resetDashboardLayout(); setSaveState("saved"); onReload(); } catch { setSaveState("error"); }
  }

  return (
    <>
      <DashboardHero apps={tiles} canResetLayout={isCustomized(tiles)} onResetLayout={() => void reset()} layoutSaveFailed={saveState === "error"} onRetryLayoutSave={() => schedule(tilesRef.current)} />
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
              key={app.id} id={app.id} name={app.name} span={spanOf(app)} height={heightOf(app)} colStart={effectiveStart(spanOf(app), colOf(app))} index={index} count={tiles.length}
              drag={drag} onDragChange={onDragChange} onActiveChange={setResizingNow}
              onMove={(delta) => move(app.id, index + delta, app.name)}
              onPlace={(col) => commit(setTileCol(tilesRef.current, app.id, col), `${app.name} moved to column ${col}.`)}
              onResizeCommit={(change) => commit(applySize(tilesRef.current, app.id, change), `${app.name} resized.`)}
              onDropOn={(fromId) => move(fromId, index, tilesRef.current.find((t) => t.id === fromId)?.name ?? "Tile")}
            >
              {app.integrationKey === "gmail"
                ? <GmailDashboardCard app={app} onChanged={onReload} />
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

export function DashboardPage() {
  const { state, reload } = useApi(() => api.dashboardApps());
  // Did this browser last see a Gmail card? If so, hold its space while loading instead of flashing a small spinner.
  const expectGmail = useSyncExternalStore(() => () => {}, () => { try { return localStorage.getItem(GMAIL_CARD_HINT_KEY) === "1"; } catch { return false; } }, () => false);
  useEffect(() => {
    if (state.status !== "ready") return;
    try { localStorage.setItem(GMAIL_CARD_HINT_KEY, state.data.some((a) => a.integrationKey === "gmail") ? "1" : "0"); } catch { /* storage unavailable */ }
  }, [state]);

  async function remove(id: string) {
    await api.removeFromDashboard(id);
    reload();
  }

  return (
    <>
      {state.status === "loading" && (expectGmail ? <GmailCardPlaceholder /> : <Loading />)}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" &&
        (state.data.filter((a) => !MESSAGING.has(a.integrationKey)).length === 0 ? (
          <>
            <PageHeader title="Dashboard" />
            <EmptyState>
              Nothing here yet. Your WhatsApp and Telegram chats are always in the Chats sidebar on the right. Connect more apps to see them here.{" "}
              <Link href="/apps" className={`${buttonClass} mt-3 inline-flex`}>Browse apps</Link>
            </EmptyState>
          </>
        ) : (
          <DashboardBody apps={state.data.filter((a) => !MESSAGING.has(a.integrationKey))} onRemove={remove} onReload={reload} />
        ))}
    </>
  );
}
