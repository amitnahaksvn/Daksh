import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ThemeHero } from "./ThemeHero";
import { AppShell } from "./AppShell";
import { api, type DashboardApp } from "@/lib/api";
import { THEMES, STYLE_KEY } from "@/lib/theme";

vi.mock("next/link", () => ({ default: ({ href, children, className }: { href: string; children: React.ReactNode; className?: string }) => <a href={href} className={className}>{children}</a> }));
vi.mock("next/navigation", () => ({ usePathname: () => "/dashboard", useRouter: () => ({ push: vi.fn() }) }));
vi.mock("next/image", () => ({ default: () => null }));
vi.mock("./NotificationBell", () => ({ NotificationBell: () => null }));
vi.mock("./UserBadge", () => ({ UserBadge: () => null }));

function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return { get length() { return data.size; }, clear: () => data.clear(), getItem: (k) => data.get(k) ?? null, key: (i) => [...data.keys()][i] ?? null, removeItem: (k) => void data.delete(k), setItem: (k, v) => void data.set(k, String(v)) };
}
beforeEach(() => {
  Object.defineProperty(window, "localStorage", { value: memoryStorage(), configurable: true });
  window.matchMedia = vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })) as never;
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const app = (over: Partial<DashboardApp>): DashboardApp => ({ id: "w", connectionId: "c1", integrationKey: "whatsapp", name: "WhatsApp", icon: "", color: "", status: "Connected", accountIdentifier: null, lastActiveAt: null, displayOrder: 0, unreadCount: 2, lastMessagePreview: null, lastMessageAt: null, lastMessageIsGroup: false, cols: 0, height: 0, col: 0, ...over });
const stats = { needs: 3, handled: 12, next: "Call Rohan", first: "Reply to Rohan is ready" };

describe("every theme has its own dashboard hero", () => {
  it.each(THEMES.map((t) => t.id))("%s renders with real numbers", (id) => {
    window.localStorage.setItem(STYLE_KEY, id);
    vi.spyOn(api, "getConnectionStatus").mockResolvedValue({ autoReplyEnabled: false } as never);
    const { container } = render(<ThemeHero apps={[app({}), app({ id: "t", integrationKey: "telegram", name: "Telegram", status: "Error", connectionId: "c2" })]} stats={stats} />);
    expect(container.textContent?.length).toBeGreaterThan(20);
    if (id !== "kavach") expect(container.textContent).toMatch(/3|12/);
  });

  it("Kavach switches auto-reply for real", async () => {
    window.localStorage.setItem(STYLE_KEY, "kavach");
    vi.spyOn(api, "getConnectionStatus").mockResolvedValue({ autoReplyEnabled: false } as never);
    const update = vi.spyOn(api, "updateConnectionSettings").mockResolvedValue({ autoReplyEnabled: true } as never);
    render(<ThemeHero apps={[app({})]} stats={stats} />);
    const btn = await screen.findByRole("radio", { name: "Auto-reply" });
    await vi.waitFor(() => expect((btn as HTMLButtonElement).disabled).toBe(false));
    btn.click();
    await vi.waitFor(() => expect(update).toHaveBeenCalledWith("c1", { autoReplyEnabled: true }));
  });
});

describe("app shell follows the theme", () => {
  it.each([["hybrid", "sidebar"], ["ratna", "rail"], ["galaxy", "top"]])("%s uses the %s shell", (id, shell) => {
    window.localStorage.setItem(STYLE_KEY, id);
    const { container } = render(<AppShell><p>page</p></AppShell>);
    expect(container.querySelector(`[data-shell="${shell}"]`)).not.toBeNull();
    expect(screen.getByText("page")).toBeTruthy();
    expect(screen.getByLabelText("Main (mobile)")).toBeTruthy();
  });
});
