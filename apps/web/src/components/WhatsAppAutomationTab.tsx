"use client";
import { useEffect, useState } from "react";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/ui";
import { chatDisplayName } from "@/components/WhatsAppChatsPanel";
import { INTEL_TAGS } from "@/components/WhatsAppIntelligenceTab";
import { Toggle } from "@/components/WhatsAppSettingsParts";
import { api, ApiError, type WhatsAppAutomationRule, type WhatsAppChat, type WhatsAppRuleAction } from "@/lib/api";

const TRIGGERS: { value: string; label: string }[] = [
  { value: "NewMessage", label: "A new message arrives" },
  { value: "FromContact", label: "A message arrives from a specific contact" },
  { value: "InGroup", label: "A message arrives in a specific group" },
  { value: "ContainsKeyword", label: "A message contains a keyword" },
  { value: "ContainsQuestion", label: "A message contains a question" },
  { value: "HasAttachment", label: "A message contains an attachment" },
  { value: "OutsideHours", label: "A message arrives outside working hours" },
];

const ACTIONS: { type: string; label: string; hint?: string; needsText?: string }[] = [
  { type: "AiReply", label: "Generate an AI response and send it" },
  { type: "FixedReply", label: "Send an automatic response", needsText: "Reply text" },
  { type: "CreateReminder", label: "Create a reminder", hint: "Fires an hour later — or at \"in 2 hours\" / \"tomorrow\" if the message says so.", needsText: "Reminder text (optional — defaults to the message)" },
  { type: "ExtractActionItem", label: "Extract an action item", hint: "Uses the AI to pull out the task the message asks of you." },
  { type: "Notify", label: "Notify me", hint: "Forces a notification even if filters would suppress it." },
  { type: "Summarize", label: "Summarize the conversation" },
  { type: "SaveMessage", label: "Save the message" },
  { type: "ForwardToTasks", label: "Add to my WhatsApp task list", hint: "Daksha's main task list isn't available yet, so this goes to the list under Message Intelligence." },
];

const blank = (): Draft => ({ name: "", trigger: "NewMessage", chatIds: [], keyword: "", category: "", priority: "Any", actions: [] });
type Draft = { id?: string; name: string; trigger: string; chatIds: string[]; keyword: string; category: string; priority: "Any" | "High"; actions: WhatsAppRuleAction[] };

function summary(rule: WhatsAppAutomationRule): string {
  const when = TRIGGERS.find((t) => t.value === rule.trigger)?.label ?? rule.trigger;
  const conds = [
    rule.keyword && `text contains "${rule.keyword}"`,
    rule.category && `category is ${rule.category}`,
    rule.chatIds.length > 0 && `${rule.chatIds.length} chat${rule.chatIds.length > 1 ? "s" : ""} selected`,
    rule.priority === "High" && "high-priority chat",
  ].filter(Boolean);
  const then = rule.actions.map((a) => ACTIONS.find((x) => x.type === a.type)?.label ?? a.type).join(", ");
  return `WHEN ${when.toLowerCase()}${conds.length ? ` · IF ${conds.join(" and ")}` : ""} · THEN ${then.toLowerCase()}`;
}

function RuleEditor({ draft, chats, saving, error, onChange, onSave, onCancel }: {
  draft: Draft;
  chats: WhatsAppChat[] | null;
  saving: boolean;
  error: string | null;
  onChange: (d: Draft) => void;
  onSave: () => void;
  onCancel: () => void;
}) {
  const chatChoices = (chats ?? []).filter((c) => (draft.trigger === "InGroup" ? c.isGroup : draft.trigger === "FromContact" ? !c.isGroup : true));
  const hasAction = (type: string) => draft.actions.some((a) => a.type === type);
  const setAction = (type: string, on: boolean) =>
    onChange({ ...draft, actions: on ? [...draft.actions, { type, text: null }] : draft.actions.filter((a) => a.type !== type) });
  const setActionText = (type: string, text: string) =>
    onChange({ ...draft, actions: draft.actions.map((a) => (a.type === type ? { ...a, text } : a)) });

  return (
    <div className="space-y-4 rounded-[1.25rem] border border-accent p-4 text-sm">
      <input value={draft.name} onChange={(e) => onChange({ ...draft, name: e.target.value })} placeholder="Rule name" maxLength={80} className={inputClass} />

      <section>
        <p className="text-xs font-bold uppercase tracking-wide text-muted">When</p>
        <select value={draft.trigger} onChange={(e) => onChange({ ...draft, trigger: e.target.value, chatIds: [] })} className={`${inputClass} mt-1.5`}>
          {TRIGGERS.map((t) => (
            <option key={t.value} value={t.value}>{t.label}</option>
          ))}
        </select>
      </section>

      <section className="space-y-2">
        <p className="text-xs font-bold uppercase tracking-wide text-muted">If (all optional — every one you fill in must match)</p>
        {(draft.trigger === "FromContact" || draft.trigger === "InGroup" || chatChoices.length > 0) && (
          <div>
            <p className="text-xs text-muted">
              {draft.trigger === "FromContact" ? "Contact matches (none selected = any contact)" : draft.trigger === "InGroup" ? "Group matches (none selected = any group)" : "Only in these chats (none selected = anywhere)"}
            </p>
            {chatChoices.length === 0 ? (
              <p className="mt-1 text-xs text-muted">No matching chats yet.</p>
            ) : (
              <ul className="mt-1 max-h-32 space-y-0.5 overflow-y-auto rounded-xl border border-line p-1.5">
                {chatChoices.map((c) => (
                  <li key={c.chatId}>
                    <label className="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1 hover:bg-surface-2">
                      <input
                        type="checkbox"
                        checked={draft.chatIds.includes(c.chatId)}
                        onChange={(e) => onChange({ ...draft, chatIds: e.target.checked ? [...draft.chatIds, c.chatId] : draft.chatIds.filter((x) => x !== c.chatId) })}
                      />
                      <span className="truncate">{chatDisplayName(c)}</span>
                    </label>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
        <input
          value={draft.keyword}
          onChange={(e) => onChange({ ...draft, keyword: e.target.value })}
          placeholder={draft.trigger === "ContainsKeyword" ? "Keyword (required)" : "Message contains this text (optional)"}
          className={inputClass}
        />
        <div className="flex gap-2">
          <select value={draft.category} onChange={(e) => onChange({ ...draft, category: e.target.value })} className={inputClass}>
            <option value="">Any category</option>
            {INTEL_TAGS.map((t) => (
              <option key={t.tag} value={t.tag}>Category: {t.label}</option>
            ))}
          </select>
          <select value={draft.priority} onChange={(e) => onChange({ ...draft, priority: e.target.value as "Any" | "High" })} className={`${inputClass} w-48`}>
            <option value="Any">Any priority</option>
            <option value="High">High-priority chats only</option>
          </select>
        </div>
      </section>

      <section className="space-y-2">
        <p className="text-xs font-bold uppercase tracking-wide text-muted">Then</p>
        {ACTIONS.map((a) => (
          <div key={a.type}>
            <label className="flex cursor-pointer items-start gap-2">
              <input type="checkbox" className="mt-1" checked={hasAction(a.type)} onChange={(e) => setAction(a.type, e.target.checked)} />
              <span>
                <span className="font-semibold">{a.label}</span>
                {a.hint && <span className="block text-xs text-muted">{a.hint}</span>}
              </span>
            </label>
            {a.needsText && hasAction(a.type) && (
              <input
                value={draft.actions.find((x) => x.type === a.type)?.text ?? ""}
                onChange={(e) => setActionText(a.type, e.target.value)}
                placeholder={a.needsText}
                className={`${inputClass} mt-1.5 ml-6 w-[calc(100%-1.5rem)]`}
              />
            )}
          </div>
        ))}
      </section>

      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      <div className="flex gap-2">
        <button onClick={onSave} disabled={saving} className={`${buttonClass} px-5 py-2`}>{saving ? "Saving…" : draft.id ? "Save rule" : "Add rule"}</button>
        <button onClick={onCancel} className={`${secondaryButtonClass} px-5 py-2`}>Cancel</button>
      </div>
    </div>
  );
}

/** Custom WHEN / IF / THEN rules. Rules save and run independently of the Save Changes button, and a rule that
 *  sends a reply takes the place of the normal auto-reply for that message. */
export function WhatsAppAutomationTab({ connectionId, chats }: { connectionId: string; chats: WhatsAppChat[] | null }) {
  const [rules, setRules] = useState<WhatsAppAutomationRule[] | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void api.whatsAppAutomationRules(connectionId).then(setRules).catch(() => setRules([]));
  }, [connectionId]);

  async function save() {
    if (!draft) return;
    setSaving(true);
    setError(null);
    const input = {
      name: draft.name,
      trigger: draft.trigger,
      chatIds: draft.chatIds,
      keyword: draft.keyword,
      category: draft.category,
      priority: draft.priority,
      actions: draft.actions.map((a) => ({ type: a.type, text: a.text?.trim() || null })),
    };
    try {
      const saved = draft.id
        ? await api.updateWhatsAppAutomationRule(connectionId, draft.id, input)
        : await api.createWhatsAppAutomationRule(connectionId, input);
      setRules((list) => (draft.id ? (list ?? []).map((r) => (r.id === saved.id ? saved : r)) : [...(list ?? []), saved]));
      setDraft(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save the rule.");
    } finally {
      setSaving(false);
    }
  }

  async function toggle(rule: WhatsAppAutomationRule, enabled: boolean) {
    setRules((list) => list?.map((r) => (r.id === rule.id ? { ...r, enabled } : r)) ?? null);
    try {
      await api.updateWhatsAppAutomationRule(connectionId, rule.id, { enabled });
    } catch {
      setRules((list) => list?.map((r) => (r.id === rule.id ? { ...r, enabled: rule.enabled } : r)) ?? null);
    }
  }

  async function remove(rule: WhatsAppAutomationRule) {
    if (!confirm(`Delete the rule "${rule.name}"?`)) return;
    await api.deleteWhatsAppAutomationRule(connectionId, rule.id);
    setRules((list) => list?.filter((r) => r.id !== rule.id) ?? null);
  }

  const edit = (r: WhatsAppAutomationRule): Draft => ({
    id: r.id, name: r.name, trigger: r.trigger, chatIds: r.chatIds, keyword: r.keyword ?? "", category: r.category ?? "", priority: r.priority, actions: r.actions,
  });

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <p className="text-xs text-muted">Rules save immediately. A rule that sends a reply replaces the normal auto-reply for that message.</p>
        {!draft && <button onClick={() => { setError(null); setDraft(blank()); }} className={`${buttonClass} shrink-0 px-4 py-2 text-sm`}>Add rule</button>}
      </div>

      {draft && <RuleEditor draft={draft} chats={chats} saving={saving} error={error} onChange={setDraft} onSave={() => void save()} onCancel={() => setDraft(null)} />}

      {!rules ? (
        <p className="text-sm text-muted">Loading…</p>
      ) : rules.length === 0 && !draft ? (
        <p className="rounded-[1.25rem] border border-dashed border-line p-6 text-center text-sm text-muted">No rules yet. Add one to automate how Daksha handles specific messages.</p>
      ) : (
        <ul className="space-y-2">
          {rules.map((r) => (
            <li key={r.id} className="rounded-[1.25rem] border border-line p-3 text-sm">
              <div className="flex items-center gap-3">
                <span className="min-w-0 flex-1 truncate font-semibold">{r.name}</span>
                <Toggle checked={r.enabled} onChange={(v) => void toggle(r, v)} />
                <button onClick={() => { setError(null); setDraft(edit(r)); }} className="text-xs font-semibold text-accent">Edit</button>
                <button onClick={() => void remove(r)} className="text-xs font-semibold text-danger">Delete</button>
              </div>
              <p className="mt-1.5 text-xs text-muted">{summary(r)}</p>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
