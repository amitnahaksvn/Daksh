"use client";
import { useCallback, useEffect, useState } from "react";
import { buttonClass, EmptyState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";
import { ApiError } from "@/lib/api";
import { gmailControl, type AccountSettings, type FollowUp, type GmailAccount, type GmailConfig, type GmailRule, type RuleRun } from "@/lib/gmailControl";
import { Banner, errText, Field, FieldErrors, ScopeUpgrade, Section, StatusBadge, Switch, TextField } from "./parts";
import { FollowUpSettings } from "./ConfigTabs";

// ───────────────────────────── Accounts ─────────────────────────────
export function AccountsTab({ accounts, reload, selected, onSelect }: { accounts: GmailAccount[]; reload: () => void; selected: string; onSelect: (id: string) => void }) {
  const [msg, setMsg] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  async function run(id: string, fn: () => Promise<unknown>, ok: string) {
    setBusy(id); setMsg(null);
    try { await fn(); setMsg({ tone: "ok", text: ok }); reload(); } catch (e) { setMsg({ tone: "error", text: errText(e) }); } finally { setBusy(null); }
  }
  async function connect() {
    try { const { authorizationUrl } = await gmailControl.connect(); window.location.assign(authorizationUrl); } catch (e) { setMsg({ tone: "error", text: errText(e) }); }
  }
  return (
    <>
      {msg && <Banner tone={msg.tone}>{msg.text}</Banner>}
      {accounts.map((a) => (
        <AccountCard key={`${a.id}:${a.nickname}`} a={a} selected={selected === a.id} busy={busy === a.id} onSelect={() => onSelect(a.id)}
          onRename={(nickname) => run(a.id, () => gmailControl.updateAccount(a.id, { nickname }), "Nickname saved.")}
          onEnabled={(enabled) => run(a.id, () => gmailControl.updateAccount(a.id, { enabled }), enabled ? "Account enabled." : "Account disabled.")}
          onDefault={() => run(a.id, () => gmailControl.updateAccount(a.id, { isDefault: true }), "Default sending account updated.")}
          onTest={() => run(a.id, async () => { const r = await gmailControl.testAccount(a.id); setMsg({ tone: "ok", text: `Connection works: ${r.email} (${r.messagesTotal.toLocaleString()} messages).` }); }, "")}
          onReconnect={connect}
          onDisconnect={() => { if (window.confirm(`Disconnect ${a.email}? Daksha will revoke its access to this account and delete the synced data it stored.`)) void run(a.id, () => gmailControl.disconnect(a.id), "Account disconnected."); }} />
      ))}
      <button className={`${buttonClass} px-5 py-2.5 text-sm`} onClick={connect}>Connect another Gmail account</button>
      <p className="mt-2 text-xs text-muted">Personal Gmail and Google Workspace accounts both work. Each account has its own settings, rules and history.</p>
    </>
  );
}

function AccountCard({ a, selected, busy, onSelect, onRename, onEnabled, onDefault, onTest, onReconnect, onDisconnect }: {
  a: GmailAccount; selected: boolean; busy: boolean; onSelect: () => void; onRename: (n: string) => void; onEnabled: (v: boolean) => void; onDefault: () => void; onTest: () => void; onReconnect: () => void; onDisconnect: () => void;
}) {
  const [nick, setNick] = useState(a.nickname);
  const needsReconnect = ["ConnectionExpired", "AuthenticationFailed", "PermissionRevoked", "Disconnected"].includes(a.status);
  return (
    <section className={`mb-3 p-4 ${tileClass} ${selected ? "ring-2 ring-accent" : ""}`} aria-label={`Account ${a.email}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <p className="truncate text-base font-bold">{a.nickname || a.email}</p>
          <p className="truncate text-xs text-muted">{a.nickname ? a.email : ""}{a.isWorkspace ? " · Google Workspace" : ""}{a.isDefault ? " · Default sender" : ""}</p>
        </div>
        <span className="flex items-center gap-2"><StatusBadge status={a.status} /><button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} onClick={onSelect} disabled={selected}>{selected ? "Selected" : "Configure"}</button></span>
      </div>
      {a.statusDetail && <p className="mt-2 text-xs text-danger">{a.statusDetail}</p>}
      <p className="mt-2 text-xs text-muted">
        {a.messagesSynced.toLocaleString()} messages synced · {a.lastSuccessUtc ? `last sync ${new Date(a.lastSuccessUtc).toLocaleString()}` : "never synced"}
        {a.lastFailureUtc && <> · last failure {new Date(a.lastFailureUtc).toLocaleString()}{a.lastError ? `: ${a.lastError}` : ""}</>}
      </p>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <input aria-label={`Nickname for ${a.email}`} className={`${inputClass} !w-44`} placeholder="Nickname" maxLength={60} value={nick} onChange={(e) => setNick(e.target.value)} />
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} disabled={busy || nick === a.nickname} onClick={() => onRename(nick)}>Save nickname</button>
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} disabled={busy || a.isDefault || !a.enabled} onClick={onDefault}>Make default sender</button>
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} disabled={busy} onClick={onTest}>Test connection</button>
        {needsReconnect && <button className={`${buttonClass} px-3 py-1.5 text-xs`} onClick={onReconnect}>Reconnect</button>}
        <label className="ml-auto flex items-center gap-2 text-xs font-semibold"><input type="checkbox" aria-label={`Enable ${a.email}`} checked={a.enabled} disabled={busy} onChange={(e) => onEnabled(e.target.checked)} />Enabled</label>
        <button className="text-xs font-semibold text-danger underline" onClick={onDisconnect}>Disconnect</button>
      </div>
    </section>
  );
}

// ───────────────────────────── Rules ─────────────────────────────
const FIELD_LABELS: Record<string, string> = {
  from: "Sender address", fromDomain: "Sender domain", to: "Recipient", cc: "CC", subject: "Subject", body: "Message body", hasAttachment: "Has attachment", attachmentType: "Attachment type",
  label: "Gmail label", important: "Is important", starred: "Is starred", category: "AI category", urgency: "AI urgency", receivedAfter: "Received after (date)", receivedBefore: "Received before (date)", keyword: "Keyword (subject or body)",
};
const ACTION_LABELS: Record<string, string> = {
  addLabel: "Apply label", createLabel: "Create & apply label", removeLabel: "Remove label", markRead: "Mark as read", markUnread: "Mark as unread", star: "Star", unstar: "Remove star",
  markImportant: "Mark important", archive: "Archive", trash: "Move to trash", forward: "Forward to verified address", createTask: "Create a task", createReminder: "Create a reminder",
  generateDraft: "Generate a reply draft", notify: "Notify me", runWorkflow: "Add to an automation workflow",
};
const NO_VALUE_FIELDS = ["hasAttachment", "important", "starred"];
const NO_VALUE_ACTIONS = ["markRead", "markUnread", "star", "unstar", "markImportant", "archive", "trash", "generateDraft", "notify"];
const blankRule = (): Partial<GmailRule> => ({ name: "", description: "", enabled: true, priority: 100, stopProcessing: false, match: "all", conditions: [{ field: "from", op: "contains", value: "", negate: false }], actions: [{ type: "addLabel", value: "" }], runAutomatically: false });

export function RulesTab({ accountId }: { accountId: string }) {
  const [rules, setRules] = useState<GmailRule[] | null>(null);
  const [destructive, setDestructive] = useState<string[]>(["trash", "forward"]);
  const [editing, setEditing] = useState<Partial<GmailRule> | null>(null);
  const [msg, setMsg] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const load = useCallback(() => gmailControl.rules(accountId).then((r) => { setRules(r.rules); setDestructive(r.destructive); }, (e) => { setRules([]); setMsg({ tone: "error", text: errText(e) }); }), [accountId]);
  useEffect(() => { void load(); }, [load]);
  if (rules === null) return <Loading label="Loading rules" />;
  return (
    <>
      {msg && <Banner tone={msg.tone}>{msg.text}</Banner>}
      <Banner>These are <strong>Daksha rules</strong>: they run inside Daksha on mail it has seen. They&apos;re separate from Gmail&apos;s own filters (see Gmail settings). Rules that trash or forward mail never run automatically.</Banner>
      {editing ? (
        <RuleEditor rule={editing} destructive={destructive} onCancel={() => setEditing(null)} onSaved={() => { setEditing(null); setMsg({ tone: "ok", text: "Rule saved." }); void load(); }} accountId={accountId} />
      ) : (
        <button className={`${buttonClass} mb-3 px-5 py-2.5 text-sm`} onClick={() => setEditing(blankRule())}>New rule</button>
      )}
      {rules.length === 0 && !editing && <EmptyState>No rules yet. Create one to label, archive or act on mail automatically.</EmptyState>}
      {rules.map((r) => <RuleCard key={r.id} rule={r} destructive={destructive} onEdit={() => setEditing(structuredClone(r))} onChanged={load} setMsg={setMsg} />)}
    </>
  );
}

function RuleEditor({ rule, destructive, onCancel, onSaved, accountId }: { rule: Partial<GmailRule>; destructive: string[]; onCancel: () => void; onSaved: () => void; accountId: string }) {
  const [r, setR] = useState<Partial<GmailRule>>(rule);
  const [errs, setErrs] = useState<Record<string, string[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [sample, setSample] = useState({ from: "billing@acme.com", subject: "Invoice 1042", body: "Amount due: $120" });
  const [test, setTest] = useState<{ matched: boolean; reasons: string[]; wouldRun: string[] } | null>(null);
  const hasDestructive = (r.actions ?? []).some((a) => destructive.includes(a.type));
  const set = (m: (x: Partial<GmailRule>) => void) => setR((p) => { const n = structuredClone(p); m(n); return n; });

  async function save() {
    setErrs({}); setError(null);
    try { if (r.id) await gmailControl.updateRule(r as GmailRule); else await gmailControl.createRule(accountId, r); onSaved(); }
    catch (e) { if (e instanceof ApiError) { setErrs(e.fieldErrors); setError(e.message); } else setError("Could not save the rule."); }
  }
  async function runTest() {
    setError(null);
    try {
      setTest(await gmailControl.testRule(r, { from: sample.from, to: "me@example.com", cc: "", subject: sample.subject, body: sample.body, hasAttachment: false, attachmentTypes: [], labels: ["INBOX"], important: false, starred: false, categories: [], urgent: false, date: new Date().toISOString() }));
    } catch (e) { if (e instanceof ApiError) setErrs(e.fieldErrors); setError(errText(e)); }
  }
  return (
    <section className={`mb-4 p-4 ${tileClass}`} aria-label="Rule editor">
      <h3 className="text-base font-bold">{r.id ? "Edit rule" : "New rule"}</h3>
      {error && <Banner tone="error">{error}</Banner>}<FieldErrors errors={errs} />
      <TextField label="Rule name" value={r.name ?? ""} onChange={(v) => set((x) => { x.name = v; })} maxLength={100} />
      <TextField label="Description" value={r.description ?? ""} onChange={(v) => set((x) => { x.description = v; })} maxLength={500} />
      <Field label="When" hint="Combine conditions with AND (all) or OR (any). Tick NOT to invert one.">
        <select aria-label="Match" className={`${inputClass} !w-44`} value={r.match} onChange={(e) => set((x) => { x.match = e.target.value as "all" | "any"; })}>
          <option value="all">All of these (AND)</option><option value="any">Any of these (OR)</option>
        </select>
      </Field>
      {(r.conditions ?? []).map((c, i) => (
        <div key={i} className="mb-2 flex flex-wrap items-center gap-2">
          <label className="flex items-center gap-1 text-xs"><input type="checkbox" aria-label={`NOT condition ${i + 1}`} checked={c.negate} onChange={(e) => set((x) => { x.conditions![i].negate = e.target.checked; })} />NOT</label>
          <select aria-label={`Condition ${i + 1} field`} className={`${inputClass} !w-48`} value={c.field} onChange={(e) => set((x) => { x.conditions![i].field = e.target.value; })}>
            {Object.entries(FIELD_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
          {!NO_VALUE_FIELDS.includes(c.field) && (
            <select aria-label={`Condition ${i + 1} operator`} className={`${inputClass} !w-36`} value={c.op} onChange={(e) => set((x) => { x.conditions![i].op = e.target.value; })}>
              <option value="contains">contains</option><option value="equals">equals</option><option value="startsWith">starts with</option><option value="endsWith">ends with</option>
            </select>
          )}
          {!NO_VALUE_FIELDS.includes(c.field) && <input aria-label={`Condition ${i + 1} value`} className={`${inputClass} !w-48`} value={c.value} onChange={(e) => set((x) => { x.conditions![i].value = e.target.value; })} />}
          <button className="text-xs text-danger underline" onClick={() => set((x) => { x.conditions!.splice(i, 1); })}>Remove</button>
        </div>
      ))}
      <button className={`${secondaryButtonClass} mb-3 px-3 py-1.5 text-xs`} onClick={() => set((x) => { x.conditions!.push({ field: "subject", op: "contains", value: "", negate: false }); })}>Add condition</button>
      <p className="text-sm font-semibold">Then</p>
      {(r.actions ?? []).map((a, i) => (
        <div key={i} className="mb-2 flex flex-wrap items-center gap-2">
          <select aria-label={`Action ${i + 1} type`} className={`${inputClass} !w-60`} value={a.type} onChange={(e) => set((x) => { x.actions![i].type = e.target.value; })}>
            {Object.entries(ACTION_LABELS).map(([v, l]) => <option key={v} value={v}>{l}{destructive.includes(v) ? " ⚠" : ""}</option>)}
          </select>
          {!NO_VALUE_ACTIONS.includes(a.type) && <input aria-label={`Action ${i + 1} value`} className={`${inputClass} !w-56`} value={a.value} placeholder={a.type === "forward" ? "verified@address.com" : "value"} onChange={(e) => set((x) => { x.actions![i].value = e.target.value; })} />}
          <button className="text-xs text-danger underline" onClick={() => set((x) => { x.actions!.splice(i, 1); })}>Remove</button>
        </div>
      ))}
      <button className={`${secondaryButtonClass} mb-3 px-3 py-1.5 text-xs`} onClick={() => set((x) => { x.actions!.push({ type: "markRead", value: "" }); })}>Add action</button>
      <Switch label="Enabled" checked={r.enabled ?? true} onChange={(v) => set((x) => { x.enabled = v; })} />
      <Field label="Priority" hint="Lower numbers run first."><input aria-label="Priority" type="number" className={`${inputClass} !w-24`} value={r.priority ?? 100} onChange={(e) => set((x) => { x.priority = Number(e.target.value); })} /></Field>
      <Switch label="Stop processing further rules" checked={r.stopProcessing ?? false} onChange={(v) => set((x) => { x.stopProcessing = v; })} />
      <Switch label="Run automatically on new mail" hint={hasDestructive ? "Not available: this rule trashes or forwards mail. Run it manually after a preview." : "Needs the Autonomous profile. Off = you run it by hand."} checked={(r.runAutomatically ?? false) && !hasDestructive} disabled={hasDestructive} onChange={(v) => set((x) => { x.runAutomatically = v; })} />
      <div className="mt-3 rounded-2xl bg-surface-2 p-3">
        <p className="text-sm font-semibold">Test against a sample message</p>
        <div className="mt-2 flex flex-wrap gap-2">
          <input aria-label="Sample sender" className={`${inputClass} !w-48`} value={sample.from} onChange={(e) => setSample({ ...sample, from: e.target.value })} />
          <input aria-label="Sample subject" className={`${inputClass} !w-48`} value={sample.subject} onChange={(e) => setSample({ ...sample, subject: e.target.value })} />
          <input aria-label="Sample body" className={`${inputClass} !w-56`} value={sample.body} onChange={(e) => setSample({ ...sample, body: e.target.value })} />
          <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} onClick={runTest}>Test</button>
        </div>
        {test && <div className="mt-2 text-xs"><p className="font-semibold">{test.matched ? `Matches — would run: ${test.wouldRun.join(", ")}` : "Does not match"}</p><ul className="mt-1 list-disc pl-5 text-muted">{test.reasons.map((x, i) => <li key={i}>{x}</li>)}</ul></div>}
      </div>
      <div className="mt-4 flex gap-2"><button className={`${buttonClass} px-5 py-2 text-sm`} onClick={save}>Save rule</button><button className={`${secondaryButtonClass} px-5 py-2 text-sm`} onClick={onCancel}>Cancel</button></div>
    </section>
  );
}

function RuleCard({ rule, destructive, onEdit, onChanged, setMsg }: { rule: GmailRule; destructive: string[]; onEdit: () => void; onChanged: () => void; setMsg: (m: { tone: "ok" | "error"; text: string }) => void }) {
  const [preview, setPreview] = useState<{ scanned: number; matches: { id: string; from: string; subject: string }[] } | null>(null);
  const [runs, setRuns] = useState<RuleRun[] | null>(null);
  const isDestructive = rule.actions.some((a) => destructive.includes(a.type));
  async function act(fn: () => Promise<unknown>, ok?: string) {
    try { await fn(); if (ok) setMsg({ tone: "ok", text: ok }); onChanged(); } catch (e) { setMsg({ tone: "error", text: errText(e) }); }
  }
  async function run() {
    if (!preview || preview.matches.length === 0) return;
    const ids = preview.matches.map((m) => m.id);
    const confirm = !isDestructive || window.confirm(`This rule trashes or forwards mail and will change ${ids.length} message(s). Continue?`);
    if (!confirm) return;
    await act(async () => { const res = await gmailControl.runRule(rule.id, ids, isDestructive); setMsg({ tone: "ok", text: `Ran on ${res.ran} message(s).` }); setPreview(null); });
  }
  return (
    <section className={`mb-3 p-4 ${tileClass}`} aria-label={`Rule ${rule.name}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div><p className="font-bold">{rule.name}</p><p className="text-xs text-muted">{rule.description || `${rule.conditions.length} condition(s) → ${rule.actions.map((a) => ACTION_LABELS[a.type] ?? a.type).join(", ")}`}{rule.runAutomatically ? " · automatic" : " · manual"} · priority {rule.priority}</p></div>
        <label className="flex items-center gap-2 text-xs font-semibold"><input type="checkbox" aria-label={`Enable ${rule.name}`} checked={rule.enabled} onChange={(e) => act(() => gmailControl.setRuleEnabled(rule.id, e.target.checked))} />Enabled</label>
      </div>
      <div className="mt-2 flex flex-wrap gap-2">
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} onClick={onEdit}>Edit</button>
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} onClick={() => act(() => gmailControl.duplicateRule(rule.id), "Rule duplicated (disabled).")}>Duplicate</button>
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} onClick={() => act(async () => setPreview(await gmailControl.previewRule(rule.id)))}>Preview matches</button>
        <button className={`${secondaryButtonClass} px-3 py-1.5 text-xs`} onClick={() => act(async () => setRuns((await gmailControl.ruleRuns(rule.id)).runs))}>History</button>
        <button className="text-xs font-semibold text-danger underline" onClick={() => window.confirm(`Delete "${rule.name}"?`) && act(() => gmailControl.deleteRule(rule.id), "Rule deleted.")}>Delete</button>
      </div>
      {preview && (
        <div className="mt-3 rounded-2xl bg-surface-2 p-3 text-sm">
          <p className="font-semibold">{preview.matches.length} of {preview.scanned} recent messages match.</p>
          <ul className="mt-1 list-disc pl-5 text-xs text-muted">{preview.matches.slice(0, 10).map((m) => <li key={m.id}>{m.subject || "(no subject)"} — {m.from}</li>)}</ul>
          {preview.matches.length > 0 && <button className={`${buttonClass} mt-2 px-4 py-1.5 text-xs`} onClick={run}>Run on these {preview.matches.length}</button>}
        </div>
      )}
      {runs && (runs.length === 0 ? <p className="mt-2 text-xs text-muted">This rule hasn&apos;t run yet.</p> : (
        <ul className="mt-2 divide-y divide-line text-xs">{runs.slice(0, 10).map((x) => <li key={x.id} className="py-1.5">{new Date(x.atUtc).toLocaleString()} · {x.trigger} · {x.status} — {x.applied.join(", ") || "nothing applied"}{x.skipped.length > 0 && <span className="text-muted"> (skipped: {x.skipped.join("; ")})</span>}</li>)}</ul>
      ))}
    </section>
  );
}

// ───────────────────────────── Follow-ups ─────────────────────────────
const STATUS_LABELS: Record<string, string> = {
  AwaitingMyResponse: "Awaiting my response", AwaitingTheirResponse: "Awaiting their response", FollowUpDue: "Follow-up due", FollowUpDrafted: "Follow-up drafted", FollowUpSent: "Follow-up sent", Completed: "Completed", Ignored: "Ignored",
};
export function FollowUpsTab({ accountId, cfg, update }: { accountId: string; cfg: GmailConfig; update: (m: (c: GmailConfig) => void) => void }) {
  const [items, setItems] = useState<FollowUp[] | null>(null);
  const [filter, setFilter] = useState("");
  const [msg, setMsg] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const load = useCallback(() => gmailControl.followUps(accountId, filter || undefined).then((r) => setItems(r.items), () => setItems([])), [accountId, filter]);
  useEffect(() => { void load(); }, [load]);
  async function act(fn: () => Promise<unknown>, ok: string) { try { await fn(); setMsg({ tone: "ok", text: ok }); void load(); } catch (e) { setMsg({ tone: "error", text: errText(e) }); } }
  const snooze = (f: FollowUp, days: number) => act(() => gmailControl.updateFollowUp(f.threadId, accountId, { snoozeUntil: new Date(Date.now() + days * 86_400_000).toISOString() }), `Snoozed for ${days} day(s).`);
  return (
    <>
      <FollowUpSettings cfg={cfg} update={update} />
      <Section title="Conversations to follow up">
        {msg && <Banner tone={msg.tone}>{msg.text}</Banner>}
        <select aria-label="Filter by status" className={`${inputClass} mb-3 !w-64`} value={filter} onChange={(e) => setFilter(e.target.value)}>
          <option value="">All statuses</option>{Object.entries(STATUS_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        {items === null ? <Loading label="Loading follow-ups" /> : items.length === 0 ? <EmptyState>Nothing to follow up. Daksha tracks conversations as it syncs.</EmptyState> : (
          <ul className="divide-y divide-line">
            {items.map((f) => (
              <li key={f.key} className="py-3 text-sm">
                <p className="font-semibold">{f.subject || "(no subject)"}</p>
                <p className="text-xs text-muted">{STATUS_LABELS[f.status] ?? f.status} · {f.counterparty}{f.dueAtUtc ? ` · due ${new Date(f.dueAtUtc).toLocaleDateString()}` : ""}{f.snoozedUntilUtc ? ` · snoozed until ${new Date(f.snoozedUntilUtc).toLocaleDateString()}` : ""}</p>
                <div className="mt-1.5 flex flex-wrap gap-2">
                  <button className={`${secondaryButtonClass} px-3 py-1 text-xs`} onClick={() => act(() => gmailControl.followUpDraft(f.threadId, accountId), "Follow-up draft created in Gmail.")}>Draft follow-up</button>
                  <button className={`${secondaryButtonClass} px-3 py-1 text-xs`} onClick={() => snooze(f, 1)}>Snooze 1d</button>
                  <button className={`${secondaryButtonClass} px-3 py-1 text-xs`} onClick={() => snooze(f, 3)}>Snooze 3d</button>
                  <button className={`${secondaryButtonClass} px-3 py-1 text-xs`} onClick={() => act(() => gmailControl.updateFollowUp(f.threadId, accountId, { status: "Completed" }), "Marked completed.")}>Completed</button>
                  <button className={`${secondaryButtonClass} px-3 py-1 text-xs`} onClick={() => act(() => gmailControl.updateFollowUp(f.threadId, accountId, { status: "Ignored" }), "Ignored.")}>Ignore</button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </>
  );
}

// ───────────────────────────── Gmail account settings ─────────────────────────────
export function GmailSettingsTab({ accountId }: { accountId: string }) {
  const [s, setS] = useState<AccountSettings | null>(null);
  const [err, setErr] = useState<ApiError | null>(null);
  const [msg, setMsg] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const load = useCallback(() => gmailControl.accountSettings(accountId).then((r) => { setS(r); setErr(null); }, (e) => setErr(e instanceof ApiError ? e : new ApiError(0, "Could not load Gmail settings."))), [accountId]);
  useEffect(() => { void load(); }, [load]);
  if (err) return err.code === "scope_upgrade_required" || err.code === "permission_denied"
    ? <>{err.code === "scope_upgrade_required" ? <ScopeUpgrade error={err} feature="settings" /> : <Banner tone="error">{err.message}</Banner>}</>
    : <Banner tone="error">{err.message}</Banner>;
  if (!s) return <Loading label="Loading Gmail settings" />;
  return (
    <>
      {msg && <Banner tone={msg.tone}>{msg.text}</Banner>}
      <VacationCard section={s.vacation.value as Record<string, unknown> | null} onSave={async (v) => { try { await gmailControl.saveVacation(accountId, v); setMsg({ tone: "ok", text: "Vacation responder saved to Gmail." }); void load(); } catch (e) { setMsg({ tone: "error", text: errText(e) }); } }} />
      <SendAsCard section={s.sendAs.value as { sendAs?: { sendAsEmail: string; displayName?: string; signature?: string; isDefault?: boolean }[] } | null} onSave={async (email, p) => { try { await gmailControl.saveSendAs(accountId, email, p); setMsg({ tone: "ok", text: "Sender details saved to Gmail." }); void load(); } catch (e) { setMsg({ tone: "error", text: errText(e) }); } }} />
      <Section title="Read-only in Daksha" hint="Gmail's API lets Daksha view these but changing them needs a restricted Google permission Daksha doesn't ask for, or isn't available through the API.">
        {[["Forwarding addresses", s.forwardingAddresses], ["Auto-forwarding", s.autoForwarding], ["IMAP", s.imap], ["POP", s.pop], ["Display language", s.language]].map(([label, sec]) => (
          <details key={label as string} className="border-b border-line py-2 text-sm"><summary className="cursor-pointer font-semibold">{label as string}</summary>
            {(sec as AccountSettings["imap"]).note && <p className="mt-1 text-xs text-muted">{(sec as AccountSettings["imap"]).note}</p>}
            <pre className="mt-1 max-h-40 overflow-auto rounded-2xl bg-surface-2 p-2 text-xs">{JSON.stringify((sec as AccountSettings["imap"]).value, null, 2)}</pre></details>
        ))}
      </Section>
      <Section title="Not available through the Gmail API">
        <ul className="space-y-2 text-sm">{s.notSupported.map((n) => <li key={n.setting}><strong>{n.setting}.</strong> {n.reason} {n.url && <a className="underline" href={n.url} target="_blank" rel="noreferrer">Open Gmail settings</a>}</li>)}</ul>
      </Section>
      <FiltersAndLabels accountId={accountId} />
    </>
  );
}

function VacationCard({ section, onSave }: { section: Record<string, unknown> | null; onSave: (v: { enableAutoReply: boolean; subject: string; body: string; startDate: string; endDate: string; restrictToContacts: boolean; restrictToDomain: boolean }) => void }) {
  const ms = (v: unknown) => (typeof v === "string" && v ? new Date(Number(v)).toISOString().slice(0, 10) : "");
  const [v, setV] = useState({ enableAutoReply: !!section?.enableAutoReply, subject: (section?.responseSubject as string) ?? "", body: (section?.responseBodyPlainText as string) ?? "", startDate: ms(section?.startTime), endDate: ms(section?.endTime), restrictToContacts: !!section?.restrictToContacts, restrictToDomain: !!section?.restrictToDomain });
  return (
    <Section title="Vacation responder" hint="Saved directly to Gmail.">
      <Switch label="Auto-reply is on" checked={v.enableAutoReply} onChange={(x) => setV({ ...v, enableAutoReply: x })} />
      <TextField label="Subject" value={v.subject} onChange={(x) => setV({ ...v, subject: x })} maxLength={255} />
      <TextField label="Message" multiline value={v.body} onChange={(x) => setV({ ...v, body: x })} maxLength={10000} />
      <Field label="First day"><input aria-label="First day" type="date" className={`${inputClass} !w-44`} value={v.startDate} onChange={(e) => setV({ ...v, startDate: e.target.value })} /></Field>
      <Field label="Last day"><input aria-label="Last day" type="date" className={`${inputClass} !w-44`} value={v.endDate} onChange={(e) => setV({ ...v, endDate: e.target.value })} /></Field>
      <Switch label="Only reply to my contacts" checked={v.restrictToContacts} onChange={(x) => setV({ ...v, restrictToContacts: x })} />
      <Switch label="Only reply to people in my organisation" checked={v.restrictToDomain} onChange={(x) => setV({ ...v, restrictToDomain: x })} />
      <button className={`${buttonClass} mt-3 px-5 py-2 text-sm`} onClick={() => onSave(v)}>Save to Gmail</button>
    </Section>
  );
}

function SendAsCard({ section, onSave }: { section: { sendAs?: { sendAsEmail: string; displayName?: string; signature?: string; isDefault?: boolean }[] } | null; onSave: (email: string, patch: { displayName?: string; signature?: string }) => void }) {
  const list = section?.sendAs ?? [];
  const [edits, setEdits] = useState<Record<string, { displayName: string; signature: string }>>({});
  return (
    <Section title="Send-as addresses & signature" hint="Each address has its own display name and signature in Gmail.">
      {list.length === 0 ? <p className="text-sm text-muted">No send-as addresses were returned.</p> : list.map((a) => {
        const e = edits[a.sendAsEmail] ?? { displayName: a.displayName ?? "", signature: a.signature ?? "" };
        return (
          <div key={a.sendAsEmail} className="mb-3 border-b border-line pb-3 last:border-b-0">
            <p className="text-sm font-semibold">{a.sendAsEmail}{a.isDefault ? " (default)" : ""}</p>
            <input aria-label={`Display name for ${a.sendAsEmail}`} className={`${inputClass} mt-1`} value={e.displayName} onChange={(x) => setEdits({ ...edits, [a.sendAsEmail]: { ...e, displayName: x.target.value } })} />
            <textarea aria-label={`Signature for ${a.sendAsEmail}`} rows={3} className={`${inputClass} mt-2`} value={e.signature} onChange={(x) => setEdits({ ...edits, [a.sendAsEmail]: { ...e, signature: x.target.value } })} />
            <button className={`${secondaryButtonClass} mt-2 px-4 py-1.5 text-xs`} onClick={() => onSave(a.sendAsEmail, e)}>Save</button>
          </div>
        );
      })}
    </Section>
  );
}

function FiltersAndLabels({ accountId }: { accountId: string }) {
  const [labels, setLabels] = useState<{ id: string; name: string; type: string }[] | null>(null);
  const [filters, setFilters] = useState<{ id: string; criteria: Record<string, unknown>; action: Record<string, unknown> }[] | null>(null);
  const [name, setName] = useState("");
  const [msg, setMsg] = useState<string | null>(null);
  const load = useCallback(() => {
    gmailControl.labels(accountId).then((r) => setLabels(r.labels), () => setLabels([]));
    gmailControl.filters(accountId).then((r) => setFilters(r.filters), () => setFilters([]));
  }, [accountId]);
  useEffect(() => { load(); }, [load]);
  async function act(fn: () => Promise<unknown>) { setMsg(null); try { await fn(); load(); } catch (e) { setMsg(errText(e)); } }
  return (
    <>
      <Section title="Labels">
        {msg && <Banner tone="error">{msg}</Banner>}
        <div className="mb-2 flex gap-2"><input aria-label="New label name" className={`${inputClass} !w-60`} value={name} placeholder="New label" onChange={(e) => setName(e.target.value)} /><button className={`${buttonClass} px-4 py-2 text-xs`} disabled={!name.trim()} onClick={() => act(async () => { await gmailControl.createLabel(accountId, name); setName(""); })}>Create</button></div>
        {labels === null ? <Loading label="Loading labels" /> : <ul className="flex flex-wrap gap-2">{labels.filter((l) => l.type === "user").map((l) => <li key={l.id} className="flex items-center gap-1 rounded-full bg-surface-2 px-3 py-1 text-xs">{l.name}<button aria-label={`Delete label ${l.name}`} className="text-danger" onClick={() => window.confirm(`Delete label "${l.name}"? Messages keep existing but lose this label.`) && act(() => gmailControl.deleteLabel(accountId, l.id))}>×</button></li>)}</ul>}
      </Section>
      <Section title="Gmail's own filters" hint="These run inside Gmail itself, even when Daksha is offline. (Daksha rules are on the Rules tab.)">
        {filters === null ? <Loading label="Loading filters" /> : filters.length === 0 ? <p className="text-sm text-muted">No Gmail filters.</p> : (
          <ul className="divide-y divide-line text-xs">{filters.map((f) => <li key={f.id} className="flex items-start justify-between gap-3 py-2"><span>{JSON.stringify(f.criteria)} → {JSON.stringify(f.action)}</span><button className="shrink-0 font-semibold text-danger underline" onClick={() => window.confirm("Delete this Gmail filter?") && act(() => gmailControl.deleteFilter(accountId, f.id))}>Delete</button></li>)}</ul>
        )}
      </Section>
    </>
  );
}
