"use client";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import Link from "next/link";
import { NotificationItem } from "@/components/NotificationItem";
import { getNotifications, getUnreadCount, markAllRead, startNotifications, subscribeNotifications } from "@/lib/notifications";

const DROPDOWN_LIMIT = 20;

/** A bell with a live unread badge, mounted once in the authenticated layout so every page can see arrivals. */
export function NotificationBell() {
  const notifications = useSyncExternalStore(subscribeNotifications, getNotifications, () => []);
  const unread = useSyncExternalStore(subscribeNotifications, getUnreadCount, () => 0);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => startNotifications(), []);

  // Ring the bell whenever the unread count goes up.
  const [ringing, setRinging] = useState(false);
  const previousUnread = useRef(unread);
  useEffect(() => {
    if (unread > previousUnread.current) {
      setRinging(true);
      const t = setTimeout(() => setRinging(false), 1000);
      previousUnread.current = unread;
      return () => clearTimeout(t);
    }
    previousUnread.current = unread;
  }, [unread]);

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
        <svg viewBox="0 0 24 24" fill="none" className={`h-4.5 w-4.5 ${ringing ? "bell-ring" : ""}`}>
          <path d="M6 10a6 6 0 1 1 12 0c0 3 1 4.5 1.5 5.5H4.5C5 14.5 6 13 6 10Z" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
          <path d="M9.5 18.5a2.5 2.5 0 0 0 5 0" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
        {unread > 0 && (
          <span key={unread} className="badge-pop absolute -right-1 -top-1 flex h-4.5 min-w-[1.125rem] items-center justify-center rounded-full bg-pink px-1 text-[10px] font-bold text-on-tint">
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
            <>
              <ul className="max-h-96 overflow-y-auto">
                {notifications.slice(0, DROPDOWN_LIMIT).map((n) => (
                  <li key={n.id}><NotificationItem n={n} onClick={() => setOpen(false)} /></li>
                ))}
              </ul>
              <Link
                href="/notifications"
                onClick={() => setOpen(false)}
                className="mt-1 block rounded-xl px-3 py-2 text-center text-sm font-semibold text-accent hover:bg-surface-2"
              >
                View all notifications
              </Link>
            </>
          )}
        </div>
      )}
    </div>
  );
}
