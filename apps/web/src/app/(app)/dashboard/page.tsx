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
        <ul className="grid gap-3 sm:grid-cols-2">
          <li className="rounded-[1.25rem] bg-lime p-6 text-on-tint"><Link href="/tasks" className="font-semibold underline-offset-4 hover:underline">Open tasks</Link><p className="ai-number ai-mono mt-2 text-5xl font-extrabold tracking-tight">{state.data.open}</p></li>
          <li className="rounded-[1.25rem] bg-pink p-6 text-on-tint"><Link href="/reminders" className="font-semibold underline-offset-4 hover:underline">Reminders</Link><p className="ai-number ai-mono mt-2 text-5xl font-extrabold tracking-tight">{state.data.reminders}</p></li>
        </ul>
      )}
    </>
  );
}
