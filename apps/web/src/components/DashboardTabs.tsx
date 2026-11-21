"use client";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import { AdSlots } from "@/components/AdSlots";
import { AssistantChat } from "@/components/AssistantChat";
import { DashboardContent } from "@/components/DashboardPage";
import { ErrorState } from "@/components/ui";
import { api, ApiError, type DashboardTab, type DashboardWidget } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { announceDashboardsChanged, ASK_TAB, OPEN_TAB_EVENT, TAB_KEY } from "@/lib/dashboardTabsState";

export { TAB_KEY, ASK_TAB };
const MAX_TABS = 12;
const WIDGET_LABELS: Record<DashboardWidget, string> = { command: "Command Centre", apps: "App tiles" };

const subscribeNone = () => () => {};
const WIDE = "(min-width: 768px)";
const subscribeWide = (cb: () => void) => { const m = window.matchMedia?.(WIDE); m?.addEventListener("change", cb); return () => m?.removeEventListener("change", cb); };
const readWide = () => window.matchMedia?.(WIDE).matches ?? false;
const readSlot = () => document.getElementById("topbar-slot");
function readTab(): string | null { try { return localStorage.getItem(TAB_KEY); } catch { return null; } }

/** The dashboard page: an Ask tab (a chat box, first and always there), then the person's own named dashboards, and a + to add more. */
export function DashboardTabs() {
  const { state, reload } = useApi(() => api.dashboards());
  const [tabs, setTabs] = useState<DashboardTab[] | null>(null);
  const [seen, setSeen] = useState<unknown>(null);
  if (state.status === "ready" && state.data !== seen) { setSeen(state.data); setTabs(state.data); }

  const remembered = useSyncExternalStore(subscribeNone, readTab, () => null);
  const [picked, setPicked] = useState<string | null>(null);
  const wanted = picked ?? remembered ?? ASK_TAB;
  // A remembered tab that no longer exists falls back to Ask.
  const current = wanted === ASK_TAB || tabs?.some((t) => t.id === wanted) ? wanted : ASK_TAB;
  const tab = tabs?.find((t) => t.id === current) ?? null;

  const [renaming, setRenaming] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [menuFor, setMenuFor] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const renameBox = useRef<HTMLInputElement>(null);
  useEffect(() => { if (renaming) renameBox.current?.select(); }, [renaming]);

  // The Ask page is exactly one screen tall on desktop (the conversation scrolls inside its own box), so the page itself never scrolls.
  const onAsk = current === ASK_TAB;
  useEffect(() => {
    if (!onAsk || typeof window === "undefined" || window.innerWidth < 768) return;
    const html = document.documentElement;
    const before = html.style.overflowY;
    html.style.overflowY = "hidden";
    return () => { html.style.overflowY = before; };
  }, [onAsk]);

  // The sidebar can ask for a particular tab ("New chat", or one of the listed dashboards).
  useEffect(() => {
    const onOpen = (e: Event) => { const id = (e as CustomEvent<string>).detail; if (typeof id === "string") { setPicked(id); setMenuFor(null); setError(null); } };
    window.addEventListener(OPEN_TAB_EVENT, onOpen);
    return () => window.removeEventListener(OPEN_TAB_EVENT, onOpen);
  }, []);


  function select(id: string) {
    setPicked(id); setMenuFor(null); setError(null);
    try { localStorage.setItem(TAB_KEY, id); } catch { /* storage unavailable */ }
  }
  const fail = (e: unknown, fallback: string) => setError(e instanceof ApiError ? e.message : fallback);
  const replace = (next: DashboardTab) => { setTabs((all) => all?.map((t) => (t.id === next.id ? next : t)) ?? all); announceDashboardsChanged(); };

  async function add() {
    setBusy(true); setError(null);
    try {
      const created = await api.createDashboard();
      setTabs((all) => [...(all ?? []), created]);
      announceDashboardsChanged();
      select(created.id);
      setRenaming(created.id); setDraft(created.name); // name it straight away
    } catch (e) { fail(e, "Could not add a dashboard."); }
    finally { setBusy(false); }
  }

  async function commitRename(t: DashboardTab) {
    const name = draft.trim();
    setRenaming(null);
    if (!name || name === t.name) return;
    replace({ ...t, name }); // show it at once; the server's cleaned-up version replaces it
    try { replace(await api.updateDashboard(t.id, { name })); }
    catch (e) { replace(t); fail(e, "Could not rename the dashboard."); }
  }

  async function toggleWidget(t: DashboardTab, widget: DashboardWidget) {
    const widgets = t.widgets.includes(widget) ? t.widgets.filter((w) => w !== widget) : [...t.widgets, widget];
    replace({ ...t, widgets });
    try { replace(await api.updateDashboard(t.id, { widgets })); }
    catch (e) { replace(t); fail(e, "Could not change what this dashboard shows."); }
  }

  async function remove(t: DashboardTab) {
    setMenuFor(null);
    if (!window.confirm(`Delete the dashboard “${t.name}”? Its blocks are not deleted, only this view of them.`)) return;
    try {
      await api.deleteDashboard(t.id);
      setTabs((all) => all?.filter((x) => x.id !== t.id) ?? all);
      announceDashboardsChanged();
      select(ASK_TAB);
    } catch (e) { fail(e, "Could not delete the dashboard."); }
  }

  const tabClass = (active: boolean) => `flex items-center gap-1 rounded-full px-4 py-2 text-sm font-semibold transition-colors ${active ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2 hover:text-text"}`;

  // On wide screens the tabs live in the top bar; on phones (no top bar) they stay above the content.
  const slot = useSyncExternalStore(subscribeNone, readSlot, () => null);
  const wide = useSyncExternalStore(subscribeWide, readWide, () => false);
  const inBar = !!slot && wide;

  const strip = (
      <div role="tablist" aria-label="Dashboards" className={`flex flex-wrap items-center gap-1.5 ${inBar ? "" : "rounded-[1.25rem] border border-line bg-surface p-1.5 shadow-[var(--shadow)]"}`}>
        <button type="button" role="tab" aria-selected={current === ASK_TAB} id="tab-ask" aria-controls="panel-ask" onClick={() => select(ASK_TAB)} className={tabClass(current === ASK_TAB)}>
          <span aria-hidden>✨</span> Ask
        </button>
        {tabs?.map((t) => {
          const active = current === t.id;
          return (
            <div key={t.id} className="relative">
              {renaming === t.id ? (
                <input ref={renameBox} value={draft} onChange={(e) => setDraft(e.target.value)} aria-label="Dashboard name" maxLength={40}
                  onKeyDown={(e) => { if (e.key === "Enter") void commitRename(t); if (e.key === "Escape") setRenaming(null); }} onBlur={() => void commitRename(t)}
                  className="field-in-pill w-40 rounded-full border border-accent bg-surface px-3 py-2 text-sm font-semibold text-text" />
              ) : (
                <div className={tabClass(active)}>
                  <button type="button" role="tab" aria-selected={active} id={`tab-${t.id}`} aria-controls={`panel-${t.id}`} onClick={() => select(t.id)} onDoubleClick={() => { select(t.id); setRenaming(t.id); setDraft(t.name); }} title="Double-click to rename" className="max-w-[12rem] truncate">{t.name}</button>
                  {active && <button type="button" onClick={() => setMenuFor(menuFor === t.id ? null : t.id)} aria-label={`Options for ${t.name}`} aria-expanded={menuFor === t.id} className="-mr-2 rounded-full px-2 opacity-80 hover:opacity-100">⋯</button>}
                </div>
              )}
              {menuFor === t.id && (<>
                <button type="button" aria-label="Close menu" onClick={() => setMenuFor(null)} className="fixed inset-0 z-10 cursor-default" />
                <div role="menu" className="absolute left-0 top-full z-20 mt-1 w-56 rounded-xl border border-line bg-surface p-1 text-text shadow-[var(--shadow)]">
                  <button role="menuitem" className="block w-full rounded-lg px-3 py-2 text-left text-sm hover:bg-surface-2" onClick={() => { setMenuFor(null); setRenaming(t.id); setDraft(t.name); }}>Rename</button>
                  <p className="px-3 pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Shows</p>
                  {(Object.keys(WIDGET_LABELS) as DashboardWidget[]).map((w) => (
                    <label key={w} className="flex cursor-pointer items-center gap-2 rounded-lg px-3 py-2 text-sm hover:bg-surface-2">
                      <input type="checkbox" checked={t.widgets.includes(w)} onChange={() => void toggleWidget(t, w)} />{WIDGET_LABELS[w]}
                    </label>
                  ))}
                  <hr className="my-1 border-line" />
                  <button role="menuitem" disabled={(tabs?.length ?? 0) <= 1} className="block w-full rounded-lg px-3 py-2 text-left text-sm text-danger hover:bg-surface-2 disabled:opacity-40" onClick={() => void remove(t)}>Delete dashboard</button>
                </div>
              </>)}
            </div>
          );
        })}
        {state.status === "loading" && !tabs && <span role="status" className="px-3 text-xs text-muted">Loading dashboards…</span>}
        <button type="button" onClick={() => void add()} disabled={busy || (tabs?.length ?? 0) >= MAX_TABS} aria-label="New dashboard" title="New dashboard" className="ml-auto flex items-center gap-1.5 rounded-full border border-dashed border-line px-3.5 py-2 text-sm font-semibold text-muted hover:border-solid hover:text-text disabled:opacity-50">
          <span aria-hidden>＋</span><span className="hidden sm:inline">New dashboard</span>
        </button>
      </div>
  );

  return (
    <div className="flex flex-1 flex-col gap-4">
      {inBar ? createPortal(strip, slot) : strip}

      {error && <p role="alert" className="rounded-2xl bg-danger-bg px-4 py-2 text-sm text-danger">{error}</p>}
      {state.status === "error" && !tabs && <ErrorState error={state.error} onRetry={reload} />}

      {current === ASK_TAB ? (
        <div role="tabpanel" id="panel-ask" aria-labelledby="tab-ask" className="grid w-full flex-1 gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,1fr)]">
          <div className="flex min-w-0 flex-col"><AssistantChat tall /></div>
          <AdSlots />
        </div>
      ) : tab ? (
        <div role="tabpanel" id={`panel-${tab.id}`} aria-labelledby={`tab-${tab.id}`}><DashboardContent key={tab.id} widgets={tab.widgets} /></div>
      ) : null}
    </div>
  );
}
