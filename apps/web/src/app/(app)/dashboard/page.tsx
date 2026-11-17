"use client";
import { useState } from "react";
import Link from "next/link";
import { BrandTile, DashboardAppCard, StatusChip } from "@/components/integrations";
import { buttonClass, EmptyState, ErrorState, Loading, PageHeader, tileClass } from "@/components/ui";
import { AttentionSidebar, DashboardHero, StatTiles, useWhatsAppOverview } from "@/components/DashboardWidgets";
import { NewMessageForm, WhatsAppChatsPanel } from "@/components/WhatsAppChatsPanel";
import { api, type DashboardApp } from "@/lib/api";
import { useApi } from "@/lib/useApi";

/** WhatsApp's box: its own stats, the full chat, and what needs attention — all self-contained so any number
 *  of other app boxes can sit beside/below it. */
function WhatsAppDashboardCard({ app }: { app: DashboardApp }) {
  const [chatsKey, setChatsKey] = useState(0);
  const [overview, refresh] = useWhatsAppOverview(app.connectionId);

  return (
    <li className={`col-span-full p-5 ${tileClass}`}>
      <div className="flex flex-wrap items-center gap-4">
        <BrandTile icon={app.icon} color={app.color} size={48} />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-3">
            <p className="text-lg font-extrabold">{app.name}</p>
            <StatusChip status={app.status} />
          </div>
          {app.accountIdentifier && (
            <p className="mt-0.5 truncate text-sm text-muted">📱 {app.accountIdentifier.split(/[:@]/)[0]}</p>
          )}
        </div>
        <NewMessageForm connectionId={app.connectionId} onSent={() => setChatsKey((k) => k + 1)} />
      </div>

      <div className="mt-4">
        <StatTiles overview={overview} />
      </div>

      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_21rem]">
        <WhatsAppChatsPanel key={chatsKey} connectionId={app.connectionId} heightClass="h-[calc(100vh-16rem)] min-h-[32rem]" />
        <AttentionSidebar connectionId={app.connectionId} overview={overview} onChanged={refresh} />
      </div>
    </li>
  );
}

function DashboardBody({ apps, onRemove }: { apps: DashboardApp[]; onRemove: (id: string) => void }) {
  return (
    <>
      <DashboardHero apps={apps} />
      <ul className="grid items-start gap-4 sm:grid-cols-2 xl:grid-cols-3">
        {apps.map((app) =>
          app.integrationKey === "whatsapp" && app.status === "Connected" ? (
            <WhatsAppDashboardCard key={app.id} app={app} />
          ) : (
            <DashboardAppCard key={app.id} app={app} onRemove={onRemove} />
          ),
        )}
      </ul>
    </>
  );
}

export default function DashboardPage() {
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
        (state.data.length === 0 ? (
          <>
            <PageHeader title="Dashboard" />
            <EmptyState>
              Nothing here yet. Connect an app and choose <strong>Show on Dashboard</strong> on its page to pin it here.{" "}
              <Link href="/apps" className={`${buttonClass} mt-3 inline-flex`}>Browse apps</Link>
            </EmptyState>
          </>
        ) : (
          <DashboardBody apps={state.data} onRemove={remove} />
        ))}
    </>
  );
}
