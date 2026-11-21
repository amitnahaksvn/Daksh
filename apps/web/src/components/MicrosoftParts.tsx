"use client";
import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { api, ApiError, type DevOpsSummary, type DriveListing, type MicrosoftFeature, type OutlookMessage, type OutlookMessageSummary } from "@/lib/api";
import { buttonClass, Dropdown, EmptyState, ErrorState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { Avatar, listDate } from "@/components/GmailMailbox";

export const MICROSOFT_SERVICES: { id: MicrosoftFeature; name: string; description: string }[] = [
  { id: "mail", name: "Outlook Mail", description: "Read, search and send email from your Outlook inbox." },
  { id: "calendar", name: "Outlook Calendar", description: "See and add events. They appear in Daksha's Calendar next to Google." },
  { id: "onedrive", name: "OneDrive", description: "Browse and search your files." },
  { id: "devops", name: "Azure DevOps", description: "See the work items assigned to you. Needs a work or school account." },
];

export function microsoftCallbackError(reason: string | null) {
  switch (reason) {
    case "consent_denied": return "Microsoft sign-in was cancelled.";
    case "admin_denied": return "The administrator did not approve Daksha.";
    case "oauth_not_configured": return "Microsoft sign-in is not configured on the API yet.";
    case "refresh_token_missing": return "Microsoft did not return offline access. Try again and approve the requested permissions.";
    case "profile_lookup": return "Daksha could not read your Microsoft profile.";
    case "microsoft_unavailable": return "Microsoft could not be reached. Please try again.";
    default: return "Microsoft could not be connected. Please try again.";
  }
}

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

/**
 * Opens Microsoft sign-in in a popup (like Gmail) and resolves with the result: "connected", or an error reason.
 * The popup is opened synchronously on the click so the browser does not block it; it lands on /apps/microsoft, which reports
 * back with postMessage and closes itself. If popups are blocked the whole page is redirected instead.
 */
export function signInWithMicrosoft(features: MicrosoftFeature[], write = false): Promise<string> {
  const width = 520, height = 700;
  const left = Math.max(0, window.screenX + (window.outerWidth - width) / 2);
  const top = Math.max(0, window.screenY + (window.outerHeight - height) / 2);
  const popup = window.open("about:blank", "daksh-microsoft-oauth", `popup=yes,width=${width},height=${height},left=${left},top=${top}`);
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = (fn: () => void) => { if (done) return; done = true; window.removeEventListener("message", onMessage); window.clearInterval(timer); fn(); };
    const onMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || event.data?.type !== "daksh-microsoft-oauth") return;
      finish(() => resolve(String(event.data.result ?? "")));
    };
    window.addEventListener("message", onMessage);
    // The user closed the popup without finishing.
    const timer = window.setInterval(() => { if (popup?.closed) finish(() => resolve("closed")); }, 600);
    api.connectMicrosoft(features, write).then(({ authorizationUrl }) => {
      if (popup && !popup.closed) popup.location.href = authorizationUrl;
      else finish(() => window.location.assign(authorizationUrl)); // popup blocked: fall back to a full redirect
    }, (err) => { popup?.close(); finish(() => reject(err)); });
  });
}

export function signInResultMessage(result: string): string | null {
  return result === "connected" || result === "closed" ? null : microsoftCallbackError(result);
}

/** One sign-in for everything: pick the services, then continue to Microsoft. Adding a service later asks only for its permissions. */
export function MicrosoftConnectCard({ already = [], title = "Connect your Microsoft account", initialError, onConnected }: { already?: MicrosoftFeature[]; title?: string; initialError?: string | null; onConnected?: () => void }) {
  const [chosen, setChosen] = useState<MicrosoftFeature[]>(already.length ? already : ["mail", "calendar", "onedrive"]);
  const [full, setFull] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(initialError ?? null);

  async function connect() {
    setError(null);
    setBusy(true);
    try {
      const result = await signInWithMicrosoft(chosen, full);
      const message = signInResultMessage(result);
      if (message) setError(message);
      else if (result === "connected") onConnected?.();
    } catch (err) {
      setError(errorText(err, "Could not start Microsoft sign-in."));
    } finally { setBusy(false); }
  }

  return (
    <div className={`p-5 ${tileClass}`}>
      <h3 className="text-lg font-bold">{title}</h3>
      <p className="mt-1 text-sm text-muted">Sign in once and choose what Daksha can use. Daksha never sees or stores your Microsoft password.</p>
      <ul className="mt-4 space-y-2">
        {MICROSOFT_SERVICES.map((s) => (
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
        <span><span className="block text-sm font-semibold">Allow Daksha to send mail and change things</span><span className="block text-xs text-muted">Off by default: Daksha can then only read. Read-only is also much easier for your company to approve. You can turn this on later.</span></span>
      </label>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      <button type="button" onClick={() => void connect()} disabled={busy || chosen.length === 0} className={`${buttonClass} mt-4 px-5`}>{busy ? "Opening Microsoft…" : "Continue with Microsoft"}</button>
      <AdminApprovalHelp features={chosen} write={full} />
    </div>
  );
}

/** For a work or school account that Microsoft blocks with "Need admin approval": a one-time link and a ready message for the company's IT administrator. */
export function AdminApprovalHelp({ features, write }: { features: MicrosoftFeature[]; write: boolean }) {
  const [open, setOpen] = useState(false);
  const [domain, setDomain] = useState("");
  const [result, setResult] = useState<{ link: string; message: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  async function make() {
    setError(null); setCopied(false);
    try { setResult(await api.microsoftAdminApproval(domain, features, write)); }
    catch (err) { setError(errorText(err, "Could not make the approval link.")); }
  }
  async function copy() {
    if (!result) return;
    try { await navigator.clipboard.writeText(result.message); setCopied(true); } catch { setError("Copy failed. Select the text and copy it."); }
  }

  return (
    <div className="mt-4 border-t border-line pt-3">
      <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open} className="text-sm font-semibold text-accent hover:underline">
        {open ? "Hide" : "Microsoft says \"Need admin approval\"?"}
      </button>
      {open && (
        <div className="mt-2 space-y-2 text-sm">
          <p className="text-muted">Some companies only let an administrator approve new apps. Send your IT team this one-time link. Once they accept, everyone at your company can sign in.</p>
          <div className="flex flex-wrap gap-2">
            <input value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="Company domain, e.g. example.com (optional)" aria-label="Company domain" className={`${inputClass} min-w-0 flex-1`} />
            <button type="button" onClick={() => void make()} className={secondaryButtonClass}>Make link</button>
          </div>
          {error && <p role="alert" className="text-danger">{error}</p>}
          {result && (
            <div className="space-y-2">
              <textarea readOnly value={result.message} rows={6} aria-label="Message for your administrator" className={`${inputClass} text-xs`} onFocus={(e) => e.currentTarget.select()} />
              <button type="button" onClick={() => void copy()} className={`${buttonClass} px-4`}>{copied ? "Copied" : "Copy message"}</button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ---- Outlook mail ------------------------------------------------------------------------------------

const FOLDERS = [{ id: "inbox", label: "Inbox" }, { id: "sentitems", label: "Sent" }, { id: "drafts", label: "Drafts" }, { id: "deleteditems", label: "Deleted" }];

export function OutlookMailbox({ canWrite = false, showImages = false }: { canWrite?: boolean; showImages?: boolean }) {
  const [folder, setFolder] = useState("inbox");
  const [query, setQuery] = useState("");
  const [applied, setApplied] = useState("");
  const [rows, setRows] = useState<OutlookMessageSummary[] | null>(null);
  const [next, setNext] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<OutlookMessage | null>(null);
  const [composing, setComposing] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (q: string, f: string, skip?: number | null) => {
    setError(null);
    try {
      const page = await api.outlookMessages(q, skip, f);
      setRows((prev) => (skip && prev ? [...prev, ...page.messages] : page.messages));
      setNext(page.nextSkip);
    } catch (err) { setError(errorText(err, "Could not load your Outlook mail.")); setRows((r) => r ?? []); }
  }, []);
  useEffect(() => { const t = setTimeout(() => void load(applied, folder), 0); return () => clearTimeout(t); }, [applied, folder, load]);

  async function openMessage(m: OutlookMessageSummary) {
    setBusy(true);
    try {
      setOpen(await api.outlookMessage(m.id));
      if (!m.isRead && canWrite) { await api.markOutlookRead(m.id, true).catch(() => undefined); setRows((r) => r?.map((x) => (x.id === m.id ? { ...x, isRead: true } : x)) ?? r); }
    } catch (err) { setError(errorText(err, "Could not open that email.")); }
    finally { setBusy(false); }
  }

  return (
    <div className="grid gap-3 lg:grid-cols-[minmax(0,26rem)_minmax(0,1fr)]">
      <section className={`flex min-h-[28rem] flex-col ${tileClass}`}>
        <div className="flex flex-wrap items-center gap-2 border-b border-line p-3">
          <form className="flex min-w-0 flex-1 gap-2" onSubmit={(e: FormEvent) => { e.preventDefault(); setRows(null); setApplied(query); }}>
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search Outlook mail" aria-label="Search Outlook mail" className={`${inputClass} min-w-0 flex-1`} />
          </form>
          {canWrite && <button type="button" onClick={() => setComposing(true)} className={`${buttonClass} px-4`}>Compose</button>}
        </div>
        <div className="flex gap-1 border-b border-line px-3 py-2">
          {FOLDERS.map((f) => (
            <button key={f.id} type="button" onClick={() => { setRows(null); setApplied(""); setQuery(""); setFolder(f.id); }} aria-pressed={folder === f.id}
              className={`rounded-full px-3 py-1 text-xs font-semibold ${folder === f.id ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2"}`}>{f.label}</button>
          ))}
        </div>
        {!canWrite && <p className="px-3 py-2 text-xs text-muted">Read-only. Turn on full access in Manage to send mail.</p>}
        {error && <p role="alert" className="px-3 py-2 text-sm text-danger">{error}</p>}
        {rows === null ? <Loading label="Loading mail" /> : rows.length === 0 ? <EmptyState>No emails here.</EmptyState> : (
          <ul className="min-h-0 flex-1 divide-y divide-line overflow-y-auto">
            {rows.map((m) => (
              <li key={m.id}>
                <button type="button" onClick={() => void openMessage(m)} className={`flex w-full gap-3 px-3 py-2.5 text-left hover:bg-surface-2 ${open?.id === m.id ? "bg-surface-2" : ""}`}>
                  <Avatar name={m.from} />
                  <span className="min-w-0 flex-1">
                    <span className="flex items-baseline justify-between gap-2"><span className={`truncate text-sm ${m.isRead ? "" : "font-bold"}`}>{m.from || "(unknown sender)"}</span><span className="shrink-0 text-xs text-muted">{listDate(m.received)}</span></span>
                    <span className={`block truncate text-sm ${m.isRead ? "text-muted" : "font-semibold"}`}>{m.subject}</span>
                    <span className="block truncate text-xs text-muted">{m.preview}</span>
                  </span>
                </button>
              </li>
            ))}
            {next !== null && <li className="p-3 text-center"><button type="button" onClick={() => void load(applied, folder, next)} className={secondaryButtonClass}>Load more</button></li>}
          </ul>
        )}
      </section>

      <section className={`min-h-[28rem] p-4 ${tileClass}`}>
        {composing ? <OutlookCompose onClose={() => setComposing(false)} onSent={() => { setComposing(false); void load(applied, folder); }} />
          : busy ? <Loading label="Opening email" />
          : open ? <OutlookReader m={open} showImages={showImages} />
          : <EmptyState>Select an email to read it.</EmptyState>}
      </section>
    </div>
  );
}

/** Blocks scripts and, unless the user chose to see them, remote images (which also stops tracking pixels). */
function safeHtml(html: string, showImages: boolean) {
  const csp = `default-src 'none'; style-src 'unsafe-inline'; img-src data: ${showImages ? "https:" : ""}`;
  return `<meta http-equiv="Content-Security-Policy" content="${csp}"><base target="_blank">${html}`;
}

function OutlookReader({ m, showImages }: { m: OutlookMessage; showImages: boolean }) {
  return (
    <article>
      <h3 className="text-lg font-bold">{m.subject}</h3>
      <p className="mt-1 text-sm text-muted">From {m.from}{m.to ? ` · To ${m.to}` : ""}{m.received ? ` · ${new Date(m.received).toLocaleString()}` : ""}</p>
      {/* Email HTML is untrusted: sandboxed with no scripts, forms or top navigation. */}
      {m.isHtml
        ? <iframe title="Email" sandbox="" srcDoc={safeHtml(m.body, showImages)} className="mt-4 h-[28rem] w-full rounded-xl border border-line bg-white" />
        : <pre className="mt-4 whitespace-pre-wrap break-words font-sans text-sm">{m.body}</pre>}
    </article>
  );
}

function OutlookCompose({ onClose, onSent }: { onClose: () => void; onSent: () => void }) {
  const [to, setTo] = useState(""); const [subject, setSubject] = useState(""); const [body, setBody] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  async function send(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError(null);
    try { await api.sendOutlookMessage({ to, subject, body }); onSent(); }
    catch (err) { setError(errorText(err, "Could not send the email.")); setBusy(false); }
  }
  return (
    <form onSubmit={(e) => void send(e)} className="space-y-3">
      <h3 className="text-lg font-bold">New email</h3>
      <input value={to} onChange={(e) => setTo(e.target.value)} placeholder="To" aria-label="To" className={inputClass} />
      <input value={subject} onChange={(e) => setSubject(e.target.value)} placeholder="Subject" aria-label="Subject" className={inputClass} />
      <textarea value={body} onChange={(e) => setBody(e.target.value)} placeholder="Write your message" aria-label="Message" rows={10} className={inputClass} />
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      <div className="flex gap-2"><button type="submit" disabled={busy} className={`${buttonClass} px-5`}>{busy ? "Sending…" : "Send"}</button><button type="button" onClick={onClose} className={secondaryButtonClass}>Discard</button></div>
    </form>
  );
}

// ---- OneDrive ----------------------------------------------------------------------------------------

const sizeText = (b: number) => (b > 1048576 ? `${(b / 1048576).toFixed(1)} MB` : b > 0 ? `${Math.max(1, Math.round(b / 1024))} KB` : "");

export function OneDriveBrowser() {
  const [trail, setTrail] = useState<{ id: string | null; name: string }[]>([{ id: null, name: "OneDrive" }]);
  const [query, setQuery] = useState(""); const [applied, setApplied] = useState("");
  const [listing, setListing] = useState<DriveListing | null>(null);
  const [error, setError] = useState<string | null>(null);
  const folder = trail[trail.length - 1];

  useEffect(() => {
    let live = true;
    const t = setTimeout(() => {
      setError(null);
      api.oneDriveFiles(folder.id, applied).then((l) => live && setListing(l), (err) => { if (live) { setError(errorText(err, "Could not load OneDrive.")); setListing({ folderId: null, folderName: "", items: [] }); } });
    }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [folder.id, applied]);

  return (
    <section className={`p-4 ${tileClass}`}>
      <div className="flex flex-wrap items-center gap-2">
        <nav aria-label="Folder path" className="flex min-w-0 flex-1 flex-wrap items-center gap-1 text-sm">
          {trail.map((t, i) => (
            <span key={`${t.id}-${i}`} className="flex items-center gap-1">
              {i > 0 && <span className="text-muted">/</span>}
              <button type="button" onClick={() => { setListing(null); setApplied(""); setQuery(""); setTrail(trail.slice(0, i + 1)); }} className="font-semibold hover:underline">{t.name}</button>
            </span>
          ))}
        </nav>
        <form className="flex gap-2" onSubmit={(e: FormEvent) => { e.preventDefault(); setListing(null); setApplied(query); }}>
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search OneDrive" aria-label="Search OneDrive" className={inputClass} />
        </form>
      </div>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      {listing === null ? <Loading label="Loading files" /> : listing.items.length === 0 ? <EmptyState>No files here.</EmptyState> : (
        <ul className="mt-3 divide-y divide-line rounded-xl border border-line">
          {listing.items.map((f) => (
            <li key={f.id} className="flex items-center gap-3 px-3 py-2">
              <span aria-hidden>{f.isFolder ? "📁" : "📄"}</span>
              {f.isFolder
                ? <button type="button" onClick={() => { setListing(null); setApplied(""); setQuery(""); setTrail([...trail, { id: f.id, name: f.name }]); }} className="min-w-0 flex-1 truncate text-left text-sm font-semibold hover:underline">{f.name}</button>
                : <a href={f.webUrl ?? "#"} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate text-sm hover:underline">{f.name}</a>}
              <span className="shrink-0 text-xs text-muted">{sizeText(f.size)}</span>
              <span className="hidden shrink-0 text-xs text-muted sm:block">{f.modified ? new Date(f.modified).toLocaleDateString() : ""}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ---- Azure DevOps ------------------------------------------------------------------------------------

export function DevOpsPanel() {
  const [org, setOrg] = useState<string | undefined>();
  const [data, setData] = useState<DevOpsSummary | null>(null);
  const [error, setError] = useState<Error | null>(null);
  useEffect(() => {
    let live = true;
    const t = setTimeout(() => { api.devOpsSummary(org).then((d) => live && (setData(d), setError(null)), (e) => live && setError(e)); }, 0);
    return () => { live = false; clearTimeout(t); };
  }, [org]);
  if (error) return <ErrorState error={error} onRetry={() => { setError(null); setData(null); setOrg((o) => o); }} />;
  if (!data) return <Loading label="Loading Azure DevOps" />;
  return (
    <section className={`p-4 ${tileClass}`}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div><h3 className="text-lg font-bold">My work items</h3><p className="text-xs text-muted">{data.organization ? `${data.projects.length} projects in ${data.organization}` : "No Azure DevOps organization found for this account."}</p></div>
        {data.organizations.length > 1 && <Dropdown className="w-56" value={data.organization ?? ""} options={data.organizations.map((o) => ({ value: o, label: o }))} onChange={(v) => { setData(null); setOrg(v); }} />}
      </div>
      {data.workItems.length === 0 ? <EmptyState>Nothing is assigned to you.</EmptyState> : (
        <ul className="mt-3 divide-y divide-line rounded-xl border border-line">
          {data.workItems.map((w) => (
            <li key={w.id}>
              <a href={w.url} target="_blank" rel="noreferrer" className="flex items-center gap-3 px-3 py-2 hover:bg-surface-2">
                <span className="ai-mono shrink-0 text-xs text-muted">#{w.id}</span>
                <span className="min-w-0 flex-1 truncate text-sm font-semibold">{w.title}</span>
                <span className="shrink-0 rounded-full bg-surface-2 px-2 py-0.5 text-[11px] font-semibold">{w.state}</span>
                <span className="hidden shrink-0 text-xs text-muted sm:block">{w.type} · {w.project}</span>
              </a>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
