"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { getIntegrationsHub } from "@/lib/signalr";

type Kind = "incoming" | "outgoing" | "reminder";
type Toast = { id: string; kind: Kind; title: string; body: string; isGroup: boolean; source?: string };
const TOAST_LIFETIME_MS = 6000;

const KIND_STYLE: Record<Kind, { tint: string; bar: string; label: string }> = {
  incoming: { tint: "bg-lime text-on-tint", bar: "bg-lime", label: "New message" },
  outgoing: { tint: "bg-surface-2 text-text", bar: "bg-muted", label: "Sent" },
  reminder: { tint: "bg-pink text-on-tint", bar: "bg-pink", label: "Reminder" },
};

function KindIcon({ kind, isGroup }: { kind: Kind; isGroup: boolean }) {
  const common = { viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 1.7, strokeLinecap: "round", strokeLinejoin: "round", className: "h-5 w-5" } as const;
  if (kind === "reminder")
    return (
      <svg {...common} className="toast-ring h-5 w-5">
        <path d="M6 10a6 6 0 1 1 12 0c0 3 1 4.5 1.5 5.5H4.5C5 14.5 6 13 6 10Z" />
        <path d="M9.5 18.5a2.5 2.5 0 0 0 5 0" />
      </svg>
    );
  if (kind === "outgoing")
    return (
      <svg {...common}>
        <path d="M21 3 10.5 13.5" />
        <path d="M21 3l-6.5 18-4-7.5L3 9.5 21 3Z" />
      </svg>
    );
  if (isGroup)
    return (
      <svg {...common}>
        <circle cx="9" cy="8" r="3" />
        <path d="M3 19c0-3 2.7-5 6-5s6 2 6 5" />
        <path d="M16 5.2a3 3 0 0 1 0 5.6M18 14.3c1.8.6 3 2.1 3 4.7" />
      </svg>
    );
  return (
    <svg {...common}>
      <path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 13.5v-8Z" />
      <path d="M8.5 8.5h7M8.5 11.5h4" />
    </svg>
  );
}

function ToastCard({ toast, onClose }: { toast: Toast; onClose: (id: string) => void }) {
  const [paused, setPaused] = useState(false);
  const remaining = useRef(TOAST_LIFETIME_MS);
  const startedAt = useRef(0);
  const style = KIND_STYLE[toast.kind];

  // Counts down the lifetime, pausing while the pointer is over the card so a message can be read.
  useEffect(() => {
    if (paused) return;
    startedAt.current = Date.now();
    const timer = setTimeout(() => onClose(toast.id), remaining.current);
    return () => {
      clearTimeout(timer);
      remaining.current = Math.max(0, remaining.current - (Date.now() - startedAt.current));
    };
  }, [paused, onClose, toast.id]);

  return (
    <div
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      className="toast-in group relative overflow-hidden rounded-[1.25rem] border border-line bg-surface p-3.5 pr-9 shadow-[0_12px_32px_rgb(0_0_0/0.18)] backdrop-blur"
    >
      <div className="flex items-start gap-3">
        <span className={`toast-pop flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl ${style.tint}`}>
          <KindIcon kind={toast.kind} isGroup={toast.isGroup} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1.5">
            <p className="ai-mono text-[10px] font-semibold uppercase tracking-wide text-muted">{toast.source && toast.kind === "incoming" ? `${toast.source} message` : style.label}</p>
            {toast.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
          </div>
          <p className="truncate text-sm font-semibold">{toast.title}</p>
          {toast.body && <p className="mt-0.5 line-clamp-2 text-sm text-muted">{toast.body}</p>}
        </div>
      </div>
      <button
        type="button"
        aria-label="Dismiss notification"
        onClick={() => onClose(toast.id)}
        className="absolute right-2.5 top-2.5 flex h-6 w-6 items-center justify-center rounded-full text-muted opacity-0 transition hover:bg-surface-2 hover:text-text focus-visible:opacity-100 group-hover:opacity-100"
      >
        <svg viewBox="0 0 16 16" className="h-3 w-3" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"><path d="M3.5 3.5l9 9M12.5 3.5l-9 9" /></svg>
      </button>
      <span
        aria-hidden
        className={`toast-progress absolute inset-x-0 bottom-0 h-0.5 origin-left ${style.bar}`}
        style={{ animationDuration: `${TOAST_LIFETIME_MS}ms`, animationPlayState: paused ? "paused" : "running" }}
      />
    </div>
  );
}

/** A transient pop-up for the moment a WhatsApp message arrives, on top of the persistent bell list in
 *  NotificationBell. Mounted once in the authenticated layout. */
export function ToastHost() {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const close = useCallback((id: string) => setToasts((prev) => prev.filter((t) => t.id !== id)), []);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const add = (t: Toast) => setToasts((prev) => [t, ...prev.filter((x) => x.id !== t.id)].slice(0, 4));
    const onMessage = (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notify?: boolean }) => {
      if (payload.notify === false) return;
      add({
        id: `${payload.connectionId}:${payload.timestamp ?? Date.now()}`,
        kind: "incoming",
        title: payload.chatName ?? "a contact",
        body: payload.preview ?? "",
        isGroup: payload.isGroup ?? false,
      });
    };
    const onSent = (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notifyOutgoing?: boolean }) => {
      if (!payload.notifyOutgoing) return;
      add({
        id: `out:${payload.connectionId}:${payload.timestamp ?? Date.now()}`,
        kind: "outgoing",
        title: `To ${payload.chatName ?? (payload.isGroup ? "a group" : "a contact")}`,
        body: payload.preview ?? "",
        isGroup: payload.isGroup ?? false,
      });
    };
    const onReminder = (payload: { connectionId: string; chatName?: string; text: string; dueAt?: string }) => {
      add({
        id: `reminder:${payload.connectionId}:${payload.dueAt ?? Date.now()}:${payload.text}`,
        kind: "reminder",
        title: payload.chatName ?? "Reminder",
        body: payload.text,
        isGroup: false,
      });
    };
    hub.on("WhatsAppMessageReceived", onMessage);
    const onTelegram = (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notify?: boolean }) => {
      if (payload.notify === false) return;
      add({
        id: `tg:${payload.connectionId}:${payload.timestamp ?? Date.now()}`,
        kind: "incoming",
        title: payload.chatName ?? "a contact",
        body: payload.preview ?? "",
        isGroup: payload.isGroup ?? false,
        source: "Telegram",
      });
    };
    hub.on("TelegramMessageReceived", onTelegram);
    hub.on("WhatsAppMessageSent", onSent);
    hub.on("WhatsAppReminderDue", onReminder);
    return () => {
      hub.off("WhatsAppReminderDue", onReminder);
      hub.off("WhatsAppMessageReceived", onMessage);
      hub.off("WhatsAppMessageSent", onSent);
      hub.off("TelegramMessageReceived", onTelegram);
    };
  }, []);

  if (toasts.length === 0) return null;
  return (
    <div role="status" aria-live="polite" className="fixed right-4 top-4 z-50 flex w-84 max-w-[92vw] flex-col gap-2.5">
      {toasts.map((t) => (
        <ToastCard key={t.id} toast={t} onClose={close} />
      ))}
    </div>
  );
}
