import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GmailControlCentre } from "./GmailControlCentre";
import { ApiError } from "@/lib/api";
import { gmailControl, type GmailAccount, type GmailConfig } from "@/lib/gmailControl";

vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("next/link", () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));

const FEATURES = ["summarisation", "longThreadSummary", "important", "urgent", "actionItems", "deadlines", "meetingRequest", "invoice", "receipt", "paymentReminder", "recruitment", "travel", "flightHotel", "newsletter", "promotional", "spamSuspicious", "sentiment", "complaint", "duplicate", "followUp", "decisions", "contacts", "orderDelivery", "subscription", "legal"];

function config(over: Partial<{ id: string; autoSend: boolean; syncEnabled: boolean; profile: string; interval: number; version: number }> = {}): GmailConfig {
  return {
    connectionId: over.id ?? "a1", userId: "u", version: over.version ?? 3,
    sync: { enabled: over.syncEnabled ?? true, intervalMinutes: over.interval ?? 15, nearRealTime: true, initialPeriodDays: 30, labels: ["INBOX", "SENT"], includeSpam: false, includeTrash: false, syncAttachments: false, maxAttachmentMb: 10, excludedSenders: [], excludedDomains: [], excludedSubjectKeywords: [], excludedLabels: [] },
    ai: { enabled: true, scope: "all", categories: [], summaryLength: "balanced", language: "auto", modelId: null, allowExternalProviders: false, excludedSenders: [], excludedDomains: [], excludedLabels: [], features: Object.fromEntries(FEATURES.map((f) => [f, { enabled: true, confidenceThreshold: 0.6, priority: "normal" }])) },
    reply: { suggestionsEnabled: true, autoDraft: false, autoSend: over.autoSend ?? false, mode: "AskFirst", autoSendCategories: [], replyToNewMessages: true, replyToExistingThreads: true, onlySenders: [], excludedSenders: [], excludedDomains: [], excludedLabels: [], excludeAutomated: true, excludeOtpSecurity: true, excludeFinancial: true, excludeLegal: true, excludeSuspicious: true, businessHoursStart: "09:00", businessHoursEnd: "18:00", onlyInBusinessHours: false, replyOnWeekends: false, holidays: [], replyDelayMinutes: 5, maxPerHour: 5, maxPerDay: 25, minConfidence: 0.8, style: "Professional", personalInstructions: "", businessInstructions: "", greeting: "Hello", closing: "Best regards", signature: "", language: "auto", maxLength: 600, phrasesToAvoid: [], neverCommitTo: ["meetings"] },
    followUp: { enabled: true, intervalsDays: [3], notifyBeforeDeadline: true, deadlineLeadHours: 24, autoDraft: false, autoSend: false },
    notifications: { types: { newEmail: false, urgentEmail: true }, quietHoursStart: "", quietHoursEnd: "", digest: "off", maxPerHour: 20, group: true },
    attachments: { enabled: true, allowedExtensions: [], excludedExtensions: [], maxSizeMb: 10, maxTotalStorageMb: 500, aiAnalysis: false, autoClassify: true, detectDuplicates: true, retentionDays: 90, deleteProcessedCopies: false },
    permissions: { profile: over.profile ?? "Assisted", overrides: {} },
    privacy: { storeSnippets: true, retentionDays: 180 },
  };
}
const account = (id: string, email: string, extra: Partial<GmailAccount> = {}): GmailAccount => ({
  id, email, nickname: "", isDefault: id === "a1", enabled: true, isWorkspace: false, status: "Connected", statusDetail: null, scopes: [], syncStatus: "Idle",
  lastSuccessUtc: null, lastFailureUtc: null, lastError: null, messagesSynced: 12, watchExpiresUtc: null, profile: "Assisted", ...extra,
});

beforeEach(() => {
  vi.spyOn(gmailControl, "syncStatus").mockResolvedValue({ state: null, enabled: true, pushConfigured: false, deadLetters: 0 });
  vi.spyOn(gmailControl, "syncHistory").mockResolvedValue({ runs: [] });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const tab = async (name: string) => userEvent.click(await screen.findByRole("tab", { name }));

describe("GmailControlCentre", () => {
  it("offers to connect when no Gmail account exists", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([]);
    render(<GmailControlCentre />);
    expect(await screen.findByRole("button", { name: "Connect Gmail" })).toBeInTheDocument();
  });

  it("shows each account's real status and lets the user switch accounts", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com"), account("a2", "two@work.com", { status: "ConnectionExpired", statusDetail: "Reconnect this account." })]);
    const cfg = vi.spyOn(gmailControl, "config").mockImplementation(async (id) => config({ id: id ?? "a1", interval: id === "a2" ? 60 : 15 }));
    render(<GmailControlCentre />);
    expect(await screen.findByText("one@example.com")).toBeInTheDocument();
    expect(screen.getByText("Connection Expired")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reconnect" })).toBeInTheDocument();

    await tab("Sync & inbox");
    expect(await screen.findByLabelText("Check interval")).toHaveValue(15);
    await userEvent.selectOptions(screen.getByLabelText("Gmail account"), "a2");
    await waitFor(() => expect(screen.getByLabelText("Check interval")).toHaveValue(60));
    expect(cfg).toHaveBeenCalledWith("a2");
  });

  it("loads real toggle values from the backend and persists a change on save", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config({ syncEnabled: false }));
    const save = vi.spyOn(gmailControl, "saveConfig").mockImplementation(async (_id, c) => ({ ...c, version: c.version + 1 }));
    render(<GmailControlCentre />);
    await tab("Sync & inbox");
    const toggle = await screen.findByRole("switch", { name: "Synchronise this account" });
    expect(toggle).not.toBeChecked(); // the backend value, not a hard-coded default

    await userEvent.click(toggle);
    expect(screen.getByText("You have unsaved changes.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(save).toHaveBeenCalledTimes(1));
    expect(save.mock.calls[0][0]).toBe("a1");
    expect(save.mock.calls[0][1].sync.enabled).toBe(true);
    expect(save.mock.calls[0][1].version).toBe(3); // the version the server gave us, for conflict detection
    expect(await screen.findByText("Settings saved.")).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Synchronise this account" })).toBeChecked();
  });

  it("discard restores the saved values", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    const save = vi.spyOn(gmailControl, "saveConfig");
    render(<GmailControlCentre />);
    await tab("Sync & inbox");
    const interval = await screen.findByLabelText("Check interval");
    await userEvent.clear(interval);
    await userEvent.type(interval, "45");
    expect(interval).toHaveValue(45);
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(screen.getByLabelText("Check interval")).toHaveValue(15);
    expect(save).not.toHaveBeenCalled();
  });

  it("shows the server's validation and conflict errors instead of pretending it saved", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    vi.spyOn(gmailControl, "saveConfig").mockRejectedValue(new ApiError(409, "These settings changed elsewhere. Reload and try again.", {}, "version_conflict"));
    render(<GmailControlCentre />);
    await tab("Sync & inbox");
    await userEvent.click(await screen.findByRole("switch", { name: "Near-real-time updates" }));
    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("changed elsewhere");
    expect(screen.getByText("You have unsaved changes.")).toBeInTheDocument();
  });

  it("keeps automatic sending unavailable while the approval mode asks first", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    render(<GmailControlCentre />);
    await tab("Replies");
    expect(await screen.findByRole("switch", { name: "Allow automatic sending" })).toBeDisabled();
    await userEvent.selectOptions(screen.getByLabelText("Approval mode"), "AutoCategories");
    expect(screen.getByRole("switch", { name: "Allow automatic sending" })).toBeEnabled();
    expect(screen.getByRole("switch", { name: "Allow automatic sending" })).not.toBeChecked();
  });

  it("changes the permission profile and per-permission overrides", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    vi.spyOn(gmailControl, "permissions").mockResolvedValue({
      profile: "Assisted", scopes: [{ scope: "https://www.googleapis.com/auth/gmail.readonly", why: "Read your email." }],
      features: [{ permission: "send", label: "send messages", allowed: true, requiredScope: "https://www.googleapis.com/auth/gmail.send", scopeGranted: true, why: "Send email." }],
      upgradeFeatures: [{ feature: "modify", scope: "https://www.googleapis.com/auth/gmail.modify", why: "Archive, trash, star.", granted: false }],
    });
    vi.spyOn(gmailControl, "audit").mockResolvedValue({ total: 0, items: [] });
    const save = vi.spyOn(gmailControl, "saveConfig").mockImplementation(async (_i, c) => c);
    render(<GmailControlCentre />);
    await tab("Security");
    expect(await screen.findByRole("radio", { name: "Assisted" })).toBeChecked();
    await userEvent.click(screen.getByRole("radio", { name: "Read only" }));
    await userEvent.selectOptions(await screen.findByLabelText("Override for send messages"), "deny");
    expect(await screen.findByText("Archive, trash, star.")).toBeInTheDocument(); // a not-yet-granted permission is offered, with its reason
    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(save).toHaveBeenCalled());
    expect(save.mock.calls[0][1].permissions).toEqual({ profile: "ReadOnly", overrides: { send: false } });
  });

  it("asks for extra Google permission instead of failing silently when Gmail settings need it", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    vi.spyOn(gmailControl, "accountSettings").mockRejectedValue(new ApiError(403, "Google hasn't been asked for permission to manage Gmail account settings yet.", {}, "scope_upgrade_required", "settings"));
    render(<GmailControlCentre />);
    await tab("Gmail settings");
    expect(await screen.findByText(/hasn't been asked for permission/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Approve in Google" })).toBeInTheDocument();
  });

  it("shows API validation when saving an invalid rule and never claims success", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    vi.spyOn(gmailControl, "rules").mockResolvedValue({ rules: [], fields: [], actionTypes: [], destructive: ["trash", "forward"] });
    vi.spyOn(gmailControl, "createRule").mockRejectedValue(new ApiError(400, "One or more validation errors occurred.", { name: ["Give the rule a name (up to 100 characters)."] }));
    render(<GmailControlCentre />);
    await tab("Rules");
    await userEvent.click(await screen.findByRole("button", { name: "New rule" }));
    await userEvent.click(screen.getByRole("button", { name: "Save rule" }));
    expect(await screen.findByText(/Give the rule a name/)).toBeInTheDocument();
    expect(screen.queryByText("Rule saved.")).not.toBeInTheDocument();
  });

  it("blocks automatic running for a rule that trashes mail", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    vi.spyOn(gmailControl, "rules").mockResolvedValue({ rules: [], fields: [], actionTypes: [], destructive: ["trash", "forward"] });
    render(<GmailControlCentre />);
    await tab("Rules");
    await userEvent.click(await screen.findByRole("button", { name: "New rule" }));
    const editor = screen.getByRole("region", { name: "Rule editor" });
    await userEvent.selectOptions(within(editor).getByLabelText("Action 1 type"), "trash");
    expect(within(editor).getByRole("switch", { name: "Run automatically on new mail" })).toBeDisabled();
  });

  it("disconnect needs confirmation", async () => {
    vi.spyOn(gmailControl, "accounts").mockResolvedValue([account("a1", "one@example.com")]);
    vi.spyOn(gmailControl, "config").mockResolvedValue(config());
    const disconnect = vi.spyOn(gmailControl, "disconnect").mockResolvedValue({ status: "Disconnected" });
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValueOnce(true);
    render(<GmailControlCentre />);
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(disconnect).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(disconnect).toHaveBeenCalledWith("a1"));
    expect(confirm).toHaveBeenCalledTimes(2);
  });
});
