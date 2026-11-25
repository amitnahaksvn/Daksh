"use client";
import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { OneAppShell, Rows, when } from "@/components/OneAppParts";
import { ONE_APPS, manageFor } from "@/components/oneAppConfigs";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { Modal } from "@/components/Modal";
import { api, ApiError, type GhItem, type GhItemDetail, type GhNotification, type GhRepo, type OneAppStatus } from "@/lib/api";

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);
const TABS = [{ id: "work", label: "Pull requests & issues" }, { id: "notifications", label: "Notifications" }, { id: "repos", label: "Repositories" }];
const KINDS = [{ value: "review", label: "Review requested" }, { value: "assigned", label: "Assigned to me" }, { value: "created", label: "Created by me" }, { value: "mentioned", label: "Mentioning me" }];
const TYPES = [{ value: "all", label: "PRs and issues" }, { value: "pr", label: "Pull requests" }, { value: "issue", label: "Issues" }];
const STATES = [{ value: "open", label: "Open" }, { value: "closed", label: "Closed" }, { value: "all", label: "Any state" }];

function useList<T>(load: () => Promise<T[]>, key: string) {
  const [state, setState] = useState<{ data: T[] | null; error: Error | null; key: string }>({ data: null, error: null, key });
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    const t = setTimeout(() => { load().then((d) => live && setState({ data: d, error: null, key }), (e) => live && setState({ data: null, error: e instanceof Error ? e : new Error("Could not load."), key })); }, 0);
    return () => { live = false; clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, tick]);
  return { data: state.key === key ? state.data : null, error: state.key === key ? state.error : null, reload: () => setTick((n) => n + 1) };
}

function ItemDetail({ item, canWrite }: { item: GhItem; canWrite: boolean }) {
  const [detail, setDetail] = useState<GhItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [comment, setComment] = useState(""); const [busy, setBusy] = useState(false);
  useEffect(() => { const t = setTimeout(() => { api.githubItem(item.repo, item.number).then(setDetail, (e) => setError(errorText(e, "Could not open it."))); }, 0); return () => clearTimeout(t); }, [item]);
  async function send(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try { const c = await api.commentOnGithub(item.repo, item.number, comment); setDetail((d) => d && { ...d, comments: [...d.comments, c] }); setComment(""); }
    catch (err) { setError(errorText(err, "Could not post the comment.")); }
    finally { setBusy(false); }
  }
  return (
    <div>
      <header className="pr-10"><p className="text-xs text-muted">{item.repo}#{item.number} · {item.isPr ? "pull request" : "issue"} · {item.state}</p><h2 className="text-xl font-bold">{item.title}</h2>
        <a href={item.url} target="_blank" rel="noreferrer" className={`${secondaryButtonClass} mt-2`}>Open on GitHub</a></header>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      {!detail && !error ? <Loading label="Opening" /> : detail && (
        <>
          <pre className="mt-3 max-h-[30vh] overflow-auto whitespace-pre-wrap rounded-xl border border-line bg-surface-2 p-3 text-sm">{detail.body.trim() || "No description."}</pre>
          <h3 className="mt-4 text-sm font-bold">{detail.comments.length} comment{detail.comments.length === 1 ? "" : "s"}</h3>
          <ul className="mt-2 max-h-[30vh] space-y-2 overflow-auto">{detail.comments.map((c, i) => <li key={i} className="rounded-xl border border-line p-3 text-sm"><span className="font-semibold">{c.author}</span> <span className="text-xs text-muted">{when(c.created)}</span><p className="mt-1 whitespace-pre-wrap break-words">{c.body}</p></li>)}</ul>
          {canWrite ? (
            <form onSubmit={(e) => void send(e)} className="mt-3 space-y-2">
              <textarea value={comment} onChange={(e) => setComment(e.target.value)} rows={3} aria-label="Comment" placeholder="Write a comment" className={`${inputClass} resize-y`} />
              <button type="submit" disabled={busy || !comment.trim()} className={buttonClass}>{busy ? "Posting…" : "Comment"}</button>
            </form>
          ) : <p className="mt-3 text-xs text-muted">Daksha has read-only access to GitHub. Turn on full access in Manage to comment.</p>}
        </>
      )}
    </div>
  );
}

function Work({ status }: { status: OneAppStatus }) {
  const [kind, setKind] = useState("review"); const [type, setType] = useState("all"); const [state, setState] = useState("open");
  const [open, setOpen] = useState<GhItem | null>(null);
  const { data, error, reload } = useList(() => api.githubItems(kind, type, state), `${kind}|${type}|${state}`);
  return (
    <section className={`p-4 ${tileClass}`}>
      <div className="flex flex-wrap gap-2">
        <Dropdown className="w-48" value={kind} options={KINDS} onChange={setKind} />
        {kind !== "review" && <Dropdown className="w-44" value={type} options={TYPES} onChange={setType} />}
        <Dropdown className="w-36" value={state} options={STATES} onChange={setState} />
      </div>
      {error ? <ErrorState error={error} onRetry={reload} /> : data === null ? <Loading label="Loading" /> : data.length === 0 ? <EmptyState>Nothing here.</EmptyState> : (
        <div className="mt-3"><Rows>{data.map((i) => (
          <li key={i.key} className="px-3 py-2">
            <button type="button" onClick={() => setOpen(i)} className="block w-full text-left"><span className="block truncate text-sm font-semibold hover:underline">{i.title}</span>
              <span className="block truncate text-xs text-muted">{i.repo}#{i.number} · {i.isPr ? (i.draft ? "draft PR" : "PR") : "issue"} · {i.author} · {i.comments} comments · {when(i.updated)}</span></button>
            {i.labels.length > 0 && <span className="mt-1 flex flex-wrap gap-1">{i.labels.map((l) => <span key={l} className="rounded-full bg-surface-2 px-2 py-0.5 text-[10px] font-semibold">{l}</span>)}</span>}
          </li>))}</Rows></div>
      )}
      <Modal wide open={open !== null} onClose={() => setOpen(null)}>{open && <ItemDetail item={open} canWrite={status.write} />}</Modal>
    </section>
  );
}

function Notifications({ status }: { status: OneAppStatus }) {
  const [all, setAll] = useState(false);
  const { data, error, reload } = useList<GhNotification>(() => api.githubNotifications(all), `n|${all}`);
  const [msg, setMsg] = useState<string | null>(null);
  async function markRead() { setMsg(null); try { await api.markGithubRead(); reload(); } catch (e) { setMsg(errorText(e, "Could not mark as read.")); } }
  return (
    <section className={`p-4 ${tileClass}`}>
      <div className="flex flex-wrap items-center gap-2">
        <Dropdown className="w-44" value={all ? "all" : "unread"} options={[{ value: "unread", label: "Unread" }, { value: "all", label: "All" }]} onChange={(v) => setAll(v === "all")} />
        <button type="button" onClick={() => void markRead()} disabled={!status.write} title={status.write ? "" : "Needs full access"} className={secondaryButtonClass}>Mark all read</button>
      </div>
      {msg && <p role="alert" className="mt-2 text-sm text-danger">{msg}</p>}
      {error ? <ErrorState error={error} onRetry={reload} /> : data === null ? <Loading label="Loading" /> : data.length === 0 ? <EmptyState>No notifications.</EmptyState> : (
        <div className="mt-3"><Rows>{data.map((n) => (
          <li key={n.id} className="px-3 py-2"><a href={n.url ?? "https://github.com/notifications"} target="_blank" rel="noreferrer" className={`block truncate text-sm hover:underline ${n.unread ? "font-semibold" : ""}`}>{n.title}</a>
            <span className="block truncate text-xs text-muted">{n.repo} · {n.reason.replace(/_/g, " ")} · {when(n.updated)}</span></li>))}</Rows></div>
      )}
    </section>
  );
}

function Repos() {
  const [q, setQ] = useState(""); const [applied, setApplied] = useState("");
  const { data, error, reload } = useList<GhRepo>(() => api.githubRepos(applied), `r|${applied}`);
  return (
    <section className={`p-4 ${tileClass}`}>
      <form onSubmit={(e) => { e.preventDefault(); setApplied(q.trim()); }}><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find a repository" aria-label="Find a repository" className={inputClass} /></form>
      {error ? <ErrorState error={error} onRetry={reload} /> : data === null ? <Loading label="Loading" /> : data.length === 0 ? <EmptyState>No repositories.</EmptyState> : (
        <div className="mt-3"><Rows>{data.map((r) => (
          <li key={r.fullName} className="px-3 py-2"><a href={r.url} target="_blank" rel="noreferrer" className="block truncate text-sm font-semibold hover:underline">{r.private ? "🔒 " : ""}{r.fullName}</a>
            <span className="block truncate text-xs text-muted">{r.description ?? "No description"} · {r.language ?? "—"} · ★ {r.stars} · {r.openIssues} open · pushed {when(r.pushed)}</span></li>))}</Rows></div>
      )}
    </section>
  );
}

/** The /apps/github page: pull requests and issues (with comments), notifications and repositories. */
export function GitHubWorkspace() {
  const [tab, setTab] = useState(() => (typeof window === "undefined" ? null : new URLSearchParams(window.location.search).get("tab")) ?? "work");
  return (
    <OneAppShell config={ONE_APPS.github} manage={manageFor("github")}>
      {(status) => (
        <>
          <div role="tablist" aria-label="GitHub" className="flex flex-wrap gap-1">
            {TABS.map((t) => <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)} className={`rounded-full px-4 py-2 text-sm font-semibold ${tab === t.id ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{t.label}</button>)}
          </div>
          {tab === "notifications" ? <Notifications status={status} /> : tab === "repos" ? <Repos /> : <Work status={status} />}
        </>
      )}
    </OneAppShell>
  );
}
