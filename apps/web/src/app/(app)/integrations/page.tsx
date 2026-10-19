"use client";
import { api } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { ErrorState, Loading, PageHeader } from "@/components/ui";

export default function IntegrationsPage() {
  const { state, reload } = useApi(api.integrations);
  return (
    <>
      <PageHeader title="Integrations">Status is reported by the API. “Mock” means simulated, not a working connection.</PageHeader>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (
        <ul className="divide-y rounded-lg border">
          {state.data.map((i) => (
            <li key={i.key} className="p-3">
              <p className="font-medium">{i.key} <span className="ml-2 rounded bg-neutral-200 px-2 py-0.5 text-xs text-neutral-900">{i.state}</span></p>
              <p className="text-sm text-neutral-600 dark:text-neutral-400">{i.note}</p>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
