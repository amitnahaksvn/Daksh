import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ThemeHero } from "./ThemeHero";
import { AppShell } from "./AppShell";
import { api, type DashboardApp } from "@/lib/api";
import { THEMES, STYLE_KEY } from "@/lib/theme";
import { SIDEBAR_KEY } from "@/lib/sidebarPref";
import userEvent from "@testing-library/user-event";

vi.mock("next/link", () => ({ default: ({ href, children, className, ...rest }: { href: string; children: React.ReactNode; className?: string; "aria-current"?: "page" }) => <a href={href} className={className} aria-current={rest["aria-current"]}>{children}</a> }));
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

describe("every theme has the same sidebar", () => {
  it.each(THEMES.map((t) => t.id))("%s renders the sidebar with its destinations", (id) => {
    window.localStorage.setItem(STYLE_KEY, id);
    const { container } = render(<AppShell><p>page</p></AppShell>);
    expect(container.querySelector(`[data-nav="${id}"]`)).not.toBeNull();
    expect(container.querySelector("aside")).not.toBeNull();
    const nav = screen.getAllByRole("navigation", { name: "Main" })[0];
    expect(nav.textContent).toContain("Dashboard");
    expect(nav.textContent).toContain("Apps");
    expect(nav.querySelector('[aria-current="page"]')).not.toBeNull(); // the current page is marked
    expect(screen.getByText("page")).toBeTruthy();
    expect(screen.getByLabelText("Main (mobile)")).toBeTruthy();
  });

  it("can be minimized to a slim strip and expanded again, and remembers the choice", async () => {
    window.localStorage.setItem(STYLE_KEY, "galaxy");
    const { container } = render(<AppShell><p>page</p></AppShell>);
    expect(container.querySelector('[data-sidebar="open"]')).not.toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Minimize sidebar" }));
    expect(container.querySelector('[data-sidebar="collapsed"]')).not.toBeNull();
    expect(window.localStorage.getItem(SIDEBAR_KEY)).toBe("1");
    const nav = screen.getAllByRole("navigation", { name: "Main" })[0];
    expect(nav.textContent).not.toContain("Dashboard"); // only the first letters are drawn...
    expect(screen.getByRole("link", { name: "Dashboard" }).getAttribute("aria-current")).toBe("page"); // ...but each link keeps its name
    expect(screen.getByRole("button", { name: /Change theme/ })).toBeTruthy(); // the theme picker is still one click away
    await userEvent.click(screen.getByRole("button", { name: "Expand sidebar" }));
    expect(container.querySelector('[data-sidebar="open"]')).not.toBeNull();
    expect(window.localStorage.getItem(SIDEBAR_KEY)).toBe("0");
  });

  it("starts minimized when it was left minimized", () => {
    window.localStorage.setItem(STYLE_KEY, "hybrid");
    window.localStorage.setItem(SIDEBAR_KEY, "1");
    const { container } = render(<AppShell><p>page</p></AppShell>);
    expect(container.querySelector('[data-sidebar="collapsed"]')).not.toBeNull();
  });

  it("has a top search box that is labelled as a placeholder", () => {
    window.localStorage.setItem(STYLE_KEY, "hybrid");
    render(<AppShell><p>x</p></AppShell>);
    const box = screen.getByLabelText("Search everything (placeholder)");
    expect(box).toBeTruthy();
    expect(screen.getAllByText("Fake").length).toBeGreaterThan(0);
  });

  it("is laid out like a chat app's: logo with a collapse icon, icon rows, and a list of your dashboards", async () => {
    window.localStorage.setItem(STYLE_KEY, "hybrid");
    vi.spyOn(api, "dashboards").mockResolvedValue([{ id: "d1", name: "Dashboard", order: 0, widgets: ["command", "apps"] }, { id: "d2", name: "Sales", order: 1, widgets: ["apps"] }]);
    const seen: string[] = [];
    const onOpen = (e: Event) => seen.push((e as CustomEvent<string>).detail);
    window.addEventListener("daksh:open-tab", onOpen);
    render(<AppShell><p>page</p></AppShell>);
    expect(screen.queryByRole("button", { name: "Search" })).toBeNull(); // the sidebar has no search icon; the top bar has the search box
    expect(screen.getByRole("button", { name: "Minimize sidebar" })).toBeTruthy();
    const nav = screen.getAllByRole("navigation", { name: "Main" })[0];
    for (const label of ["New chat", "Dashboard", "Apps", "Chats"]) expect(nav.textContent).toContain(label);
    await userEvent.click(await screen.findByRole("button", { name: "Sales" })); // the dashboards list
    expect(seen).toEqual(["d2"]);
    expect(window.localStorage.getItem("daksh.dashboard.tab")).toBe("d2");
    await userEvent.click(screen.getByRole("button", { name: "New chat" }));
    expect(seen).toEqual(["d2", "ask"]);
    window.removeEventListener("daksh:open-tab", onOpen);
  });

});
