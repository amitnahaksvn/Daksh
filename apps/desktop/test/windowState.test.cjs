"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { DEFAULTS, MIN, restoreBounds } = require("../src/windowState.cjs");

const screen1 = [{ workArea: { x: 0, y: 0, width: 1920, height: 1040 } }];

test("with nothing saved the window gets its default size", () => {
  assert.deepEqual(restoreBounds(null, screen1), { ...DEFAULTS, maximized: false });
  assert.deepEqual(restoreBounds("junk", screen1), { ...DEFAULTS, maximized: false });
});

test("a saved size and place on a connected screen is restored", () => {
  assert.deepEqual(restoreBounds({ x: 100, y: 80, width: 1000, height: 700, maximized: true }, screen1), { x: 100, y: 80, width: 1000, height: 700, maximized: true });
});

test("a window saved on a screen that is gone is brought back (size kept, place dropped)", () => {
  const r = restoreBounds({ x: 3000, y: 100, width: 1000, height: 700 }, screen1);
  assert.equal(r.x, undefined); assert.equal(r.width, 1000);
});

test("tiny or nonsense sizes are raised to the minimum", () => {
  const r = restoreBounds({ width: 10, height: 10 }, screen1);
  assert.equal(r.width, MIN.width); assert.equal(r.height, MIN.height);
  assert.equal(restoreBounds({ x: "a", y: NaN, width: 900, height: 700 }, screen1).x, undefined);
});

test("a window whose title bar is on a second screen is kept", () => {
  const two = [...screen1, { workArea: { x: 1920, y: 0, width: 1920, height: 1040 } }];
  assert.equal(restoreBounds({ x: 2400, y: 50, width: 1000, height: 700 }, two).x, 2400);
});
