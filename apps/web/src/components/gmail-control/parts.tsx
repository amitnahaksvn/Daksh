"use client";
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Notice, inputClass, buttonClass, secondaryButtonClass, tileClass } from "@/components/ui";
import { Toggle } from "@/components/WhatsAppSettingsParts";
import { ApiError } from "@/lib/api";
import { gmailControl, type GmailConfig } from "@/lib/gmailControl";

export function errText(err: unknown, fallback = "Something went wrong. Please try again.") {
  return err instanceof ApiError ? err.message : fallback;
}

/** A titled group of settings. */
export function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <section className={`mb-4 p-4 ${tileClass}`}>
      <h3 className="text-base font-bold">{title}</h3>
      {hint && <p className="mt-0.5 text-xs text-muted">{hint}</p>}
      <div className="mt-2">{children}</div>
    </section>
  );
}

/** A label + hint on the left, a control on the right. */
export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-2 border-b border-line py-2.5 last:border-b-0 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
      <div className="min-w-0 sm:max-w-[60%]">
        <p className="text-sm font-semibold">{label}</p>
        {hint && <p className="mt-0.5 text-xs text-muted">{hint}</p>}
      </div>
      <div className="shrink-0 sm:max-w-[40%]">{children}</div>
    </div>
  );
}

export function Switch({ label, hint, checked, onChange, disabled }: { label: string; hint?: string; checked: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <Field label={label} hint={hint}>
      <Toggle label={label} checked={checked} onChange={onChange} disabled={disabled} />
    </Field>
  );
}

export function NumberField({ label, hint, value, onChange, min, max, suffix }: { label: string; hint?: string; value: number; onChange: (v: number) => void; min?: number; max?: number; suffix?: string }) {
  return (
    <Field label={label} hint={hint}>
      <span className="flex items-center gap-2">
        <input aria-label={label} type="number" className={`${inputClass} !w-24`} value={Number.isFinite(value) ? value : 0} min={min} max={max} onChange={(e) => onChange(Number(e.target.value))} />
        {suffix && <span className="text-xs text-muted">{suffix}</span>}
      </span>
    </Field>
  );
}

export function TextField({ label, hint, value, onChange, placeholder, multiline, maxLength }: { label: string; hint?: string; value: string; onChange: (v: string) => void; placeholder?: string; multiline?: boolean; maxLength?: number }) {
  return (
    <Field label={label} hint={hint}>
      {multiline
        ? <textarea aria-label={label} rows={3} maxLength={maxLength} className={`${inputClass} w-full sm:w-72`} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
        : <input aria-label={label} maxLength={maxLength} className={`${inputClass} w-full sm:w-64`} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />}
    </Field>
  );
}

/** Comma/newline separated list kept as a real string[] in the config. */
export function ListField({ label, hint, value, onChange, placeholder }: { label: string; hint?: string; value: string[]; onChange: (v: string[]) => void; placeholder?: string }) {
  // While typing, keep the raw text (so "a, " isn't collapsed); when the list is replaced from outside (discard/reload) show the new list.
  const [typed, setTyped] = useState<{ text: string; basis: string } | null>(null);
  const basis = value.join("\u0000");
  const text = typed && typed.basis === basis ? typed.text : value.join(", ");
  return (
    <Field label={label} hint={hint ?? "Separate entries with commas."}>
      <textarea
        aria-label={label}
        rows={2}
        className={`${inputClass} w-full sm:w-72`}
        value={text}
        placeholder={placeholder}
        onChange={(e) => {
          const next = e.target.value.split(/[,\n]/).map((x) => x.trim()).filter(Boolean);
          setTyped({ text: e.target.value, basis: next.join("\u0000") });
          onChange(next);
        }}
      />
    </Field>
  );
}

export function SelectField({ label, hint, value, options, onChange }: { label: string; hint?: string; value: string; options: { value: string; label: string }[]; onChange: (v: string) => void }) {
  return (
    <Field label={label} hint={hint}>
      <select aria-label={label} className={`${inputClass} w-full sm:w-56`} value={value} onChange={(e) => onChange(e.target.value)}>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </Field>
  );
}

export function Banner({ tone = "info", children }: { tone?: "info" | "error" | "ok"; children: ReactNode }) {
  const cls = tone === "error" ? "bg-danger-bg text-danger" : tone === "ok" ? "bg-lime text-on-tint" : "bg-surface-2";
  return <p role={tone === "error" ? "alert" : "status"} className={`mb-3 rounded-[1.25rem] p-3 text-sm ${cls}`}>{children}</p>;
}

/** Shown when the API says Google needs another permission; sends the user through Google's consent for just that feature. */
export function ScopeUpgrade({ error, feature, onStart }: { error: ApiError; feature: string; onStart?: () => void }) {
  async function go() {
    try {
      onStart?.();
      const { authorizationUrl } = await gmailControl.connect([feature]);
      window.location.assign(authorizationUrl);
    } catch { /* surfaced by the caller's next action */ }
  }
  return (
    <div className="mb-3 rounded-[1.25rem] bg-surface-2 p-3 text-sm">
      <p>{error.message}</p>
      <button className={`${buttonClass} mt-2 px-4 py-2 text-xs`} onClick={go}>Approve in Google</button>
    </div>
  );
}

/**
 * Loads the account's real configuration, tracks edits, and saves with the server's version number.
 * Every tab edits this one object, so "unsaved changes" is a single, honest flag.
 */
export function useGmailConfig(accountId: string | null) {
  // `loadedFor` ties the loaded document to the account it belongs to, so switching accounts shows "loading" until the new one arrives.
  const [store, setStore] = useState<{ accountId: string; saved: GmailConfig; draft: GmailConfig } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!accountId) return;
    gmailControl.config(accountId).then(
      (c) => { setError(null); setStore({ accountId, saved: c, draft: structuredClone(c) }); },
      (e) => setError(errText(e, "Could not load Gmail settings.")),
    );
  }, [accountId]);
  useEffect(load, [load]);

  const current = store && store.accountId === accountId ? store : null;
  const dirty = !!current && JSON.stringify(current.saved) !== JSON.stringify(current.draft);
  useEffect(() => {
    if (!dirty) return;
    const guard = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ""; };
    window.addEventListener("beforeunload", guard);
    return () => window.removeEventListener("beforeunload", guard);
  }, [dirty]);

  const update = useCallback((mutate: (c: GmailConfig) => void) => {
    setStore((s) => { if (!s) return s; const draft = structuredClone(s.draft); mutate(draft); return { ...s, draft }; });
    setNotice(null);
  }, []);

  async function save() {
    if (!current || !accountId) return;
    setSaving(true); setError(null); setFieldErrors({});
    try {
      const next = await gmailControl.saveConfig(accountId, current.draft);
      setStore({ accountId, saved: next, draft: structuredClone(next) }); setNotice("Settings saved.");
    } catch (e) {
      if (e instanceof ApiError) { setFieldErrors(e.fieldErrors); setError(e.message); }
      else setError("Could not save. Please try again.");
    } finally { setSaving(false); }
  }
  function discard() { setStore((s) => (s ? { ...s, draft: structuredClone(s.saved) } : s)); setFieldErrors({}); setError(null); }

  return { config: current?.draft ?? null, dirty, loading: !current && !error, error, fieldErrors, saving, notice, update, save, discard, reload: load };
}

export function SaveBar({ dirty, saving, onSave, onDiscard, notice }: { dirty: boolean; saving: boolean; onSave: () => void; onDiscard: () => void; notice: string | null }) {
  if (!dirty && !notice) return null;
  return (
    <div className={`sticky bottom-3 z-10 mt-2 flex flex-wrap items-center justify-between gap-3 p-3 ${tileClass}`} role="region" aria-label="Save changes">
      <span className="text-sm">{dirty ? "You have unsaved changes." : notice}</span>
      {dirty && (
        <span className="flex gap-2">
          <button className={`${secondaryButtonClass} px-4 py-2 text-xs`} onClick={onDiscard} disabled={saving}>Discard</button>
          <button className={`${buttonClass} px-4 py-2 text-xs`} onClick={onSave} disabled={saving}>{saving ? "Saving…" : "Save changes"}</button>
        </span>
      )}
    </div>
  );
}

export function FieldErrors({ errors }: { errors: Record<string, string[]> }) {
  const all = Object.entries(errors);
  if (all.length === 0) return null;
  return <Banner tone="error">{all.map(([k, v]) => `${k}: ${v.join(" ")}`).join(" · ")}</Banner>;
}

export function StatusBadge({ status }: { status: string }) {
  const good = status === "Connected";
  const warn = ["Synchronising", "Connecting", "Disabled"].includes(status);
  return <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${good ? "bg-lime text-on-tint" : warn ? "bg-surface-2" : "bg-danger-bg text-danger"}`}>{status.replace(/([a-z])([A-Z])/g, "$1 $2")}</span>;
}

export { Notice };
