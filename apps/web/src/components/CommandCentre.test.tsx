import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CommandCentre } from "./CommandCentre";
import { api, type DashboardApp } from "@/lib/api";

vi.mock("next/link", () => ({ default: ({ href, children, className }: { href: string; children: React.ReactNode; className?: string }) => <a href={href} className={className}>{children}</a> }));
function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return { get length() { return data.size; }, clear: () => data.clear(), getItem: (k) => data.get(k) ?? null, key: (i) => [...data.keys()][i] ?? null, removeItem: (k) => void data.delete(k), setItem: (k, v) => void data.set(k, String(v)) };
}
beforeEach(() => {
  Object.defineProperty(window, "localStorage", { value: memoryStorage(), configurable: true });
  window.matchMedia = vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })) as never;
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const wa = { id: "w", connectionId: "c1", integrationKey: "whatsapp", name: "WhatsApp", icon: "", color: "", status: "Connected", accountIdentifier: null, lastActiveAt: null, displayOrder: 0, unreadCount: 2, lastMessagePreview: "hi", lastMessageAt: null, lastMessageIsGroup: false, cols: 0, height: 0, col: 0 } as DashboardApp;

function mockApi() {
  vi.spyOn(api, "tasks").mockResolvedValue([{ id: "t1", title: "Send report", dueAt: "2020-01-01T09:00:00", completed: false }]);
  vi.spyOn(api, "reminders").mockResolvedValue([]);
  vi.spyOn(api, "auditEvents").mockResolvedValue([]);
  vi.spyOn(api, "whatsAppPendingReplies").mockResolvedValue([{ id: "r1", chatId: "1@s.whatsapp.net", chatName: "Rohan", text: "Yes, I’ll call at 8.", createdAt: "" }]);
}

describe("Command Centre", () => {
  it("merges real items from different apps into one Needs you list", async () => {
    mockApi();
    render(<CommandCentre apps={[wa]} />);
    expect(await screen.findByText("Reply to Rohan is ready")).toBeTruthy();
    expect(screen.getByText("Send report")).toBeTruthy();
    expect(screen.getByText("2 unread on WhatsApp")).toBeTruthy();
  });

  it("approves a drafted reply from the list and refreshes", async () => {
    mockApi();
    const approve = vi.spyOn(api, "approveWhatsAppPendingReply").mockResolvedValue(undefined);
    render(<CommandCentre apps={[wa]} />);
    await screen.findByText("Reply to Rohan is ready");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(approve).toHaveBeenCalledWith("c1", "r1"));
  });

  it("labels everything that is not wired up as Fake", async () => {
    mockApi();
    render(<CommandCentre apps={[wa]} />);
    await screen.findByText("Reply to Rohan is ready");
    expect(screen.getAllByText("Fake").length).toBeGreaterThanOrEqual(8); // search box, 2 stats, 3 tiles + heading, automations
    const search = screen.getByLabelText("Search everything (placeholder)");
    expect(within(search).getByText("Fake")).toBeTruthy();
  });

  it("remembers the chosen view", async () => {
    mockApi();
    render(<CommandCentre apps={[wa]} />);
    await screen.findByText("Reply to Rohan is ready");
    await userEvent.click(screen.getByRole("radio", { name: "Ops" }));
    expect(localStorage.getItem("daksh.dashboardView")).toBe("ops");
    expect(await screen.findByText("Connection health")).toBeTruthy();
  });
});
