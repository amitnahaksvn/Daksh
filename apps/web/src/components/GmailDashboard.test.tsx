import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { GmailDashboard } from "./GmailDashboard";
import { ApiError } from "@/lib/api";
import { gmailControl, type GmailDashboardData } from "@/lib/gmailControl";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const row = (n: number, over: Partial<GmailDashboardData["priority"][number]> = {}) => ({
  id: `m${n}`, threadId: `t${n}`, from: `Person ${n} <p${n}@example.com>`, subject: `Subject ${n}`, date: new Date().toISOString(), label: "Important", unread: false, starred: false, analysed: true, ...over,
});
function data(over: Partial<GmailDashboardData> = {}): GmailDashboardData {
  return {
    generatedAtUtc: new Date().toISOString(), account: { id: "a1", email: "me@example.com", nickname: "", enabled: true },
    counts: { today: 48, yesterday: 40, capped: false, unread: 12, important: 7, needResponse: 5, deadlines: 3 },
    priority: [row(1, { label: "Action Required" }), row(2, { label: "Payment" }), row(3, { label: "Meeting" }), row(4), row(5)],
    briefing: { needResponse: [{ messageId: "n1", threadId: "t", subject: "Please reply", from: "Boss <b@x.com>", dueAtUtc: null }], deadlines: [{ messageId: "d1", subject: "Tax filing", from: "Gov <g@x.com>", next: new Date(Date.now() + 2 * 86_400_000).toISOString() }] },
    sync: { status: "Idle", lastSuccessUtc: null, lastFailureUtc: null, lastError: null, initialSyncDone: true, analysedToday: 9 }, aiEnabled: true, ...over,
  };
}
function mount(d: GmailDashboardData | Error = data()) {
  vi.spyOn(gmailControl, "dashboard")[d instanceof Error ? "mockRejectedValue" : "mockResolvedValue"](d as never);
  const onOpenMailbox = vi.fn(); const onOpenManage = vi.fn();
  render(<GmailDashboard icon="mail" color="#EA4335" onOpenMailbox={onOpenMailbox} onOpenManage={onOpenManage} />);
  return { onOpenMailbox, onOpenManage };
}

describe("GmailDashboard", () => {
  it("shows the header, real statistics and the briefing built from them", async () => {
    mount();
    expect(await screen.findByText("Gmail Dashboard")).toBeInTheDocument();
    expect(screen.getByText("Manage, summarize and take action on your Gmail with Daksha.")).toBeInTheDocument();
    expect(screen.getByText("Connected")).toBeInTheDocument();
    const stats = within(screen.getByRole("list", { name: "Email statistics" }));
    expect(stats.getByLabelText("Total Emails: 48")).toBeInTheDocument();
    expect(stats.getByLabelText("Unread: 12")).toBeInTheDocument();
    expect(stats.getByLabelText("Important: 7")).toBeInTheDocument();
    expect(stats.getByLabelText("Need Response: 5")).toBeInTheDocument();
    expect(stats.getByText(/20% vs yesterday/)).toBeInTheDocument(); // 48 vs 40
    const briefing = within(screen.getByRole("region", { name: "AI inbox briefing" }));
    expect(briefing.getByText(/contain/)).toHaveTextContent("3 contain upcoming deadlines");
  });

  it("lists the priority emails with sender, time, badge and opens the real email on click", async () => {
    const { onOpenMailbox } = mount();
    const list = within(await screen.findByRole("region", { name: "Priority emails" }));
    expect(list.getAllByRole("listitem")).toHaveLength(5);
    expect(list.getByText("Action Required")).toBeInTheDocument();
    expect(list.getByText(/Person 1/)).toBeInTheDocument();
    await userEvent.click(list.getByRole("button", { name: /Open email: Subject 2/ }));
    expect(onOpenMailbox).toHaveBeenCalledWith({ openMessageId: "m2" });
    await userEvent.click(list.getByRole("button", { name: /View All/ }));
    expect(onOpenMailbox).toHaveBeenLastCalledWith({ query: "in:inbox is:important" });
  });

  it("wires every quick action to its real module", async () => {
    const { onOpenMailbox, onOpenManage } = mount();
    const actions = within(await screen.findByRole("region", { name: "Quick actions" }));
    await userEvent.click(actions.getByRole("button", { name: "Compose" }));
    expect(onOpenMailbox).toHaveBeenLastCalledWith({ compose: true });
    await userEvent.click(actions.getByRole("button", { name: "Search" }));
    expect(onOpenMailbox).toHaveBeenLastCalledWith({ focusSearch: true });
    await userEvent.click(actions.getByRole("button", { name: "Follow-ups" }));
    expect(onOpenManage).toHaveBeenLastCalledWith("followups");
    await userEvent.click(actions.getByRole("button", { name: "Automations" }));
    expect(onOpenManage).toHaveBeenLastCalledWith("rules");
    await userEvent.click(screen.getByRole("button", { name: "Gmail settings" }));
    expect(onOpenManage).toHaveBeenLastCalledWith();
  });

  it("expands the briefing into the emails that need a response and upcoming deadlines", async () => {
    const { onOpenMailbox } = mount();
    await userEvent.click(await screen.findByRole("button", { name: /View Briefing/ }));
    expect(screen.getByText("Please reply")).toBeInTheDocument();
    expect(screen.getByText("Tax filing")).toBeInTheDocument();
    await userEvent.click(screen.getByText("Tax filing"));
    expect(onOpenMailbox).toHaveBeenCalledWith({ openMessageId: "d1" });
  });

  it("is honest before the first sync: no made-up numbers for what it can't know yet", async () => {
    mount(data({ counts: { today: 3, yesterday: 0, capped: false, unread: 1, important: 0, needResponse: null, deadlines: null }, priority: [], sync: { status: "Synchronising", lastSuccessUtc: null, lastFailureUtc: null, lastError: null, initialSyncDone: false, analysedToday: 0 } }));
    expect(await screen.findByLabelText("Need Response: not available yet")).toHaveTextContent("—");
    expect(screen.getByText("Syncing")).toBeInTheDocument();
    expect(screen.getByText(/No important emails right now\./)).toHaveTextContent("still analysing");
    expect(screen.getByText(/Responses and deadlines will appear once/)).toBeInTheDocument();
  });

  it("shows a sync failure instead of hiding it", async () => {
    mount(data({ sync: { status: "Failed", lastSuccessUtc: null, lastFailureUtc: new Date().toISOString(), lastError: "Gmail authorization expired.", initialSyncDone: true, analysedToday: 0 } }));
    expect(await screen.findByText("Sync issue")).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("Gmail authorization expired.");
  });

  it("explains a permission problem and offers the permissions screen", async () => {
    const { onOpenManage } = mount(new ApiError(403, "Daksha is not allowed to read messages on this account.", {}, "permission_denied"));
    expect(await screen.findByRole("alert")).toHaveTextContent("not allowed to read");
    await userEvent.click(screen.getByRole("button", { name: "Open permissions" }));
    expect(onOpenManage).toHaveBeenCalledWith("security");
  });

  it("stars an email and rolls back with an explanation if Google needs more permission", async () => {
    mount();
    const star = vi.spyOn(gmailControl, "messageAction").mockRejectedValue(new ApiError(403, "Google hasn't been asked for permission to star messages yet.", {}, "scope_upgrade_required", "modify"));
    const list = within(await screen.findByRole("region", { name: "Priority emails" }));
    await userEvent.click(list.getAllByRole("button", { name: "Star this email" })[0]);
    await waitFor(() => expect(star).toHaveBeenCalledWith("m1", "star"));
    expect(await screen.findByRole("status")).toHaveTextContent("Open Settings → Security");
    expect(list.getAllByRole("button", { name: "Star this email" })).toHaveLength(5); // rolled back
  });

  it("stars successfully", async () => {
    mount();
    vi.spyOn(gmailControl, "messageAction").mockResolvedValue({});
    const list = within(await screen.findByRole("region", { name: "Priority emails" }));
    await userEvent.click(list.getAllByRole("button", { name: "Star this email" })[0]);
    expect(await list.findByRole("button", { name: "Remove star" })).toHaveAttribute("aria-pressed", "true");
  });

  it("shows a working effect over the whole card while loading, and again on refresh, then clears it", async () => {
    let release!: (d: GmailDashboardData) => void;
    const spy = vi.spyOn(gmailControl, "dashboard").mockReturnValueOnce(new Promise<GmailDashboardData>((r) => { release = r; }));
    const { container } = render(<GmailDashboard icon="mail" color="#EA4335" onOpenMailbox={vi.fn()} onOpenManage={vi.fn()} />);
    expect(container.querySelector("[data-card-busy]")).not.toBeNull(); // first load: effect + skeleton, header and settings icon still there
    expect(screen.getByRole("status", { name: "Loading your Gmail dashboard" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Gmail settings" })).toBeInTheDocument();
    release(data());
    await screen.findByLabelText("Total Emails: 48");
    expect(container.querySelector("[data-card-busy]")).toBeNull();

    let release2!: (d: GmailDashboardData) => void;
    spy.mockReturnValueOnce(new Promise<GmailDashboardData>((r) => { release2 = r; }));
    await userEvent.click(screen.getByRole("button", { name: "Refresh dashboard" }));
    expect(container.querySelector("[data-card-busy]")).not.toBeNull(); // refresh: the numbers stay, the whole card shows the effect
    expect(screen.getByLabelText("Total Emails: 48")).toBeInTheDocument();
    release2(data());
    await waitFor(() => expect(container.querySelector("[data-card-busy]")).toBeNull());
  });
});
