"use client";
import { api } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { Chip, ErrorState, listClass, Loading, PageHeader } from "@/components/ui";

export default function IntegrationsPage() {
  const { state, reload } = useApi(api.integrations);
  return (
    <>
      <PageHeader title="Integrations">Status is reported by the API. “Mock” means simulated, not a working connection.</PageHeader>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (
        <ul className={listClass}>
          {state.data.map((i) => (
            <li key={i.key} className="px-5 py-3.5">
              <p className="flex flex-wrap items-center gap-2 font-semibold">{i.key} <Chip tone={i.state === "Mock" ? "pink" : "lime"}>{i.state}</Chip></p>
              <p className="mt-1 text-sm text-muted">{i.note}</p>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
