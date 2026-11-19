import { describe, expect, it } from "vitest";
import type { DashboardApp } from "@/lib/api";
import { colOf, effectiveStart, placeTile, setTileCol, startFromPointer, GRID_COLUMNS, MAX_SPAN, MIN_SPAN, applySize, clampHeight, columnsForWidth, defaultSpan, heightFromPointer, heightOf, isCustomized, moveTile, moveTileBy, resizeTile, setTileHeight, sortTiles, spanClass, spanFromPointer, spanOf, toLayoutPayload } from "./dashboardLayout";

const app = (id: string, order: number, key = "notion", cols = 0, height = 0, col = 0) => ({ id, displayOrder: order, integrationKey: key, cols, height, col }) as unknown as DashboardApp;

describe("dashboard layout (12-column grid)", () => {
  it("is a 12-column grid and a tile can be 3 to 12 columns wide", () => {
    expect([GRID_COLUMNS, MIN_SPAN, MAX_SPAN]).toEqual([12, 3, 12]);
  });

  it("defaults: Gmail is 8 of 12 columns, everything else 4; any saved width from 3 to 12 wins", () => {
    expect(defaultSpan(app("a", 0, "gmail"))).toBe(8);
    expect(spanOf(app("b", 0, "notion"))).toBe(4);
    for (const cols of [3, 4, 5, 6, 7, 8, 9, 10, 11, 12]) expect(spanOf(app("c", 0, "notion", cols))).toBe(cols);
    expect(spanOf(app("a", 0, "gmail", 5))).toBe(5);
    expect(spanOf(app("b", 0, "notion", 2))).toBe(4); // below the minimum: treated as unset
    expect(spanOf(app("b", 0, "notion", 99))).toBe(4);
  });

  it("has a class for every allowed width, not just thirds", () => {
    for (let n = MIN_SPAN; n <= MAX_SPAN; n++) expect(spanClass(n)).toBe(`lg:col-span-${n}`);
    expect(spanClass(1)).toBe("lg:col-span-3"); // out-of-range values are clamped
    expect(spanClass(40)).toBe("lg:col-span-12");
  });

  it("sorts by saved position without mutating the input", () => {
    const input = [app("c", 2), app("a", 0), app("b", 1)];
    expect(sortTiles(input).map((t) => t.id)).toEqual(["a", "b", "c"]);
    expect(input.map((t) => t.id)).toEqual(["c", "a", "b"]);
  });

  it("moves a tile to an index, clamps, and is a no-op when nothing changes", () => {
    const t = [app("a", 0), app("b", 1), app("c", 2)];
    expect(moveTile(t, "a", 2).map((x) => x.id)).toEqual(["b", "c", "a"]);
    expect(moveTile(t, "c", 0).map((x) => x.id)).toEqual(["c", "a", "b"]);
    expect(moveTile(t, "b", 1)).toBe(t);
    expect(moveTile(t, "zzz", 0)).toBe(t);
    expect(moveTileBy(t, "b", 1).map((x) => x.id)).toEqual(["a", "c", "b"]);
    expect(moveTileBy(t, "a", -1)).toBe(t);
  });

  it("resizes to any whole column count within 3–12 and only touches the target tile", () => {
    const t = [app("a", 0), app("b", 1), app("g", 2, "gmail")];
    expect(resizeTile(t, "a", 7).map((x) => x.cols)).toEqual([7, 0, 0]);
    expect(resizeTile(t, "a", 99)[0].cols).toBe(12);
    expect(resizeTile(t, "a", 1)[0].cols).toBe(3); // clamped up to the minimum
    expect(resizeTile(t, "g", 8)).toEqual(t); // already 8 by default
    expect(resizeTile(t, "a", 4)).toEqual(t); // already 4 by default
  });

  it("maps viewport widths to columns: 12 from lg up, otherwise stacked", () => {
    expect([500, 900, 1023].map(columnsForWidth)).toEqual([1, 1, 1]);
    expect([1024, 1400].map(columnsForWidth)).toEqual([12, 12]);
  });

  it("turns a pointer position into a snapped column span", () => {
    // 12 columns, grid 1200 wide, 16 gap → column ≈ 84.7; tile starts at x=100.
    const at = (px: number) => spanFromPointer(100 + px, 100, 1200, 16, 12);
    expect(at(300)).toBe(3);     // 3 columns ≈ 286 wide
    expect(at(400)).toBe(4);
    expect(at(500)).toBe(5);     // not just 4 / 8 / 12
    expect(at(700)).toBe(7);
    expect(at(1180)).toBe(12);
    expect(at(5000)).toBe(12);
    expect(at(-50)).toBe(3);     // never below the minimum
    expect(spanFromPointer(5000, 100, 600, 16, 1)).toBe(3); // a single stacked column is clamped but the handle isn't offered there
  });

  it("builds the save payload with positions and 0 for 'default width'", () => {
    expect(toLayoutPayload([app("b", 5, "notion", 7), app("a", 9)])).toEqual([{ id: "b", displayOrder: 0, cols: 7, height: 0, col: 0 }, { id: "a", displayOrder: 1, cols: 0, height: 0, col: 0 }]);
    expect(isCustomized([app("a", 0)])).toBe(false);
    expect(isCustomized([app("a", 0, "notion", 5)])).toBe(true);
  });

  it("heights: saved values are validated, clamped and snapped; 0 means fit the content", () => {
    expect(heightOf(app("a", 0, "x", 0, 420))).toBe(420);
    expect(heightOf(app("a", 0, "x", 0, 50))).toBe(0);
    expect(heightOf(app("a", 0, "x", 0, 99999))).toBe(0);
    expect(clampHeight(10)).toBe(200); expect(clampHeight(5000)).toBe(1600); expect(clampHeight(433)).toBe(430);
    expect(heightFromPointer(700, 200)).toBe(500);
    const t = [app("a", 0), app("b", 1)];
    expect(setTileHeight(t, "a", 450)[0].height).toBe(450);
    expect(setTileHeight(t, "a", 0)).toEqual(t);
    expect(applySize(t, "a", { span: 9, height: 300 })[0]).toMatchObject({ cols: 9, height: 300 });
    expect(applySize(t, "a", { span: 5, col: 4 })[0]).toMatchObject({ cols: 5, col: 4 }); // resizing from the left edge also moves the start column
    expect(toLayoutPayload(setTileHeight(t, "b", 333))[1]).toEqual({ id: "b", displayOrder: 1, cols: 0, height: 330, col: 0 });
    expect(isCustomized(setTileHeight(t, "a", 300))).toBe(true);
  });

  it("columns: a tile can start in any column 1–10 and is pulled left when it would not fit", () => {
    expect(colOf(app("a", 0, "x", 0, 0, 5))).toBe(5);
    expect(colOf(app("a", 0, "x", 0, 0, 0))).toBe(0);
    expect(colOf(app("a", 0, "x", 0, 0, 11))).toBe(0);
    expect(effectiveStart(8, 0)).toBe(0);
    expect(effectiveStart(8, 3)).toBe(3);
    expect(effectiveStart(8, 9)).toBe(5);   // 8 wide can start no later than column 5
    expect(effectiveStart(12, 4)).toBe(1);  // full width always starts at 1
    const t = [app("a", 0), app("b", 1)];
    expect(setTileCol(t, "a", 6)[0].col).toBe(6);
    expect(setTileCol(t, "a", 99)[0].col).toBe(10);
    expect(setTileCol(setTileCol(t, "a", 6), "a", 0)[0].col).toBe(0);
    expect(isCustomized([app("a", 0, "x", 0, 0, 3)])).toBe(true);
    expect(toLayoutPayload(setTileCol(t, "b", 4))[1]).toMatchObject({ id: "b", col: 4 });
  });

  it("placing a tile sets its column and, when dropped below everything, moves it to the end", () => {
    const t = [app("a", 0), app("b", 1), app("c", 2)];
    expect(placeTile(t, "a", 5, false).map((x) => [x.id, x.col])).toEqual([["a", 5], ["b", 0], ["c", 0]]);
    expect(placeTile(t, "a", 5, true).map((x) => x.id)).toEqual(["b", "c", "a"]);
  });

  it("turns a dragged tile's left edge into a start column, kept inside the grid", () => {
    // 12 columns, grid 1200 wide at x=100, gap 16 → column ≈ 84.7, pitch ≈ 100.7
    const at = (x: number, span: number) => startFromPointer(x, 100, 1200, 16, 12, span);
    expect(at(100, 4)).toBe(1);
    expect(at(100 + 3 * 100.7, 4)).toBe(4);
    expect(at(100 + 6 * 100.7, 4)).toBe(7);
    expect(at(2000, 4)).toBe(9);   // a 4-wide tile can start no later than column 9
    expect(at(2000, 8)).toBe(5);
    expect(at(-500, 8)).toBe(1);
  });
});
