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
};
