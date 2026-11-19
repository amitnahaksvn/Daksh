"use client";
import { useState } from "react";
import { api, ApiError, type ListItem } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { buttonClass, EmptyState, ErrorState, inputClass, Loading, PageHeader, tileClass } from "@/components/ui";

export default function ListsPage() {
  const { state, reload } = useApi(api.lists);
  const [list, setList] = useState("Shopping");
  const [text, setText] = useState("");
  const [error, setError] = useState<string>();

  async function add(e: React.FormEvent) {
    e.preventDefault();
    setError(undefined);
    if (!text.trim()) return;
    try {
      await api.addListItem(list, text);
      setText("");
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not add that. Please try again.");
    }
  }

  const groups = state.status === "ready"
    ? Object.entries(state.data.reduce<Record<string, ListItem[]>>((acc, i) => { (acc[i.list] ??= []).push(i); return acc; }, {}))
    : [];

  return (
    <>
      <PageHeader title="Lists">
        Things to buy and do. Add them here, or message yourself: &ldquo;buy milk&rdquo;, &ldquo;add headphones to Amazon list&rdquo;, &ldquo;todo call the bank&rdquo;. Ask Daksha &ldquo;what&apos;s on my list?&rdquo; any time.
      </PageHeader>
      <form onSubmit={add} className="mb-6 flex flex-wrap gap-2">
        <label className="sr-only" htmlFor="list-name">List</label>
        <input id="list-name" className={`${inputClass} w-40`} value={list} onChange={(e) => setList(e.target.value)} placeholder="List (e.g. Amazon)" maxLength={40} />
        <label className="sr-only" htmlFor="list-text">Item</label>
        <input id="list-text" className={`${inputClass} min-w-0 flex-1`} value={text} onChange={(e) => setText(e.target.value)} placeholder="Add an item" maxLength={300} />
        <button className={buttonClass}>Add</button>
      </form>
      {error && <p role="alert" className="mb-4 text-sm text-danger">{error}</p>}
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && groups.length === 0 && <EmptyState>Nothing here yet. Add your first item above, or message yourself &ldquo;buy milk&rdquo;.</EmptyState>}
      <div className="grid gap-4 md:grid-cols-2">
        {groups.map(([name, items]) => (
          <section key={name} className={`p-5 ${tileClass}`}>
            <h2 className="mb-3 text-lg font-bold">{name} <span className="text-sm font-normal text-muted">({items.filter((i) => !i.done).length} open)</span></h2>
            <ul className="flex flex-col gap-2">
              {items.map((i) => (
                <li key={i.id} className="flex items-center gap-3">
                  <input type="checkbox" className="h-5 w-5" checked={i.done} aria-label={`Done: ${i.text}`} onChange={async () => { await api.toggleListItem(i.id); reload(); }} />
                  <span className={`min-w-0 flex-1 break-words ${i.done ? "text-muted line-through" : ""}`}>{i.text}</span>
                  <button type="button" aria-label={`Delete ${i.text}`} className="text-sm text-muted hover:text-danger" onClick={async () => { await api.deleteListItem(i.id); reload(); }}>✕</button>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </>
  );
}
