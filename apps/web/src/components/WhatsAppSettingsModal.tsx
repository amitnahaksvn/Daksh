"use client";
import { useEffect, useMemo, useState } from "react";
import { StatusChip } from "@/components/integrations";
import { WhatsAppAutomationTab } from "@/components/WhatsAppAutomationTab";
import { WhatsAppContactsTab } from "@/components/WhatsAppContactsTab";
import { WhatsAppIntelligenceTab } from "@/components/WhatsAppIntelligenceTab";
import { Card, PickerRow, Row, SearchSelect, Toggle } from "@/components/WhatsAppSettingsParts";
import { buttonClass, Dropdown, inputClass, Loading, secondaryButtonClass } from "@/components/ui";
import {
  api,
  ApiError,
  type ConnectionStatusValue,
  type WhatsAppFullSettings,
  type WhatsAppChat,
  type WhatsAppFullSettingsUpdate,
  type WhatsAppPendingReply,
} from "@/lib/api";

type TabId = "general" | "monitoring" | "autoReply" | "aiBehaviour" | "contacts" | "notifications" | "intelligence" | "automation" | "privacy";

const TABS: { id: TabId; label: string }[] = [
  { id: "general", label: "General" },
  { id: "monitoring", label: "Monitoring" },
  { id: "autoReply", label: "Auto Reply" },
  { id: "aiBehaviour", label: "AI Behaviour" },
  { id: "contacts", label: "Contacts & Groups" },
  { id: "notifications", label: "Notifications" },
  { id: "intelligence", label: "Message Intelligence" },
  { id: "automation", label: "Automation Rules" },
  { id: "privacy", label: "Privacy & Security" },
];

const NOTIFY_TRIGGERS: { tag: string; label: string }[] = [
  { tag: "question", label: "Messages containing questions" },
  { tag: "deadline", label: "Messages containing deadlines or commitments" },
  { tag: "payment", label: "Messages containing payment requests" },
  { tag: "meeting", label: "Messages containing meeting invitations" },
  { tag: "otp", label: "Messages containing OTPs or verification codes" },
  { tag: "suspicious-link", label: "Messages containing suspicious links" },
  { tag: "attachment", label: "Messages containing attachments" },
  { tag: "unknown-number", label: "Messages from unknown numbers" },
  { tag: "priority-contact", label: "Messages from priority contacts" },
];

const TIME_OPTIONS = Array.from({ length: 48 }, (_, i) => `${String(Math.floor(i / 2)).padStart(2, "0")}:${i % 2 ? "30" : "00"}`);

function formatTime(v: string): string {
  const [h, m] = v.split(":").map(Number);
  if (Number.isNaN(h) || Number.isNaN(m)) return v;
  return `${h % 12 || 12}:${String(m).padStart(2, "0")} ${h < 12 ? "AM" : "PM"}`;
}

function TimeSelect({ value, onChange, disabled }: { value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const times = TIME_OPTIONS.includes(value) ? TIME_OPTIONS : [value, ...TIME_OPTIONS].sort();
  return <SearchSelect value={value} onChange={onChange} disabled={disabled} className="w-36" options={times.map((t) => ({ value: t, label: formatTime(t) }))} />;
}

function systemTimezone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"; } catch { return "UTC"; }
}

function timezoneOptions(current: string): string[] {
  let zones: string[] = [];
  try { zones = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.("timeZone") ?? []; } catch { /* older runtime */ }
  const set = new Set(["UTC", ...zones, current, systemTimezone()]);
  return [...set].sort();
}

function diffSection<T extends Record<string, unknown>>(baseline: T, draft: T): Partial<T> | undefined {
  const diff: Partial<T> = {};
  let changed = false;
  for (const key of Object.keys(draft) as (keyof T)[]) {
    if (JSON.stringify(draft[key]) !== JSON.stringify(baseline[key])) {
      diff[key] = draft[key];
      changed = true;
    }
  }
  return changed ? diff : undefined;
}

/** The same settings screen serves WhatsApp and Telegram — both keep their settings, rules and insights keyed by connection id. */
export function WhatsAppSettingsModal({ connectionId, onClose, inline = false, provider = "whatsapp" }: { connectionId: string; onClose?: () => void; inline?: boolean; provider?: "whatsapp" | "telegram" }) {
  const isTelegram = provider === "telegram";
  const brand = isTelegram ? "Telegram" : "WhatsApp";
  const [tab, setTab] = useState<TabId>("general");
  const [search, setSearch] = useState("");
  const [baseline, setBaseline] = useState<WhatsAppFullSettings | null>(null);
  const [draft, setDraft] = useState<WhatsAppFullSettings | null>(null);
  const [pending, setPending] = useState<WhatsAppPendingReply[] | null>(null);
  const [chats, setChats] = useState<WhatsAppChat[] | null>(null);
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void api
      .whatsAppFullSettings(connectionId)
      .then((s) => {
        setBaseline(s);
        // Still on the server default (UTC)? Preselect this device's timezone — it shows as an unsaved change.
        const system = systemTimezone();
        setDraft(s.general.timezone === "UTC" && system !== "UTC" ? { ...s, general: { ...s.general, timezone: system } } : s);
        setStatus("ready");
      })
      .catch((err) => {
        setError(err instanceof ApiError ? err.message : `Could not load ${brand} settings.`);
        setStatus("error");
      });
    void api.whatsAppPendingReplies(connectionId).then(setPending).catch(() => setPending([]));
    const loadChats = isTelegram
      ? api.telegramChats(connectionId).then((list): WhatsAppChat[] => list.map((c) => ({ chatId: c.chatId, chatName: c.name, isGroup: c.isGroup, lastMessagePreview: c.lastMessagePreview ?? "", lastMessageAt: c.lastMessageAt ?? "", unreadCount: c.unreadCount })))
      : api.whatsAppChats(connectionId);
    void loadChats.then(setChats).catch(() => setChats([]));
  }, [connectionId, isTelegram, brand]);

  const dirty = useMemo(() => JSON.stringify(baseline) !== JSON.stringify(draft), [baseline, draft]);

  type SectionKey = "general" | "monitoring" | "autoReply" | "aiBehaviour" | "privacy" | "notifications" | "intelligence";
  function update<K extends SectionKey>(section: K, patch: Partial<WhatsAppFullSettings[K]>) {
    setDraft((d) => (d ? { ...d, [section]: { ...d[section], ...patch } } : d));
  }

  async function save() {
    if (!draft || !baseline) return;
    const payload: WhatsAppFullSettingsUpdate = {
      general: diffSection(baseline.general, draft.general),
      monitoring: diffSection(baseline.monitoring, draft.monitoring),
      autoReply: diffSection(baseline.autoReply, draft.autoReply),
      aiBehaviour: diffSection(baseline.aiBehaviour, draft.aiBehaviour),
      privacy: diffSection(baseline.privacy, draft.privacy),
      notifications: diffSection(baseline.notifications, draft.notifications),
      intelligence: diffSection(baseline.intelligence, draft.intelligence),
    };
    if (!Object.values(payload).some(Boolean)) return;

    setSaveState("saving");
    setError(null);
    try {
      const fresh = await api.updateWhatsAppFullSettings(connectionId, payload);
      setBaseline(fresh);
      setDraft(fresh);
      setSaveState("saved");
      setTimeout(() => setSaveState("idle"), 2000);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save settings.");
      setSaveState("error");
    }
  }

  function discard() {
    setDraft(baseline);
  }

  function resetToDefaults() {
    if (!draft) return;
    setDraft({
      ...draft,
      general: { ...draft.general, paused: false, operateContinuously: true, activeHoursStart: "09:00", activeHoursEnd: "18:00", timezone: "UTC", activeDays: [0, 1, 2, 3, 4, 5, 6] },
      monitoring: {
        monitorPersonal: true, monitorGroups: true, monitorImages: true, monitorDocuments: true,
        monitorAudio: true, monitorVideos: true, monitorLinks: true, monitorLocation: true,
        monitorCalls: true, monitorSavedContacts: true, monitorUnknownNumbers: true, monitorBusiness: true,
      },
      autoReply: { ...draft.autoReply, scope: "All", customMessageEnabled: false, customMessage: null, responseDelaySeconds: 0, cooldownSeconds: 0, batchWindowSeconds: 5, requireApproval: false, replyGateEnabled: true, bossReference: "my boss", holdingCooldownMinutes: 240, allowedChats: [], blockedChats: [], outsideHoursMessage: null, businessMessage: null },
      aiBehaviour: {
        personality: "Friendly", customPersonality: null, responseLength: "Balanced",
        customInstructions: null, neverRespondTopics: null, useConversationContext: true, contextRetentionMessages: 5,
        replyInSenderLanguage: false, summarizeLongConversations: false, aiSignatureEnabled: true, aiSignatureText: null,
      },
      privacy: { hideMessagePreviews: false, messageRetentionDays: 0, allowMessageContentAsAiContext: true, aiExcludedChats: [] },
      notifications: {
        scope: "All", tags: ["question", "deadline", "payment", "meeting", "otp", "suspicious-link"],
        email: false, whatsAppSelf: false, desktop: false, sound: true,
      },
      intelligence: { disabledTags: [] },
    });
  }

  async function approveReply(id: string) {
    await api.approveWhatsAppPendingReply(connectionId, id);
    setPending((p) => p?.filter((r) => r.id !== id) ?? null);
  }

  async function discardReply(id: string) {
    await api.discardWhatsAppPendingReply(connectionId, id);
    setPending((p) => p?.filter((r) => r.id !== id) ?? null);
  }

  async function purgeMessages() {
    if (!confirm(`Delete all stored ${brand} message content for this connection? This can't be undone.`)) return;
    await api.purgeWhatsAppMessages(connectionId);
  }

  /** Turning desktop notifications on needs the browser's permission; if it's refused the switch stays off. */
  async function setDesktop(on: boolean) {
    if (on && typeof Notification !== "undefined" && Notification.permission !== "granted") {
      const permission = await Notification.requestPermission();
      if (permission !== "granted") {
        setError("Your browser blocked notifications, so desktop notifications can't be turned on.");
        setSaveState("error");
        return;
      }
    }
    update("notifications", { desktop: on });
  }

  function requestClose() {
    if (dirty && !confirm("Discard unsaved changes?")) return;
    onClose?.();
  }

  return (
    <div
      className={inline ? "" : "fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-0 sm:p-4"}
      onClick={inline ? undefined : requestClose}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className={
          inline
            ? "flex h-[calc(100vh-9rem)] min-h-[34rem] w-full flex-col overflow-hidden rounded-[1.25rem] border border-line bg-surface"
            : "flex h-full w-full flex-col overflow-hidden rounded-none bg-surface sm:h-[96vh] sm:max-w-[96rem] sm:rounded-[1.25rem] sm:shadow-[var(--shadow)]"
        }
      >
        {!inline && <div className="flex items-start justify-between gap-4 border-b border-line p-5">
          <div>
            <h1 className="text-xl font-extrabold">{brand} Settings</h1>
            <p className="mt-0.5 text-sm text-muted">Control how Daksha interacts with your {brand} account.</p>
            {draft && (
              <div className="mt-2 flex items-center gap-2 text-sm">
                <StatusChip status={draft.status as ConnectionStatusValue} />
                <span className="text-muted">{draft.accountIdentifier}</span>
              </div>
            )}
          </div>
          <button onClick={requestClose} aria-label="Close" className="shrink-0 rounded-full p-1.5 text-muted hover:bg-surface-2 hover:text-text">
            ✕
          </button>
        </div>}

        {status === "loading" && (
          <div className="flex-1 p-8">
            <Loading />
          </div>
        )}
        {status === "error" && <p role="alert" className="flex-1 p-8 text-sm text-danger">{error}</p>}

        {status === "ready" && draft && (
          <>
            <div className="flex min-h-0 flex-1 flex-col sm:flex-row">
              <div className="shrink-0 border-b border-line p-3 sm:w-56 sm:border-b-0 sm:border-r sm:p-4">
                <input
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder="Search settings…"
                  className={`${inputClass} mb-3 text-sm`}
                />
                <nav className="flex gap-1 overflow-x-auto sm:flex-col sm:overflow-visible">
                  {TABS.map((t) => (
                    <button
                      key={t.id}
                      onClick={() => setTab(t.id)}
                      className={`shrink-0 rounded-xl px-3 py-2 text-left text-sm font-semibold transition-colors ${
                        tab === t.id ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2 hover:text-text"
                      }`}
                    >
                      {t.label}
                    </button>
                  ))}
                </nav>
              </div>

              <div className="flex-1 overflow-y-auto p-5">
                {tab === "general" && (
                  <Card>
                    <Row label="Pause all automation" hint="A quick kill-switch — stays connected, but Daksha won't auto-reply or react to anything." search={search}>
                      <Toggle checked={draft.general.paused} onChange={(v) => update("general", { paused: v })} />
                    </Row>
                    <Row label="Incoming message notifications" hint={`Bell and toast when a new ${brand} message arrives.`} search={search}>
                      <Toggle checked={draft.general.incomingNotificationsEnabled} onChange={(v) => update("general", { incomingNotificationsEnabled: v })} />
                    </Row>
                    <Row
                      label="Outgoing message notifications"
                      hint="Bell and toast when a message goes out — including replies Daksha sends for you or you send from your phone."
                      search={search}
                    >
                      <Toggle checked={draft.general.outgoingNotificationsEnabled} onChange={(v) => update("general", { outgoingNotificationsEnabled: v })} />
                    </Row>
                    <Row label="Operate continuously" hint="Off to apply the schedule below (days, hours, timezone); while on, auto-reply runs around the clock. Outside them it stays silent, or sends your out-of-hours message (Auto Reply tab)." search={search}>
                      <Toggle checked={draft.general.operateContinuously} onChange={(v) => update("general", { operateContinuously: v })} />
                    </Row>
                    <div className={draft.general.operateContinuously ? "pointer-events-none select-none opacity-50" : undefined} aria-disabled={draft.general.operateContinuously}>
                      <>
                        <Row label="Active hours start" search={search}>
                          <TimeSelect disabled={draft.general.operateContinuously} value={draft.general.activeHoursStart} onChange={(v) => update("general", { activeHoursStart: v })} />
                        </Row>
                        <Row label="Active hours end" search={search}>
                          <TimeSelect disabled={draft.general.operateContinuously} value={draft.general.activeHoursEnd} onChange={(v) => update("general", { activeHoursEnd: v })} />
                        </Row>
                        <Row label="Active days" hint="Daksha auto-replies only on the selected days." search={search}>
                          <div className="flex gap-1">
                            {["S", "M", "T", "W", "T", "F", "S"].map((label, i) => {
                              const on = draft.general.activeDays.includes(i);
                              return (
                                <button
                                  key={i}
                                  type="button"
                                  disabled={draft.general.operateContinuously}
                                  aria-pressed={on}
                                  aria-label={["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"][i]}
                                  onClick={() => update("general", { activeDays: on ? draft.general.activeDays.filter((d) => d !== i) : [...draft.general.activeDays, i].sort() })}
                                  className={`h-8 w-8 rounded-full text-xs font-semibold disabled:cursor-not-allowed ${on ? "bg-accent text-white" : "bg-surface-2 text-muted"}`}
                                >
                                  {label}
                                </button>
                              );
                            })}
                          </div>
                        </Row>
                        <Row label="Timezone" hint="IANA timezone id, e.g. Asia/Kolkata or UTC." search={search}>
                          <SearchSelect
                            value={draft.general.timezone}
                            disabled={draft.general.operateContinuously}
                            onChange={(v) => update("general", { timezone: v })}
                            className="w-64"
                            placeholder="Search timezone…"
                            options={timezoneOptions(draft.general.timezone).map((tz) => ({ value: tz, label: tz.replace(/_/g, " ") }))}
                          />
                        </Row>
                      </>
                    </div>
                  </Card>
                )}

                {tab === "monitoring" && (
                  <Card>
                    <Row label="Personal messages" hint="1:1 chats." search={search}>
                      <Toggle checked={draft.monitoring.monitorPersonal} onChange={(v) => update("monitoring", { monitorPersonal: v })} />
                    </Row>
                    <Row label="Group messages" search={search}>
                      <Toggle checked={draft.monitoring.monitorGroups} onChange={(v) => update("monitoring", { monitorGroups: v })} />
                    </Row>
                    {!isTelegram && (
<Row label="Messages from saved contacts" hint="People with a name saved in your phone's address book." search={search}>
                      <Toggle checked={draft.monitoring.monitorSavedContacts} onChange={(v) => update("monitoring", { monitorSavedContacts: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Messages from unknown numbers" hint="Anyone WhatsApp has no saved name for." search={search}>
                      <Toggle checked={draft.monitoring.monitorUnknownNumbers} onChange={(v) => update("monitoring", { monitorUnknownNumbers: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Business messages" hint="Contacts WhatsApp reports a business profile for." search={search}>
                      <Toggle checked={draft.monitoring.monitorBusiness} onChange={(v) => update("monitoring", { monitorBusiness: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Messages containing images" search={search}>
                      <Toggle checked={draft.monitoring.monitorImages} onChange={(v) => update("monitoring", { monitorImages: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Messages containing documents" search={search}>
                      <Toggle checked={draft.monitoring.monitorDocuments} onChange={(v) => update("monitoring", { monitorDocuments: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Messages containing audio or voice notes" search={search}>
                      <Toggle checked={draft.monitoring.monitorAudio} onChange={(v) => update("monitoring", { monitorAudio: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Messages containing videos" search={search}>
                      <Toggle checked={draft.monitoring.monitorVideos} onChange={(v) => update("monitoring", { monitorVideos: v })} />
                    </Row>
)}
                    <Row label="Messages containing links" search={search}>
                      <Toggle checked={draft.monitoring.monitorLinks} onChange={(v) => update("monitoring", { monitorLinks: v })} />
                    </Row>
                    {!isTelegram && (
<Row label="Messages containing location information" search={search}>
                      <Toggle checked={draft.monitoring.monitorLocation} onChange={(v) => update("monitoring", { monitorLocation: v })} />
                    </Row>
)}
                    {!isTelegram && (
<Row label="Missed calls" hint="Voice and video calls nobody picked up while Daksha was connected." search={search}>
                      <Toggle checked={draft.monitoring.monitorCalls} onChange={(v) => update("monitoring", { monitorCalls: v })} />
                    </Row>
)}
                  </Card>
                )}

                {tab === "autoReply" && (
                  <div className="space-y-4">
                    <Card>
                      <Row label="Enable auto-reply" hint="Daksha generates and sends a reply to incoming 1:1 messages automatically." search={search}>
                        <Toggle checked={draft.autoReply.enabled} onChange={(v) => update("autoReply", { enabled: v })} />
                      </Row>
                      <Row label="Also reply in groups" hint="Off by default — everyone in the group sees the reply." search={search}>
                        <Toggle checked={draft.autoReply.inGroups} onChange={(v) => update("autoReply", { inGroups: v })} />
                      </Row>
                      <Row label="Reply scope" search={search}>
                        <Dropdown value={draft.autoReply.scope} onChange={(v) => update("autoReply", { scope: v as "All" | "UnknownOnly" | "SelectedOnly" })} className="w-44" options={[{ value: "All", label: "All contacts" }, { value: "UnknownOnly", label: "Unknown contacts only" }, { value: "SelectedOnly", label: "Selected chats only" }]} />
                      </Row>
                      {draft.autoReply.scope === "SelectedOnly" && (
                        <PickerRow
                          label="Reply only to these contacts and groups"
                          hint="Daksha only auto-replies in the chats ticked here."
                          search={search}
                          chats={chats}
                          selected={draft.autoReply.allowedChats}
                          onChange={(v) => update("autoReply", { allowedChats: v })}
                        />
                      )}
                      <PickerRow
                        label="Never auto-reply to these contacts and groups"
                        hint="Applies whatever the reply scope above is."
                        search={search}
                        chats={chats}
                        selected={draft.autoReply.blockedChats}
                        onChange={(v) => update("autoReply", { blockedChats: v })}
                      />
                      <Row label="Use a fixed custom message instead of AI" search={search}>
                        <Toggle checked={draft.autoReply.customMessageEnabled} onChange={(v) => update("autoReply", { customMessageEnabled: v })} />
                      </Row>
                      {draft.autoReply.customMessageEnabled && (
                        <Row label="Custom message" search={search}>
                          <textarea
                            value={draft.autoReply.customMessage ?? ""}
                            onChange={(e) => update("autoReply", { customMessage: e.target.value })}
                            rows={2}
                            className={`${inputClass} w-64`}
                          />
                        </Row>
                      )}
                      <Row
                        label="Out-of-hours message"
                        hint="Sent instead of the AI or the message above when someone writes outside your active hours (set on the General tab). Leave empty to turn off."
                        search={search}
                      >
                        <textarea
                          value={draft.autoReply.outsideHoursMessage ?? ""}
                          onChange={(e) => update("autoReply", { outsideHoursMessage: e.target.value })}
                          rows={2}
                          className={`${inputClass} w-64`}
                        />
                      </Row>
                      {!isTelegram && (
<Row
                        label="Message for business contacts"
                        hint="Sent instead of the AI reply when the sender has a WhatsApp Business profile. Leave empty to treat them like anyone else."
                        search={search}
                      >
                        <textarea
                          value={draft.autoReply.businessMessage ?? ""}
                          onChange={(e) => update("autoReply", { businessMessage: e.target.value })}
                          rows={2}
                          className={`${inputClass} w-64`}
                        />
                      </Row>
)}
                      <Row
                        label="Only auto-reply to simple messages"
                        hint="The reply gate. The AI answers greetings and thanks only. Anything that asks for a decision, money, time, a document or any information is NOT answered by the AI: the sender is told you'll get back to them, and you get a task. Reels, photos, videos, voice notes, stickers and emoji never get a reply."
                        search={search}
                      >
                        <Toggle checked={draft.autoReply.replyGateEnabled} onChange={(v) => update("autoReply", { replyGateEnabled: v })} />
                      </Row>
                      <Row label="What the assistant calls you" hint="Used in the holding reply, e.g. “I'll let my boss know and my boss will get back to you.”" search={search}>
                        <input
                          value={draft.autoReply.bossReference}
                          maxLength={60}
                          disabled={!draft.autoReply.replyGateEnabled}
                          onChange={(e) => update("autoReply", { bossReference: e.target.value })}
                          className={`${inputClass} w-48`}
                          aria-label="What the assistant calls you"
                        />
                      </Row>
                      <Row label="Holding reply at most once every (minutes)" hint="So a sender isn't told “I'll check with my boss” on every message. It resets as soon as you reply yourself. 0 = every time." search={search}>
                        <input
                          type="number"
                          min={0}
                          max={10080}
                          value={draft.autoReply.holdingCooldownMinutes}
                          disabled={!draft.autoReply.replyGateEnabled}
                          onChange={(e) => update("autoReply", { holdingCooldownMinutes: Math.min(10080, Math.max(0, Number(e.target.value))) })}
                          className={`${inputClass} w-24`}
                          aria-label="Holding reply interval in minutes"
                        />
                      </Row>
                      <Row label="Response delay (seconds)" hint="Wait this long before sending the reply." search={search}>
                        <input
                          type="number"
                          min={0}
                          value={draft.autoReply.responseDelaySeconds}
                          onChange={(e) => update("autoReply", { responseDelaySeconds: Number(e.target.value) })}
                          className={`${inputClass} w-24`}
                        />
                      </Row>
                      <Row label="Group quick messages (seconds)" hint="When several messages arrive close together, wait this long after the last one, read them all and send a single reply. 0 replies to each message separately." search={search}>
                        <input
                          type="number"
                          min={0}
                          max={60}
                          value={draft.autoReply.batchWindowSeconds}
                          onChange={(e) => update("autoReply", { batchWindowSeconds: Math.min(60, Math.max(0, Number(e.target.value))) })}
                          className={`${inputClass} w-24`}
                        />
                      </Row>
                      <Row label="Cooldown between replies (seconds)" hint="Don't auto-reply again to the same chat within this window." search={search}>
                        <input
                          type="number"
                          min={0}
                          value={draft.autoReply.cooldownSeconds}
                          onChange={(e) => update("autoReply", { cooldownSeconds: Number(e.target.value) })}
                          className={`${inputClass} w-24`}
                        />
                      </Row>
                      <Row label="Require approval before sending" hint="Daksha drafts the reply but waits for you to approve it below." search={search}>
                        <Toggle checked={draft.autoReply.requireApproval} onChange={(v) => update("autoReply", { requireApproval: v })} />
                      </Row>
                    </Card>

                    <div>
                      <h2 className="text-sm font-semibold">Pending replies awaiting approval</h2>
                      {!pending ? (
                        <p className="mt-2 text-sm text-muted">Loading…</p>
                      ) : pending.length === 0 ? (
                        <p className="mt-2 text-sm text-muted">Nothing waiting right now.</p>
                      ) : (
                        <ul className="mt-2 space-y-2">
                          {pending.map((r) => (
                            <li key={r.id} className="rounded-xl bg-surface-2 p-3 text-sm">
                              <p className="text-xs font-semibold text-muted">{r.chatName ?? r.chatId}</p>
                              <p className="mt-0.5">{r.text}</p>
                              <div className="mt-2 flex gap-2">
                                <button onClick={() => void approveReply(r.id)} className={`${buttonClass} px-3 py-1.5 text-xs`}>Send</button>
                                <button onClick={() => void discardReply(r.id)} className={`${secondaryButtonClass} px-3 py-1.5 text-xs`}>Discard</button>
                              </div>
                            </li>
                          ))}
                        </ul>
                      )}
                    </div>
                  </div>
                )}

                {tab === "aiBehaviour" && (
                  <Card>
                    <Row label="Response personality" search={search}>
                      <Dropdown value={draft.aiBehaviour.personality} onChange={(v) => update("aiBehaviour", { personality: v as WhatsAppFullSettings["aiBehaviour"]["personality"] })} className="w-40" options={[{ value: "Professional", label: "Professional" }, { value: "Friendly", label: "Friendly" }, { value: "Casual", label: "Casual" }, { value: "Formal", label: "Formal" }, { value: "Custom", label: "Custom" }]} />
                    </Row>
                    {draft.aiBehaviour.personality === "Custom" && (
                      <Row label="Custom personality" search={search}>
                        <input
                          value={draft.aiBehaviour.customPersonality ?? ""}
                          onChange={(e) => update("aiBehaviour", { customPersonality: e.target.value })}
                          className={`${inputClass} w-64`}
                        />
                      </Row>
                    )}
                    <Row label="Response length" search={search}>
                      <Dropdown value={draft.aiBehaviour.responseLength} onChange={(v) => update("aiBehaviour", { responseLength: v as WhatsAppFullSettings["aiBehaviour"]["responseLength"] })} className="w-36" options={[{ value: "Short", label: "Short" }, { value: "Balanced", label: "Balanced" }, { value: "Detailed", label: "Detailed" }]} />
                    </Row>
                    <Row label="Reply in the sender's language" hint="Detects the language and script of the incoming message and answers in the same one." search={search}>
                      <Toggle checked={draft.aiBehaviour.replyInSenderLanguage} onChange={(v) => update("aiBehaviour", { replyInSenderLanguage: v })} />
                    </Row>
                    <Row label="Use previous conversation as context" search={search}>
                      <Toggle checked={draft.aiBehaviour.useConversationContext} onChange={(v) => update("aiBehaviour", { useConversationContext: v })} />
                    </Row>
                    {draft.aiBehaviour.useConversationContext && (
                      <Row label="Messages of context to retain" search={search}>
                        <input
                          type="number"
                          min={0}
                          max={30}
                          value={draft.aiBehaviour.contextRetentionMessages}
                          onChange={(e) => update("aiBehaviour", { contextRetentionMessages: Number(e.target.value) })}
                          className={`${inputClass} w-24`}
                        />
                      </Row>
                    )}
                    <Row label="Summarize long conversations before responding" hint="Condenses messages older than the context window into a short summary (one extra AI request per reply)." search={search}>
                      <Toggle checked={draft.aiBehaviour.summarizeLongConversations} onChange={(v) => update("aiBehaviour", { summarizeLongConversations: v })} />
                    </Row>
                    <Row label="Mark replies as AI-generated" hint="Adds a line under every AI reply so the person knows it came from an assistant." search={search}>
                      <Toggle checked={draft.aiBehaviour.aiSignatureEnabled} onChange={(v) => update("aiBehaviour", { aiSignatureEnabled: v })} />
                    </Row>
                    {draft.aiBehaviour.aiSignatureEnabled && (
                      <Row label="AI reply line" hint="Your own wording, emoji included. Leave empty for “🤖 Reply by AI Assistant”." search={search}>
                        <input
                          value={draft.aiBehaviour.aiSignatureText ?? ""}
                          onChange={(e) => update("aiBehaviour", { aiSignatureText: e.target.value })}
                          maxLength={80}
                          placeholder="🤖 Reply by AI Assistant"
                          className={`${inputClass} w-64`}
                        />
                      </Row>
                    )}
                    <Row label="Custom instructions" hint={`Tell Daksha how you want it to communicate on ${brand}.`} search={search}>
                      <textarea
                        value={draft.aiBehaviour.customInstructions ?? ""}
                        onChange={(e) => update("aiBehaviour", { customInstructions: e.target.value })}
                        rows={3}
                        className={`${inputClass} w-64`}
                      />
                    </Row>
                    <Row label="Topics Daksha should never respond to" hint="Comma-separated. The AI stays silent on these instead of auto-replying." search={search}>
                      <textarea
                        value={draft.aiBehaviour.neverRespondTopics ?? ""}
                        onChange={(e) => update("aiBehaviour", { neverRespondTopics: e.target.value })}
                        rows={2}
                        className={`${inputClass} w-64`}
                      />
                    </Row>
                  </Card>
                )}

                {tab === "privacy" && (
                  <div className="space-y-4">
                    <Card>
                      <Row label="Hide message previews in notifications" hint={`Shows just 'New ${brand} message' without the text.`} search={search}>
                        <Toggle checked={draft.privacy.hideMessagePreviews} onChange={(v) => update("privacy", { hideMessagePreviews: v })} />
                      </Row>
                      <Row label="Allow message content to be used as AI context" hint="Off disables conversation history in auto-reply, regardless of the AI Behaviour setting." search={search}>
                        <Toggle checked={draft.privacy.allowMessageContentAsAiContext} onChange={(v) => update("privacy", { allowMessageContentAsAiContext: v })} />
                      </Row>
                      <Row
                        label="Message retention (days)"
                        hint="0 = keep forever. Stored for reference; not yet auto-enforced — use Delete below for an immediate purge."
                        search={search}
                      >
                        <input
                          type="number"
                          min={0}
                          value={draft.privacy.messageRetentionDays}
                          onChange={(e) => update("privacy", { messageRetentionDays: Number(e.target.value) })}
                          className={`${inputClass} w-24`}
                        />
                      </Row>
                      <PickerRow
                        label="Exclude specific contacts and groups from AI processing"
                        hint="Daksha never auto-replies in, or sends the messages of, the chats ticked here to the AI."
                        search={search}
                        chats={chats}
                        selected={draft.privacy.aiExcludedChats}
                        onChange={(v) => update("privacy", { aiExcludedChats: v })}
                      />
                    </Card>
                    <div className="rounded-[1.25rem] border border-line p-4">
                      <p className="text-sm font-semibold">Delete stored message content</p>
                      <p className="mt-0.5 text-xs text-muted">
                        Permanently deletes every message Daksha has stored for this {brand} connection. This can&rsquo;t be undone.
                      </p>
                      <button onClick={() => void purgeMessages()} className={`${secondaryButtonClass} mt-3 px-4 py-2 text-sm`}>
                        Delete stored messages now
                      </button>
                    </div>
                    <div className="rounded-[1.25rem] border border-line p-4 text-xs text-muted">
                      <p className="font-semibold text-text">What Daksha stores</p>
                      <p className="mt-1">
                        Message text, sender/chat identifiers, and reactions for this {brand} connection, kept so the chat view and
                        auto-reply context can work. Connection credentials are encrypted separately. See
                        docs/integrations/whatsapp/limitations.md for the full picture.
                      </p>
                    </div>
                  </div>
                )}

                {tab === "contacts" && <WhatsAppContactsTab connectionId={connectionId} chats={chats} search={search} />}

                {tab === "notifications" && (
                  <div className="space-y-4">
                    <Card>
                      <Row label="Notify me about" hint="Everything, or only messages matching the triggers below." search={search}>
                        <Dropdown value={draft.notifications.scope} onChange={(v) => update("notifications", { scope: v as "All" | "Filtered" })} className="w-44" options={[{ value: "All", label: "Every incoming message" }, { value: "Filtered", label: "Only selected types" }]} />
                      </Row>
                    </Card>
                    {draft.notifications.scope === "Filtered" && (
                      <Card>
                        {NOTIFY_TRIGGERS.map((t) => (
                          <Row key={t.tag} label={t.label} search={search}>
                            <Toggle
                              checked={draft.notifications.tags.includes(t.tag)}
                              onChange={(on) =>
                                update("notifications", { tags: on ? [...draft.notifications.tags, t.tag] : draft.notifications.tags.filter((x) => x !== t.tag) })
                              }
                            />
                          </Row>
                        ))}
                        <p className="py-2.5 text-xs text-muted">
                          High-priority chats (Contacts &amp; Groups) always notify. Detection categories can be switched off under Message Intelligence.
                        </p>
                      </Card>
                    )}
                    <Card>
                      <Row label="Daksha dashboard" hint="The bell and pop-up toasts — controlled by &ldquo;Incoming message notifications&rdquo; on the General tab." search={search}>
                        <Toggle checked={draft.general.incomingNotificationsEnabled} onChange={(v) => update("general", { incomingNotificationsEnabled: v })} />
                      </Row>
                      <Row label="Desktop notifications" hint="Browser notifications while Daksha is open in a tab. Your browser will ask for permission." search={search}>
                        <Toggle checked={draft.notifications.desktop} onChange={(v) => void setDesktop(v)} />
                      </Row>
                      <Row label="Email" hint="Also email each notification to your account's address." search={search}>
                        <Toggle checked={draft.notifications.email} onChange={(v) => update("notifications", { email: v })} />
                      </Row>
                      {!isTelegram && (
<Row label="WhatsApp (message yourself)" hint="Also send each notification to your own WhatsApp number's &ldquo;Message yourself&rdquo; chat." search={search}>
                        <Toggle checked={draft.notifications.whatsAppSelf} onChange={(v) => update("notifications", { whatsAppSelf: v })} />
                      </Row>
)}
                      <Row label="Notification sound" search={search}>
                        <Toggle checked={draft.notifications.sound} onChange={(v) => update("notifications", { sound: v })} />
                      </Row>
                    </Card>
                  </div>
                )}

                {tab === "intelligence" && (
                  <WhatsAppIntelligenceTab
                    connectionId={connectionId}
                    disabledTags={draft.intelligence.disabledTags}
                    onChange={(next) => update("intelligence", { disabledTags: next })}
                    search={search}
                  />
                )}

                {tab === "automation" && <WhatsAppAutomationTab connectionId={connectionId} chats={chats} />}
              </div>
            </div>

            <div className="flex items-center justify-between gap-3 border-t border-line p-4">
              <div className="text-sm">
                {saveState === "saved" && <span className="text-lime">Saved ✓</span>}
                {saveState === "error" && error && <span className="text-danger">{error}</span>}
              </div>
              <div className="flex gap-2">
                <button onClick={resetToDefaults} className={`${secondaryButtonClass} px-4 py-2 text-sm`}>Reset to Defaults</button>
                <button onClick={discard} disabled={!dirty} className={`${secondaryButtonClass} px-4 py-2 text-sm disabled:opacity-50`}>Discard Changes</button>
                <button onClick={() => void save()} disabled={!dirty || saveState === "saving"} className={`${buttonClass} px-5 py-2 text-sm`}>
                  {saveState === "saving" ? "Saving…" : "Save Changes"}
                </button>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
