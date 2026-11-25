"use client";
import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { OneAppShell, Rows, when } from "@/components/OneAppParts";
import { ONE_APPS, manageFor } from "@/components/oneAppConfigs";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { Modal } from "@/components/Modal";
import { api, ApiError, type NotionDatabase, type NotionObject, type NotionPage, type OneAppStatus } from "@/lib/api";

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const KINDS = [{ value: "all", label: "Pages and databases" }, { value: "page", label: "Pages" }, { value: "database", label: "Databases" }];

function PageView({ id, canWrite }: { id: string; canWrite: boolean }) {
  const [page, setPage] = useState<NotionPage | null>(null); const [error, setError] = useState<string | null>(null);
  const [text, setText] = useState(""); const [busy, setBusy] = useState(false); const [tick, setTick] = useState(0);
  useEffect(() => { const t = setTimeout(() => { api.notionPage(id).then(setPage, (e) => setError(errorText(e, "Could not open the page."))); }, 0); return () => clearTimeout(t); }, [id, tick]);
  async function add(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try { await api.appendNotionPage(id, text); setText(""); setTick((n) => n + 1); }
    catch (err) { setError(errorText(err, "Could not add the text.")); }
    finally { setBusy(false); }
  }
  if (error && !page) return <p role="alert" className="text-sm text-danger">{error}</p>;
  if (!page) return <Loading label="Opening page" />;
  return (
    <div>
      <header className="flex items-start justify-between gap-3 pr-10"><h2 className="min-w-0 truncate text-xl font-bold">{page.title}</h2>{page.url && <a href={page.url} target="_blank" rel="noreferrer" className={`${secondaryButtonClass} shrink-0`}>Open in Notion</a>}</header>
      <pre className="mt-3 max-h-[50vh] overflow-auto whitespace-pre-wrap rounded-xl border border-line bg-surface-2 p-3 text-sm">{page.text.trim() || "This page is empty."}</pre>
      {error && <p role="alert" className="mt-2 text-sm text-danger">{error}</p>}
      {canWrite ? (
        <form onSubmit={(e) => void add(e)} className="mt-4 space-y-2 border-t border-line pt-3">
          <label htmlFor="notion-append" className="text-sm font-semibold">Add text to the end</label>
          <textarea id="notion-append" value={text} onChange={(e) => setText(e.target.value)} rows={3} className={`${inputClass} resize-y`} />
          <button type="submit" disabled={busy || !text.trim()} className={buttonClass}>{busy ? "Adding…" : "Add"}</button>
        </form>
      ) : <p className="mt-3 text-xs text-muted">Daksha has read-only access. Turn on full access in Manage to add text.</p>}
    </div>
  );
}

function DatabaseView({ id }: { id: string }) {
  const [db, setDb] = useState<NotionDatabase | null>(null); const [error, setError] = useState<string | null>(null);
  useEffect(() => { const t = setTimeout(() => { api.notionDatabase(id).then(setDb, (e) => setError(errorText(e, "Could not open the database."))); }, 0); return () => clearTimeout(t); }, [id]);
  if (error) return <p role="alert" className="text-sm text-danger">{error}</p>;
  if (!db) return <Loading label="Opening database" />;
  return (
    <div>
      <h2 className="pr-10 text-xl font-bold">{db.title}</h2>
      {db.rows.length === 0 ? <EmptyState>No rows.</EmptyState> : (
        <div className="mt-3 max-h-[55vh] overflow-auto"><Rows>{db.rows.map((r) => (
          <li key={r.id} className="px-3 py-2"><a href={r.url ?? "#"} target="_blank" rel="noreferrer" className="block truncate text-sm font-semibold hover:underline">{r.title}</a>
            <span className="block truncate text-xs text-muted">{Object.entries(r.properties).map(([k, v]) => `${k}: ${v}`).join(" · ")}</span></li>))}</Rows></div>
      )}
    </div>
  );
}

function CreatePage({ status }: { status: OneAppStatus }) {
  const [parent, setParent] = useState<NotionObject[] | null>(null); const [parentId, setParentId] = useState("");
  const [title, setTitle] = useState(""); const [text, setText] = useState(""); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null); const [made, setMade] = useState<string | null>(null);
  useEffect(() => { const t = setTimeout(() => { api.notionSearch("", "page").then((p) => { setParent(p); setParentId((c) => c || p[0]?.id || ""); }, () => setParent([])); }, 0); return () => clearTimeout(t); }, []);
  if (!status.write) return null;
  async function create(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null); setMade(null);
    try { const p = await api.createNotionPage(parentId, title, text); setMade(p.url ?? ""); setTitle(""); setText(""); }
    catch (err) { setError(errorText(err, "Could not create the page.")); }
    finally { setBusy(false); }
  }
  return (
    <form onSubmit={(e) => void create(e)} className={`space-y-2 p-4 ${tileClass}`}>
      <h3 className="text-base font-bold">New page</h3>
      {parent && parent.length > 0 ? <Dropdown value={parentId} options={parent.map((p) => ({ value: p.id, label: `Under: ${p.title}` }))} onChange={setParentId} /> : <p className="text-xs text-muted">Share a page with the Daksha integration in Notion first, so a new page can be made under it.</p>}
      <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Title" aria-label="Title" maxLength={200} className={inputClass} />
      <textarea value={text} onChange={(e) => setText(e.target.value)} rows={3} aria-label="Text" placeholder="Optional text" className={`${inputClass} resize-y`} />
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      {made !== null && <p role="status" className="text-sm text-muted">Created. {made && <a href={made} target="_blank" rel="noreferrer" className="font-semibold underline">Open in Notion</a>}</p>}
      <button type="submit" disabled={busy || !title.trim() || !parentId} className={buttonClass}>{busy ? "Creating…" : "Create"}</button>
    </form>
  );
}

/** The /apps/notion page: search the pages and databases shared with Daksha, read them, and (with full access) create pages and add text. */
export function NotionWorkspace() {
  const [kind, setKind] = useState("all"); const [q, setQ] = useState(""); const [applied, setApplied] = useState("");
  const [items, setItems] = useState<NotionObject[] | null>(null); const [error, setError] = useState<Error | null>(null);
  const [open, setOpen] = useState<NotionObject | null>(null); const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    const t = setTimeout(() => { api.notionSearch(applied, kind).then((r) => live && (setItems(r), setError(null)), (e) => live && setError(e instanceof Error ? e : new Error("Could not search Notion."))); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [applied, kind, tick]);
  return (
    <OneAppShell config={ONE_APPS.notion} manage={manageFor("notion")}>
      {(status) => (
        <>
          <CreatePage status={status} />
          <section className={`p-4 ${tileClass}`}>
            <form className="flex flex-wrap gap-2" onSubmit={(e: FormEvent) => { e.preventDefault(); setItems(null); setApplied(q.trim()); }}>
              <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search Notion" aria-label="Search Notion" className={`${inputClass} min-w-0 flex-1`} />
              <Dropdown className="w-48" value={kind} options={KINDS} onChange={(v) => { setItems(null); setKind(v); }} />
              <button type="submit" className={secondaryButtonClass}>Search</button>
            </form>
            {error ? <ErrorState error={error} onRetry={() => { setItems(null); setTick((n) => n + 1); }} /> : items === null ? <Loading label="Searching" /> : items.length === 0 ? <EmptyState>Nothing found. Notion only shows pages you shared with the Daksha integration.</EmptyState> : (
              <div className="mt-3"><Rows>{items.map((i) => (
                <li key={i.id} className="flex items-center gap-3 px-3 py-2"><span aria-hidden>{i.icon ?? (i.kind === "database" ? "🗂" : "📄")}</span>
                  <button type="button" onClick={() => setOpen(i)} className="min-w-0 flex-1 truncate text-left text-sm font-semibold hover:underline">{i.title}</button>
                  <span className="shrink-0 text-xs text-muted">{when(i.edited)}</span></li>))}</Rows></div>
            )}
          </section>
          <Modal wide open={open !== null} onClose={() => setOpen(null)}>{open && (open.kind === "database" ? <DatabaseView id={open.id} /> : <PageView id={open.id} canWrite={status.write} />)}</Modal>
        </>
      )}
    </OneAppShell>
  );
}
