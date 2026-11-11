"use client";
import { useState } from "react";
import { api, type PrivacySettings } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { buttonClass, Chip, ErrorState, inputClass, Loading, PageHeader, tileClass } from "@/components/ui";

export default function PrivacyPage() {
  const { state, reload } = useApi(api.privacy);
  const [edited, setForm] = useState<PrivacySettings>();
  const form = edited ?? (state.status === "ready" ? state.data : undefined);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string>();

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!form) return;
    setError(undefined);
    try { await api.updatePrivacy(form); setSaved(true); } catch (err) { setError((err as Error).message); }
  }

  return (
    <>
      <PageHeader title="Privacy & memory">Memory and data sharing are off unless you turn them on.</PageHeader>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {form && (
        <form onSubmit={save} className={`flex max-w-xl flex-col gap-5 p-6 ${tileClass}`}>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.memoryEnabled} onChange={(e) => { setSaved(false); setForm({ ...form, memoryEnabled: e.target.checked }); }} /> Let Daksh remember things between conversations</label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={form.allowProviderDataSharing} onChange={(e) => { setSaved(false); setForm({ ...form, allowProviderDataSharing: e.target.checked }); }} /> Allow sharing content with AI providers</label>
          <label className="flex flex-col gap-1">Retention (days)
            <input type="number" min={1} max={3650} className={`${inputClass} !w-32`} value={form.retentionDays} onChange={(e) => { setSaved(false); setForm({ ...form, retentionDays: Number(e.target.value) }); }} />
          </label>
          {error && <p role="alert" className="text-sm font-medium text-danger">{error}</p>}
          {saved && <p role="status"><Chip tone="lime">Saved</Chip></p>}
          <button className={`${buttonClass} self-start`}>Save</button>
        </form>
      )}
    </>
  );
}
