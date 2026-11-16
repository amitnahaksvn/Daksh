/**
 * Typed client for the Daksh API. The website contains no business logic: every
 * operation is a call to the shared API.
 */
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
    if (res.status === 401) title = "You need to sign in to continue.";
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
};
export type AppDetail = { catalog: AppCatalogEntry; connection: ConnectionStatusDto | null };
export type QrResponse = { qr: string | null; expiresAt: string | null; status: string };
export type ConnectResponse = { connectionId: string; status: string };
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
};

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
  login: (email: string, password: string) => apiFetch<AccountProfile>("/api/v1/account/login", json({ email, password })),

  // App catalogue + per-user connection lifecycle (distinct from the mock-adapter `integrations()` above).
  appCatalog: () => apiFetch<AppCatalogEntry[]>("/api/integrations"),
  appDetail: (integrationKey: string) => apiFetch<AppDetail>(`/api/integrations/${integrationKey}`),
  connectWhatsApp: () => apiFetch<ConnectResponse>("/api/integrations/whatsapp/connect", { method: "POST" }),
  getQr: (connectionId: string) => apiFetch<QrResponse>(`/api/integrations/${connectionId}/qr`),
  getConnectionStatus: (connectionId: string) => apiFetch<ConnectionStatusDto>(`/api/integrations/${connectionId}/status`),
  reconnectApp: (connectionId: string) => apiFetch<{ status: string }>(`/api/integrations/${connectionId}/reconnect`, { method: "POST" }),
  disconnectApp: (connectionId: string) => apiFetch<{ status: string }>(`/api/integrations/${connectionId}/disconnect`, { method: "POST" }),

  dashboardApps: () => apiFetch<DashboardApp[]>("/api/dashboard/apps"),
  addToDashboard: (connectionId: string) => apiFetch<{ id: string }>("/api/dashboard/apps", json({ connectionId })),
  removeFromDashboard: (id: string) => apiFetch<void>(`/api/dashboard/apps/${id}`, { method: "DELETE" }),
};
