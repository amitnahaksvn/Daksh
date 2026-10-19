"use client";
import { api } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { EmptyState, ErrorState, Loading, PageHeader } from "@/components/ui";

export default function ActivityPage() {
  const { state, reload } = useApi(api.auditEvents);
  return (
    <>
      <PageHeader title="Activity">An audit trail of actions Daksh has taken for you.</PageHeader>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (state.data.length === 0 ? <EmptyState>No activity yet.</EmptyState> : (
        <ul className="divide-y rounded-lg border">
          {state.data.map((a) => (
            <li key={a.id} className="p-3 text-sm"><span className="font-mono">{a.action}</span> <time className="text-neutral-500" dateTime={a.occurredAt}>{new Date(a.occurredAt).toLocaleString()}</time></li>
          ))}
        </ul>
      ))}
    </>
  );
}
