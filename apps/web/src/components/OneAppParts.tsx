"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { BrandTile } from "@/components/integrations";
import { Modal } from "@/components/Modal";
import { buttonClass, ErrorState, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { api, ApiError, type DashboardApp, type OneAppKey, type OneAppStatus } from "@/lib/api";
import { useApi } from "@/lib/useApi";

/** What the shared GitHub / Notion / Discord / Spotify screens need to know about one app. */
export type OneAppOption = { id: string; label: string; hint: string; granted: (status: OneAppStatus) => boolean };
export type OneAppSettingRow = { key: string; label: string; hint?: string; type: "bool" | "enum" | "count" | "text"; options?: { value: string; label: string }[]; noun?: string; placeholder?: string };
export type OneAppTab = { id: string; label: string; sections: { title: string; hint?: string; rows: OneAppSettingRow[] }[] };
export type OneAppConfig = {
  key: OneAppKey; name: string; icon: string; color: string; pageHref: string; account: string;
  connectTitle: string; connectBlurb: string; connectLabel: string;
  writeLabel: string; writeHint: string;
  /** True when turning full access on needs the provider's sign-in again (GitHub, Spotify); false when it is only Daksha's own switch. */
  writeNeedsSignIn: boolean;
  options?: OneAppOption[];
  tabs: OneAppTab[];
  setupDoc: string;
  /** Replaces the sign-in card (Discord asks for a bot token instead). */
  connect?: (onConnected: () => void) => ReactNode;
  /** Extra controls on the General tab (for example Discord's invite link). */
  extraGeneral?: (status: OneAppStatus, reload: () => void) => ReactNode;
};

const errorText = (err: unknown, fallback: string) => (err instanceof ApiError ? err.message : fallback);

export function oneAppCallbackError(name: string, reason: string | null) {
  switch (reason) {
    case "consent_denied": return `${name} sign-in was cancelled.`;
    case "oauth_not_configured": return `${name} sign-in is not configured on the API yet.`;
    case "token_exchange": return `${name} did not accept the sign-in. Please try again.`;
    case "token_missing": return `${name} did not return an access token. Please try again.`;
    case "profile_lookup": return `Daksha could not read your ${name} profile.`;
    case "unavailable": return `${name} could not be reached. Please try again.`;
    default: return `${name} could not be connected. Please try again.`;
  }
}

/**
 * Opens the provider's sign-in in a popup and resolves with "connected", "closed" or an error reason.
 * The popup lands on the app's own page, which reports back with postMessage and closes itself. If popups are blocked the page is redirected.
 */
export function signInOneApp(key: OneAppKey, params: Record<string, string | boolean> = {}): Promise<string> {
  const width = 560, height = 720;
  const left = Math.max(0, window.screenX + (window.outerWidth - width) / 2);
  const top = Math.max(0, window.screenY + (window.outerHeight - height) / 2);
  const popup = window.open("about:blank", `daksh-${key}-oauth`, `popup=yes,width=${width},height=${height},left=${left},top=${top}`);
  return new Promise((resolve, reject) => {
    let done = false;
    const finish = (fn: () => void) => { if (done) return; done = true; window.removeEventListener("message", onMessage); window.clearInterval(timer); fn(); };
    const onMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || event.data?.type !== "daksh-app-oauth" || event.data?.key !== key) return;
      finish(() => resolve(String(event.data.result ?? "")));
    };
    window.addEventListener("message", onMessage);
    const timer = window.setInterval(() => { if (popup?.closed) finish(() => resolve("closed")); }, 600);
    api.connectOneApp(key, params).then(({ authorizationUrl }) => {
      if (popup && !popup.closed) popup.location.href = authorizationUrl;
      else finish(() => window.location.assign(authorizationUrl));
    }, (err) => { popup?.close(); finish(() => reject(err)); });
  });
}

/** The sign-in card: what the app can do, optional extra permissions, and a button that opens the provider's sign-in. */
export function OneAppConnectCard({ config, initialError, onConnected }: { config: OneAppConfig; initialError?: string | null; onConnected?: () => void }) {
  const [full, setFull] = useState(false);
  const [chosen, setChosen] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(initialError ?? null);

  async function connect() {
    setError(null); setBusy(true);
    try {
      const params: Record<string, string | boolean> = { write: full };
      for (const id of chosen) params[id] = true;
      const result = await signInOneApp(config.key, params);
      if (result === "connected") onConnected?.();
      else if (result !== "closed") setError(oneAppCallbackError(config.name, result));
    } catch (err) { setError(errorText(err, `Could not start ${config.name} sign-in.`)); }
    finally { setBusy(false); }
  }

  return (
    <div className={`p-5 ${tileClass}`}>
      <h3 className="text-lg font-bold">{config.connectTitle}</h3>
      <p className="mt-1 text-sm text-muted">{config.connectBlurb}</p>
      {config.options?.map((o) => (
        <label key={o.id} className="mt-3 flex cursor-pointer items-start gap-3 rounded-xl border border-line p-3">
          <input type="checkbox" className="mt-1" checked={chosen.includes(o.id)} onChange={(e) => setChosen((c) => (e.target.checked ? [...c, o.id] : c.filter((x) => x !== o.id)))} />
          <span><span className="block text-sm font-semibold">{o.label}</span><span className="block text-xs text-muted">{o.hint}</span></span>
        </label>
      ))}
      <label className="mt-3 flex cursor-pointer items-start gap-3 rounded-xl border border-line p-3">
        <input type="checkbox" className="mt-1" checked={full} onChange={(e) => setFull(e.target.checked)} />
        <span><span className="block text-sm font-semibold">{config.writeLabel}</span><span className="block text-xs text-muted">{config.writeHint} Off by default; you can change it later in Manage.</span></span>
      </label>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      <button type="button" onClick={() => void connect()} disabled={busy} className={`${buttonClass} mt-4 px-5`}>{busy ? `Opening ${config.name}…` : config.connectLabel}</button>
    </div>
  );
}

/**
 * The page wrapper: finishes the sign-in popup, shows the sign-in card until connected, then the app's own content
 * with a Manage button. `manage` renders the Manage panel inside the popup.
 */
export function OneAppShell({ config, manage, children }: { config: OneAppConfig; manage: (onChanged: () => void) => ReactNode; children: (status: OneAppStatus) => ReactNode }) {
  const { state, reload } = useApi(() => api.oneAppStatus(config.key));
  const [open, setOpen] = useState(false);
  const params = typeof window === "undefined" ? null : new URLSearchParams(window.location.search);
  const feedbackError = params?.get("error") ?? null;
  // In the sign-in popup: the provider sent the browser here. Tell the opener the result, then close.
  const feedback = params?.get("app") === config.key ? (params?.get("connected") ? "connected" : params?.get("error")) : null;
  const inPopup = typeof window !== "undefined" && Boolean(window.opener) && window.opener !== window;
  useEffect(() => {
    if (!feedback || !inPopup) return;
    try { window.opener.postMessage({ type: "daksh-app-oauth", key: config.key, result: feedback }, window.location.origin); } catch { return; }
    window.close();
  }, [feedback, inPopup, config.key]);
  if (inPopup && feedback) return <Loading label={`Finishing ${config.name} sign-in`} />;

  if (state.status === "loading") return <Loading label={`Loading ${config.name}`} />;
  if (state.status === "error") return <ErrorState error={state.error} onRetry={reload} />;
  const status = state.data;
  if (!status.connected) return (config.connect ? config.connect(reload) : <OneAppConnectCard config={config} onConnected={reload} initialError={feedbackError ? oneAppCallbackError(config.name, feedbackError) : null} />);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 flex-1 truncate text-sm font-semibold">{status.accountName}{status.write ? "" : " · read-only"}</span>
        <button type="button" onClick={() => setOpen(true)} className={secondaryButtonClass}>Manage</button>
      </div>
      {children(status)}
      <Modal wide fixedHeight open={open} onClose={() => { setOpen(false); reload(); }}>{manage(reload)}</Modal>
    </div>
  );
}

/** The dashboard tile for GitHub, Notion, Discord and Spotify: the sections the server prepared from the person's settings. */
export function OneAppDashboardCard({ app, config, manage, onChanged }: { app: DashboardApp; config: OneAppConfig; manage: (onChanged: () => void) => ReactNode; onChanged?: () => void }) {
  const [open, setOpen] = useState(false);
  const { state, reload } = useApi(() => api.oneAppDashboard(config.key));
  return (
    <div className={`min-h-full p-4 ${tileClass}`}>
      <header className="flex items-start gap-3">
        <BrandTile icon={app.icon} color={app.color} size={40} />
        <div className="min-w-0 flex-1">
          <h2 className="text-xl font-bold leading-tight">{config.name}</h2>
          <p className="truncate text-xs text-muted sm:text-sm">{app.accountIdentifier ?? config.account}</p>
        </div>
        <Link href={config.pageHref} className="self-center text-xs font-semibold text-accent hover:underline">Open</Link>
        <button type="button" onClick={() => setOpen(true)} aria-label={`Manage ${config.name}`} className="rounded-xl border border-line p-2 text-muted hover:bg-surface-2">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" className="h-4.5 w-4.5"><path d="M12 15a3 3 0 100-6 3 3 0 000 6zM19 12l2-1-2-4-2 .5-1.5-1L15 4h-4l-.5 2.5-1.5 1L7 7l-2 4 2 1v2l-2 1 2 4 2-.5 1.5 1L11 20h4l.5-2.5 1.5-1 2 .5 2-4-2-1z" /></svg>
        </button>
      </header>
      <div className="mt-3">
        {state.status === "loading" && <Loading label={`Loading ${config.name}`} />}
        {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
        {state.status === "ready" && (
          <div className="space-y-3">
            {state.data.errors.map((e) => <p key={e} role="alert" className="text-xs text-danger">{e}</p>)}
            {state.data.sections.map((s) => (
              <section key={s.title} className="min-w-0 rounded-xl border border-line bg-surface-2 p-3">
                <div className="mb-1.5 flex items-center justify-between"><h3 className="text-xs font-bold uppercase tracking-wide text-muted">{s.title}</h3>{s.href && <Link href={s.href} className="text-xs font-semibold text-accent hover:underline">Open</Link>}</div>
                {s.items.length === 0 ? <p className="text-xs text-muted">Nothing to show.</p> : (
                  <ul className="space-y-1.5">{s.items.map((i) => (
                    <li key={i.id} className="min-w-0 text-sm">
                      {i.url ? <a href={i.url} target="_blank" rel="noreferrer" className="block truncate font-semibold hover:underline">{i.title}</a> : <span className="block truncate font-semibold">{i.title}</span>}
                      {(i.subtitle || i.badge) && <span className="block truncate text-xs text-muted">{i.badge ? `${i.badge} · ` : ""}{i.subtitle}</span>}
                    </li>))}</ul>
                )}
              </section>
            ))}
          </div>
        )}
      </div>
      <Modal wide fixedHeight open={open} onClose={() => { setOpen(false); reload(); }}>{manage(() => { reload(); onChanged?.(); })}</Modal>
    </div>
  );
}

/** A plain list of linked rows used by the app pages. */
export function Rows({ children }: { children: ReactNode }) {
  return <ul className="divide-y divide-line rounded-xl border border-line">{children}</ul>;
}

export const when = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString([], { month: "short", day: "numeric" }) : "");
