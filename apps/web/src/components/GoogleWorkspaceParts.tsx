"use client";
import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { api, ApiError, type GoogleDoc, type GoogleFile, type GooglePresentation, type GoogleService, type GoogleSheet, type GoogleWorkspaceSettings } from "@/lib/api";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { Modal } from "@/components/Modal";

export const GOOGLE_SERVICES: { id: GoogleService; name: string; description: string }[] = [
  { id: "drive", name: "Google Drive", description: "Browse, search and open your files and folders." },
  { id: "docs", name: "Google Docs", description: "Read your documents, and write new ones with full access." },
  { id: "sheets", name: "Google Sheets", description: "Read your spreadsheets, and add rows with full access." },
  { id: "slides", name: "Google Slides", description: "Read the text of your presentations." },
];

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

export function googleCallbackError(reason: string | null) {
  switch (reason) {
    case "consent_denied": return "Google sign-in was cancelled.";
    case "oauth_not_configured": return "Google sign-in is not configured on the API yet.";
    case "refresh_token_missing": return "Google did not return offline access. Try again and approve the requested permissions.";
    case "profile_lookup": return "Daksha could not read your Google profile.";
    case "google_unavailable": return "Google could not be reached. Please try again.";
    default: return "Google could not be connected. Please try again.";
  }
}

/**
 * Opens Google sign-in in a popup (the same one Gmail uses) and resolves with "connected" or an error reason.
 * `features` is what Google is asked for, e.g. "drive,docs" or "workspace_write". Google remembers earlier approvals, so only new permissions are asked.
 */
export function signInWithGoogle(features: string): Promise<string> {
  const width = 520, height = 700;
  const left = Math.max(0, window.screenX + (window.outerWidth - width) / 2);
  const top = Math.max(0, window.screenY + (window.outerHeight - height) / 2);
  const popup = window.open("about:blank", "daksh-gmail-oauth", `popup=yes,width=${width},height=${height},left=${left},top=${top}`);
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = (fn: () => void) => { if (done) return; done = true; window.removeEventListener("message", onMessage); window.clearInterval(timer); fn(); };
    const onMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || event.data?.type !== "daksh-gmail-oauth") return;
      finish(() => resolve(String(event.data.result ?? "")));
    };
    window.addEventListener("message", onMessage);
    const timer = window.setInterval(() => { if (popup?.closed) finish(() => resolve("closed")); }, 600);
    api.connectGmail(features).then(({ authorizationUrl }) => {
      if (popup && !popup.closed) popup.location.href = authorizationUrl;
      else finish(() => window.location.assign(authorizationUrl));
    }, (err) => { popup?.close(); finish(() => reject(err)); });
  });
}

/**
 * Turns Google services on. If Google has not been asked for the permissions yet it opens the Google sign-in first, then turns them on.
 * Resolves with an error message to show, or null when everything worked (or the user simply closed the window).
 */
export async function turnOnGoogleServices(services: GoogleService[], write?: boolean): Promise<string | null> {
  try {
    await api.enableGoogleServices(services, write);
    return null;
  } catch (err) {
    if (!(err instanceof ApiError) || err.code !== "scope_upgrade_required") return errorText(err, "Could not turn Google on.");
    const result = await signInWithGoogle(err.requiredScope ?? services.join(","));
    if (result === "closed") return null;
    if (result !== "connected") return googleCallbackError(result);
    try { await api.enableGoogleServices(services, write); return null; }
    catch (again) { return errorText(again, "Could not turn Google on."); }
  }
}

/** One Google sign-in for everything: pick the services, then continue to Google. */
export function GoogleConnectCard({ title = "Connect Google Drive, Docs and Sheets", initialServices, onConnected }: { title?: string; initialServices?: GoogleService[]; onConnected?: () => void }) {
  const [chosen, setChosen] = useState<GoogleService[]>(initialServices?.length ? initialServices : ["drive", "docs", "sheets"]);
  const [full, setFull] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function connect() {
    setError(null); setBusy(true);
    try {
      const message = await turnOnGoogleServices(chosen, full);
      if (message) setError(message); else onConnected?.();
    } finally { setBusy(false); }
  }

  return (
    <div className={`p-5 ${tileClass}`}>
      <h3 className="text-lg font-bold">{title}</h3>
      <p className="mt-1 text-sm text-muted">One Google sign-in, the same one Gmail and Calendar use. Choose what Daksha can use. Daksha never sees or stores your Google password.</p>
      <ul className="mt-4 space-y-2">
        {GOOGLE_SERVICES.map((s) => (
          <li key={s.id}>
            <label className="flex cursor-pointer items-start gap-3 rounded-xl border border-line bg-surface-2 p-3">
              <input type="checkbox" className="mt-1" checked={chosen.includes(s.id)} onChange={(e) => setChosen((c) => (e.target.checked ? [...c, s.id] : c.filter((x) => x !== s.id)))} />
              <span><span className="block text-sm font-semibold">{s.name}</span><span className="block text-xs text-muted">{s.description}</span></span>
            </label>
          </li>
        ))}
      </ul>
      <label className="mt-3 flex cursor-pointer items-start gap-3 rounded-xl border border-line p-3">
        <input type="checkbox" className="mt-1" checked={full} onChange={(e) => setFull(e.target.checked)} />
        <span><span className="block text-sm font-semibold">Allow Daksha to create and edit files</span><span className="block text-xs text-muted">Off by default: Daksha can then only read. You can change this later in Manage.</span></span>
      </label>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      <button type="button" onClick={() => void connect()} disabled={busy || chosen.length === 0} className={`${buttonClass} mt-4 px-5`}>{busy ? "Opening Google…" : "Continue with Google"}</button>
    </div>
  );
}

// ---- Drive -------------------------------------------------------------------------------------------

const KIND_ICON: Record<GoogleFile["kind"], string> = { folder: "📁", doc: "📝", sheet: "📊", slides: "📽️", pdf: "📕", image: "🖼️", file: "📄" };
const VIEWS = [{ id: "recent", label: "Recent" }, { id: "mine", label: "My Drive" }, { id: "shared", label: "Shared with me" }, { id: "starred", label: "Starred" }];
const KINDS = [{ value: "all", label: "All types" }, { value: "folders", label: "Folders" }, { value: "docs", label: "Docs" }, { value: "sheets", label: "Sheets" }, { value: "slides", label: "Slides" }, { value: "pdf", label: "PDFs" }, { value: "images", label: "Images" }];
const sizeText = (n: number | null) => (n == null ? "" : n < 1024 ? `${n} B` : n < 1048576 ? `${Math.round(n / 1024)} KB` : `${(n / 1048576).toFixed(1)} MB`);

type Opener = { doc: boolean; sheets: boolean; slides: boolean };

/** Browse and search Drive. Docs, Sheets and Slides open in Daksha (when that service is on and the owner prefers it), everything else opens in Google. */
export function DriveBrowser({ initialView = "recent", initialKind = "all", openIn, enabled, onOpen }: {
  initialView?: string; initialKind?: string; openIn: GoogleWorkspaceSettings["openIn"]; enabled: Opener; onOpen: (file: GoogleFile) => void;
}) {
  const [view, setView] = useState(initialView);
  const [kind, setKind] = useState(initialKind);
  const [trail, setTrail] = useState<{ id: string | null; name: string }[]>([{ id: null, name: "Drive" }]);
  const [query, setQuery] = useState(""); const [applied, setApplied] = useState("");
  const [items, setItems] = useState<GoogleFile[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [more, setMore] = useState(false);
  const folder = trail[trail.length - 1];

  useEffect(() => {
    let live = true;
    const t = setTimeout(() => {
      setError(null);
      api.googleFiles({ folderId: folder.id, q: applied, view, kind }).then((l) => { if (live) { setItems(l.items); setNext(l.nextPageToken); } },
        (err) => { if (live) { setError(errorText(err, "Could not load Google Drive.")); setItems([]); } });
    }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [folder.id, applied, view, kind]);

  async function loadMore() {
    if (!next) return;
    setMore(true);
    try { const l = await api.googleFiles({ folderId: folder.id, q: applied, view, kind, pageToken: next }); setItems((cur) => [...(cur ?? []), ...l.items]); setNext(l.nextPageToken); }
    catch (err) { setError(errorText(err, "Could not load more files.")); }
    finally { setMore(false); }
  }

  const reset = () => { setItems(null); setNext(null); };
  const canOpenHere = (f: GoogleFile) => openIn === "daksha" && ((f.kind === "doc" && enabled.doc) || (f.kind === "sheet" && enabled.sheets) || (f.kind === "slides" && enabled.slides));

  return (
    <section className={`p-4 ${tileClass}`}>
      <div role="tablist" aria-label="Drive views" className="flex flex-wrap gap-1">
        {VIEWS.map((v) => (
          <button key={v.id} role="tab" aria-selected={view === v.id && trail.length === 1} onClick={() => { reset(); setTrail([{ id: null, name: "Drive" }]); setApplied(""); setQuery(""); setView(v.id); }}
            className={`rounded-full px-3 py-1.5 text-sm font-semibold ${view === v.id && trail.length === 1 ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{v.label}</button>
        ))}
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <nav aria-label="Folder path" className="flex min-w-0 flex-1 flex-wrap items-center gap-1 text-sm">
          {trail.map((t, i) => (
            <span key={`${t.id}-${i}`} className="flex items-center gap-1">
              {i > 0 && <span className="text-muted">/</span>}
              <button type="button" onClick={() => { reset(); setApplied(""); setQuery(""); setTrail(trail.slice(0, i + 1)); }} className="font-semibold hover:underline">{t.name}</button>
            </span>
          ))}
        </nav>
        <Dropdown className="w-36" value={kind} options={KINDS} onChange={(v) => { reset(); setKind(v); }} />
        <form className="flex gap-2" onSubmit={(e: FormEvent) => { e.preventDefault(); reset(); setTrail([{ id: null, name: "Drive" }]); setApplied(query.trim()); }}>
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search Drive" aria-label="Search Drive" className={inputClass} />
        </form>
      </div>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      {items === null ? <Loading label="Loading files" /> : items.length === 0 ? <EmptyState>{applied ? "No files match that search." : "No files here."}</EmptyState> : (
        <>
          <ul className="mt-3 divide-y divide-line rounded-xl border border-line">
            {items.map((f) => (
              <li key={f.id} className="flex items-center gap-3 px-3 py-2">
                <span aria-hidden>{KIND_ICON[f.kind]}</span>
                {f.isFolder
                  ? <button type="button" onClick={() => { reset(); setApplied(""); setQuery(""); setTrail([...trail, { id: f.id, name: f.name }]); }} className="min-w-0 flex-1 truncate text-left text-sm font-semibold hover:underline">{f.name}</button>
                  : canOpenHere(f)
                    ? <button type="button" onClick={() => onOpen(f)} className="min-w-0 flex-1 truncate text-left text-sm hover:underline">{f.name}</button>
                    : <a href={f.webUrl ?? "#"} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate text-sm hover:underline">{f.name}</a>}
                {f.starred && <span aria-label="Starred" className="text-xs">⭐</span>}
                <span className="hidden shrink-0 text-xs text-muted md:block">{f.owner ?? ""}</span>
                <span className="shrink-0 text-xs text-muted">{sizeText(f.size)}</span>
                <span className="hidden shrink-0 text-xs text-muted sm:block">{f.modified ? new Date(f.modified).toLocaleDateString() : ""}</span>
                {!f.isFolder && f.webUrl && canOpenHere(f) && <a href={f.webUrl} target="_blank" rel="noreferrer" className="shrink-0 text-xs font-semibold text-accent hover:underline" aria-label={`Open ${f.name} in Google`}>Google</a>}
              </li>
            ))}
          </ul>
          {next && <div className="mt-3 text-center"><button type="button" onClick={() => void loadMore()} disabled={more} className={secondaryButtonClass}>{more ? "Loading…" : "Load more"}</button></div>}
        </>
      )}
    </section>
  );
}

// ---- viewers -----------------------------------------------------------------------------------------

function useLoad<T>(load: () => Promise<T>, key: string) {
  const [state, setState] = useState<{ data: T | null; error: Error | null }>({ data: null, error: null });
  const [tick, setTick] = useState(0);
  const loadRef = useRef(load);
  useEffect(() => { loadRef.current = load; });
  useEffect(() => {
    let live = true;
    const t = setTimeout(() => { loadRef.current().then((d) => live && setState({ data: d, error: null }), (e) => live && setState({ data: null, error: e instanceof Error ? e : new Error("Could not load.") })); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [key, tick]);
  return { ...state, reload: () => { setState({ data: null, error: null }); setTick((n) => n + 1); } };
}

function AppendForm({ label, placeholder, rows, onSubmit }: { label: string; placeholder: string; rows: number; onSubmit: (text: string) => Promise<void> }) {
  const [text, setText] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null); const [done, setDone] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null); setDone(false);
    try { await onSubmit(text); setText(""); setDone(true); }
    catch (err) { setError(errorText(err, "Could not save.")); }
    finally { setBusy(false); }
  }
  return (
    <form onSubmit={(e) => void submit(e)} className="mt-4 space-y-2 border-t border-line pt-3">
      <label className="text-sm font-semibold" htmlFor="gw-append">{label}</label>
      <textarea id="gw-append" value={text} onChange={(e) => setText(e.target.value)} rows={rows} className={`${inputClass} resize-y`} placeholder={placeholder} />
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      {done && <p role="status" className="text-sm text-muted">Added.</p>}
      <button type="submit" disabled={busy || !text.trim()} className={buttonClass}>{busy ? "Adding…" : "Add"}</button>
    </form>
  );
}

export function DocViewer({ id, canWrite }: { id: string; canWrite: boolean }) {
  const { data, error, reload } = useLoad<GoogleDoc>(() => api.googleDoc(id), id);
  if (error) return <ErrorState error={error} onRetry={reload} />;
  if (!data) return <Loading label="Opening document" />;
  return (
    <div>
      <header className="flex items-start justify-between gap-3 pr-10">
        <h2 className="min-w-0 truncate text-xl font-bold">{data.title}</h2>
        {data.webUrl && <a href={data.webUrl} target="_blank" rel="noreferrer" className={`${secondaryButtonClass} shrink-0`}>Open in Google</a>}
      </header>
      <pre className="mt-3 max-h-[50vh] overflow-auto whitespace-pre-wrap rounded-xl border border-line bg-surface-2 p-3 text-sm">{data.text.trim() || "This document is empty."}</pre>
      {canWrite
        ? <AppendForm label="Add text to the end" placeholder="Write something to add…" rows={4} onSubmit={async (t) => { await api.appendGoogleDoc(id, t); reload(); }} />
        : <p className="mt-3 text-xs text-muted">Daksha has read-only access. Turn on full access in Manage to add text.</p>}
    </div>
  );
}

export function SheetViewer({ id, canWrite }: { id: string; canWrite: boolean }) {
  const [range, setRange] = useState<string | undefined>();
  const { data, error, reload } = useLoad<GoogleSheet>(() => api.googleSheet(id, range), `${id}|${range ?? ""}`);
  if (error) return <ErrorState error={error} onRetry={reload} />;
  if (!data) return <Loading label="Opening spreadsheet" />;
  const width = Math.max(1, ...data.values.map((r) => r.length));
  return (
    <div>
      <header className="flex items-start justify-between gap-3 pr-10">
        <h2 className="min-w-0 truncate text-xl font-bold">{data.title}</h2>
        {data.webUrl && <a href={data.webUrl} target="_blank" rel="noreferrer" className={`${secondaryButtonClass} shrink-0`}>Open in Google</a>}
      </header>
      {data.tabs.length > 1 && (
        <div role="tablist" aria-label="Sheets" className="mt-3 flex flex-wrap gap-1">
          {data.tabs.map((t) => (
            <button key={t.title} role="tab" aria-selected={data.range === t.title} onClick={() => setRange(t.title)}
              className={`rounded-full px-3 py-1 text-sm font-semibold ${data.range === t.title ? "bg-accent text-accent-contrast" : "border border-line text-muted hover:bg-surface-2"}`}>{t.title}</button>
          ))}
        </div>
      )}
      {data.values.length === 0 ? <EmptyState>This sheet is empty.</EmptyState> : (
        <div className="mt-3 max-h-[50vh] overflow-auto rounded-xl border border-line">
          <table className="min-w-full border-collapse text-sm">
            <tbody>
              {data.values.map((row, r) => (
                <tr key={r} className={r === 0 ? "bg-surface-2 font-semibold" : "border-t border-line"}>
                  {Array.from({ length: width }, (_, c) => <td key={c} className="max-w-[16rem] truncate whitespace-nowrap px-3 py-1.5">{row[c] ?? ""}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {data.truncated && <p className="mt-2 text-xs text-muted">Showing the first {data.values.length} rows. Open in Google to see the rest, or change the row limit in Manage.</p>}
      {canWrite
        ? <AppendForm label="Add a row (separate cells with commas)" placeholder="Name, 42, Done" rows={2} onSubmit={async (t) => { await api.appendGoogleSheet(id, [t.split(",").map((c) => c.trim())], data.range); reload(); }} />
        : <p className="mt-3 text-xs text-muted">Daksha has read-only access. Turn on full access in Manage to add rows.</p>}
    </div>
  );
}

export function SlidesViewer({ id }: { id: string }) {
  const { data, error, reload } = useLoad<GooglePresentation>(() => api.googleSlides(id), id);
  if (error) return <ErrorState error={error} onRetry={reload} />;
  if (!data) return <Loading label="Opening presentation" />;
  return (
    <div>
      <header className="flex items-start justify-between gap-3 pr-10">
        <h2 className="min-w-0 truncate text-xl font-bold">{data.title}</h2>
        {data.webUrl && <a href={data.webUrl} target="_blank" rel="noreferrer" className={`${secondaryButtonClass} shrink-0`}>Open in Google</a>}
      </header>
      {data.slides.length === 0 ? <EmptyState>This presentation has no slides.</EmptyState> : (
        <ol className="mt-3 max-h-[55vh] space-y-2 overflow-auto">
          {data.slides.map((s) => (
            <li key={s.number} className="rounded-xl border border-line bg-surface-2 p-3"><span className="text-xs font-bold uppercase text-muted">Slide {s.number}</span><p className="mt-1 whitespace-pre-wrap text-sm">{s.text || "(no text)"}</p></li>
          ))}
        </ol>
      )}
    </div>
  );
}

/** Opens a file in the right viewer, in a popup over the page. */
export function FileModal({ file, canWrite, onClose }: { file: GoogleFile | null; canWrite: boolean; onClose: () => void }) {
  return (
    <Modal wide open={file !== null} onClose={onClose}>
      {file?.kind === "doc" && <DocViewer id={file.id} canWrite={canWrite} />}
      {file?.kind === "sheet" && <SheetViewer id={file.id} canWrite={canWrite} />}
      {file?.kind === "slides" && <SlidesViewer id={file.id} />}
    </Modal>
  );
}

// ---- create ------------------------------------------------------------------------------------------

/** Make a new Google Doc or Sheet. Needs full access. */
export function CreatePanel({ kind, onCreated }: { kind: "doc" | "sheet"; onCreated: (url: string) => void }) {
  const [title, setTitle] = useState(""); const [text, setText] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  async function create(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try {
      if (kind === "doc") { const d = await api.createGoogleDoc(title, text); onCreated(d.webUrl ?? ""); }
      else { const rows = text.split("\n").map((l) => l.split(",").map((c) => c.trim())).filter((r) => r.some(Boolean)); const s = await api.createGoogleSheet(title, rows.length ? rows : undefined); onCreated(s.webUrl); }
      setTitle(""); setText("");
    } catch (err) { setError(errorText(err, "Could not create it.")); }
    finally { setBusy(false); }
  }
  return (
    <form onSubmit={(e) => void create(e)} className={`space-y-2 p-4 ${tileClass}`}>
      <h3 className="text-base font-bold">{kind === "doc" ? "New document" : "New spreadsheet"}</h3>
      <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Title" aria-label="Title" maxLength={200} className={inputClass} />
      <textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} className={`${inputClass} resize-y`} aria-label={kind === "doc" ? "Text" : "Rows"}
        placeholder={kind === "doc" ? "Optional text to start with" : "Optional rows, one per line, cells separated by commas"} />
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      <button type="submit" disabled={busy || !title.trim()} className={buttonClass}>{busy ? "Creating…" : "Create"}</button>
    </form>
  );
}
