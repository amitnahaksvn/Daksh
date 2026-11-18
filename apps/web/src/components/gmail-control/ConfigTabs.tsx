"use client";
import { useEffect, useState } from "react";
import { buttonClass, secondaryButtonClass, Loading, inputClass } from "@/components/ui";
import { api } from "@/lib/api";
import { gmailControl, type Analysis, type GmailConfig, type GmailNotification, type SyncRun, type SyncStatus } from "@/lib/gmailControl";
import { Banner, errText, ListField, NumberField, Section, SelectField, Switch, TextField, Field } from "./parts";

type Props = { cfg: GmailConfig; update: (m: (c: GmailConfig) => void) => void; accountId: string };

const FEATURE_LABELS: Record<string, string> = {
  summarisation: "Email summaries", longThreadSummary: "Long conversation summaries", important: "Important email detection", urgent: "Urgent email detection",
  actionItems: "Action items", deadlines: "Deadlines", meetingRequest: "Meeting requests", invoice: "Invoices", receipt: "Receipts", paymentReminder: "Payment reminders",
  recruitment: "Recruitment & interviews", travel: "Travel bookings", flightHotel: "Flight & hotel confirmations", newsletter: "Newsletters", promotional: "Promotions",
  spamSuspicious: "Spam & suspicious messages", sentiment: "Sentiment & tone", complaint: "Customer complaints", duplicate: "Duplicate messages", followUp: "Follow-up needed",
  decisions: "Decisions & commitments", contacts: "Contacts & organisations", orderDelivery: "Orders & deliveries", subscription: "Subscriptions & renewals", legal: "Legal & compliance",
};
const NOTIFICATION_LABELS: Record<string, string> = {
  newEmail: "New email", importantEmail: "Important email", urgentEmail: "Urgent email", selectedSender: "Email from a selected sender", invoiceReceived: "Invoice received",
  paymentReminder: "Payment reminder", meetingInvitation: "Meeting invitation", actionRequired: "Action required", followUpDue: "Follow-up due", deadlineApproaching: "Deadline approaching",
  aiProcessingCompleted: "AI processing completed", attachmentReceived: "Attachment received", sendFailure: "Email sending failure", connectionExpired: "Connection expired",
  syncFailure: "Sync failure", oauthPermissionIssue: "Google permission issue",
};

export function SyncTab({ cfg, update, accountId }: Props) {
  const [status, setStatus] = useState<SyncStatus | null>(null);
  const [runs, setRuns] = useState<SyncRun[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const refresh = () => {
    gmailControl.syncStatus(accountId).then(setStatus, () => undefined);
    gmailControl.syncHistory(accountId).then((r) => setRuns(r.runs), () => undefined);
  };
  useEffect(refresh, [accountId]);

  async function act(fn: () => Promise<unknown>, ok: string) {
    setBusy(true); setMsg(null);
    try { await fn(); setMsg({ tone: "ok", text: ok }); refresh(); } catch (e) { setMsg({ tone: "error", text: errText(e) }); } finally { setBusy(false); }
  }
  const s = cfg.sync;
  return (
    <>
      {msg && <Banner tone={msg.tone}>{msg.text}</Banner>}
      <Section title="Synchronisation status">
        <p className="text-sm">
          {status?.state?.status ?? "Idle"} · {status?.state?.messagesSynced ?? 0} messages synced
          {status?.state?.lastSuccessUtc && <> · last success {new Date(status.state.lastSuccessUtc).toLocaleString()}</>}
        </p>
        {status?.state?.lastError && <p className="mt-1 text-xs text-danger">Last error: {status.state.lastError}</p>}
        <p className="mt-1 text-xs text-muted">
          {status?.pushConfigured ? `Push notifications are configured${status.state?.watchExpiresUtc ? ` (watch expires ${new Date(status.state.watchExpiresUtc).toLocaleDateString()})` : ""}.` : "Push notifications aren't configured on this server, so Daksha checks Gmail on the interval below."}
          {status && status.deadLetters > 0 && ` ${status.deadLetters} message(s) failed repeatedly and were set aside.`}
        </p>
        <div className="mt-3 flex flex-wrap gap-2">
          <button className={`${buttonClass} px-4 py-2 text-xs`} disabled={busy} onClick={() => act(() => gmailControl.syncRun(accountId), "Sync finished.")}>Sync now</button>
          <button className={`${secondaryButtonClass} px-4 py-2 text-xs`} disabled={busy} onClick={() => act(() => gmailControl.syncToggle(accountId, !s.enabled).then(() => update((c) => { c.sync.enabled = !s.enabled; })), s.enabled ? "Sync stopped." : "Sync started.")}>{s.enabled ? "Stop syncing" : "Start syncing"}</button>
          {status?.pushConfigured && <button className={`${secondaryButtonClass} px-4 py-2 text-xs`} disabled={busy} onClick={() => act(() => gmailControl.renewWatch(accountId), "Push subscription renewed.")}>Renew push subscription</button>}
        </div>
      </Section>
      <Section title="What to synchronise">
        <Switch label="Synchronise this account" checked={s.enabled} onChange={(v) => update((c) => { c.sync.enabled = v; })} />
        <Switch label="Near-real-time updates" hint="Uses Gmail push notifications when the server has them configured; otherwise falls back to the interval." checked={s.nearRealTime} onChange={(v) => update((c) => { c.sync.nearRealTime = v; })} />
        <NumberField label="Check interval" hint="Used when push isn't available (5–1440)." value={s.intervalMinutes} min={5} max={1440} suffix="minutes" onChange={(v) => update((c) => { c.sync.intervalMinutes = v; })} />
        <SelectField label="Initial synchronisation period" value={String(s.initialPeriodDays)} onChange={(v) => update((c) => { c.sync.initialPeriodDays = Number(v); })}
          options={[{ value: "7", label: "Last 7 days" }, { value: "30", label: "Last 30 days" }, { value: "90", label: "Last 90 days" }, { value: "180", label: "Last 180 days" }, { value: "0", label: "All available history" }]} />
        <ListField label="Labels to synchronise" hint="Gmail label ids, e.g. INBOX, SENT, DRAFT, STARRED, IMPORTANT. Empty = everything." value={s.labels} onChange={(v) => update((c) => { c.sync.labels = v; })} />
        <Switch label="Include spam" checked={s.includeSpam} onChange={(v) => update((c) => { c.sync.includeSpam = v; })} />
        <Switch label="Include trash" checked={s.includeTrash} onChange={(v) => update((c) => { c.sync.includeTrash = v; })} />
      </Section>
      <Section title="Exclusions" hint="Matching messages are skipped entirely — not stored, classified or notified.">
        <ListField label="Excluded senders" value={s.excludedSenders} onChange={(v) => update((c) => { c.sync.excludedSenders = v; })} placeholder="person@example.com" />
        <ListField label="Excluded domains" value={s.excludedDomains} onChange={(v) => update((c) => { c.sync.excludedDomains = v; })} placeholder="example.com" />
        <ListField label="Excluded subject keywords" value={s.excludedSubjectKeywords} onChange={(v) => update((c) => { c.sync.excludedSubjectKeywords = v; })} />
        <ListField label="Excluded labels" value={s.excludedLabels} onChange={(v) => update((c) => { c.sync.excludedLabels = v; })} />
      </Section>
      <Section title="Recent sync runs">
        {runs.length === 0 ? <p className="text-sm text-muted">No runs yet.</p> : (
          <ul className="divide-y divide-line text-sm">
            {runs.slice(0, 8).map((r) => (
              <li key={r.id} className="flex flex-wrap justify-between gap-2 py-2">
                <span>{new Date(r.startedUtc).toLocaleString()} · {r.kind} · {r.trigger}</span>
                <span className={r.status === "failed" ? "text-danger" : "text-muted"}>{r.processed}/{r.fetched} processed{r.failed ? `, ${r.failed} failed` : ""}{r.error ? ` — ${r.error}` : ""}</span>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </>
  );
}

export function AiTab({ cfg, update }: Props) {
  const a = cfg.ai;
  return (
    <>
      <Section title="AI email intelligence" hint="Daksha analyses mail on its own server with a built-in English classifier. Email text is never sent to an external AI provider.">
        <Switch label="Enable AI processing for this account" checked={a.enabled} onChange={(v) => update((c) => { c.ai.enabled = v; })} />
        <SelectField label="Process" value={a.scope} onChange={(v) => update((c) => { c.ai.scope = v; })}
          options={[{ value: "all", label: "All emails" }, { value: "important", label: "Only important emails" }, { value: "categories", label: "Only selected categories" }]} />
        {a.scope === "categories" && <ListField label="Categories to process" hint="Feature ids such as invoice, meetingRequest, recruitment." value={a.categories} onChange={(v) => update((c) => { c.ai.categories = v; })} />}
        <SelectField label="Summary length" value={a.summaryLength} onChange={(v) => update((c) => { c.ai.summaryLength = v; })}
          options={[{ value: "short", label: "Short" }, { value: "balanced", label: "Balanced" }, { value: "detailed", label: "Detailed" }]} />
        <ListField label="Excluded senders" value={a.excludedSenders} onChange={(v) => update((c) => { c.ai.excludedSenders = v; })} />
        <ListField label="Excluded domains" value={a.excludedDomains} onChange={(v) => update((c) => { c.ai.excludedDomains = v; })} />
        <ListField label="Excluded labels" value={a.excludedLabels} onChange={(v) => update((c) => { c.ai.excludedLabels = v; })} />
      </Section>
      <Section title="Features" hint="Turn each capability on or off and set how sure Daksha must be before it applies a result.">
        <ul className="divide-y divide-line">
          {Object.keys(FEATURE_LABELS).map((id) => {
            const f = a.features[id];
            if (!f) return null;
            return (
              <li key={id} className="flex flex-wrap items-center justify-between gap-3 py-2.5">
                <label className="flex items-center gap-3 text-sm font-semibold">
                  <input type="checkbox" aria-label={FEATURE_LABELS[id]} checked={f.enabled} onChange={(e) => update((c) => { c.ai.features[id].enabled = e.target.checked; })} />
                  {FEATURE_LABELS[id]}
                </label>
                <span className="flex items-center gap-2 text-xs text-muted">
                  Confidence ≥
                  <input aria-label={`${FEATURE_LABELS[id]} confidence`} type="number" step={0.05} min={0} max={1} className={`${inputClass} !w-20`} value={f.confidenceThreshold} disabled={!f.enabled}
                    onChange={(e) => update((c) => { c.ai.features[id].confidenceThreshold = Number(e.target.value); })} />
                </span>
              </li>
            );
          })}
        </ul>
      </Section>
    </>
  );
}

export function RepliesTab({ cfg, update, accountId }: Props) {
  const r = cfg.reply;
  const [messages, setMessages] = useState<{ id: string; subject: string; from: string }[]>([]);
  const [pick, setPick] = useState("");
  const [analysis, setAnalysis] = useState<Analysis | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.gmailMessages("in:inbox").then((p) => setMessages(p.messages.slice(0, 15).map((m) => ({ id: m.id, subject: m.subject || "(no subject)", from: m.from }))), () => undefined); }, []);
  async function preview() {
    if (!pick) return;
    setBusy(true); setErr(null); setAnalysis(null);
    try { setAnalysis(await gmailControl.analyze(pick, accountId)); } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  }
  return (
    <>
      <Banner>Automatic sending is off until you choose an approval mode that allows it <strong>and</strong> switch it on, on an Autonomous profile. Daksha never sends a follow-up on its own.</Banner>
      <Section title="Approval">
        <Switch label="Suggest replies" checked={r.suggestionsEnabled} onChange={(v) => update((c) => { c.reply.suggestionsEnabled = v; })} />
        <SelectField label="Approval mode" value={r.mode} onChange={(v) => update((c) => { c.reply.mode = v; })}
          options={[
            { value: "AskFirst", label: "Always ask before doing anything" },
            { value: "DraftOnly", label: "Create drafts automatically, never send" },
            { value: "AutoRules", label: "Send automatically only for selected rules" },
            { value: "AutoCategories", label: "Send automatically for approved categories" },
          ]} />
        <Switch label="Allow automatic sending" hint="Only has an effect in the two automatic modes, on an Autonomous profile." checked={r.autoSend} disabled={r.mode === "AskFirst" || r.mode === "DraftOnly"} onChange={(v) => update((c) => { c.reply.autoSend = v; })} />
        <ListField label="Categories approved for auto-send" hint="Category ids such as invoice, meetingRequest, orderDelivery." value={r.autoSendCategories} onChange={(v) => update((c) => { c.reply.autoSendCategories = v; })} />
        <NumberField label="Minimum confidence" hint="Below this Daksha only suggests." value={r.minConfidence} min={0} max={1} onChange={(v) => update((c) => { c.reply.minConfidence = v; })} />
      </Section>
      <Section title="Who to reply to">
        <Switch label="Reply to new messages" checked={r.replyToNewMessages} onChange={(v) => update((c) => { c.reply.replyToNewMessages = v; })} />
        <Switch label="Reply in existing conversations" checked={r.replyToExistingThreads} onChange={(v) => update((c) => { c.reply.replyToExistingThreads = v; })} />
        <ListField label="Only reply to these senders" hint="Empty = anyone not excluded." value={r.onlySenders} onChange={(v) => update((c) => { c.reply.onlySenders = v; })} />
        <ListField label="Never reply to these senders" value={r.excludedSenders} onChange={(v) => update((c) => { c.reply.excludedSenders = v; })} />
        <ListField label="Never reply to these domains" value={r.excludedDomains} onChange={(v) => update((c) => { c.reply.excludedDomains = v; })} />
        <ListField label="Never reply to these labels" value={r.excludedLabels} onChange={(v) => update((c) => { c.reply.excludedLabels = v; })} />
        <Switch label="Skip automated email" checked={r.excludeAutomated} onChange={(v) => update((c) => { c.reply.excludeAutomated = v; })} />
        <Switch label="Skip OTP & security email" checked={r.excludeOtpSecurity} onChange={(v) => update((c) => { c.reply.excludeOtpSecurity = v; })} />
        <Switch label="Skip financial correspondence" checked={r.excludeFinancial} onChange={(v) => update((c) => { c.reply.excludeFinancial = v; })} />
        <Switch label="Skip legal correspondence" checked={r.excludeLegal} onChange={(v) => update((c) => { c.reply.excludeLegal = v; })} />
        <Switch label="Skip suspicious messages" checked={r.excludeSuspicious} onChange={(v) => update((c) => { c.reply.excludeSuspicious = v; })} />
      </Section>
      <Section title="Timing & limits">
        <Switch label="Only act in business hours" checked={r.onlyInBusinessHours} onChange={(v) => update((c) => { c.reply.onlyInBusinessHours = v; })} />
        <TextField label="Business hours start (HH:mm)" value={r.businessHoursStart} onChange={(v) => update((c) => { c.reply.businessHoursStart = v; })} />
        <TextField label="Business hours end (HH:mm)" value={r.businessHoursEnd} onChange={(v) => update((c) => { c.reply.businessHoursEnd = v; })} />
        <Switch label="Reply on weekends" checked={r.replyOnWeekends} onChange={(v) => update((c) => { c.reply.replyOnWeekends = v; })} />
        <ListField label="Holidays (yyyy-MM-dd)" value={r.holidays} onChange={(v) => update((c) => { c.reply.holidays = v; })} />
        <NumberField label="Reply delay" value={r.replyDelayMinutes} min={0} max={1440} suffix="minutes" onChange={(v) => update((c) => { c.reply.replyDelayMinutes = v; })} />
        <NumberField label="Maximum automatic replies per hour" value={r.maxPerHour} min={0} max={100} onChange={(v) => update((c) => { c.reply.maxPerHour = v; })} />
        <NumberField label="Maximum automatic replies per day" value={r.maxPerDay} min={0} max={500} onChange={(v) => update((c) => { c.reply.maxPerDay = v; })} />
      </Section>
      <Section title="Style">
        <SelectField label="Reply style" value={r.style} onChange={(v) => update((c) => { c.reply.style = v; })}
          options={["Professional", "Friendly", "Formal", "Casual", "Short", "Detailed", "Custom"].map((x) => ({ value: x, label: x === "Short" ? "Short and direct" : x }))} />
        <TextField label="Greeting" value={r.greeting} onChange={(v) => update((c) => { c.reply.greeting = v; })} maxLength={100} />
        <TextField label="Closing" value={r.closing} onChange={(v) => update((c) => { c.reply.closing = v; })} maxLength={100} />
        <TextField label="Signature" multiline value={r.signature} onChange={(v) => update((c) => { c.reply.signature = v; })} maxLength={2000} />
        <NumberField label="Maximum length" value={r.maxLength} min={50} max={5000} suffix="characters" onChange={(v) => update((c) => { c.reply.maxLength = v; })} />
        <ListField label="Phrases to avoid" value={r.phrasesToAvoid} onChange={(v) => update((c) => { c.reply.phrasesToAvoid = v; })} />
        <ListField label="Never make commitments about" hint="If a message touches these topics, Daksha only drafts — it never sends." value={r.neverCommitTo} onChange={(v) => update((c) => { c.reply.neverCommitTo = v; })} />
      </Section>
      <Section title="Test before you rely on it" hint="Runs your current saved settings against a real message and shows exactly what Daksha would do. Nothing is sent or drafted.">
        <div className="flex flex-wrap items-center gap-2">
          <select aria-label="Message to test" className={`${inputClass} !w-auto min-w-0 flex-1`} value={pick} onChange={(e) => setPick(e.target.value)}>
            <option value="">Choose a recent message…</option>
            {messages.map((m) => <option key={m.id} value={m.id}>{m.subject} — {m.from}</option>)}
          </select>
          <button className={`${buttonClass} px-4 py-2 text-xs`} disabled={!pick || busy} onClick={preview}>{busy ? "Analysing…" : "Preview"}</button>
        </div>
        {err && <Banner tone="error">{err}</Banner>}
        {analysis && (
          <div className="mt-3 space-y-2 text-sm">
            <p><strong>Decision:</strong> {analysis.replyDecision.action} — {analysis.replyDecision.reason}</p>
            <p><strong>Summary:</strong> {analysis.summary}</p>
            {analysis.suggestedReply && <pre className="whitespace-pre-wrap rounded-2xl bg-surface-2 p-3 text-xs">{analysis.suggestedReply}</pre>}
          </div>
        )}
      </Section>
    </>
  );
}

export function NotificationsTab({ cfg, update, accountId }: Props) {
  const n = cfg.notifications;
  const [items, setItems] = useState<GmailNotification[] | null>(null);
  const load = () => gmailControl.notifications(accountId).then((r) => setItems(r.items), () => setItems([]));
  useEffect(() => { void load(); }, [accountId]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <>
      <Section title="Notify me about">
        {Object.entries(NOTIFICATION_LABELS).map(([id, label]) => (
          <Switch key={id} label={label} checked={n.types[id] ?? false} onChange={(v) => update((c) => { c.notifications.types[id] = v; })} />
        ))}
      </Section>
      <Section title="Delivery">
        <TextField label="Quiet hours start (HH:mm)" hint="Leave empty for none. Events are still recorded." value={n.quietHoursStart} onChange={(v) => update((c) => { c.notifications.quietHoursStart = v; })} />
        <TextField label="Quiet hours end (HH:mm)" value={n.quietHoursEnd} onChange={(v) => update((c) => { c.notifications.quietHoursEnd = v; })} />
        <NumberField label="Maximum notifications per hour" value={n.maxPerHour} min={1} max={200} onChange={(v) => update((c) => { c.notifications.maxPerHour = v; })} />
      </Section>
      <Section title="Recent notifications">
        {items === null ? <Loading label="Loading notifications" /> : items.length === 0 ? <p className="text-sm text-muted">Nothing yet.</p> : (
          <>
            <button className={`${secondaryButtonClass} mb-2 px-3 py-1.5 text-xs`} onClick={() => gmailControl.markNotificationsRead(null).then(load)}>Mark all read</button>
            <ul className="divide-y divide-line text-sm">
              {items.slice(0, 20).map((i) => (
                <li key={i.key} className={`py-2 ${i.read ? "text-muted" : ""}`}>
                  <p className="font-semibold">{i.title}</p><p className="text-xs">{i.body} · {new Date(i.createdAtUtc).toLocaleString()}</p>
                </li>
              ))}
            </ul>
          </>
        )}
      </Section>
    </>
  );
}

export function AttachmentsTab({ cfg, update }: Props) {
  const a = cfg.attachments;
  return (
    <>
      <Banner>Daksha checks an attachment&apos;s real contents, not just its name, and never opens or runs it. Programs and scripts (.exe, .bat, .js, …) are always blocked. Attachments are streamed to you on request and not stored; there is no built-in malware scanner.</Banner>
      <Section title="Attachment processing">
        <Switch label="Process attachments" checked={a.enabled} onChange={(v) => update((c) => { c.attachments.enabled = v; })} />
        <ListField label="Allowed file types" hint="Empty = any safe type. Example: pdf, docx, xlsx" value={a.allowedExtensions} onChange={(v) => update((c) => { c.attachments.allowedExtensions = v; })} />
        <ListField label="Excluded file types" value={a.excludedExtensions} onChange={(v) => update((c) => { c.attachments.excludedExtensions = v; })} />
        <NumberField label="Maximum attachment size" value={a.maxSizeMb} min={1} max={25} suffix="MB" onChange={(v) => update((c) => { c.attachments.maxSizeMb = v; })} />
        <Switch label="Classify documents automatically" hint="Invoice, receipt, resume, contract, bank statement, purchase order." checked={a.autoClassify} onChange={(v) => update((c) => { c.attachments.autoClassify = v; })} />
        <Switch label="Detect duplicate attachments" checked={a.detectDuplicates} onChange={(v) => update((c) => { c.attachments.detectDuplicates = v; })} />
        <NumberField label="Keep attachment records for" hint="Records hold the file name, type and checksum — never the file." value={a.retentionDays} min={1} max={3650} suffix="days" onChange={(v) => update((c) => { c.attachments.retentionDays = v; })} />
      </Section>
    </>
  );
}

export function FollowUpSettings({ cfg, update }: Pick<Props, "cfg" | "update">) {
  const f = cfg.followUp;
  return (
    <Section title="Follow-up settings">
      <Switch label="Track follow-ups" checked={f.enabled} onChange={(v) => update((c) => { c.followUp.enabled = v; })} />
      <Field label="Remind me after" hint="Pick the days between follow-ups; later reminders use the later intervals.">
        <span className="flex flex-wrap gap-2">
          {[1, 2, 3, 5, 7, 14].map((d) => (
            <label key={d} className="flex items-center gap-1 text-sm">
              <input type="checkbox" aria-label={`${d} day`} checked={f.intervalsDays.includes(d)} onChange={(e) => update((c) => { c.followUp.intervalsDays = e.target.checked ? [...c.followUp.intervalsDays, d].sort((x, y) => x - y) : c.followUp.intervalsDays.filter((x) => x !== d); })} />{d}d
            </label>
          ))}
        </span>
      </Field>
      <Switch label="Notify before deadlines" checked={f.notifyBeforeDeadline} onChange={(v) => update((c) => { c.followUp.notifyBeforeDeadline = v; })} />
      <NumberField label="Deadline warning" value={f.deadlineLeadHours} min={1} max={336} suffix="hours before" onChange={(v) => update((c) => { c.followUp.deadlineLeadHours = v; })} />
      <p className="py-2 text-xs text-muted">Daksha prepares follow-up drafts for you to review. It does not send follow-ups automatically.</p>
    </Section>
  );
}
