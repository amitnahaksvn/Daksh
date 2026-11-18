"use client";
import { useCallback, useEffect, useState } from "react";
import { buttonClass, EmptyState, ErrorState, Loading } from "@/components/ui";
import { gmailControl, type GmailAccount } from "@/lib/gmailControl";
import { AccountsTab, FollowUpsTab, GmailSettingsTab, RulesTab } from "@/components/gmail-control/WorkTabs";
import { AiTab, AttachmentsTab, NotificationsTab, RepliesTab, SyncTab } from "@/components/gmail-control/ConfigTabs";
import { SecurityTab } from "@/components/gmail-control/SecurityTab";
import { Banner, FieldErrors, SaveBar, errText, useGmailConfig } from "@/components/gmail-control/parts";

export const CONTROL_TABS = [
  { id: "accounts", label: "Accounts" }, { id: "sync", label: "Sync & inbox" }, { id: "ai", label: "AI intelligence" }, { id: "rules", label: "Rules" },
  { id: "replies", label: "Replies" }, { id: "followups", label: "Follow-ups" }, { id: "notifications", label: "Notifications" }, { id: "attachments", label: "Attachments" },
  { id: "settings", label: "Gmail settings" }, { id: "security", label: "Security" },
] as const;
export type ControlTabId = (typeof CONTROL_TABS)[number]["id"];
type TabId = ControlTabId;
/** Tabs that edit the saved per-account configuration (and so show the Save bar). */
const CONFIG_TABS: TabId[] = ["sync", "ai", "replies", "followups", "notifications", "attachments", "security"];

/**
 * Every Gmail setting that lives in the Daksha database or in Gmail itself. It is hosted by the Manage popup (GmailManagePanel), which is the
 * single place to change Gmail behaviour. When `tab` is passed the popup owns the tab strip; otherwise it draws its own.
 */
export function GmailControlCentre({ tab: controlledTab, onTabChange, hideTabs, onDirtyChange, onAccountsChanged }: {
  tab?: ControlTabId; onTabChange?: (t: ControlTabId) => void; hideTabs?: boolean; onDirtyChange?: (dirty: boolean) => void; onAccountsChanged?: () => void;
} = {}) {
  const [accounts, setAccounts] = useState<GmailAccount[] | null>(null);
  const [loadError, setLoadError] = useState<Error | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [ownTab, setOwnTab] = useState<TabId>("accounts");
  const tab = controlledTab ?? ownTab;
  const setTab = (t: TabId) => { setOwnTab(t); onTabChange?.(t); };
  const loadAccounts = useCallback(() => {
    gmailControl.accounts().then((a) => {
      setAccounts(a);
      setSelected((cur) => (cur && a.some((x) => x.id === cur) ? cur : (a.find((x) => x.isDefault) ?? a[0])?.id ?? null));
    }, (e) => setLoadError(e instanceof Error ? e : new Error(errText(e))));
  }, []);
  useEffect(() => { loadAccounts(); }, [loadAccounts]);

  const editor = useGmailConfig(selected);
  useEffect(() => { onDirtyChange?.(editor.dirty); }, [editor.dirty, onDirtyChange]);

  function switchTab(next: TabId) {
    if (editor.dirty && CONFIG_TABS.includes(tab) && !CONFIG_TABS.includes(next) && !window.confirm("You have unsaved changes. Leave without saving?")) return;
    setTab(next);
  }
  function switchAccount(id: string) {
    if (editor.dirty && !window.confirm("You have unsaved changes. Switch account without saving?")) return;
    setSelected(id);
  }

  if (loadError) return <ErrorState error={loadError} onRetry={() => { setLoadError(null); loadAccounts(); }} />;
  if (!accounts) return <Loading label="Loading Gmail" />;
  if (accounts.length === 0) {
    return (
      <EmptyState>
        <p className="mb-3">No Gmail account is connected yet.</p>
        <button className={`${buttonClass} px-5 py-2.5 text-sm`} onClick={async () => { try { window.location.assign((await gmailControl.connect()).authorizationUrl); } catch { /* surfaced on the Apps page */ } }}>Connect Gmail</button>
      </EmptyState>
    );
  }

  const account = accounts.find((a) => a.id === selected) ?? accounts[0];
  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center gap-3">
        <label className="flex items-center gap-2 text-sm font-semibold">Account
          <select aria-label="Gmail account" className="rounded-2xl border border-line bg-surface-2 px-3 py-2 text-sm" value={account.id} onChange={(e) => switchAccount(e.target.value)}>
            {accounts.map((a) => <option key={a.id} value={a.id}>{a.nickname || a.email}{a.isDefault ? " (default)" : ""}</option>)}
          </select>
        </label>
      </div>
      {!hideTabs && <div role="tablist" aria-label="Gmail settings" className="mb-4 flex gap-1 overflow-x-auto border-b border-line">
        {CONTROL_TABS.map((t) => (
          <button key={t.id} role="tab" aria-selected={tab === t.id} onClick={() => switchTab(t.id)}
            className={`shrink-0 border-b-2 px-4 py-2 text-sm font-semibold transition-colors ${tab === t.id ? "border-accent text-text" : "border-transparent text-muted hover:text-text"}`}>{t.label}</button>
        ))}
      </div>}

      {CONFIG_TABS.includes(tab) && (
        <>
          {editor.error && <Banner tone="error">{editor.error}</Banner>}
          <FieldErrors errors={editor.fieldErrors} />
        </>
      )}
      {tab === "accounts" && <AccountsTab accounts={accounts} reload={() => { loadAccounts(); onAccountsChanged?.(); }} selected={account.id} onSelect={switchAccount} />}
      {tab === "rules" && <RulesTab accountId={account.id} />}
      {tab === "settings" && <GmailSettingsTab accountId={account.id} />}
      {CONFIG_TABS.includes(tab) && (editor.loading || !editor.config ? <Loading label="Loading settings" /> : (
        <>
          {tab === "sync" && <SyncTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          {tab === "ai" && <AiTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          {tab === "replies" && <RepliesTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          {tab === "followups" && <FollowUpsTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          {tab === "notifications" && <NotificationsTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          {tab === "attachments" && <AttachmentsTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          {tab === "security" && <SecurityTab cfg={editor.config} update={editor.update} accountId={account.id} />}
          <SaveBar dirty={editor.dirty} saving={editor.saving} notice={editor.notice} onSave={editor.save} onDiscard={editor.discard} />
        </>
      ))}
    </div>
  );
}
