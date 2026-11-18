"use client";
import { chatDisplayName } from "@/components/WhatsAppChatsPanel";
import type { WhatsAppChat } from "@/lib/api";

export function Toggle({ checked, onChange, disabled, label }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; label?: string }) {
  return (
    <label className={`relative inline-flex h-6 w-11 shrink-0 items-center ${disabled ? "cursor-not-allowed opacity-50" : "cursor-pointer"}`}>
      <input
        type="checkbox"
        role="switch"
        aria-label={label}
        aria-checked={checked}
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className="peer sr-only"
      />
      <span aria-hidden className="absolute inset-0 rounded-full border border-line bg-surface-2 transition-colors peer-checked:border-transparent peer-checked:bg-accent peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent" />
      <span
        aria-hidden
        className={`absolute left-0.5 flex h-5 w-5 items-center justify-center rounded-full bg-muted shadow transition-transform ${checked ? "translate-x-5 bg-accent-contrast" : ""}`}
      >
        {checked && (
          <svg viewBox="0 0 16 16" className="h-3 w-3 text-accent" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3.5 8.5l3 3 6-6.5" />
          </svg>
        )}
      </span>
    </label>
  );
}

export function Row({
  label,
  hint,
  search,
  disabledReason,
  children,
}: {
  label: string;
  hint?: string;
  search: string;
  disabledReason?: string;
  children: React.ReactNode;
}) {
  if (search && !label.toLowerCase().includes(search.toLowerCase())) return null;
  return (
    <div className="flex items-start justify-between gap-4 border-b border-line py-2.5">
      <div className="min-w-0">
        <p className="text-sm font-semibold">{label}</p>
        {hint && <p className="mt-0.5 text-xs text-muted">{hint}</p>}
        {disabledReason && <p className="mt-0.5 text-xs italic text-muted">Not available yet — {disabledReason}</p>}
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}

/** A multi-select of the chats Daksha has seen — there's no phone address book to browse, so these are the
 *  contacts and groups that have actually exchanged messages with this account. */
export function PickerRow({
  label,
  hint,
  search,
  chats,
  selected,
  onChange,
}: {
  label: string;
  hint: string;
  search: string;
  chats: WhatsAppChat[] | null;
  selected: string[];
  onChange: (next: string[]) => void;
}) {
  if (search && !label.toLowerCase().includes(search.toLowerCase())) return null;
  return (
    <div className="border-b border-line py-2.5 lg:col-span-2">
      <p className="text-sm font-semibold">{label}</p>
      <p className="mt-0.5 text-xs text-muted">{hint}</p>
      {!chats ? (
        <p className="mt-2 text-xs text-muted">Loading chats…</p>
      ) : chats.length === 0 ? (
        <p className="mt-2 text-xs text-muted">No chats yet — they appear here once messages have been exchanged.</p>
      ) : (
        <ul className="mt-2 max-h-40 space-y-0.5 overflow-y-auto rounded-xl border border-line p-1.5">
          {chats.map((c) => (
            <li key={c.chatId}>
              <label className="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1 text-sm hover:bg-surface-2">
                <input
                  type="checkbox"
                  checked={selected.includes(c.chatId)}
                  onChange={(e) => onChange(e.target.checked ? [...selected, c.chatId] : selected.filter((id) => id !== c.chatId))}
                />
                <span className="truncate">{chatDisplayName(c)}</span>
                {c.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
              </label>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function Card({ children }: { children: React.ReactNode }) {
  return <div className="grid gap-x-10 rounded-[1.25rem] border border-line px-4 lg:grid-cols-2">{children}</div>;
}

export { Dropdown as SearchSelect } from "@/components/ui";
