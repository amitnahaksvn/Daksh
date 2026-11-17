"use client";
import { useCallback, useEffect, useState } from "react";
import { Dropdown } from "@/components/ui";
import { Card, Row, Toggle } from "@/components/WhatsAppSettingsParts";
import { api, type WhatsAppInsight } from "@/lib/api";

export const INTEL_TAGS: { tag: string; label: string; hint: string }[] = [
  { tag: "question", label: "Questions needing an answer", hint: "Messages that ask something." },
  { tag: "task", label: "Tasks and action items", hint: "\"Please send…\", \"don't forget to…\"" },
  { tag: "meeting", label: "Meeting requests", hint: "Meetings, calls, Zoom / Meet / Teams invites." },
  { tag: "appointment", label: "Appointments", hint: "Bookings, reservations, doctor / dentist slots." },
  { tag: "deadline", label: "Dates and deadlines", hint: "\"Due by…\", ASAP, urgent." },
  { tag: "reminder", label: "Reminders", hint: "\"Remind me…\"" },
  { tag: "payment", label: "Payment requests", hint: "Amounts, invoices, UPI / GPay / Paytm." },
  { tag: "transaction", label: "Transaction information", hint: "Debited / credited alerts, reference numbers." },
  { tag: "address", label: "Addresses and locations", hint: "Street addresses, pincodes." },
  { tag: "contact", label: "Phone numbers and email addresses", hint: "Extracted into the detection list." },
  { tag: "document", label: "Important documents", hint: "Files sent as documents." },
  { tag: "delivery", label: "Delivery updates", hint: "Shipped, out for delivery, tracking." },
  { tag: "travel", label: "Travel information", hint: "Flights, PNRs, tickets, hotels." },
  { tag: "job", label: "Job opportunities", hint: "Hiring, vacancies, interviews." },
  { tag: "announcement", label: "Important announcements", hint: "Notices and circulars." },
  { tag: "otp", label: "OTPs and verification codes", hint: "Tagged for notification rules only — the code itself is never stored." },
  { tag: "suspicious-link", label: "Suspicious links", hint: "Link shorteners, odd domains, phishing wording." },
  { tag: "link", label: "Links", hint: "Any web link." },
  { tag: "attachment", label: "Attachments", hint: "Photos, videos, voice notes, documents, stickers." },
];

const KIND_FILTERS = [
  { value: "", label: "Everything" },
  { value: "Task", label: "Tasks" },
  { value: "Reminder", label: "Reminders" },
  { value: "Summary", label: "Summaries" },
  { value: "Saved", label: "Saved messages" },
];

const KIND_LABEL: Record<string, string> = Object.fromEntries(INTEL_TAGS.map((t) => [t.tag, t.tag.replace("-", " ")]));

function InsightsList({ connectionId }: { connectionId: string }) {
  const [kind, setKind] = useState("");
  const [items, setItems] = useState<WhatsAppInsight[] | null>(null);

  const load = useCallback(() => {
    void api.whatsAppInsights(connectionId, kind || undefined).then(setItems).catch(() => setItems([]));
  }, [connectionId, kind]);

  useEffect(() => {
    const timer = setTimeout(load, 0);
    return () => clearTimeout(timer);
  }, [load]);

  async function toggleDone(id: string) {
    await api.toggleWhatsAppInsightDone(connectionId, id);
    load();
  }
  async function remove(id: string) {
    await api.deleteWhatsAppInsight(connectionId, id);
    setItems((list) => list?.filter((i) => i.id !== id) ?? null);
  }

  return (
    <div>
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold">What Daksha has picked up</h2>
        <Dropdown value={kind} onChange={setKind} className="w-44" options={KIND_FILTERS} />
      </div>
      {!items ? (
        <p className="mt-2 text-sm text-muted">Loading…</p>
      ) : items.length === 0 ? (
        <p className="mt-2 text-sm text-muted">Nothing yet. Detections, tasks, reminders and summaries show up here as messages arrive.</p>
      ) : (
        <ul className="mt-2 space-y-2">
          {items.map((i) => (
            <li key={i.id} className="rounded-xl bg-surface-2 p-3 text-sm">
              <div className="flex items-center gap-2">
                <span className="ai-mono rounded-full bg-surface px-2 py-0.5 text-[10px] font-semibold uppercase text-muted">{KIND_LABEL[i.kind] ?? i.kind}</span>
                <span className="min-w-0 flex-1 truncate text-xs text-muted">{i.chatName ?? "Unknown contact"} · {new Date(i.createdAt).toLocaleString()}</span>
                {(i.kind === "Task" || i.kind === "Reminder") && (
                  <button onClick={() => void toggleDone(i.id)} className="text-xs font-semibold text-accent">{i.done ? "Reopen" : "Done"}</button>
                )}
                <button onClick={() => void remove(i.id)} aria-label="Delete" className="text-xs text-muted hover:text-text">✕</button>
              </div>
              <p className={`mt-1.5 ${i.done ? "line-through opacity-60" : ""}`}>{i.text}</p>
              {i.value && <p className="mt-1 text-xs font-semibold">{i.value}</p>}
              {i.dueAt && <p className="mt-1 text-xs text-muted">Due {new Date(i.dueAt).toLocaleString()}</p>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Which kinds of information Daksha picks out of incoming messages. Detection is pattern-based (keywords and
 *  regular expressions, no AI call), so it's fast and free but can miss things or over-match. */
export function WhatsAppIntelligenceTab({
  connectionId,
  disabledTags,
  onChange,
  search,
}: {
  connectionId: string;
  disabledTags: string[];
  onChange: (next: string[]) => void;
  search: string;
}) {
  return (
    <div className="space-y-4">
      <Card>
        {INTEL_TAGS.map((t) => (
          <Row key={t.tag} label={t.label} hint={t.hint} search={search}>
            <Toggle
              checked={!disabledTags.includes(t.tag)}
              onChange={(on) => onChange(on ? disabledTags.filter((x) => x !== t.tag) : [...disabledTags, t.tag])}
            />
          </Row>
        ))}
      </Card>
      <p className="text-xs text-muted">
        Detection uses keyword and pattern matching on message text, not AI, so it can miss things or flag the odd false positive. Turning a
        category off stops it being tagged, extracted, or used in notification filters and automation rules.
      </p>
      <InsightsList connectionId={connectionId} />
    </div>
  );
}
