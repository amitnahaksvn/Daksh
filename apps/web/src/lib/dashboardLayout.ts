/** Pure helpers for the dashboard tile layout (order + width). Kept free of React so they can be tested directly. */
import type { DashboardApp, DashboardLayoutItem } from "@/lib/api";

/** The dashboard is a 12-column grid on wide screens (a single stacked column on narrow ones). A tile can be any width from 3 to 12 columns. */
export const GRID_COLUMNS = 12;
export const MIN_SPAN = 3;
export const MAX_SPAN = 12;

/** A tile's width when the user hasn't resized it: the Gmail dashboard is two-thirds wide (8 of 12), everything else a third (4 of 12). */
export function defaultSpan(app: Pick<DashboardApp, "integrationKey">): number {
  return app.integrationKey === "gmail" ? 8 : 4;
}
export function spanOf(app: Pick<DashboardApp, "integrationKey" | "cols">): number {
  return app.cols >= MIN_SPAN && app.cols <= MAX_SPAN ? app.cols : defaultSpan(app);
}

/** Tailwind only generates classes it can see as whole strings, so every width is spelled out. Below `lg` tiles stack full width. */
const SPAN_CLASSES: Record<number, string> = {
  3: "lg:col-span-3", 4: "lg:col-span-4", 5: "lg:col-span-5", 6: "lg:col-span-6", 7: "lg:col-span-7",
  8: "lg:col-span-8", 9: "lg:col-span-9", 10: "lg:col-span-10", 11: "lg:col-span-11", 12: "lg:col-span-12",
};
const START_CLASSES: Record<number, string> = {
  1: "lg:col-start-1", 2: "lg:col-start-2", 3: "lg:col-start-3", 4: "lg:col-start-4", 5: "lg:col-start-5",
  6: "lg:col-start-6", 7: "lg:col-start-7", 8: "lg:col-start-8", 9: "lg:col-start-9", 10: "lg:col-start-10",
};
/** Width class, plus a start-column class when the tile has been placed in a specific column. */
export const spanClass = (span: number, start = 0): string => {
  const width = SPAN_CLASSES[Math.max(MIN_SPAN, Math.min(MAX_SPAN, Math.round(span)))];
  return start > 0 ? `${width} ${START_CLASSES[Math.max(1, Math.min(10, start))]}` : width;
};

/** The column a tile starts in (1–10), or 0 for "flow after the previous tile". */
export const colOf = (app: Pick<DashboardApp, "col">) => (app.col >= 1 && app.col <= 10 ? app.col : 0);
/** A saved start column, pulled left if the tile is now too wide to fit from there. */
export const effectiveStart = (span: number, col: number) => (col > 0 ? Math.max(1, Math.min(col, GRID_COLUMNS - span + 1)) : 0);
/** The start column that dragging a tile's left edge to `leftX` means, kept inside the grid for a tile `span` columns wide. */
export function startFromPointer(leftX: number, gridLeft: number, gridWidth: number, gap: number, columns: number, span: number): number {
  const column = (gridWidth - gap * (columns - 1)) / columns;
  const raw = Math.round((leftX - gridLeft) / (column + gap)) + 1;
  return Math.max(1, Math.min(columns - span + 1, raw));
}
export function setTileCol(tiles: DashboardApp[], id: string, col: number): DashboardApp[] {
  const next = col <= 0 ? 0 : Math.max(1, Math.min(10, Math.round(col)));
  return tiles.map((t) => (t.id === id && t.col !== next ? { ...t, col: next } : t));
}
/** Places a tile in a column and, when dropped below everything else, at the end of the order. */
export function placeTile(tiles: DashboardApp[], id: string, col: number, atEnd: boolean): DashboardApp[] {
  const placed = setTileCol(tiles, id, col);
  return atEnd ? moveTile(placed, id, placed.length - 1) : placed;
}

export const MIN_HEIGHT = 200;
export const MAX_HEIGHT = 1600;
export const HEIGHT_STEP = 120;
/** A saved height in pixels, or 0 for "fit the content". Anything out of range is treated as unset. */
export const heightOf = (app: Pick<DashboardApp, "height">) => (app.height >= 160 && app.height <= 2000 ? app.height : 0);
export const clampHeight = (h: number) => Math.max(MIN_HEIGHT, Math.min(MAX_HEIGHT, Math.round(h / 10) * 10));
/** The height that dragging a tile's bottom edge to `pointerY` means (snapped to 10 px). */
export const heightFromPointer = (pointerY: number, tileTop: number) => clampHeight(pointerY - tileTop);

export const sortTiles = (apps: DashboardApp[]) => [...apps].sort((a, b) => a.displayOrder - b.displayOrder);

export function moveTile<T extends { id: string }>(tiles: T[], id: string, toIndex: number): T[] {
  const from = tiles.findIndex((t) => t.id === id);
  if (from < 0) return tiles;
  const to = Math.max(0, Math.min(tiles.length - 1, toIndex));
  if (from === to) return tiles;
  const next = [...tiles];
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item);
  return next;
}
export const moveTileBy = <T extends { id: string }>(tiles: T[], id: string, delta: number) =>
  moveTile(tiles, id, tiles.findIndex((t) => t.id === id) + delta);

export function resizeTile(tiles: DashboardApp[], id: string, span: number): DashboardApp[] {
  const clamped = Math.max(MIN_SPAN, Math.min(MAX_SPAN, Math.round(span)));
  return tiles.map((t) => (t.id === id && spanOf(t) !== clamped ? { ...t, cols: clamped } : t));
}

/** How many grid columns exist at a viewport width: 12 from the `lg` breakpoint up, otherwise tiles stack in one column. */
export const columnsForWidth = (width: number) => (width >= 1024 ? GRID_COLUMNS : 1);

/** The width, in columns, that dragging a tile's right edge to `pointerX` means (3–12, whole columns). */
export function spanFromPointer(pointerX: number, tileLeft: number, gridWidth: number, gap: number, columns: number): number {
  const column = (gridWidth - gap * (columns - 1)) / columns;
  const raw = (pointerX - tileLeft + gap) / (column + gap);
  return Math.max(MIN_SPAN, Math.min(Math.min(MAX_SPAN, columns), Math.round(raw)));
}

/** Applies a width and/or height change to one tile. */
export function applySize(tiles: DashboardApp[], id: string, change: { span?: number; height?: number }): DashboardApp[] {
  let next = tiles;
  if (change.span !== undefined) next = resizeTile(next, id, change.span);
  if (change.height !== undefined) next = setTileHeight(next, id, change.height);
  return next;
}

/** `height` 0 puts the tile back to fitting its content. */
export function setTileHeight(tiles: DashboardApp[], id: string, height: number): DashboardApp[] {
  const next = height <= 0 ? 0 : clampHeight(height);
  return tiles.map((t) => (t.id === id && t.height !== next ? { ...t, height: next } : t));
}

export const toLayoutPayload = (tiles: DashboardApp[]): DashboardLayoutItem[] =>
  tiles.map((t, index) => ({ id: t.id, displayOrder: index, cols: t.cols > 0 ? t.cols : 0, height: heightOf(t), col: colOf(t) }));

/** True when anything differs from the default arrangement, so "Reset layout" is only offered when it would do something. */
export function isCustomized(tiles: DashboardApp[]): boolean {
  return tiles.some((t) => t.cols > 0 || heightOf(t) > 0 || colOf(t) > 0);
}
