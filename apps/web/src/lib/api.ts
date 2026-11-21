/**
 * Typed client for the Daksh API. The website contains no business logic: every
 * operation is a call to the shared API.
 */
import { SESSION_STORAGE_KEY } from "@/lib/session";

export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5080";

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public fieldErrors: Record<string, string[]> = {},
    /** Machine-readable reason from the API, e.g. "scope_upgrade_required", "permission_denied", "confirmation_required". */
    public code: string | null = null,
    public requiredScope: string | null = null,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

let accessToken: string | null = null;
/** Called by the auth integration once an OIDC token is available. */
export function setAccessToken(token: string | null) {
  accessToken = token;
}

/** A 401 means the stored token is gone/expired — clear it and send the user to sign in, carrying the
 *  current URL as `returnUrl` so the login page can send them straight back afterwards. */
function redirectToLogin(): void {
  if (typeof window === "undefined") return;
  if (window.location.pathname.startsWith("/login")) return; // already there; avoid a redirect loop

  accessToken = null;
  try {
    localStorage.removeItem(SESSION_STORAGE_KEY);
  } catch {
    /* ignore (private browsing, storage disabled, etc.) */
  }

  const returnUrl = window.location.pathname + window.location.search;
  // apiFetch is a plain module function, not a component — there's no router instance to push() with here.
  // eslint-disable-next-line @next/next/no-location-assign-relative-destination
  window.location.href = `/login?returnUrl=${encodeURIComponent(returnUrl)}`;
}

function authHeaders(): Record<string, string> {
  if (accessToken) return { Authorization: `Bearer ${accessToken}` };
  // Local development only; the API rejects this header unless Auth:Mode=Development.
  if (process.env.NEXT_PUBLIC_DEV_AUTH === "true") {
    return { "X-Dev-User": process.env.NEXT_PUBLIC_DEV_USER ?? "dev-user" };
  }
  return {};
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      ...init,
      headers: { "Content-Type": "application/json", ...authHeaders(), ...init.headers },
    });
  } catch {
    throw new ApiError(0, "Cannot reach the Daksh API. Check your connection and try again.");
  }
  if (!res.ok) {
    let title = `Request failed (${res.status})`;
    let fieldErrors: Record<string, string[]> = {};
    let code: string | null = null;
    let requiredScope: string | null = null;
    try {
      const body = await res.json();
      title = body.title ?? title;
      fieldErrors = body.errors ?? {};
      code = body.code ?? null;
      requiredScope = body.requiredScope ?? null;
    } catch {
      /* non-JSON error body */
    }
    if (res.status === 401) {
      title = "You need to sign in to continue.";
      redirectToLogin();
    }
    throw new ApiError(res.status, title, fieldErrors, code, requiredScope);
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T);
}

export type AssistantReply = {
  conversationId: string;
  messageId: string;
  reply: string;
  provider: string;
  isSimulated: boolean;
  /** The connected apps the assistant looked at to answer (for example WhatsApp, Gmail). */
  sources?: string[];
};
export type ConversationSummary = { id: string; createdAt: string; messageCount: number; title?: string | null };
export type ChatMessage = { id: string; role: "user" | "assistant"; content: string; createdAt: string };
export type TaskDto = { id: string; title: string; dueAt: string | null; completed: boolean };
export type ReminderDto = { id: string; text: string; remindAt: string };
export type PrivacySettings = { memoryEnabled: boolean; allowProviderDataSharing: boolean; retentionDays: number };
export type AuditEvent = { id: string; action: string; entityType: string | null; entityId: string | null; detail: string | null; occurredAt: string };
export type Integration = { key: string; category: string; provider: string; state: string; note: string };
export type RegisterRequest = {
  firstName: string;
  lastName: string;
  gender: string;
  dateOfBirth: string;
  email: string;
  password: string;
};
export type UserDetails = { id: string; firstName: string; lastName: string; gender: string; dateOfBirth: string; email: string; createdAtUtc: string };
/** "I'm busy" mode, on the profile: while it's active, auto-reply turns itself on for every connected app. */
export type BusyProfile = {
  enabled: boolean;
  timezone: string;
  start: string;
  end: string;
  days: number[];
  until: string | null;
  replyMode: "Ai" | "Fixed";
  fixedMessage: string | null;
  /** Optional note people may be told, e.g. "in a meeting". */
  note: string | null;
  isBusyNow: boolean;
};
export type UpdateBusyRequest = Partial<Omit<BusyProfile, "isBusyNow" | "until">> & { until?: string; clearUntil?: boolean };
export type CalendarEvent = { id: string; title: string; start: string; end: string; allDay: boolean; location?: string | null; htmlLink?: string | null; accountId?: string; account?: string };
export type CalendarAccount = { id: string; email: string; hasCalendar: boolean; isDefault: boolean; error?: string | null };
export type CalendarEvents = { connected: boolean; status: "Ok" | "NeedsAccess" | "Failed"; message?: string | null; timezone: string; today: string; events: CalendarEvent[]; accounts: CalendarAccount[] };
export type ListItem = { id: string; list: string; text: string; done: boolean; createdAt: string };
/** Copy one-time codes from email to your own WhatsApp / Telegram chat. */
export type OtpForward = { enabled: boolean; whatsApp: boolean; telegram: boolean; whatsAppConnected: boolean; telegramConnected: boolean };
export type UpdateProfileRequest = { firstName: string; lastName: string; gender: string; dateOfBirth: string };
export type MessageResponse = { message: string };
export type AccountProfile = { id: string; firstName: string; lastName: string; email: string; accessToken: string; expiresAt: string };

export type AppCatalogEntry = {
  integrationKey: string;
  name: string;
  description: string;
  icon: string;
  color: string;
  category: string;
  isEnabled: boolean;
};
export type ConnectionStatusValue = "NotConnected" | "Connecting" | "Connected" | "Disconnected" | "Error";
export type ConnectionStatusDto = {
  connectionId: string;
  integrationKey: string;
  status: ConnectionStatusValue;
  accountIdentifier: string | null;
  connectedAt: string | null;
  lastActiveAt: string | null;
  lastError: string | null;
  notificationsEnabled: boolean;
  autoReplyEnabled: boolean;
  autoReplyInGroups: boolean;
};
export type TelegramLoginState = { status: ConnectionStatusValue; qr: string | null; expiresAt: string | null; passwordRequired: boolean; error: string | null };
export type TelegramChat = { chatId: string; name: string; isGroup: boolean; lastMessagePreview: string | null; lastMessageAt: string | null; unreadCount: number; pinned?: boolean; archived?: boolean; mutedUntil?: string | null };
export type TelegramMessage = { messageId: string; chatId: string; fromMe: boolean; isAiGenerated: boolean; senderName: string | null; text: string; timestamp: string; hasMedia?: boolean;
  mediaKind?: "photo" | "video" | "audio" | "voice" | "document" | "sticker" | null; mimeType?: string | null; fileName?: string | null;
  /** Outgoing only: 1 pending, 2 sent, 4 read. */
  status?: number | null; deleted?: boolean; edited?: boolean; starred?: boolean; replyToId?: string | null; replyToText?: string | null;
  reactions?: { emoji: string; count: number; mine: boolean }[] | null };
export type TelegramPresence = { state: "typing" | "recording" | "uploading" | "online" | "offline" | "recently" | "unknown"; lastSeen?: number };
export type TelegramGroupInfo = { subject: string; description: string | null; members: { id: string; name: string; isAdmin: boolean }[] };
export type TelegramSearchHit = { messageId: string; chatId: string; chatName: string | null; fromMe: boolean; text: string; timestamp: string };
export type WhatsAppReaction = { by: string; emoji: string };
export type WhatsAppMessage = {
  messageId: string;
  chatId: string;
  chatName: string | null;
  fromMe: boolean;
  isAiGenerated: boolean;
  isGroup: boolean;
  text: string;
  hasMedia: boolean;
  quotedMessageId: string | null;
  quotedText: string | null;
  reactions: WhatsAppReaction[];
  timestamp: string;
  mediaKind: "image" | "sticker" | "video" | "audio" | "document" | null;
  mimeType: string | null;
  fileName: string | null;
  mediaSeconds: number | null;
  voiceNote: boolean;
  /** Outgoing only: 1 pending, 2 sent, 3 delivered, 4 read, 5 played. */
  status: number | null;
  deleted: boolean;
  edited: boolean;
  starred: boolean;
};
export type WhatsAppChat = {
  chatId: string;
  chatName: string | null;
  isGroup: boolean;
  lastMessagePreview: string;
  lastMessageAt: string;
  unreadCount: number;
  pinned?: boolean;
  archived?: boolean;
  mutedUntil?: string | null;
};
export type WhatsAppPresence = { state: "composing" | "recording" | "available" | "unavailable" | "paused" | "unknown"; lastSeen?: number };
export type WhatsAppGroupMember = { id: string; name: string | null; isAdmin: boolean; isMe: boolean };
export type WhatsAppGroupInfo = { subject: string; description: string | null; createdAt: string | null; members: WhatsAppGroupMember[] };
export type ChatAssistResult = { mode: "summary" | "suggest" | "translate"; text: string | null; suggestions: string[]; isSimulated: boolean };
export type WhatsAppSearchHit = { messageId: string; chatId: string; chatName: string | null; fromMe: boolean; text: string; timestamp: string };
export type ActivityItem = {
  integrationKey: string;
  connectionId: string;
  icon: string;
  color: string;
  chatId: string;
  chatName: string | null;
  isGroup: boolean;
  fromMe: boolean;
  text: string;
  timestamp: string;
};
export type AppDetail = { catalog: AppCatalogEntry; connection: ConnectionStatusDto | null };
export type QrResponse = { qr: string | null; expiresAt: string | null; status: string };
export type ConnectResponse = { connectionId: string; status: string };
export type ShoppingStore = { key: string; name: string; home: string; searchUrl: string; connected: boolean; connectionId: string | null; cart: boolean; session: "none" | "signing_in" | "connected" | "unavailable" };
export type ShoppingSession = { state: string; remote: boolean; streamUrl: string | null };
export type CartItemResult = { query: string; status: "added" | "unavailable" | "not_found" | "failed"; title: string | null; price: string | null; note: string | null };
export type CartSummary = { count: number | null; total: string | null; items: { title: string; price: string | null }[]; url: string };
export type AddToCartResult = { results: CartItemResult[]; cart: CartSummary | null };
export type MicrosoftFeature = "mail" | "calendar" | "onedrive" | "devops";
export type MicrosoftStatus = { configured: boolean; connected: boolean; connectionId: string | null; email: string | null; displayName: string | null; features: MicrosoftFeature[]; error: string | null; write: boolean; devOpsOrg: string | null; devOpsToken: boolean };
export type MicrosoftSettings = {
  signature: string; showImages: boolean; mailCount: number; mailFilter: "inbox" | "unread" | "flagged" | "important";
  calendarDays: 1 | 3 | 7 | 14; calendarCount: number; calendarShowAllDay: boolean;
  fileCount: number; fileSource: "recent" | "shared";
  workItemCount: number; workItemScope: "assigned" | "created"; workItemIncludeClosed: boolean; devOpsDefaultOrg: string | null; showOnDashboard: boolean;
};
export type MicrosoftSettingsUpdate = Partial<Omit<MicrosoftSettings, "showOnDashboard">>;
export type MicrosoftAdminApproval = { link: string; message: string };
export type OutlookMessageSummary = { id: string; from: string; fromAddress: string; subject: string; preview: string; received: string | null; isRead: boolean; hasAttachments: boolean };
export type OutlookMessagePage = { messages: OutlookMessageSummary[]; nextSkip: number | null };
export type OutlookMessage = { id: string; from: string; to: string; subject: string; received: string | null; body: string; isHtml: boolean };
export type DriveItem = { id: string; name: string; isFolder: boolean; size: number; modified: string | null; webUrl: string | null; mimeType: string | null };
export type DriveListing = { folderId: string | null; folderName: string; items: DriveItem[] };
export type DevOpsWorkItem = { id: number; title: string; state: string; type: string; project: string; url: string };
export type DevOpsSummary = { organizations: string[]; organization: string | null; projects: string[]; workItems: DevOpsWorkItem[] };
export type MicrosoftDashboard = { connected: boolean; email: string | null; features: MicrosoftFeature[]; unread: number | null; messages: OutlookMessageSummary[]; events: CalendarEvent[]; files: DriveItem[]; workItems: DevOpsWorkItem[]; errors: string[] };

export type GmailAuthorizationResponse = { authorizationUrl: string };
export type GmailMessageSummary = {
  id: string;
  threadId: string;
  from: string;
  subject: string;
  snippet: string;
  isUnread?: boolean;
  isStarred?: boolean;
  date: string | null;
};
export type GmailMessagePage = { messages: GmailMessageSummary[]; nextPageToken: string | null };
export type GmailMessage = { id: string; from: string; to: string; subject: string; date: string; body: string; html: string | null };
export type GmailSettings = { signature: string; showImages: boolean; dashboardCount: number; dashboardQuery: string; showOnDashboard: boolean };
export type GmailSettingsUpdate = Partial<Pick<GmailSettings, "signature" | "showImages" | "dashboardCount" | "dashboardQuery">>;
export type DashboardApp = {
  id: string;
  connectionId: string;
  integrationKey: string;
  name: string;
  icon: string;
  color: string;
  status: ConnectionStatusValue;
  accountIdentifier: string | null;
  lastActiveAt: string | null;
  displayOrder: number;
  unreadCount: number;
  lastMessagePreview: string | null;
  lastMessageAt: string | null;
  lastMessageIsGroup: boolean;
  /** Saved tile width in a 12-column grid (3–12). 0 = the card's default. */
  cols: number;
  /** Saved tile height in pixels. 0 = fit the content. */
  height: number;
  /** The column (1–10) the tile starts in. 0 = flow after the previous tile. */
  col: number;
};
export type DashboardWidget = "command" | "apps";
export type DashboardTab = { id: string; name: string; order: number; widgets: DashboardWidget[] };
export type AvailableDashboardApp = { connectionId: string; integrationKey: string; name: string; icon: string; color: string; accountIdentifier: string | null; dashboardId: string | null };
export type DashboardLayoutItem = { id: string; displayOrder: number; cols: number; height: number; col: number };

export type WhatsAppGeneralSettings = {
  paused: boolean;
  operateContinuously: boolean;
  activeHoursStart: string;
  activeHoursEnd: string;
  timezone: string;
  activeDays: number[];
  incomingNotificationsEnabled: boolean;
  outgoingNotificationsEnabled: boolean;
};
export type WhatsAppMonitoringSettings = {
  monitorPersonal: boolean;
  monitorGroups: boolean;
  monitorImages: boolean;
  monitorDocuments: boolean;
  monitorAudio: boolean;
  monitorVideos: boolean;
  monitorLinks: boolean;
  monitorLocation: boolean;
  monitorCalls: boolean;
  monitorSavedContacts: boolean;
  monitorUnknownNumbers: boolean;
  monitorBusiness: boolean;
};
export type WhatsAppAutoReplySettings = {
  enabled: boolean;
  inGroups: boolean;
  scope: "All" | "UnknownOnly" | "SelectedOnly";
  customMessageEnabled: boolean;
  customMessage: string | null;
  responseDelaySeconds: number;
  cooldownSeconds: number;
  batchWindowSeconds: number;
  requireApproval: boolean;
  /** The rule gate before any AI reply: the AI only answers greetings; anything needing a decision is held for the boss. */
  replyGateEnabled: boolean;
  /** How the assistant refers to you in holding replies, e.g. "my boss". */
  bossReference: string;
  /** At most one holding reply per chat in this many minutes, unless you have answered since. */
  holdingCooldownMinutes: number;
  allowedChats: string[];
  blockedChats: string[];
  outsideHoursMessage: string | null;
  businessMessage: string | null;
};
export type WhatsAppAiBehaviourSettings = {
  personality: "Professional" | "Friendly" | "Casual" | "Formal" | "Custom";
  customPersonality: string | null;
  responseLength: "Short" | "Balanced" | "Detailed";
  customInstructions: string | null;
  neverRespondTopics: string | null;
  useConversationContext: boolean;
  contextRetentionMessages: number;
  replyInSenderLanguage: boolean;
  summarizeLongConversations: boolean;
  aiSignatureEnabled: boolean;
  aiSignatureText: string | null;
};
export type WhatsAppPrivacySettings = {
  hideMessagePreviews: boolean;
  messageRetentionDays: number;
  allowMessageContentAsAiContext: boolean;
  aiExcludedChats: string[];
};
export type WhatsAppNotificationSettings = {
  scope: "All" | "Filtered";
  tags: string[];
  email: boolean;
  whatsAppSelf: boolean;
  desktop: boolean;
  sound: boolean;
};
export type WhatsAppIntelligenceSettings = { disabledTags: string[] };
export type WhatsAppFullSettings = {
  connectionId: string;
  accountIdentifier: string;
  status: string;
  general: WhatsAppGeneralSettings;
  monitoring: WhatsAppMonitoringSettings;
  autoReply: WhatsAppAutoReplySettings;
  aiBehaviour: WhatsAppAiBehaviourSettings;
  privacy: WhatsAppPrivacySettings;
  notifications: WhatsAppNotificationSettings;
  intelligence: WhatsAppIntelligenceSettings;
};
export type WhatsAppFullSettingsUpdate = {
  general?: Partial<WhatsAppGeneralSettings>;
  monitoring?: Partial<WhatsAppMonitoringSettings>;
  autoReply?: Partial<WhatsAppAutoReplySettings>;
  aiBehaviour?: Partial<WhatsAppAiBehaviourSettings>;
  privacy?: Partial<WhatsAppPrivacySettings>;
  notifications?: Partial<WhatsAppNotificationSettings>;
  intelligence?: Partial<WhatsAppIntelligenceSettings>;
};
export type WhatsAppChatRule = {
  chatId: string;
  monitor: boolean;
  autoReply: "Default" | "Always" | "Never";
  priority: "Normal" | "High";
  aiExcluded: boolean;
  customReply: string | null;
};
export type WhatsAppRuleAction = { type: string; text: string | null };
export type WhatsAppAutomationRule = {
  id: string;
  name: string;
  enabled: boolean;
  trigger: string;
  chatIds: string[];
  keyword: string | null;
  category: string | null;
  priority: "Any" | "High";
  actions: WhatsAppRuleAction[];
};
export type WhatsAppAutomationRuleInput = Partial<Omit<WhatsAppAutomationRule, "id">>;
export type WhatsAppInsight = {
  id: string;
  chatId: string;
  chatName: string | null;
  kind: string;
  text: string;
  value: string | null;
  dueAt: string | null;
  done: boolean;
  createdAt: string;
};
export type WhatsAppPendingReply = { id: string; chatId: string; chatName: string | null; text: string; createdAt: string };

const json = (body: unknown) => ({ method: "POST", body: JSON.stringify(body) });

export const api = {
  sendMessage: (message: string, conversationId?: string) =>
    apiFetch<AssistantReply>("/api/v1/assistant/messages", json({ message, conversationId })),
  conversations: () => apiFetch<ConversationSummary[]>("/api/v1/assistant/conversations"),
  messages: (id: string) => apiFetch<ChatMessage[]>(`/api/v1/assistant/conversations/${id}/messages`),
  deleteConversation: (id: string) => apiFetch<void>(`/api/v1/assistant/conversations/${id}`, { method: "DELETE" }),
  tasks: () => apiFetch<TaskDto[]>("/api/v1/tasks"),
  createTask: (title: string) => apiFetch<TaskDto>("/api/v1/tasks", json({ title })),
  completeTask: (id: string) => apiFetch<TaskDto>(`/api/v1/tasks/${id}/complete`, { method: "POST" }),
  reminders: () => apiFetch<ReminderDto[]>("/api/v1/reminders"),
  createReminder: (text: string, remindAt: string) => apiFetch<ReminderDto>("/api/v1/reminders", json({ text, remindAt })),
  privacy: () => apiFetch<PrivacySettings>("/api/v1/privacy"),
  updatePrivacy: (s: PrivacySettings) => apiFetch<PrivacySettings>("/api/v1/privacy", { method: "PUT", body: JSON.stringify(s) }),
  auditEvents: () => apiFetch<AuditEvent[]>("/api/v1/audit-events"),
  integrations: () => apiFetch<Integration[]>("/api/v1/integrations"),
  register: (req: RegisterRequest) => apiFetch<MessageResponse>("/api/v1/account/register", json(req)),
  verifyOtp: (email: string, otp: string) => apiFetch<AccountProfile>("/api/v1/account/verify-otp", json({ email, otp })),
  me: () => apiFetch<UserDetails>("/api/v1/account/me"),
  lists: () => apiFetch<ListItem[]>("/api/v1/lists"),
  addListItem: (list: string, text: string) => apiFetch<ListItem>("/api/v1/lists", { method: "POST", body: JSON.stringify({ list, text }) }),
  toggleListItem: (id: string) => apiFetch<void>(`/api/v1/lists/${id}/toggle`, { method: "POST" }),
  deleteListItem: (id: string) => apiFetch<void>(`/api/v1/lists/${id}`, { method: "DELETE" }),
  calendarEvents: (from: string, to: string) => apiFetch<CalendarEvents>(`/api/v1/calendar/events?from=${from}&to=${to}`),
  addCalendarEvent: (title: string, start: string, minutes: number, location?: string, accountId?: string) => apiFetch<CalendarEvent>("/api/v1/calendar/events", { method: "POST", body: JSON.stringify({ title, start, minutes, location: location || null, accountId: accountId || null }) }),
  updateCalendarEvent: (id: string, accountId: string | undefined, change: { title?: string; start?: string; minutes?: number; location?: string }) =>
    apiFetch<CalendarEvent>(`/api/v1/calendar/events/${encodeURIComponent(id)}`, { method: "PATCH", body: JSON.stringify({ ...change, accountId: accountId || null }) }),
  deleteCalendarEvent: (id: string, accountId?: string) => apiFetch<void>(`/api/v1/calendar/events/${encodeURIComponent(id)}${accountId ? `?account=${encodeURIComponent(accountId)}` : ""}`, { method: "DELETE" }),
  otpForward: () => apiFetch<OtpForward>("/api/v1/account/me/otp-forward"),
  updateOtpForward: (req: Partial<Pick<OtpForward, "enabled" | "whatsApp" | "telegram">>) => apiFetch<OtpForward>("/api/v1/account/me/otp-forward", { method: "PUT", body: JSON.stringify(req) }),
  busy: () => apiFetch<BusyProfile>("/api/v1/account/me/busy"),
  updateBusy: (req: UpdateBusyRequest) => apiFetch<BusyProfile>("/api/v1/account/me/busy", { method: "PUT", body: JSON.stringify(req) }),
  updateMe: (req: UpdateProfileRequest) => apiFetch<UserDetails>("/api/v1/account/me", { method: "PUT", body: JSON.stringify(req) }),
  changePassword: (currentPassword: string, newPassword: string) =>
    apiFetch<MessageResponse>("/api/v1/account/me/password", json({ currentPassword, newPassword })),
  login: (email: string, password: string) => apiFetch<AccountProfile>("/api/v1/account/login", json({ email, password })),

  // App catalogue + per-user connection lifecycle (distinct from the mock-adapter `integrations()` above).
  appCatalog: () => apiFetch<AppCatalogEntry[]>("/api/integrations"),
  appDetail: (integrationKey: string) => apiFetch<AppDetail>(`/api/integrations/${integrationKey}`),
  connectWhatsApp: () => apiFetch<ConnectResponse>("/api/integrations/whatsapp/connect", { method: "POST" }),
  shoppingStores: () => apiFetch<ShoppingStore[]>("/api/integrations/shopping"),
  startShoppingSession: (key: string) => apiFetch<ShoppingSession>(`/api/integrations/shopping/${encodeURIComponent(key)}/session/start`, { method: "POST" }),
  shoppingSession: (key: string) => apiFetch<ShoppingSession>(`/api/integrations/shopping/${encodeURIComponent(key)}/session`),
  endShoppingSession: (key: string) => apiFetch<void>(`/api/integrations/shopping/${encodeURIComponent(key)}/session`, { method: "DELETE" }),
  addToCart: (key: string, items: string[]) => apiFetch<AddToCartResult>(`/api/integrations/shopping/${encodeURIComponent(key)}/cart/add`, json({ items })),
  shoppingCart: (key: string) => apiFetch<CartSummary>(`/api/integrations/shopping/${encodeURIComponent(key)}/cart`),
  enableShopping: (key: string) => apiFetch<void>(`/api/integrations/shopping/${encodeURIComponent(key)}/enable`, { method: "POST" }),
  disableShopping: (key: string) => apiFetch<void>(`/api/integrations/shopping/${encodeURIComponent(key)}`, { method: "DELETE" }),
  microsoftStatus: () => apiFetch<MicrosoftStatus>("/api/integrations/microsoft/status"),
  connectMicrosoft: (features: MicrosoftFeature[], write = false) => apiFetch<{ authorizationUrl: string }>(`/api/integrations/microsoft/connect?features=${features.join(",")}&write=${write}`, { method: "POST" }),
  disableMicrosoftWrite: () => apiFetch<void>("/api/integrations/microsoft/write", json({ enabled: false })),
  microsoftAdminApproval: (domain: string, features: MicrosoftFeature[], write: boolean) =>
    apiFetch<MicrosoftAdminApproval>(`/api/integrations/microsoft/admin-approval?domain=${encodeURIComponent(domain.trim())}&features=${features.join(",")}&write=${write}`),
  disableMicrosoftFeature: (feature: MicrosoftFeature) => apiFetch<void>("/api/integrations/microsoft/features/disable", json({ feature })),
  setDevOpsToken: (organization: string, token: string) => apiFetch<void>("/api/integrations/microsoft/devops/token", json({ organization, token })),
  removeDevOpsToken: () => apiFetch<void>("/api/integrations/microsoft/devops/token", { method: "DELETE" }),
  microsoftSettings: () => apiFetch<MicrosoftSettings>("/api/integrations/microsoft/settings"),
  updateMicrosoftSettings: (update: MicrosoftSettingsUpdate) => apiFetch<MicrosoftSettings>("/api/integrations/microsoft/settings", { method: "PUT", body: JSON.stringify(update) }),
  disconnectMicrosoft: () => apiFetch<void>("/api/integrations/microsoft/disconnect", { method: "POST" }),
  microsoftDashboard: () => apiFetch<MicrosoftDashboard>("/api/integrations/microsoft/dashboard"),
  outlookMessages: (query: string, skip?: number | null, folder = "inbox") => {
    const params = new URLSearchParams({ folder });
    if (query.trim()) params.set("q", query.trim());
    if (skip) params.set("skip", String(skip));
    return apiFetch<OutlookMessagePage>(`/api/integrations/microsoft/mail/messages?${params.toString()}`);
  },
  outlookMessage: (id: string) => apiFetch<OutlookMessage>(`/api/integrations/microsoft/mail/messages/${encodeURIComponent(id)}`),
  sendOutlookMessage: (message: { to: string; subject: string; body: string }) => apiFetch<void>("/api/integrations/microsoft/mail/messages", json(message)),
  markOutlookRead: (id: string, isRead: boolean) => apiFetch<void>(`/api/integrations/microsoft/mail/messages/${encodeURIComponent(id)}`, { method: "PATCH", body: JSON.stringify({ isRead }) }),
  oneDriveFiles: (folderId?: string | null, query?: string) => {
    const params = new URLSearchParams();
    if (folderId) params.set("folderId", folderId);
    if (query?.trim()) params.set("q", query.trim());
    return apiFetch<DriveListing>(`/api/integrations/microsoft/onedrive/files?${params.toString()}`);
  },
  devOpsSummary: (org?: string) => apiFetch<DevOpsSummary>(`/api/integrations/microsoft/devops/summary${org ? `?org=${encodeURIComponent(org)}` : ""}`),
  gmailSettings: () => apiFetch<GmailSettings>("/api/integrations/gmail/settings"),
  updateGmailSettings: (update: GmailSettingsUpdate) => apiFetch<GmailSettings>("/api/integrations/gmail/settings", { method: "PUT", body: JSON.stringify(update) }),
  connectGmail: () => apiFetch<GmailAuthorizationResponse>("/api/integrations/gmail/connect", { method: "POST" }),
  gmailMessages: (query: string, pageToken?: string | null) => {
    const params = new URLSearchParams({ q: query });
    if (pageToken) params.set("pageToken", pageToken);
    return apiFetch<GmailMessagePage>(`/api/integrations/gmail/messages?${params.toString()}`);
  },
  gmailMessage: (id: string) => apiFetch<GmailMessage>(`/api/integrations/gmail/messages/${encodeURIComponent(id)}`),
  sendGmailMessage: (message: { to: string; subject: string; body: string }) =>
    apiFetch<{ messageId: string | null }>("/api/integrations/gmail/messages", json(message)),
  connectTelegram: () => apiFetch<ConnectResponse>("/api/integrations/telegram/connect", { method: "POST" }),
  telegramState: (connectionId: string) => apiFetch<TelegramLoginState>(`/api/integrations/telegram/${connectionId}/state`),
  telegramPassword: (connectionId: string, password: string) => apiFetch<unknown>(`/api/integrations/telegram/${connectionId}/password`, json({ password })),
  telegramChats: (connectionId: string) => apiFetch<TelegramChat[]>(`/api/integrations/telegram/${connectionId}/chats`),
  telegramMessages: (connectionId: string, chatId: string, limit?: number) =>
    apiFetch<TelegramMessage[]>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages${limit ? `?limit=${limit}` : ""}`),
  /** A Telegram chat's profile or group picture, or null when it has none. */
  telegramPicture: async (connectionId: string, chatId: string): Promise<Blob | null> => {
    const res = await fetch(`${API_URL}/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/picture`, { headers: authHeaders() });
    if (res.status === 401) redirectToLogin();
    return res.ok ? res.blob() : null;
  },
  telegramMedia: async (connectionId: string, chatId: string, messageId: string): Promise<Blob> => {
    const res = await fetch(`${API_URL}/api/integrations/telegram/${connectionId}/media/${encodeURIComponent(chatId)}/${encodeURIComponent(messageId)}`, { headers: authHeaders() });
    if (res.status === 401) redirectToLogin();
    if (!res.ok) throw new ApiError(res.status, "Could not load the photo.");
    return res.blob();
  },
  sendTelegramMessage: (connectionId: string, chatId: string, text: string, replyToId?: string) =>
    apiFetch<TelegramMessage>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages`, json({ text, replyToId })),
  sendTelegramFile: async (connectionId: string, chatId: string, file: File, opts: { caption?: string; replyToId?: string } = {}): Promise<void> => {
    const form = new FormData();
    form.append("file", file);
    if (opts.caption) form.append("caption", opts.caption);
    if (opts.replyToId) form.append("replyToId", opts.replyToId);
    let res: Response;
    try { res = await fetch(`${API_URL}/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/media`, { method: "POST", headers: authHeaders(), body: form }); }
    catch { throw new ApiError(0, "Cannot reach the Daksh API. Check your connection and try again."); }
    if (res.status === 401) redirectToLogin();
    if (!res.ok) { let title = `Could not send the file (${res.status}).`; try { title = (await res.json()).title ?? title; } catch { /* non-JSON */ } throw new ApiError(res.status, title); }
  },
  markTelegramChatRead: (connectionId: string, chatId: string) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/read`, { method: "POST" }),
  telegramTyping: (connectionId: string, chatId: string, typing: boolean) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/typing`, json({ typing })),
  telegramPresence: (connectionId: string, chatId: string) =>
    apiFetch<TelegramPresence>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/presence`),
  telegramGroup: (connectionId: string, chatId: string) =>
    apiFetch<TelegramGroupInfo>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/group`),
  telegramLoadOlder: (connectionId: string, chatId: string) =>
    apiFetch<{ fetched: number }>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/history`, json({})),
  setTelegramChatPrefs: (connectionId: string, chatId: string, prefs: { pinned?: boolean; archived?: boolean; muteHours?: number }) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/prefs`, { method: "PUT", body: JSON.stringify(prefs) }),
  deleteTelegramMessage: (connectionId: string, chatId: string, messageId: string, forEveryone: boolean) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages/${encodeURIComponent(messageId)}/delete`, json({ forEveryone })),
  editTelegramMessage: (connectionId: string, chatId: string, messageId: string, text: string) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages/${encodeURIComponent(messageId)}/edit`, json({ text })),
  forwardTelegramMessage: (connectionId: string, chatId: string, messageId: string, toChatId: string) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages/${encodeURIComponent(messageId)}/forward`, json({ toChatId })),
  reactTelegramMessage: (connectionId: string, chatId: string, messageId: string, emoji: string) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages/${encodeURIComponent(messageId)}/react`, json({ emoji })),
  starTelegramMessage: (connectionId: string, chatId: string, messageId: string, starred: boolean) =>
    apiFetch<void>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages/${encodeURIComponent(messageId)}/star`, json({ starred })),
  searchTelegram: (connectionId: string, q: string) =>
    apiFetch<TelegramSearchHit[]>(`/api/integrations/telegram/${connectionId}/search?q=${encodeURIComponent(q)}`),
  disconnectGmail: () => apiFetch<{ status: string }>("/api/integrations/gmail/disconnect", { method: "POST" }),
  getQr: (connectionId: string) => apiFetch<QrResponse>(`/api/integrations/${connectionId}/qr`),
  getConnectionStatus: (connectionId: string) => apiFetch<ConnectionStatusDto>(`/api/integrations/${connectionId}/status`),
  reconnectApp: (connectionId: string) => apiFetch<{ status: string }>(`/api/integrations/${connectionId}/reconnect`, { method: "POST" }),
  disconnectApp: (connectionId: string) => apiFetch<{ status: string }>(`/api/integrations/${connectionId}/disconnect`, { method: "POST" }),
  updateConnectionSettings: (connectionId: string, settings: { notificationsEnabled?: boolean; autoReplyEnabled?: boolean; autoReplyInGroups?: boolean }) =>
    apiFetch<ConnectionStatusDto>(`/api/integrations/${connectionId}/settings`, { method: "PATCH", body: JSON.stringify(settings) }),
  whatsAppChats: (connectionId: string) => apiFetch<WhatsAppChat[]>(`/api/integrations/whatsapp/${connectionId}/chats`),
  whatsAppMedia: async (connectionId: string, messageId: string): Promise<Blob> => {
    const res = await fetch(`${API_URL}/api/integrations/whatsapp/${connectionId}/media/${encodeURIComponent(messageId)}`, { headers: authHeaders() });
    if (res.status === 401) redirectToLogin();
    if (!res.ok) throw new ApiError(res.status, "Could not load the photo.");
    return res.blob();
  },
  whatsAppChatMessages: (connectionId: string, chatId: string) =>
    apiFetch<WhatsAppMessage[]>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/messages`),
  sendWhatsAppChatMessage: (connectionId: string, chatId: string, text: string, quotedMessageId?: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/messages`, json({ text, quotedMessageId })),
  sendNewWhatsAppMessage: (connectionId: string, to: string, text: string) =>
    apiFetch<{ chatId: string }>(`/api/integrations/whatsapp/${connectionId}/send`, json({ to, text })),
  reactToWhatsAppMessage: (connectionId: string, messageId: string, emoji: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/messages/${encodeURIComponent(messageId)}/react`, json({ emoji })),
  /** A photo, video, voice note or file. Goes as a form upload, not JSON. */
  sendWhatsAppFile: async (connectionId: string, chatId: string, file: File, opts: { caption?: string; quotedMessageId?: string; voiceNote?: boolean } = {}): Promise<void> => {
    const form = new FormData();
    form.append("file", file);
    if (opts.caption) form.append("caption", opts.caption);
    if (opts.quotedMessageId) form.append("quotedMessageId", opts.quotedMessageId);
    if (opts.voiceNote) form.append("voiceNote", "true");
    let res: Response;
    try { res = await fetch(`${API_URL}/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/media`, { method: "POST", headers: authHeaders(), body: form }); }
    catch { throw new ApiError(0, "Cannot reach the Daksh API. Check your connection and try again."); }
    if (res.status === 401) redirectToLogin();
    if (!res.ok) {
      let title = `Could not send the file (${res.status}).`;
      try { title = (await res.json()).title ?? title; } catch { /* non-JSON */ }
      throw new ApiError(res.status, title);
    }
  },
  /** The chat's profile or group picture, or null when it has none. */
  whatsAppPicture: async (connectionId: string, chatId: string): Promise<Blob | null> => {
    const res = await fetch(`${API_URL}/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/picture`, { headers: authHeaders() });
    if (res.status === 401) redirectToLogin();
    return res.ok ? res.blob() : null;
  },
  markWhatsAppChatRead: (connectionId: string, chatId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/read`, { method: "POST" }),
  deleteWhatsAppMessage: (connectionId: string, messageId: string, forEveryone: boolean) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/messages/${encodeURIComponent(messageId)}/delete`, json({ forEveryone })),
  editWhatsAppMessage: (connectionId: string, messageId: string, text: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/messages/${encodeURIComponent(messageId)}/edit`, json({ text })),
  forwardWhatsAppMessage: (connectionId: string, messageId: string, toChatId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/messages/${encodeURIComponent(messageId)}/forward`, json({ toChatId })),
  starWhatsAppMessage: (connectionId: string, messageId: string, starred: boolean) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/messages/${encodeURIComponent(messageId)}/star`, json({ starred })),
  whatsAppPresence: (connectionId: string, chatId: string) =>
    apiFetch<WhatsAppPresence>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/presence`),
  setWhatsAppPresence: (connectionId: string, chatId: string, state: "composing" | "paused" | "available") =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/presence`, json({ state })),
  whatsAppGroup: (connectionId: string, chatId: string) =>
    apiFetch<WhatsAppGroupInfo>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/group`),
  setWhatsAppChatPrefs: (connectionId: string, chatId: string, prefs: { pinned?: boolean; archived?: boolean; muteHours?: number }) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/prefs`, { method: "PUT", body: JSON.stringify(prefs) }),
  /** AI help on one conversation, only when asked: a summary, suggested replies, or a translation of one message. */
  chatAssist: (channel: "whatsapp" | "telegram", connectionId: string, chatId: string, mode: "summary" | "suggest" | "translate", opts: { messageId?: string; language?: string } = {}) =>
    apiFetch<ChatAssistResult>("/api/integrations/ai/chat-assist", json({ channel, connectionId, chatId, mode, ...opts })),
  whatsAppGroupAction: (connectionId: string, chatId: string, action: "rename" | "description" | "invite" | "leave" | "add" | "remove" | "promote" | "demote", opts: { text?: string; participants?: string[] } = {}) =>
    apiFetch<{ link: string | null }>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/group-action`, json({ action, ...opts })),
  setWhatsAppDisappearing: (connectionId: string, chatId: string, seconds: number) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/disappearing`, json({ seconds })),
  blockWhatsAppChat: (connectionId: string, chatId: string, blocked: boolean) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/block`, json({ blocked })),
  clearWhatsAppChat: (connectionId: string, chatId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/chats/${encodeURIComponent(chatId)}/clear`, { method: "POST" }),
  searchWhatsApp: (connectionId: string, q: string) =>
    apiFetch<WhatsAppSearchHit[]>(`/api/integrations/whatsapp/${connectionId}/search?q=${encodeURIComponent(q)}`),

  whatsAppFullSettings: (connectionId: string) => apiFetch<WhatsAppFullSettings>(`/api/integrations/whatsapp/${connectionId}/full-settings`),
  updateWhatsAppFullSettings: (connectionId: string, update: WhatsAppFullSettingsUpdate) =>
    apiFetch<WhatsAppFullSettings>(`/api/integrations/whatsapp/${connectionId}/full-settings`, { method: "PATCH", body: JSON.stringify(update) }),
  whatsAppPendingReplies: (connectionId: string) => apiFetch<WhatsAppPendingReply[]>(`/api/integrations/whatsapp/${connectionId}/pending-replies`),
  approveWhatsAppPendingReply: (connectionId: string, replyId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/pending-replies/${replyId}/approve`, { method: "POST" }),
  discardWhatsAppPendingReply: (connectionId: string, replyId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/pending-replies/${replyId}`, { method: "DELETE" }),
  whatsAppChatRules: (connectionId: string) => apiFetch<WhatsAppChatRule[]>(`/api/integrations/whatsapp/${connectionId}/chat-rules`),
  saveWhatsAppChatRule: (connectionId: string, chatId: string, rule: Partial<Omit<WhatsAppChatRule, "chatId">>) =>
    apiFetch<WhatsAppChatRule>(`/api/integrations/whatsapp/${connectionId}/chat-rules/${encodeURIComponent(chatId)}`, { method: "PUT", body: JSON.stringify(rule) }),
  whatsAppAutomationRules: (connectionId: string) => apiFetch<WhatsAppAutomationRule[]>(`/api/integrations/whatsapp/${connectionId}/automation-rules`),
  createWhatsAppAutomationRule: (connectionId: string, rule: WhatsAppAutomationRuleInput) =>
    apiFetch<WhatsAppAutomationRule>(`/api/integrations/whatsapp/${connectionId}/automation-rules`, json(rule)),
  updateWhatsAppAutomationRule: (connectionId: string, ruleId: string, rule: WhatsAppAutomationRuleInput) =>
    apiFetch<WhatsAppAutomationRule>(`/api/integrations/whatsapp/${connectionId}/automation-rules/${ruleId}`, { method: "PUT", body: JSON.stringify(rule) }),
  deleteWhatsAppAutomationRule: (connectionId: string, ruleId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/automation-rules/${ruleId}`, { method: "DELETE" }),
  whatsAppInsights: (connectionId: string, kind?: string) =>
    apiFetch<WhatsAppInsight[]>(`/api/integrations/whatsapp/${connectionId}/insights${kind ? `?kind=${encodeURIComponent(kind)}` : ""}`),
  toggleWhatsAppInsightDone: (connectionId: string, insightId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/insights/${insightId}/toggle-done`, { method: "POST" }),
  deleteWhatsAppInsight: (connectionId: string, insightId: string) =>
    apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/insights/${insightId}`, { method: "DELETE" }),
  purgeWhatsAppMessages: (connectionId: string) => apiFetch<void>(`/api/integrations/whatsapp/${connectionId}/purge-messages`, { method: "POST" }),

  availableDashboardApps: () => apiFetch<AvailableDashboardApp[]>("/api/dashboard/apps/available"),
  dashboardApps: () => apiFetch<DashboardApp[]>("/api/dashboard/apps"),
  addToDashboard: (connectionId: string) => apiFetch<{ id: string }>("/api/dashboard/apps", json({ connectionId })),
  removeFromDashboard: (id: string) => apiFetch<void>(`/api/dashboard/apps/${id}`, { method: "DELETE" }),
  saveDashboardLayout: (items: DashboardLayoutItem[]) => apiFetch<{ updated: number }>("/api/dashboard/apps/layout", { method: "PUT", body: JSON.stringify(items) }),
  resetDashboardLayout: () => apiFetch<void>("/api/dashboard/apps/layout", { method: "DELETE" }),
  dashboards: () => apiFetch<DashboardTab[]>("/api/dashboards"),
  createDashboard: (name?: string) => apiFetch<DashboardTab>("/api/dashboards", json({ name })),
  updateDashboard: (id: string, change: { name?: string; widgets?: DashboardWidget[] }) =>
    apiFetch<DashboardTab>(`/api/dashboards/${id}`, { method: "PATCH", body: JSON.stringify(change) }),
  deleteDashboard: (id: string) => apiFetch<void>(`/api/dashboards/${id}`, { method: "DELETE" }),
  dashboardActivity: () => apiFetch<ActivityItem[]>("/api/dashboard/activity"),
};
