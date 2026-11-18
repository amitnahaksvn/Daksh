"use client";
import { useCallback, useEffect, useState } from "react";
import { buttonClass, inputClass, Loading, secondaryButtonClass } from "@/components/ui";
import { gmailControl, type AuditEntry, type GmailConfig, type PermissionInfo } from "@/lib/gmailControl";
import { Banner, errText, Field, Section, SelectField, Switch } from "./parts";

const PROFILES = [
  { id: "ReadOnly", title: "Read only", text: "Daksha can read, search, analyse and summarise your mail. It can't change or send anything." },
  { id: "Assisted", title: "Assisted", text: "Daksha can prepare drafts and perform actions you ask for. Rules and automations can't act on their own." },
  { id: "Autonomous", title: "Autonomous", text: "Daksha can run the rules and automations you've authorised, within the limits below." },
];

export function SecurityTab({ cfg, update, accountId }: { cfg: GmailConfig; update: (m: (c: GmailConfig) => void) => void; accountId: string }) {
  const [info, setInfo] = useState<PermissionInfo | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => { gmailControl.permissions(accountId).then(setInfo, (e) => setErr(errText(e))); }, [accountId]);

  async function upgrade(feature: string) {
    try { const { authorizationUrl } = await gmailControl.connect([feature]); window.location.assign(authorizationUrl); } catch (e) { setErr(errText(e)); }
  }
  return (
    <>
      {err && <Banner tone="error">{err}</Banner>}
      <Section title="Permission profile" hint="Enforced by Daksha's server for every action — hiding a button isn't what protects you.">
        <div role="radiogroup" aria-label="Permission profile" className="space-y-2">
          {PROFILES.map((p) => (
            <label key={p.id} className={`flex cursor-pointer items-start gap-3 rounded-2xl border p-3 ${cfg.permissions.profile === p.id ? "border-accent" : "border-line"}`}>
              <input type="radio" name="profile" aria-label={p.title} className="mt-1" checked={cfg.permissions.profile === p.id} onChange={() => update((c) => { c.permissions.profile = p.id; })} />
              <span><span className="block text-sm font-semibold">{p.title}</span><span className="block text-xs text-muted">{p.text}</span></span>
            </label>
          ))}
        </div>
        <p className="mt-2 text-xs text-muted">Save to apply. Permanent deletion and sending email content to external AI are never part of a profile — they need an explicit override below.</p>
      </Section>

      <Section title="What Daksha may do" hint="“Default” follows the profile; Allow or Deny overrides it for this account.">
        {!info ? <Loading label="Loading permissions" /> : info.features.map((f) => {
          const forced = cfg.permissions.overrides[f.permission];
          const effective = forced ?? info.features.find((x) => x.permission === f.permission)?.allowed;
          return (
            <Field key={f.permission} label={f.label[0].toUpperCase() + f.label.slice(1)} hint={f.requiredScope ? `${f.why}${f.scopeGranted ? "" : " — Google permission not granted yet."}` : f.why}>
              <span className="flex items-center gap-2">
                <select aria-label={`Override for ${f.label}`} className={`${inputClass} !w-28`} value={forced === undefined ? "default" : forced ? "allow" : "deny"}
                  onChange={(e) => update((c) => { if (e.target.value === "default") delete c.permissions.overrides[f.permission]; else c.permissions.overrides[f.permission] = e.target.value === "allow"; })}>
                  <option value="default">Default</option><option value="allow">Allow</option><option value="deny">Deny</option>
                </select>
                <span className="text-xs text-muted">{effective === undefined ? "" : effective ? "allowed" : "blocked"}</span>
                {!f.scopeGranted && f.requiredScope && <button className={`${secondaryButtonClass} px-2 py-1 text-xs`} onClick={() => upgrade(f.permission === "permanentDelete" ? "full" : f.requiredScope!.includes("settings") ? "settings" : f.requiredScope!.includes("labels") ? "labels" : f.requiredScope!.includes("compose") ? "compose" : "modify")}>Approve</button>}
              </span>
            </Field>
          );
        })}
      </Section>

      <Section title="Google permissions granted" hint="Daksha asks for the narrowest permission each feature needs, only when you turn that feature on.">
        {!info ? <Loading label="Loading" /> : <ul className="space-y-1 text-sm">{info.scopes.map((s) => <li key={s.scope}><code className="text-xs">{s.scope.replace("https://www.googleapis.com/auth/", "")}</code> — {s.why}</li>)}</ul>}
        {info && info.upgradeFeatures.some((u) => !u.granted) && (
          <div className="mt-3 space-y-2">{info.upgradeFeatures.filter((u) => !u.granted).map((u) => (
            <div key={u.feature} className="flex items-center justify-between gap-3 rounded-2xl bg-surface-2 p-3 text-sm"><span>{u.why}</span><button className={`${buttonClass} shrink-0 px-3 py-1.5 text-xs`} onClick={() => upgrade(u.feature)}>Approve</button></div>
          ))}</div>
        )}
      </Section>

      <Section title="Privacy">
        <Switch label="Keep subjects and snippets of synced mail" hint="Message bodies are never stored. Turn this off to keep only message ids and labels." checked={cfg.privacy.storeSnippets} onChange={(v) => update((c) => { c.privacy.storeSnippets = v; })} />
        <SelectField label="Keep synced mail info for" value={String(cfg.privacy.retentionDays)} onChange={(v) => update((c) => { c.privacy.retentionDays = Number(v); })}
          options={[30, 90, 180, 365, 1095].map((d) => ({ value: String(d), label: `${d} days` }))} />
      </Section>
      <DataControls accountId={accountId} />
      <AuditLog accountId={accountId} />
    </>
  );
}

function DataControls({ accountId }: { accountId: string }) {
  const [msg, setMsg] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  async function exportData() {
    try {
      const data = await gmailControl.exportData(accountId);
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
      const a = document.createElement("a"); a.href = url; a.download = "daksha-gmail-export.json"; a.click(); URL.revokeObjectURL(url);
      setMsg({ tone: "ok", text: "Export downloaded. It never includes tokens or message bodies." });
    } catch (e) { setMsg({ tone: "error", text: errText(e) }); }
  }
  async function remove() {
    if (!window.confirm("Delete everything Daksha has saved about this Gmail account (synced message info, rules, settings, follow-ups)? Your Gmail mailbox is not touched.")) return;
    const includeAudit = window.confirm("Also delete the action history (audit log)? Choose Cancel to keep it.");
    try { await gmailControl.deleteData(accountId, includeAudit); setMsg({ tone: "ok", text: "Daksha's saved data for this account was deleted." }); } catch (e) { setMsg({ tone: "error", text: errText(e) }); }
  }
  return (
    <Section title="Your data">
      {msg && <Banner tone={msg.tone}>{msg.text}</Banner>}
      <div className="flex flex-wrap gap-2"><button className={`${secondaryButtonClass} px-4 py-2 text-xs`} onClick={exportData}>Export my Gmail data</button><button className="px-4 py-2 text-xs font-semibold text-danger underline" onClick={remove}>Delete saved data…</button></div>
    </Section>
  );
}

function AuditLog({ accountId }: { accountId: string }) {
  const [items, setItems] = useState<AuditEntry[] | null>(null);
  const [total, setTotal] = useState(0);
  const [f, setF] = useState({ q: "", status: "", trigger: "" });
  const [page, setPage] = useState(0);
  const load = useCallback(() => gmailControl.audit({ accountId, q: f.q, status: f.status, trigger: f.trigger, skip: page * 25, take: 25 }).then((r) => { setItems(r.items); setTotal(r.total); }, () => setItems([])), [accountId, f, page]);
  useEffect(() => { void load(); }, [load]);
  return (
    <Section title="Action history" hint="Every action Daksha takes on this account — yours, rules' and automations'.">
      <div className="mb-3 flex flex-wrap gap-2">
        <input aria-label="Search history" className={`${inputClass} !w-52`} placeholder="Search actions…" value={f.q} onChange={(e) => { setPage(0); setF({ ...f, q: e.target.value }); }} />
        <select aria-label="Filter by status" className={`${inputClass} !w-36`} value={f.status} onChange={(e) => { setPage(0); setF({ ...f, status: e.target.value }); }}><option value="">Any status</option><option>Success</option><option>Failed</option><option>Denied</option></select>
        <select aria-label="Filter by trigger" className={`${inputClass} !w-36`} value={f.trigger} onChange={(e) => { setPage(0); setF({ ...f, trigger: e.target.value }); }}><option value="">Any source</option><option>User</option><option>Rule</option><option>Automation</option><option>Sync</option></select>
      </div>
      {items === null ? <Loading label="Loading history" /> : items.length === 0 ? <p className="text-sm text-muted">No actions recorded.</p> : (
        <ul className="divide-y divide-line text-xs">
          {items.map((a) => (
            <li key={a.id} className="flex flex-wrap justify-between gap-2 py-2">
              <span><strong>{a.action}</strong> · {a.trigger}{a.ruleId ? " (rule)" : ""}{a.messageRef ? ` · ${a.messageRef.slice(0, 16)}` : ""}{a.approval !== "NotRequired" ? ` · ${a.approval}` : ""}</span>
              <span className={a.status === "Success" ? "text-muted" : "text-danger"}>{a.status}{a.error ? ` — ${a.error}` : ""} · {new Date(a.atUtc).toLocaleString()}</span>
            </li>
          ))}
        </ul>
      )}
      <div className="mt-3 flex items-center gap-2 text-xs"><button className={`${secondaryButtonClass} px-3 py-1`} disabled={page === 0} onClick={() => setPage(page - 1)}>Previous</button><span>{total === 0 ? 0 : page * 25 + 1}–{Math.min(total, page * 25 + 25)} of {total}</span><button className={`${secondaryButtonClass} px-3 py-1`} disabled={(page + 1) * 25 >= total} onClick={() => setPage(page + 1)}>Next</button></div>
    </Section>
  );
}
