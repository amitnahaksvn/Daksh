import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { GmailDashboardCard, mailboxUrl } from "./GmailDashboardCard";
import { api, type DashboardApp } from "@/lib/api";
import { gmailControl } from "@/lib/gmailControl";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push }) }));
afterEach(() => { cleanup(); vi.restoreAllMocks(); push.mockClear(); });

const app = { id: "d1", connectionId: "c1", integrationKey: "gmail", name: "Gmail", icon: "mail", color: "#EA4335", status: "Connected", accountIdentifier: "me@example.com" } as unknown as DashboardApp;
const dash = () => ({
  generatedAtUtc: new Date().toISOString(), account: { id: "a1", email: "me@example.com", nickname: "", enabled: true },
  counts: { today: 2, yesterday: 1, capped: false, unread: 1, important: 1, needResponse: 0, deadlines: 0 },
  priority: [{ id: "m1", threadId: "t1", from: "Ann <a@x.com>", subject: "Hello", date: new Date().toISOString(), label: "Important", unread: true, starred: false, analysed: true }],
  briefing: { needResponse: [], deadlines: [] }, sync: { status: "Idle", lastSuccessUtc: null, lastFailureUtc: null, lastError: null, initialSyncDone: true, analysedToday: 1 }, aiEnabled: true,
});

describe("GmailDashboardCard", () => {
  it("builds mailbox links for each dashboard action", () => {
    expect(mailboxUrl()).toBe("/apps/gmail");
    expect(mailboxUrl({ compose: true })).toBe("/apps/gmail?compose=1");
    expect(mailboxUrl({ focusSearch: true })).toBe("/apps/gmail?focus=search");
    expect(mailboxUrl({ openMessageId: "m1" })).toBe("/apps/gmail?open=m1");
    expect(mailboxUrl({ query: "in:inbox is:important" })).toBe("/apps/gmail?q=in%3Ainbox+is%3Aimportant");
  });

  it("opens the real email in the mailbox when a priority row is clicked", async () => {
    vi.spyOn(gmailControl, "dashboard").mockResolvedValue(dash());
    render(<GmailDashboardCard app={app} />);
    await userEvent.click(await screen.findByRole("button", { name: /Open email: Hello/ }));
    expect(push).toHaveBeenCalledWith("/apps/gmail?open=m1");
  });

  it("opens the Manage popup on the right tab from Quick Actions and from the gear", async () => {
    vi.spyOn(gmailControl, "dashboard").mockResolvedValue(dash());
    vi.spyOn(api, "appDetail").mockResolvedValue({
      catalog: { integrationKey: "gmail", name: "Gmail", description: "", icon: "mail", color: "#000", category: "Productivity", isEnabled: true },
      connection: { connectionId: "c1", integrationKey: "gmail", status: "Connected", accountIdentifier: "me@example.com", connectedAt: null, lastActiveAt: null, lastError: null, notificationsEnabled: true, autoReplyEnabled: false, autoReplyInGroups: false },
    });
    vi.spyOn(api, "gmailSettings").mockResolvedValue({ signature: "", showImages: false, dashboardCount: 5, dashboardQuery: "in:inbox", showOnDashboard: true });
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([]);
    render(<GmailDashboardCard app={app} />);
    await userEvent.click(await screen.findByRole("button", { name: "Automations" }));
    expect(await screen.findByRole("tab", { name: "Rules" })).toHaveAttribute("aria-selected", "true");
    expect(screen.queryByText("Open Gmail")).not.toBeInTheDocument(); // the popup has no "Open Gmail" shortcut
  });
});
