"use client";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import Link from "next/link";
import { getNotifications, getUnreadCount, markAllRead, startNotifications, subscribeNotifications } from "@/lib/notifications";

function timeAgo(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/** A bell with a live unread badge, mounted once in the authenticated layout so every page can see arrivals. */
export function NotificationBell() {
  const notifications = useSyncExternalStore(subscribeNotifications, getNotifications, () => []);
  const unread = useSyncExternalStore(subscribeNotifications, getUnreadCount, () => 0);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => startNotifications(), []);

  useEffect(() => {
    if (!open) return;
    const onClickAway = (event: MouseEvent) => {
      if (ref.current && !ref.current.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onClickAway);
    return () => document.removeEventListener("mousedown", onClickAway);
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-label={unread > 0 ? `${unread} unread notifications` : "Notifications"}
        onClick={() => {
          setOpen((v) => !v);
          if (!open) markAllRead();
        }}
        className="relative inline-flex h-9 w-9 items-center justify-center rounded-full border border-line bg-surface text-muted hover:text-text"
      >
        <svg viewBox="0 0 24 24" fill="none" className="h-4.5 w-4.5">
          <path d="M6 10a6 6 0 1 1 12 0c0 3 1 4.5 1.5 5.5H4.5C5 14.5 6 13 6 10Z" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
          <path d="M9.5 18.5a2.5 2.5 0 0 0 5 0" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
        {unread > 0 && (
          <span className="absolute -right-1 -top-1 flex h-4.5 min-w-[1.125rem] items-center justify-center rounded-full bg-pink px-1 text-[10px] font-bold text-on-tint">
            {unread > 9 ? "9+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 top-11 z-20 w-80 max-w-[90vw] rounded-[1.25rem] border border-line bg-surface p-2 shadow-[var(--shadow)]">
          <p className="px-3 py-2 text-xs font-semibold uppercase tracking-wide text-muted">Notifications</p>
          {notifications.length === 0 ? (
            <p className="px-3 py-4 text-sm text-muted">Nothing yet. New WhatsApp messages will show up here.</p>
          ) : (
            <ul className="max-h-96 overflow-y-auto">
              {notifications.map((n) => (
                <li key={n.id}>
                  <Link
                    href={`/apps/${n.integrationKey}`}
                    onClick={() => setOpen(false)}
                    className="block rounded-xl px-3 py-2 text-sm hover:bg-surface-2"
                  >
                    <div className="flex items-center gap-1.5">
                      <p className="font-semibold">{n.title}</p>
                      {n.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
                      {n.priority && <span className="rounded-full bg-pink px-1.5 py-0.5 text-[9px] font-bold text-on-tint">PRIORITY</span>}
                      {n.tags?.filter((t) => t !== "link").slice(0, 3).map((t) => (
                        <span key={t} className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold uppercase text-muted">{t.replace("-", " ")}</span>
                      ))}
                    </div>
                    {n.body && <p className="mt-0.5 truncate text-muted">{n.body}</p>}
                    <p className="mt-0.5 text-xs text-muted">{timeAgo(n.createdAt)}</p>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
