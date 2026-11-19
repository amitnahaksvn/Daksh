import { beforeEach, describe, expect, it, vi } from "vitest";
import { applyAppearance, DEFAULT_STYLE, effectiveShell, LAYOUT_KEY, readLayoutPref, setLayoutPref, readStylePref, readThemePref, setStylePref, setThemePref, STYLE_KEY, THEME_KEY, THEMES } from "./theme";

/** A tiny in-memory localStorage: the one Node provides is unusable without a file, and jsdom's is shadowed by it. */
function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return {
    get length() { return data.size; },
    clear: () => data.clear(),
    getItem: (k) => data.get(k) ?? null,
    key: (i) => [...data.keys()][i] ?? null,
    removeItem: (k) => void data.delete(k),
    setItem: (k, v) => void data.set(k, String(v)),
  };
}

function mockSystem(dark: boolean) {
  window.matchMedia = vi.fn().mockImplementation(() => ({ matches: dark, addEventListener: vi.fn(), removeEventListener: vi.fn() })) as never;
}

describe("appearance", () => {
  beforeEach(() => {
    vi.stubGlobal("localStorage", memoryStorage());
    Object.defineProperty(window, "localStorage", { value: localStorage, configurable: true });
    document.documentElement.className = "";
    delete document.documentElement.dataset.style;
    mockSystem(false);
  });

  it("defaults to the recommended hybrid theme, following the system", () => {
    expect(readStylePref()).toBe(DEFAULT_STYLE);
    expect(readThemePref()).toBe("system");
    applyAppearance();
    expect(document.documentElement.dataset.style).toBe("hybrid");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("follows a dark system in Auto, and Day wins over it", () => {
    mockSystem(true);
    applyAppearance();
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    setThemePref("light");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("ignores values it does not know", () => {
    window.localStorage.setItem(STYLE_KEY, "sparkle");
    window.localStorage.setItem(THEME_KEY, "purple");
    expect(readStylePref()).toBe(DEFAULT_STYLE);
    expect(readThemePref()).toBe("system");
  });

  it("no longer offers the old Bento, Neural and Aurora styles", () => {
    expect(THEMES.map((t) => t.id)).not.toContain("bento");
    window.localStorage.setItem(STYLE_KEY, "neural");
    expect(readStylePref()).toBe(DEFAULT_STYLE);
  });

  it("offers all ten new themes, each with a day and a night look", () => {
    const ids = THEMES.map((t) => t.id);
    for (const id of ["sandhya", "prana", "yantra", "hybrid", "rangoli", "ratna", "galaxy", "mitra", "nakshatra", "kavach"]) expect(ids).toContain(id);
    setStylePref("galaxy");
    expect(readStylePref()).toBe("galaxy");
    expect(document.documentElement.dataset.style).toBe("galaxy");
    setThemePref("dark");
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    setThemePref("light");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("loads the active theme's fonts and swaps them when the theme changes", () => {
    setStylePref("sandhya");
    expect(document.getElementById("daksh-theme-fonts")?.getAttribute("href")).toContain("Fraunces");
    setStylePref("prana");
    expect(document.getElementById("daksh-theme-fonts")?.getAttribute("href")).toContain("Sora");
  });

  it("lets any theme use any layout, and 'auto' follows the theme's own", () => {
    for (const t of THEMES) {
      expect(effectiveShell(t.id, "auto")).toBe(t.shell);
      for (const layout of ["sidebar", "rail", "top"] as const) expect(effectiveShell(t.id, layout)).toBe(layout);
    }
    expect(readLayoutPref()).toBe("auto");
    setLayoutPref("top");
    expect(readLayoutPref()).toBe("top");
    setLayoutPref("auto");
    expect(readLayoutPref()).toBe("auto");
    window.localStorage.setItem(LAYOUT_KEY, "diagonal");
    expect(readLayoutPref()).toBe("auto"); // an unknown value is ignored
  });
});
