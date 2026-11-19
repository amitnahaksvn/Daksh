"use client";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { CardBusyOverlay, DashboardSkeleton, GmailDashboard, type MailboxTarget, type ManageTab } from "@/components/GmailDashboard";
import { GmailManagePanel } from "@/components/GmailManagePanel";
import { Modal } from "@/components/Modal";
import { tileClass } from "@/components/ui";
import type { DashboardApp } from "@/lib/api";

/** Where the mailbox page should start for a given dashboard click. */
export function mailboxUrl(target: MailboxTarget = {}): string {
  const p = new URLSearchParams();
  if (target.query) p.set("q", target.query);
  if (target.compose) p.set("compose", "1");
  if (target.focusSearch) p.set("focus", "search");
  if (target.openMessageId) p.set("open", target.openMessageId);
  const qs = p.toString();
  return qs ? `/apps/gmail?${qs}` : "/apps/gmail";
}

/**
 * The Gmail card on the Daksha dashboard: a compact, live dashboard (briefing, statistics, priority emails, quick actions).
 * Settings — including whether this card is shown at all — live in the Manage popup, opened by the card's gear.
 */
export function GmailDashboardCard({ app, onChanged }: { app: DashboardApp; onChanged?: () => void }) {
  const router = useRouter();
  const [manageOpen, setManageOpen] = useState(false);
  const [manageTab, setManageTab] = useState<ManageTab | undefined>();
  const [version, setVersion] = useState(0);
  const openManage = (tab?: ManageTab) => { setManageTab(tab); setManageOpen(true); };

  return (
    <div className={`min-h-full p-4 ${tileClass}`}>
      <GmailDashboard
        key={version}
        icon={app.icon}
        color={app.color}
        accountId={app.connectionId}
        accountEmail={app.accountIdentifier}
        onOpenMailbox={(target) => router.push(mailboxUrl(target))}
        onOpenManage={openManage}
      />
      <Modal wide fixedHeight open={manageOpen} onClose={() => { setManageOpen(false); setVersion((v) => v + 1); }}>
        <GmailManagePanel key={manageTab ?? "default"} initialTab={manageTab} onChanged={() => { setVersion((v) => v + 1); onChanged?.(); }} />
      </Modal>
    </div>
  );
}

/** Remembered so a full page refresh can reserve the card's space before the app list has loaded. */
export const GMAIL_CARD_HINT_KEY = "daksh.dashboard.gmailCard";

/** Same footprint as the loaded card (header with its settings icon, then the skeleton), shown while the dashboard's app list loads. */
export function GmailCardPlaceholder() {
  return (
    <ul className="grid grid-cols-1 items-start gap-4 lg:grid-cols-12" aria-busy="true">
      <li className={`relative overflow-hidden p-4 lg:col-span-8 ${tileClass}`}>
        <CardBusyOverlay />
        <div className="space-y-3">
          <header className="flex items-start gap-3">
            <span className="skeleton h-10 w-10 shrink-0 rounded-xl" />
            <div className="min-w-0 flex-1">
              <h2 className="text-xl font-bold leading-tight">Gmail Dashboard</h2>
              <p className="mt-0.5 text-xs text-muted sm:text-sm">Manage, summarize and take action on your Gmail with Daksha.</p>
            </div>
            <span aria-hidden className="rounded-xl border border-line p-2 text-muted opacity-60">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="h-4.5 w-4.5"><path d="M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z" /></svg>
            </span>
          </header>
          <DashboardSkeleton />
        </div>
      </li>
    </ul>
  );
}
