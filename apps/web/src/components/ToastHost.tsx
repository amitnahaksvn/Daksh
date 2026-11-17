"use client";
import { useEffect, useState } from "react";
import { getIntegrationsHub } from "@/lib/signalr";

type Toast = { id: string; title: string; body: string; isGroup: boolean };
const TOAST_LIFETIME_MS = 6000;

/** A transient pop-up for the moment a WhatsApp message arrives, on top of the persistent bell list in
 *  NotificationBell. Mounted once in the authenticated layout. */
export function ToastHost() {
  const [toasts, setToasts] = useState<Toast[]>([]);

  useEffect(() => {
    const hub = getIntegrationsHub();
    const onMessage = (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notify?: boolean }) => {
      if (payload.notify === false) return;
      const id = `${payload.connectionId}:${payload.timestamp ?? Date.now()}`;
      const who = payload.chatName ?? "a contact";
      setToasts((prev) => [
        { id, title: payload.isGroup ? `WhatsApp group — ${who}` : `WhatsApp — ${who}`, body: payload.preview ?? "", isGroup: payload.isGroup ?? false },
        ...prev,
      ]);
      setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), TOAST_LIFETIME_MS);
    };
    const onSent = (payload: { connectionId: string; chatName?: string; isGroup?: boolean; preview?: string; timestamp?: string; notifyOutgoing?: boolean }) => {
      if (!payload.notifyOutgoing) return;
      const id = `out:${payload.connectionId}:${payload.timestamp ?? Date.now()}`;
      const who = payload.chatName ?? (payload.isGroup ? "a group" : "a contact");
      setToasts((prev) => [{ id, title: `You sent a message to ${who}`, body: payload.preview ?? "", isGroup: payload.isGroup ?? false }, ...prev]);
      setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), TOAST_LIFETIME_MS);
    };
    const onReminder = (payload: { connectionId: string; chatName?: string; text: string; dueAt?: string }) => {
      const id = `reminder:${payload.connectionId}:${payload.dueAt ?? Date.now()}:${payload.text}`;
      setToasts((prev) => [{ id, title: payload.chatName ? `Reminder — ${payload.chatName}` : "Reminder", body: payload.text, isGroup: false }, ...prev]);
      setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), TOAST_LIFETIME_MS);
    };
    hub.on("WhatsAppMessageReceived", onMessage);
    hub.on("WhatsAppMessageSent", onSent);
    hub.on("WhatsAppReminderDue", onReminder);
    return () => {
      hub.off("WhatsAppReminderDue", onReminder);
      hub.off("WhatsAppMessageReceived", onMessage);
      hub.off("WhatsAppMessageSent", onSent);
    };
  }, []);

  if (toasts.length === 0) return null;
  return (
    <div role="status" aria-live="polite" className="fixed right-4 top-4 z-50 flex w-80 max-w-[90vw] flex-col gap-2">
      {toasts.map((t) => (
        <div key={t.id} className="rounded-[1.25rem] border border-line bg-surface p-4 shadow-[var(--shadow)]">
          <div className="flex items-center gap-1.5">
            <p className="text-sm font-semibold">{t.title}</p>
            {t.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
          </div>
          {t.body && <p className="mt-0.5 truncate text-sm text-muted">{t.body}</p>}
        </div>
      ))}
    </div>
  );
}
