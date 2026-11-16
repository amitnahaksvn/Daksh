"use client";
import Link from "next/link";
import { DashboardAppCard } from "@/components/integrations";
import { buttonClass, EmptyState, ErrorState, Loading, PageHeader } from "@/components/ui";
import { api } from "@/lib/api";
import { useApi } from "@/lib/useApi";

export default function DashboardPage() {
  const { state, reload } = useApi(() => api.dashboardApps());

  async function remove(id: string) {
    await api.removeFromDashboard(id);
    reload();
  }

  return (
    <>
      <PageHeader title="Dashboard" />
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" &&
        (state.data.length === 0 ? (
          <EmptyState>
            Nothing here yet. Connect an app and choose <strong>Add to Dashboard</strong> on its page to pin it here.{" "}
            <Link href="/apps" className={`${buttonClass} mt-3 inline-flex`}>Browse apps</Link>
          </EmptyState>
        ) : (
          <ul className="grid gap-3 sm:grid-cols-2">
            {state.data.map((app) => (
              <DashboardAppCard key={app.id} app={app} onRemove={remove} />
            ))}
          </ul>
        ))}
    </>
  );
}
