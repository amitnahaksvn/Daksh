"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { BrandTile } from "@/components/integrations";
import { Avatar, listDate, parseSender } from "@/components/GmailMailbox";
import { api, type DashboardApp, type GmailMessageSummary } from "@/lib/api";
import { Loading, tileClass } from "@/components/ui";

/** Dashboard tile for Gmail: a live preview of recent email. Managed from Apps → Gmail → Manage, so no Manage/Remove here. */
export function GmailDashboardCard({ app }: { app: DashboardApp }) {
  const [messages, setMessages] = useState<GmailMessageSummary[] | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const settings = await api.gmailSettings();
        const page = await api.gmailMessages(settings.dashboardQuery || "in:inbox");
        if (!cancelled) setMessages(page.messages.slice(0, settings.dashboardCount || 5));
      } catch {
        if (!cancelled) setError(true);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  const unread = messages?.filter((m) => m.isUnread).length ?? 0;

  return (
    <li className={`flex flex-col gap-3 p-5 sm:col-span-2 ${tileClass}`}>
      <div className="flex items-center gap-3">
        <div className="relative shrink-0">
          <BrandTile icon={app.icon} color={app.color} size={44} />
          {unread > 0 && (
            <span className="absolute -right-1 -top-1 flex h-4.5 min-w-[1.125rem] items-center justify-center rounded-full bg-pink px-1 text-[10px] font-bold text-on-tint">
              {unread > 9 ? "9+" : unread}
            </span>
          )}
        </div>
        <div className="min-w-0 flex-1">
          <p className="font-semibold">Gmail</p>
          {app.accountIdentifier && <p className="truncate text-xs text-muted">{app.accountIdentifier}</p>}
        </div>
        <Link href="/apps/gmail" className="rounded-full px-3 py-1.5 text-xs font-semibold text-accent hover:bg-surface-2">Open inbox</Link>
      </div>

      {!messages && !error && <Loading label="Loading emails" />}
      {error && <p className="text-sm text-muted">Could not load your emails right now.</p>}
      {messages && messages.length === 0 && <p className="text-sm text-muted">No emails to show.</p>}
      {messages && messages.length > 0 && (
        <ul className="divide-y divide-line overflow-hidden rounded-xl border border-line">
          {messages.map((m) => {
            const sender = parseSender(m.from);
            return (
              <li key={m.id}>
                <Link href="/apps/gmail" className="flex items-center gap-3 px-3 py-2.5 transition-colors hover:bg-surface-2">
                  <Avatar name={sender.name} size={32} />
                  <span className="min-w-0 flex-1">
                    <span className={`block truncate text-sm ${m.isUnread ? "font-bold" : "font-medium"}`}>{sender.name}</span>
                    <span className="block truncate text-xs text-muted">
                      <span className={m.isUnread ? "font-semibold text-text" : ""}>{m.subject || "(no subject)"}</span> — {m.snippet}
                    </span>
                  </span>
                  <span className={`shrink-0 text-xs ${m.isUnread ? "font-bold" : "text-muted"}`}>{listDate(m.date)}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      )}
    </li>
  );
}
