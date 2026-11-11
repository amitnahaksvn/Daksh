"use client";
import { useState } from "react";
import { api, ApiError } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { buttonClass, EmptyState, ErrorState, inputClass, listClass, Loading, PageHeader, secondaryButtonClass } from "@/components/ui";

export default function TasksPage() {
  const { state, reload } = useApi(api.tasks);
  const [title, setTitle] = useState("");
  const [error, setError] = useState<string>();

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setError(undefined);
    try {
      await api.createTask(title);
      setTitle("");
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? Object.values(err.fieldErrors).flat().join(" ") || err.message : "Unexpected error.");
    }
  }

  return (
    <>
      <PageHeader title="Tasks" />
      <form onSubmit={add} className="mb-6 flex gap-2">
        <label htmlFor="title" className="sr-only">New task</label>
        <input id="title" className={inputClass} value={title} onChange={(e) => setTitle(e.target.value)} placeholder="New task" />
        <button className={buttonClass}>Add</button>
      </form>
      {error && <p role="alert" className="mb-4 text-sm font-medium text-danger">{error}</p>}
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && (state.data.length === 0 ? <EmptyState>No tasks yet.</EmptyState> : (
        <ul className={listClass}>
          {state.data.map((t) => (
            <li key={t.id} className="flex items-center justify-between gap-3 px-5 py-3.5">
              <span className={t.completed ? "line-through opacity-60" : ""}>{t.title}</span>
              {!t.completed && <button className={`${secondaryButtonClass} !px-4 !py-1.5 !text-xs`} onClick={() => api.completeTask(t.id).then(reload)}>Complete</button>}
            </li>
          ))}
        </ul>
      ))}
    </>
  );
}
