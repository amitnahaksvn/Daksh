"use client";
import { useRef, useState, type DragEvent, type PointerEvent, type ReactNode } from "react";
import { GRID_COLUMNS, MAX_SPAN, MIN_SPAN, clampHeight, heightFromPointer, spanClass, spanFromPointer } from "@/lib/dashboardLayout";

export type TileDrag = { dragId: string | null; overId: string | null };
export type TileResize = { span?: number; height?: number; col?: number };

/** The tile being dragged right now. Module-level on purpose: drag events can fire before React re-renders, and Safari reports
 *  dataTransfer types inconsistently, so neither state nor the drag data is a reliable way to know "this is one of our tiles". */
let activeTileDrag: string | null = null;
/** How far inside the tile the pointer was when the drag began, so the tile's left edge follows the pointer, not its centre. */
let grabOffsetX = 0;
export const getActiveTileDrag = () => activeTileDrag;
export const getGrabOffsetX = () => grabOffsetX;

/** Things inside a card that must keep working normally: pressing them never starts a tile drag. */
const INTERACTIVE = "button, a, input, textarea, select, summary, [data-no-drag]";

type Edge = "right" | "left" | "bottom" | "top" | "corner";
/** What a resize would produce if the pointer were released now. Shown as a ghost; the tile itself only changes on release. */
type Ghost = { width: number; height: number; span: number; tall: number; col?: number; dx: number; dy: number };

/**
 * Wraps one dashboard card so it can be moved and resized.
 * Move: drag the tile from anywhere that isn't a button/link, or use the ← → buttons (keyboard and touch friendly).
 * Resize: drag the right edge (width, snaps to grid columns), the bottom edge (height) or the corner (both), or use the toolbar buttons.
 * While resizing, the tile stays put and a dashed "shadow" shows the size it would become — including into empty space — until you let go.
 * A tile with a set height scrolls inside itself. This component only reports intent; the page decides what to do and saves it.
 */
export function DashboardTile({ id, name, span, height, colStart, drag, onDragChange, onActiveChange, onResizeCommit, onDropOn, children }: {
  id: string; name: string; span: number; height: number; colStart: number; drag: TileDrag;
  onDragChange: (d: TileDrag) => void;
  /** True while this tile is being resized, so the page can show the grid guides. */
  onActiveChange: (active: boolean) => void;
  /** A finished resize (edge drag released, or a toolbar button): applied and saved. */
  onResizeCommit: (change: TileResize) => void;
  onDropOn: (fromId: string) => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLLIElement>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const dragAllowed = useRef(false);
  const [resizing, setResizing] = useState<Edge | null>(null);
  const [ghost, setGhost] = useState<Ghost | null>(null);
  /** True once a drag of this tile is under way. Set a tick AFTER it starts: changing the source element inside dragstart aborts the drag in Safari. */
  const [held, setHeld] = useState(false);
  const ghostRef = useRef<Ghost | null>(null);

  // The tile is only natively draggable while such a press is in progress. It is switched on directly on the element (not through React
  // state) so it is already set when the browser decides whether to start a drag — Safari needs that — and so a press on a resize handle,
  // button or scrollbar never turns into a native drag that would cancel it.
  function onPressTile(e: PointerEvent<HTMLLIElement>) {
    const target = e.target as HTMLElement;
    if (!e.currentTarget.contains(target)) return; // a press inside a pop-up opened from this tile (it bubbles here through React) is not a press on the tile
    const s = scroller.current;
    const onScrollbar = !!s && target === s && e.clientX > s.getBoundingClientRect().left + s.clientWidth;
    dragAllowed.current = !target.closest(INTERACTIVE) && !onScrollbar && e.pointerType === "mouse";
    e.currentTarget.draggable = dragAllowed.current;
  }
  function endPress(e: { currentTarget: HTMLLIElement }) { e.currentTarget.draggable = false; dragAllowed.current = false; }
  function dragStart(e: DragEvent<HTMLLIElement>) {
    if (!dragAllowed.current) { e.preventDefault(); return; }
    e.dataTransfer.effectAllowed = "move";
    e.dataTransfer.setData("text/plain", id);
    activeTileDrag = id;
    window.setTimeout(() => setHeld(true), 0);
    grabOffsetX = e.clientX - e.currentTarget.getBoundingClientRect().left;
    onDragChange({ dragId: id, overId: null });
  }

  function startResize(edge: Edge) { setResizing(edge); onActiveChange(true); }
  function moveResize(edge: Edge, e: PointerEvent<HTMLButtonElement>) {
    const tile = ref.current; const grid = tile?.parentElement;
    if (resizing !== edge || !tile || !grid) return;
    const rect = tile.getBoundingClientRect();
    const gridRect = grid.getBoundingClientRect();
    const style = getComputedStyle(grid);
    const columns = style.gridTemplateColumns.split(" ").filter(Boolean).length || 1;
    const gap = parseFloat(style.columnGap) || 0;
    const column = (gridRect.width - gap * (columns - 1)) / columns;
    const limit = Math.min(MAX_SPAN, columns);
    const current = Math.round(rect.height);
    let nextSpan = span; let col: number | undefined; let nextTall = height > 0 ? height : current;
    if (edge === "right" || edge === "corner") nextSpan = spanFromPointer(e.clientX, rect.left, gridRect.width, gap, columns);
    if (edge === "left") {
      // The right edge stays where it is; the left edge follows the pointer, so the start column moves too.
      nextSpan = Math.max(MIN_SPAN, Math.min(limit, Math.round((rect.right - e.clientX + gap) / (column + gap))));
      col = Math.max(1, currentStart() + span - nextSpan);
    }
    if (edge === "bottom" || edge === "corner") nextTall = heightFromPointer(e.clientY, rect.top);
    if (edge === "top") nextTall = clampHeight(rect.bottom - e.clientY);
    const width = nextSpan * column + (nextSpan - 1) * gap;
    const next: Ghost = { span: nextSpan, tall: nextTall, width, height: nextTall, col, dx: edge === "left" ? rect.width - width : 0, dy: edge === "top" ? current - nextTall : 0 };
    ghostRef.current = next; setGhost(next);
  }
  function endResize(edge: Edge, commit: boolean) {
    const g = ghostRef.current;
    ghostRef.current = null; setGhost(null); setResizing(null); onActiveChange(false);
    if (!commit || !g) return;
    const widthEdge = edge === "right" || edge === "left" || edge === "corner";
    const heightEdge = edge === "bottom" || edge === "top" || edge === "corner";
    onResizeCommit({ ...(widthEdge ? { span: g.span } : {}), ...(edge === "left" && g.col ? { col: g.col } : {}), ...(heightEdge ? { height: g.tall } : {}) });
  }
  const handle = (edge: Edge, label: string, cls: string) => (
    <ResizeHandle
      edge={edge} label={`${label} ${name}`} active={resizing === edge} className={cls}
      onStart={() => startResize(edge)} onMove={(e) => moveResize(edge, e)} onEnd={(commit) => endResize(edge, commit)}
    />
  );

  const over = drag.dragId !== null && drag.dragId !== id && drag.overId === id;
  /** The column the tile currently starts in, read from where it actually is (it may be flowing automatically). */
  const currentStart = () => {
    const tile = ref.current; const grid = tile?.parentElement;
    if (!tile || !grid) return 1;
    const style = getComputedStyle(grid);
    const columns = style.gridTemplateColumns.split(" ").filter(Boolean).length || 1;
    const gap = parseFloat(style.columnGap) || 0;
    const colWidth = (grid.getBoundingClientRect().width - gap * (columns - 1)) / columns;
    const pitch = colWidth + gap;
    if (!Number.isFinite(pitch) || pitch <= 0) return 1;
    return Math.max(1, Math.round((tile.getBoundingClientRect().left - grid.getBoundingClientRect().left) / pitch) + 1);
  };

  return (
    <li
      ref={ref}
      data-tile={id}
      onPointerDown={onPressTile}
      onPointerUp={endPress}
      onDragStart={dragStart}
      onDragEnd={(e) => { activeTileDrag = null; setHeld(false); endPress(e); onDragChange({ dragId: null, overId: null }); }}
      onDragOver={(e) => {
        if (!activeTileDrag) return; // not one of our tiles
        e.preventDefault();
        e.dataTransfer.dropEffect = "move";
        if (activeTileDrag !== id && drag.overId !== id) onDragChange({ dragId: activeTileDrag, overId: id });
      }}
      onDrop={(e) => {
        e.preventDefault();
        const from = activeTileDrag;
        if (from && from !== id) { activeTileDrag = null; onDropOn(from); onDragChange({ dragId: null, overId: null }); }
        // Dropped on its own area: leave the drag open so the grid can place the tile in the column under the pointer.
      }}
      style={height > 0 ? { height } : undefined}
      className={`group relative min-w-0 cursor-grab select-none active:cursor-grabbing ${spanClass(span, colStart)} ${drag.dragId === id ? "opacity-40" : ""} ${held && drag.dragId === id ? "pointer-events-none" : ""} ${over ? "rounded-[1.25rem] outline-2 outline-offset-2 outline-accent" : ""} rounded-[1.25rem] outline-1 outline-offset-2 outline-transparent transition-[outline-color] hover:outline-dashed hover:outline-accent/40`}
    >
      {/* A tile with a fixed height scrolls inside itself so nothing is cut off. */}
      <div ref={scroller} className={height > 0 ? "h-full overflow-y-auto overflow-x-hidden overscroll-contain rounded-[1.25rem]" : undefined}>{children}</div>

      {/* The resize "shadow": where the tile's edges will land if released now. It may extend into empty space beside or below the tile. */}
      {ghost && (
        <div aria-hidden style={{ width: ghost.width, height: ghost.height, left: ghost.dx, top: ghost.dy }}
          className="pointer-events-none absolute z-30 rounded-[1.25rem] border-2 border-dashed border-accent bg-accent/15 shadow-[0_0_0_6px_color-mix(in_srgb,var(--accent)_16%,transparent)]">
          <span className="absolute bottom-3 left-1/2 -translate-x-1/2 whitespace-nowrap rounded-full bg-accent px-3 py-1 text-xs font-semibold text-accent-contrast shadow">
            {ghost.span} of {GRID_COLUMNS} columns · {ghost.tall}px tall
          </span>
        </div>
      )}

      {/* Edge handles, always visible: left/right = width, top/bottom = height, corner = both. */}
      {handle("left", "Resize width from the left of", "left-0 top-1/2 hidden h-24 w-4 -translate-y-1/2 cursor-ew-resize lg:flex")}
      {handle("top", "Resize height from the top of", "left-1/2 top-0 flex h-4 w-24 -translate-x-1/2 cursor-ns-resize")}
      {handle("right", "Resize width of", "right-0 top-1/2 hidden h-24 w-4 -translate-y-1/2 cursor-ew-resize lg:flex")}
      {handle("bottom", "Resize height of", "bottom-0 left-1/2 flex h-4 w-24 -translate-x-1/2 cursor-ns-resize")}
      {handle("corner", "Resize width and height of", "bottom-0 right-0 hidden h-6 w-6 cursor-nwse-resize lg:flex")}
    </li>
  );
}

function ResizeHandle({ edge, label, active, className, onStart, onMove, onEnd }: {
  edge: Edge; label: string; active: boolean; className: string; onStart: () => void; onMove: (e: PointerEvent<HTMLButtonElement>) => void; onEnd: (commit: boolean) => void;
}) {
  // The button is a generous, invisible hit area; the small pill inside is what the user sees.
  const pill = edge === "right" || edge === "left" ? "h-10 w-1.5" : edge === "bottom" || edge === "top" ? "h-1.5 w-10" : "h-2.5 w-2.5";
  return (
    <button
      type="button" aria-label={label} title={label} data-no-drag="" data-edge={edge}
      onPointerDown={(e) => { e.currentTarget.setPointerCapture(e.pointerId); onStart(); }}
      onPointerMove={onMove}
      onPointerUp={(e) => { e.currentTarget.releasePointerCapture(e.pointerId); onEnd(true); }}
      onPointerCancel={() => onEnd(false)}
      className={`group/handle absolute z-10 touch-none items-center justify-center ${className}`}
    >
      <span className={`block rounded-full transition-[background-color,box-shadow] ${pill} ${active ? "bg-accent shadow-[0_0_0_4px_color-mix(in_srgb,var(--accent)_25%,transparent)]" : "bg-muted/40 group-hover/handle:bg-accent group-hover/handle:shadow-[0_0_0_4px_color-mix(in_srgb,var(--accent)_25%,transparent)]"}`} />
    </button>
  );
}
