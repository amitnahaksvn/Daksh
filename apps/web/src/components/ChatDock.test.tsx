import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ChatDock } from "./ChatDock";
import { api } from "@/lib/api";

vi.mock("next/link", () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));
vi.mock("@/lib/signalr", () => ({ getIntegrationsHub: () => ({ on: vi.fn(), off: vi.fn() }) }));
vi.mock("@/components/WhatsAppChatsPanel", () => ({ ChatThread: () => null, chatDisplayName: (c: { chatName: string | null }) => c.chatName ?? "?" }));
vi.mock("@/components/TelegramChatsPanel", () => ({ TelegramThread: () => null }));
vi.mock("@/components/ChatAvatar", () => ({ ChatAvatar: () => null }));
vi.mock("@/components/integrations", () => ({ BrandIcon: () => null, BrandTile: () => null }));

function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return { get length() { return data.size; }, clear: () => data.clear(), getItem: (k) => data.get(k) ?? null, key: (i) => [...data.keys()][i] ?? null, removeItem: (k) => void data.delete(k), setItem: (k, v) => void data.set(k, String(v)) };
}
beforeEach(() => {
  vi.useFakeTimers();
  Object.defineProperty(window, "localStorage", { value: memoryStorage(), configurable: true });
  window.matchMedia = vi.fn().mockImplementation(() => ({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })) as never;
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); });

const chat = { chatId: "1@s.whatsapp.net", chatName: "Aj", isGroup: false, lastMessagePreview: "hello", lastMessageAt: "2026-11-19T10:00:00", unreadCount: 1 };
const detail = (status: string, lastError: string | null = null) => ({ catalog: {}, connection: { connectionId: "c1", status, lastError } }) as never;
const tick = async (ms: number) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };

describe("Chats panel stays put", () => {
  it("keeps showing the last chats when a background refresh fails or the connection is briefly reconnecting", async () => {
    const app = vi.spyOn(api, "appDetail").mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("Connected")) : Promise.reject(new Error("offline")));
    vi.spyOn(api, "whatsAppChats").mockResolvedValue([chat] as never);
    render(<ChatDock />);
    await tick(10);
    expect(screen.getByLabelText("Chats")).toBeTruthy();

    app.mockImplementation(() => Promise.reject(new Error("network blip")));
    await tick(8100);
    expect(screen.getByLabelText("Chats")).toBeTruthy(); // a failed refresh must not remove the panel

    app.mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("Connecting")) : Promise.reject(new Error("x")));
    await tick(8100);
    expect(screen.getByLabelText("Chats")).toBeTruthy(); // reconnecting still shows the last chats
    expect(screen.getByText("Aj")).toBeTruthy();
  });

  it("stays visible with the problem and a reconnect link when the app has an error or is disconnected", async () => {
    const app = vi.spyOn(api, "appDetail").mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("Connected")) : Promise.reject(new Error("x")));
    vi.spyOn(api, "whatsAppChats").mockResolvedValue([chat] as never);
    render(<ChatDock />);
    await tick(10);
    expect(screen.queryByRole("alert")).toBeNull();

    app.mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("Error", "Session expired")) : Promise.reject(new Error("x")));
    await tick(8100);
    expect(screen.getByLabelText("Chats")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("Session expired");
    expect(screen.getByRole("link", { name: "Reconnect" }).getAttribute("href")).toBe("/apps/whatsapp");
    expect(screen.getByText("Aj")).toBeTruthy(); // last known chats stay

    app.mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("NotConnected")) : Promise.reject(new Error("x")));
    await tick(8100);
    expect(screen.getByLabelText("Chats")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("Not connected");
  });

  it("is not shown at all for an app that was never connected", async () => {
    vi.spyOn(api, "appDetail").mockImplementation(() => Promise.resolve({ catalog: {}, connection: null } as never));
    render(<ChatDock />);
    await tick(10);
    expect(screen.queryByLabelText("Chats")).toBeNull();
  });

  it("is always there but minimized, and opens by itself only for unread messages", async () => {
    const read = { ...chat, unreadCount: 0 };
    vi.spyOn(api, "appDetail").mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("Connected")) : Promise.reject(new Error("x")));
    const chats = vi.spyOn(api, "whatsAppChats").mockResolvedValue([read] as never);
    render(<ChatDock />);
    await tick(10);
    expect(screen.getByLabelText("Chats")).toBeTruthy(); // present from the start...
    expect(screen.getByRole("button", { name: "Expand chats" })).toBeTruthy(); // ...as the slim strip
    expect(screen.queryByText("Aj")).toBeNull();

    chats.mockResolvedValue([{ ...chat, unreadCount: 2 }] as never);
    await tick(8100);
    expect(screen.getByText("Aj")).toBeTruthy(); // an unread message opens it
    expect(screen.queryByRole("button", { name: "Expand chats" })).toBeNull();

    chats.mockResolvedValue([read] as never);
    await tick(8100);
    expect(screen.getByRole("button", { name: "Expand chats" })).toBeTruthy(); // read everything: back to the strip
  });

  it("stays minimized when you minimize it, until something new arrives; muted chats never open it", async () => {
    vi.spyOn(api, "appDetail").mockImplementation((key) => key === "whatsapp" ? Promise.resolve(detail("Connected")) : Promise.reject(new Error("x")));
    const chats = vi.spyOn(api, "whatsAppChats").mockResolvedValue([{ ...chat, unreadCount: 1 }] as never);
    render(<ChatDock />);
    await tick(10);
    await act(async () => { screen.getByRole("button", { name: "Collapse chats" }).click(); });
    expect(screen.getByRole("button", { name: "Expand chats" })).toBeTruthy();
    await tick(8100);
    expect(screen.getByRole("button", { name: "Expand chats" })).toBeTruthy(); // the same unread message does not reopen it
    chats.mockResolvedValue([{ ...chat, unreadCount: 2 }] as never);
    await tick(8100);
    expect(screen.queryByRole("button", { name: "Expand chats" })).toBeNull(); // a newer one does

    chats.mockResolvedValue([{ ...chat, unreadCount: 0 }] as never);
    await tick(8100);
    expect(screen.getByRole("button", { name: "Expand chats" })).toBeTruthy();
    chats.mockResolvedValue([{ ...chat, unreadCount: 3, mutedUntil: new Date(Date.now() + 3600000).toISOString() }] as never);
    await tick(8100);
    expect(screen.getByRole("button", { name: "Expand chats" })).toBeTruthy(); // muted: shown on the strip, but does not pull it open
  });
});
