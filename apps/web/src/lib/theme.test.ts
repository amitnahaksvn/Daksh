import { beforeEach, describe, expect, it, vi } from "vitest";
import { applyAppearance, readStylePref, readThemePref, setStylePref, setThemePref, STYLE_KEY, THEME_KEY } from "./theme";

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

  it("defaults to Bento, following the system", () => {
    expect(readStylePref()).toBe("bento");
    expect(readThemePref()).toBe("system");
    applyAppearance();
    expect(document.documentElement.dataset.style).toBe("bento");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("follows a dark system in Auto, and Day wins over it", () => {
    mockSystem(true);
    applyAppearance();
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    setThemePref("light");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("Neural is always night, whatever Day or Auto says", () => {
    setThemePref("light");
    setStylePref("neural");
    expect(document.documentElement.dataset.style).toBe("neural");
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    setStylePref("bento");
    expect(document.documentElement.classList.contains("dark")).toBe(false);
  });

  it("ignores values it does not know", () => {
    window.localStorage.setItem(STYLE_KEY, "sparkle");
    window.localStorage.setItem(THEME_KEY, "purple");
    expect(readStylePref()).toBe("bento");
    expect(readThemePref()).toBe("system");
  });
});
