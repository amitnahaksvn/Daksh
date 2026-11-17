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
    try {
      const body = await res.json();
      title = body.title ?? title;
      fieldErrors = body.errors ?? {};
    } catch {
      /* non-JSON error body */
    }
    if (res.status === 401) {
      title = "You need to sign in to continue.";
      redirectToLogin();
    }
    throw new ApiError(res.status, title, fieldErrors);
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T);
}

export type AssistantReply = {
  conversationId: string;
  messageId: string;
  reply: string;
  provider: string;
  isSimulated: boolean;
};
export type ConversationSummary = { id: string; createdAt: string; messageCount: number };
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
export type TelegramChat = { chatId: string; name: string; isGroup: boolean; lastMessagePreview: string | null; lastMessageAt: string | null; unreadCount: number };
export type TelegramMessage = { messageId: string; chatId: string; fromMe: boolean; isAiGenerated: boolean; senderName: string | null; text: string; timestamp: string };
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
};
export type WhatsAppChat = {
  chatId: string;
  chatName: string | null;
  isGroup: boolean;
  lastMessagePreview: string;
  lastMessageAt: string;
  unreadCount: number;
};
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
export type GmailAuthorizationResponse = { authorizationUrl: string };
export type GmailMessageSummary = {
  id: string;
  threadId: string;
  from: string;
  subject: string;
  snippet: string;
  date: string | null;
};
export type GmailMessagePage = { messages: GmailMessageSummary[]; nextPageToken: string | null };
export type GmailMessage = { id: string; from: string; to: string; subject: string; date: string; body: string };
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
};

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
  updateMe: (req: UpdateProfileRequest) => apiFetch<UserDetails>("/api/v1/account/me", { method: "PUT", body: JSON.stringify(req) }),
  changePassword: (currentPassword: string, newPassword: string) =>
    apiFetch<MessageResponse>("/api/v1/account/me/password", json({ currentPassword, newPassword })),
  login: (email: string, password: string) => apiFetch<AccountProfile>("/api/v1/account/login", json({ email, password })),

  // App catalogue + per-user connection lifecycle (distinct from the mock-adapter `integrations()` above).
  appCatalog: () => apiFetch<AppCatalogEntry[]>("/api/integrations"),
  appDetail: (integrationKey: string) => apiFetch<AppDetail>(`/api/integrations/${integrationKey}`),
  connectWhatsApp: () => apiFetch<ConnectResponse>("/api/integrations/whatsapp/connect", { method: "POST" }),
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
  telegramMessages: (connectionId: string, chatId: string) =>
    apiFetch<TelegramMessage[]>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages`),
  sendTelegramMessage: (connectionId: string, chatId: string, text: string) =>
    apiFetch<TelegramMessage>(`/api/integrations/telegram/${connectionId}/chats/${encodeURIComponent(chatId)}/messages`, json({ text })),
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

  dashboardApps: () => apiFetch<DashboardApp[]>("/api/dashboard/apps"),
  addToDashboard: (connectionId: string) => apiFetch<{ id: string }>("/api/dashboard/apps", json({ connectionId })),
  removeFromDashboard: (id: string) => apiFetch<void>(`/api/dashboard/apps/${id}`, { method: "DELETE" }),
  dashboardActivity: () => apiFetch<ActivityItem[]>("/api/dashboard/activity"),
};
