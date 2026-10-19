"use client";
import Link from "next/link";
import { api } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { ErrorState, Loading, PageHeader } from "@/components/ui";

export default function DashboardPage() {
  const { state, reload } = useApi(async () => {
    const [tasks, reminders] = await Promise.all([api.tasks(), api.reminders()]);
    return { open: tasks.filter((t) => !t.completed).length, reminders: reminders.length };
  });
  return (
    <>
      <PageHeader title="Dashboard" />
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (
        <ul className="grid gap-4 sm:grid-cols-2">
          <li className="rounded-lg border p-5"><Link href="/tasks" className="font-semibold underline">Open tasks</Link><p className="text-3xl">{state.data.open}</p></li>
          <li className="rounded-lg border p-5"><Link href="/reminders" className="font-semibold underline">Reminders</Link><p className="text-3xl">{state.data.reminders}</p></li>
        </ul>
      )}
    </>
  );
}
