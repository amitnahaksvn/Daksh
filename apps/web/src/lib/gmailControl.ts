/** Typed client for the Gmail Control Centre endpoints (/api/integrations/gmail/*). No business logic: every rule is enforced by the API. */
import { apiFetch } from "@/lib/api";

const BASE = "/api/integrations/gmail";
const q = (params: Record<string, string | number | boolean | null | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};
const body = (method: string, data?: unknown): RequestInit => ({ method, body: data === undefined ? undefined : JSON.stringify(data) });

export type GmailAccount = {
  id: string; email: string; nickname: string; isDefault: boolean; enabled: boolean; isWorkspace: boolean;
  status: string; statusDetail: string | null; scopes: string[]; syncStatus: string;
  lastSuccessUtc: string | null; lastFailureUtc: string | null; lastError: string | null; messagesSynced: number; watchExpiresUtc: string | null; profile: string;
};

export type AiFeature = { enabled: boolean; confidenceThreshold: number; priority: string };
export type GmailConfig = {
  connectionId: string; userId: string; version: number;
  sync: {
    enabled: boolean; intervalMinutes: number; nearRealTime: boolean; initialPeriodDays: number; labels: string[]; includeSpam: boolean; includeTrash: boolean;
    syncAttachments: boolean; maxAttachmentMb: number; excludedSenders: string[]; excludedDomains: string[]; excludedSubjectKeywords: string[]; excludedLabels: string[];
  };
  ai: {
    enabled: boolean; scope: string; categories: string[]; summaryLength: string; language: string; modelId: string | null; allowExternalProviders: boolean;
    excludedSenders: string[]; excludedDomains: string[]; excludedLabels: string[]; features: Record<string, AiFeature>;
  };
  reply: {
    suggestionsEnabled: boolean; autoDraft: boolean; autoSend: boolean; mode: string; autoSendCategories: string[]; replyToNewMessages: boolean; replyToExistingThreads: boolean;
    onlySenders: string[]; excludedSenders: string[]; excludedDomains: string[]; excludedLabels: string[];
    excludeAutomated: boolean; excludeOtpSecurity: boolean; excludeFinancial: boolean; excludeLegal: boolean; excludeSuspicious: boolean;
    businessHoursStart: string; businessHoursEnd: string; onlyInBusinessHours: boolean; replyOnWeekends: boolean; holidays: string[];
    replyDelayMinutes: number; maxPerHour: number; maxPerDay: number; minConfidence: number; style: string;
    personalInstructions: string; businessInstructions: string; greeting: string; closing: string; signature: string; language: string;
    maxLength: number; phrasesToAvoid: string[]; neverCommitTo: string[];
  };
  followUp: { enabled: boolean; intervalsDays: number[]; notifyBeforeDeadline: boolean; deadlineLeadHours: number; autoDraft: boolean; autoSend: boolean };
  notifications: { types: Record<string, boolean>; quietHoursStart: string; quietHoursEnd: string; digest: string; maxPerHour: number; group: boolean };
  attachments: {
    enabled: boolean; allowedExtensions: string[]; excludedExtensions: string[]; maxSizeMb: number; maxTotalStorageMb: number; aiAnalysis: boolean;
    autoClassify: boolean; detectDuplicates: boolean; retentionDays: number; deleteProcessedCopies: boolean;
  };
  permissions: { profile: string; overrides: Record<string, boolean> };
  privacy: { storeSnippets: boolean; retentionDays: number };
};

export type PermissionInfo = {
  profile: string;
  scopes: { scope: string; why: string }[];
  features: { permission: string; label: string; allowed: boolean; requiredScope: string | null; scopeGranted: boolean; why: string }[];
  upgradeFeatures: { feature: string; scope: string; why: string; granted: boolean }[];
};

export type RuleCondition = { field: string; op: string; value: string; negate: boolean };
export type RuleAction = { type: string; value: string };
export type GmailRule = {
  id: string; accountId: string; name: string; description: string; enabled: boolean; priority: number; stopProcessing: boolean;
  match: "all" | "any"; conditions: RuleCondition[]; actions: RuleAction[]; runAutomatically: boolean; version: number;
};
export type RuleRun = { id: string; ruleId: string; messageId: string; trigger: string; applied: string[]; skipped: string[]; status: string; atUtc: string };
export type RuleFacts = {
  from: string; to: string; cc: string; subject: string; body: string; hasAttachment: boolean; attachmentTypes: string[];
  labels: string[]; important: boolean; starred: boolean; categories: string[]; urgent: boolean; date: string;
};

export type FollowUp = { key: string; threadId: string; messageId: string; subject: string; counterparty: string; status: string; dueAtUtc: string | null; snoozedUntilUtc: string | null; draftId: string | null };
export type GmailNotification = { key: string; type: string; title: string; body: string; read: boolean; suppressed: boolean; priority: string; createdAtUtc: string; messageId: string | null };
export type AuditEntry = { id: string; accountId: string; messageRef: string | null; action: string; trigger: string; ruleId: string | null; approval: string; status: string; error: string | null; atUtc: string };
export type SyncStatus = {
  state: { status: string; lastSuccessUtc: string | null; lastFailureUtc: string | null; lastError: string | null; messagesSynced: number; watchExpiresUtc: string | null; initialSyncDone: boolean } | null;
  enabled: boolean; pushConfigured: boolean; deadLetters: number;
};
export type SyncRun = { id: string; kind: string; trigger: string; fetched: number; processed: number; failed: number; status: string; error: string | null; startedUtc: string };
export type SettingsSection = { supported: boolean; editable: boolean; value: unknown; note: string | null };
export type AccountSettings = {
  vacation: SettingsSection; language: SettingsSection; sendAs: SettingsSection; forwardingAddresses: SettingsSection; autoForwarding: SettingsSection; imap: SettingsSection; pop: SettingsSection;
  notSupported: { setting: string; reason: string; url: string | null }[];
};
export type Analysis = {
  categories: string[]; summary: string; important: boolean; urgent: boolean; suspicious: boolean; needsReply: boolean; sentiment: string; actionItems: string[];
  deadlines: { date: string; context: string }[]; replyDecision: { action: string; reason: string }; suggestedReply: string | null; method: string; externalProviderUsed: boolean;
};
export type SearchCriteria = {
  from?: string; to?: string; subject?: string; keywords?: string; exactPhrase?: string; after?: string; before?: string; hasAttachment?: boolean; fileType?: string;
  label?: string; isUnread?: boolean; isRead?: boolean; isStarred?: boolean; isImportant?: boolean; in?: string; raw?: string;
};

export type DashboardPriority = { id: string; threadId: string; from: string; subject: string; date: string | null; label: string; unread: boolean; starred: boolean; analysed: boolean };
export type GmailDashboardData = {
  generatedAtUtc: string;
  account: { id: string; email: string; nickname: string; enabled: boolean };
  counts: { today: number; yesterday: number; capped: boolean; unread: number; important: number; needResponse: number | null; deadlines: number | null };
  priority: DashboardPriority[];
  briefing: {
    needResponse: { messageId: string; threadId: string; subject: string; from: string; dueAtUtc: string | null }[];
    deadlines: { messageId: string; subject: string; from: string; next: string | null }[];
  };
  sync: { status: string; lastSuccessUtc: string | null; lastFailureUtc: string | null; lastError: string | null; initialSyncDone: boolean; analysedToday: number };
  aiEnabled: boolean;
};

export const gmailControl = {
  /** `since` is the start of the user's local day so "today" means today where they are. */
  dashboard: (accountId?: string) => {
    const midnight = new Date(); midnight.setHours(0, 0, 0, 0);
    return apiFetch<GmailDashboardData>(`${BASE}/dashboard${q({ accountId, since: Math.floor(midnight.getTime() / 1000) })}`);
  },
  accounts: () => apiFetch<GmailAccount[]>(`${BASE}/accounts`),
  updateAccount: (id: string, change: { nickname?: string; enabled?: boolean; isDefault?: boolean }) => apiFetch<GmailAccount>(`${BASE}/accounts/${id}`, body("PATCH", change)),
  testAccount: (id: string) => apiFetch<{ ok: boolean; email: string; messagesTotal: number; threadsTotal: number; scopes: string[] }>(`${BASE}/accounts/${id}/test`, body("POST")),
  permissions: (id: string) => apiFetch<PermissionInfo>(`${BASE}/accounts/${id}/permissions`),
  connect: (features?: string[]) => apiFetch<{ authorizationUrl: string }>(`${BASE}/connect${q({ features: features?.join(",") })}`, body("POST")),
  disconnect: (id: string) => apiFetch<{ status: string }>(`${BASE}/disconnect${q({ accountId: id })}`, body("POST")),

  config: (accountId?: string) => apiFetch<GmailConfig>(`${BASE}/config${q({ accountId })}`),
  saveConfig: (accountId: string, config: GmailConfig) => apiFetch<GmailConfig>(`${BASE}/config${q({ accountId })}`, body("PUT", config)),

  syncStatus: (accountId: string) => apiFetch<SyncStatus>(`${BASE}/sync/status${q({ accountId })}`),
  syncRun: (accountId: string) => apiFetch<SyncRun>(`${BASE}/sync/run${q({ accountId })}`, body("POST")),
  syncToggle: (accountId: string, on: boolean) => apiFetch<{ enabled: boolean; version: number }>(`${BASE}/sync/${on ? "start" : "stop"}${q({ accountId })}`, body("POST")),
  syncHistory: (accountId: string) => apiFetch<{ runs: SyncRun[] }>(`${BASE}/sync/history${q({ accountId })}`),
  renewWatch: (accountId: string) => apiFetch<{ expiresUtc: string | null }>(`${BASE}/sync/watch/renew${q({ accountId })}`, body("POST")),

  rules: (accountId?: string) => apiFetch<{ rules: GmailRule[]; fields: string[]; actionTypes: string[]; destructive: string[] }>(`${BASE}/rules${q({ accountId })}`),
  createRule: (accountId: string, rule: Partial<GmailRule>) => apiFetch<GmailRule>(`${BASE}/rules${q({ accountId })}`, body("POST", rule)),
  updateRule: (rule: GmailRule) => apiFetch<GmailRule>(`${BASE}/rules/${rule.id}`, body("PUT", rule)),
  deleteRule: (id: string) => apiFetch<void>(`${BASE}/rules/${id}`, body("DELETE")),
  duplicateRule: (id: string) => apiFetch<GmailRule>(`${BASE}/rules/${id}/duplicate`, body("POST")),
  setRuleEnabled: (id: string, on: boolean) => apiFetch<{ enabled: boolean }>(`${BASE}/rules/${id}/enabled${q({ on })}`, body("POST")),
  testRule: (rule: Partial<GmailRule>, sample: RuleFacts) => apiFetch<{ matched: boolean; reasons: string[]; wouldRun: string[]; needsConfirmation: boolean }>(`${BASE}/rules/test`, body("POST", { rule, sample })),
  previewRule: (id: string) => apiFetch<{ scanned: number; matched: number; matches: { id: string; from: string; subject: string }[]; actions: string[]; destructive: boolean }>(`${BASE}/rules/${id}/preview`, body("POST", { max: 25 })),
  runRule: (id: string, messageIds: string[], confirm: boolean) => apiFetch<{ ran: number; runs: RuleRun[] }>(`${BASE}/rules/${id}/run`, body("POST", { messageIds, confirm })),
  ruleRuns: (id: string) => apiFetch<{ runs: RuleRun[] }>(`${BASE}/rules/${id}/runs`),

  followUps: (accountId?: string, status?: string) => apiFetch<{ items: FollowUp[]; statuses: string[] }>(`${BASE}/follow-ups${q({ accountId, status })}`),
  updateFollowUp: (threadId: string, accountId: string, change: { status?: string; snoozeUntil?: string; dueAt?: string }) => apiFetch<FollowUp>(`${BASE}/follow-ups/${encodeURIComponent(threadId)}${q({ accountId })}`, body("PATCH", change)),
  followUpDraft: (threadId: string, accountId: string) => apiFetch<{ draftId: string; body: string }>(`${BASE}/follow-ups/${encodeURIComponent(threadId)}/draft${q({ accountId })}`, body("POST")),

  notifications: (accountId?: string, unread?: boolean) => apiFetch<{ items: GmailNotification[]; unreadCount: number }>(`${BASE}/notifications${q({ accountId, unread })}`),
  markNotificationsRead: (keys: string[] | null) => apiFetch<{ updated: number }>(`${BASE}/notifications/read`, body("POST", keys ? { keys } : { all: true })),

  audit: (f: { accountId?: string; action?: string; status?: string; trigger?: string; q?: string; skip?: number; take?: number }) => apiFetch<{ total: number; items: AuditEntry[] }>(`${BASE}/audit${q(f)}`),

  accountSettings: (accountId: string) => apiFetch<AccountSettings>(`${BASE}/account-settings${q({ accountId })}`),
  saveVacation: (accountId: string, v: { enableAutoReply: boolean; subject: string; body: string; startDate: string; endDate: string; restrictToContacts: boolean; restrictToDomain: boolean }) =>
    apiFetch<unknown>(`${BASE}/account-settings/vacation${q({ accountId })}`, body("PUT", v)),
  saveLanguage: (accountId: string, displayLanguage: string) => apiFetch<unknown>(`${BASE}/account-settings/language${q({ accountId })}`, body("PUT", { displayLanguage })),
  saveSendAs: (accountId: string, email: string, patch: { displayName?: string; signature?: string }) =>
    apiFetch<unknown>(`${BASE}/account-settings/send-as/${encodeURIComponent(email)}${q({ accountId })}`, body("PATCH", patch)),
  labels: (accountId: string) => apiFetch<{ labels: { id: string; name: string; type: string }[] }>(`${BASE}/labels${q({ accountId })}`),
  createLabel: (accountId: string, name: string) => apiFetch<{ id: string; name: string }>(`${BASE}/labels${q({ accountId })}`, body("POST", { name })),
  deleteLabel: (accountId: string, id: string) => apiFetch<void>(`${BASE}/labels/${encodeURIComponent(id)}${q({ accountId })}`, body("DELETE")),
  filters: (accountId: string) => apiFetch<{ filters: { id: string; criteria: Record<string, unknown>; action: Record<string, unknown> }[] }>(`${BASE}/filters${q({ accountId })}`),
  deleteFilter: (accountId: string, id: string) => apiFetch<void>(`${BASE}/filters/${encodeURIComponent(id)}${q({ accountId })}`, body("DELETE")),

  buildSearch: (c: SearchCriteria) => apiFetch<{ query: string }>(`${BASE}/search/build`, body("POST", c)),
  savedSearches: () => apiFetch<{ id: string; name: string; query: string }[]>(`${BASE}/saved-searches`),
  saveSearch: (name: string, criteria: SearchCriteria) => apiFetch<{ id: string; name: string; query: string }>(`${BASE}/saved-searches`, body("POST", { name, criteria })),
  deleteSavedSearch: (id: string) => apiFetch<void>(`${BASE}/saved-searches/${id}`, body("DELETE")),

  analyze: (messageId: string, accountId: string) => apiFetch<Analysis>(`${BASE}/messages/${encodeURIComponent(messageId)}/analyze${q({ accountId })}`, body("POST")),
  messageAction: (messageId: string, action: string, accountId?: string) => apiFetch<unknown>(`${BASE}/messages/${encodeURIComponent(messageId)}/actions/${action}${q({ accountId })}`, body("POST")),

  exportData: (accountId: string) => apiFetch<unknown>(`${BASE}/export${q({ accountId })}`),
  deleteData: (accountId: string, includeAudit: boolean) => apiFetch<{ deleted: boolean }>(`${BASE}/data${q({ accountId, confirm: true, includeAudit })}`, body("DELETE")),
};
