"use strict";
// Remembers where the window was. Pure functions here; main.cjs does the file reads and writes.

const DEFAULTS = { width: 1280, height: 860 };
const MIN = { width: 420, height: 560 };

function isNum(n) { return typeof n === "number" && Number.isFinite(n); }

/** Picks the saved size and place if it still fits a connected screen; otherwise the default size, centred by the system. */
function restoreBounds(saved, displays) {
  const out = { ...DEFAULTS, maximized: false };
  if (!saved || typeof saved !== "object") return out;
  if (isNum(saved.width) && isNum(saved.height)) {
    out.width = Math.max(MIN.width, Math.round(saved.width));
    out.height = Math.max(MIN.height, Math.round(saved.height));
  }
  out.maximized = saved.maximized === true;
  if (isNum(saved.x) && isNum(saved.y)) {
    // At least 80 x 40 pixels of the title bar must be on some screen, so a window is never lost off to the side.
    const visible = (displays || []).some((d) => {
      const a = d.workArea || d.bounds;
      return a && saved.x + 80 <= a.x + a.width && saved.x + out.width - 80 >= a.x && saved.y >= a.y - 4 && saved.y + 40 <= a.y + a.height;
    });
    if (visible) { out.x = Math.round(saved.x); out.y = Math.round(saved.y); }
  }
  return out;
}

module.exports = { DEFAULTS, MIN, restoreBounds };
