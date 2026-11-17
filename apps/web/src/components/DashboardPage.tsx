"use client";
import Link from "next/link";
import { DashboardAppCard } from "@/components/integrations";
import { buttonClass, EmptyState, ErrorState, Loading, PageHeader } from "@/components/ui";
import { GmailDashboardCard } from "@/components/GmailDashboardCard";
import { DashboardHero } from "@/components/DashboardWidgets";
import { api, type DashboardApp } from "@/lib/api";
import { useApi } from "@/lib/useApi";

/** Messaging apps (WhatsApp, Telegram) live in the Chats sidebar on every page, so they are not tiles here. */
const MESSAGING = new Set(["whatsapp", "telegram"]);

function DashboardBody({ apps, onRemove }: { apps: DashboardApp[]; onRemove: (id: string) => void }) {
  return (
    <>
      <DashboardHero apps={apps} />
      <ul className="grid items-start gap-4 sm:grid-cols-2 xl:grid-cols-3">
        {apps.map((app) => app.integrationKey === "gmail"
          ? <GmailDashboardCard key={app.id} app={app} />
          : <DashboardAppCard key={app.id} app={app} onRemove={onRemove} />)}
      </ul>
    </>
  );
}

export function DashboardPage() {
  const { state, reload } = useApi(() => api.dashboardApps());

  async function remove(id: string) {
    await api.removeFromDashboard(id);
    reload();
  }

  return (
    <>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" &&
        (state.data.filter((a) => !MESSAGING.has(a.integrationKey)).length === 0 ? (
          <>
            <PageHeader title="Dashboard" />
            <EmptyState>
              Nothing here yet. Your WhatsApp and Telegram chats are always in the Chats sidebar on the right. Connect more apps to see them here.{" "}
              <Link href="/apps" className={`${buttonClass} mt-3 inline-flex`}>Browse apps</Link>
            </EmptyState>
          </>
        ) : (
          <DashboardBody apps={state.data.filter((a) => !MESSAGING.has(a.integrationKey))} onRemove={remove} />
        ))}
    </>
  );
}
