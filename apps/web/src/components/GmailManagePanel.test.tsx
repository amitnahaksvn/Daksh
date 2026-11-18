import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { GmailManagePanel } from "./GmailManagePanel";
import { api } from "@/lib/api";
import { gmailControl } from "@/lib/gmailControl";

vi.mock("next/link", () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe("GmailManagePanel", () => {
  it("is the one place for every Gmail setting: simple tabs and the control-centre tabs", async () => {
    vi.spyOn(api, "appDetail").mockResolvedValue({
      catalog: { integrationKey: "gmail", name: "Gmail", description: "", icon: "mail", color: "#000", category: "Productivity", isEnabled: true },
      connection: { connectionId: "c1", integrationKey: "gmail", status: "Connected", accountIdentifier: "me@example.com", connectedAt: null, lastActiveAt: null, lastError: null, notificationsEnabled: true, autoReplyEnabled: false, autoReplyInGroups: false },
    });
    vi.spyOn(api, "gmailSettings").mockResolvedValue({ signature: "", showImages: false, dashboardCount: 5, dashboardQuery: "in:inbox", showOnDashboard: false });
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([]);
    render(<GmailManagePanel />);

    for (const name of ["General", "Reading", "Compose", "Dashboard", "Accounts", "Sync & inbox", "AI intelligence", "Rules", "Replies", "Follow-ups", "Notifications", "Attachments", "Gmail settings", "Security"])
      expect(await screen.findByRole("tab", { name })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: "Account" })).not.toBeInTheDocument(); // folded into Accounts
    await userEvent.click(screen.getByRole("tab", { name: "Rules" }));
    expect(screen.getByRole("tab", { name: "Rules" })).toHaveAttribute("aria-selected", "true");
  });
});
