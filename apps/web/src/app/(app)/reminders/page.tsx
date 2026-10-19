"use client";
import { useState } from "react";
import { api, ApiError } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { buttonClass, EmptyState, ErrorState, inputClass, Loading, Notice, PageHeader } from "@/components/ui";

export default function RemindersPage() {
  const { state, reload } = useApi(api.reminders);
  const [text, setText] = useState("");
  const [when, setWhen] = useState("");
  const [error, setError] = useState<string>();

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setError(undefined);
    try {
      await api.createReminder(text, new Date(when).toISOString());
      setText("");
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? Object.values(err.fieldErrors).flat().join(" ") || err.message : "Enter a valid date and time.");
    }
  }

  return (
    <>
      <PageHeader title="Reminders" />
      <Notice>Push delivery is simulated until a push provider is configured and verified.</Notice>
      <form onSubmit={add} className="mb-6 grid gap-2 sm:grid-cols-[1fr_auto_auto]">
        <label htmlFor="text" className="sr-only">Reminder text</label>
        <input id="text" className={inputClass} value={text} onChange={(e) => setText(e.target.value)} placeholder="Remind me to…" />
        <label htmlFor="when" className="sr-only">When</label>
        <input id="when" type="datetime-local" className={inputClass} value={when} onChange={(e) => setWhen(e.target.value)} />
        <button className={buttonClass}>Add</button>
      </form>
      {error && <p role="alert" className="mb-4 text-sm text-red-700">{error}</p>}
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (state.data.length === 0 ? <EmptyState>No reminders yet.</EmptyState> : (
        <ul className="divide-y rounded-lg border">
          {state.data.map((r) => (
            <li key={r.id} className="p-3"><span>{r.text}</span> <time className="text-sm text-neutral-500" dateTime={r.remindAt}>{new Date(r.remindAt).toLocaleString()}</time></li>
          ))}
        </ul>
      ))}
    </>
  );
}
