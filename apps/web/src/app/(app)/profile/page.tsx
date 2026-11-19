"use client";
import { useState, type FormEvent } from "react";
import { BusyModeForm } from "@/components/BusyControls";
import { DatePicker } from "@/components/DatePicker";
import { buttonClass, Dropdown, ErrorState, inputClass, Loading, Notice, PageHeader, tileClass } from "@/components/ui";
import { api, ApiError, type UserDetails } from "@/lib/api";
import { getSession, setSession } from "@/lib/session";
import { useApi } from "@/lib/useApi";

const GENDER_OPTIONS = ["Female", "Male", "Non-binary", "Prefer not to say"];

type Status = { kind: "ok" | "error"; text: string } | null;

function StatusNote({ status }: { status: Status }) {
  if (!status) return null;
  return status.kind === "error" ? (
    <Notice>{status.text}</Notice>
  ) : (
    <p role="status" className="rounded-[1.25rem] bg-lime p-4 text-sm text-on-tint">{status.text}</p>
  );
}

function errorText(err: unknown) {
  return err instanceof ApiError ? err.message : "Something went wrong. Please try again.";
}

function DetailsForm({ user }: { user: UserDetails }) {
  const [firstName, setFirstName] = useState(user.firstName);
  const [lastName, setLastName] = useState(user.lastName);
  const [gender, setGender] = useState(user.gender);
  const [dateOfBirth, setDateOfBirth] = useState(user.dateOfBirth.slice(0, 10));
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<Status>(null);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setStatus(null);
    if (!firstName.trim() || !lastName.trim() || !gender || !dateOfBirth) {
      setStatus({ kind: "error", text: "Please fill in every field." });
      return;
    }
    setSaving(true);
    try {
      const updated = await api.updateMe({ firstName, lastName, gender, dateOfBirth });
      // Keep the header badge in sync with the new name.
      const session = getSession();
      if (session) setSession({ ...session, firstName: updated.firstName, lastName: updated.lastName });
      setStatus({ kind: "ok", text: "Profile updated." });
    } catch (err) {
      setStatus({ kind: "error", text: errorText(err) });
    } finally {
      setSaving(false);
    }
  }

  return (
    <form onSubmit={onSubmit} className={`flex flex-col gap-4 p-6 ${tileClass}`}>
      <h2 className="text-lg font-bold">Personal details</h2>
      <StatusNote status={status} />
      <div className="grid gap-4 sm:grid-cols-2">
        <label className="block text-sm text-muted">
          First name
          <input className={`${inputClass} mt-1`} value={firstName} onChange={(e) => setFirstName(e.target.value)} maxLength={100} />
        </label>
        <label className="block text-sm text-muted">
          Last name
          <input className={`${inputClass} mt-1`} value={lastName} onChange={(e) => setLastName(e.target.value)} maxLength={100} />
        </label>
        <div className="text-sm text-muted">
          <span className="mb-1 block">Gender</span>
          <Dropdown value={gender} onChange={setGender} placeholder="Select gender" options={GENDER_OPTIONS.map((g) => ({ value: g, label: g }))} />
        </div>
        <div className="text-sm text-muted">
          <span className="mb-1 block">Date of birth</span>
          <DatePicker value={dateOfBirth} onChange={setDateOfBirth} />
        </div>
        <label className="block text-sm text-muted sm:col-span-2">
          Email
          <input className={`${inputClass} mt-1 opacity-70`} value={user.email} readOnly disabled />
          <span className="mt-1 block text-xs">Your email is your sign-in and can&apos;t be changed here.</span>
        </label>
      </div>
      <div>
        <button type="submit" disabled={saving} className={buttonClass}>{saving ? "Saving…" : "Save changes"}</button>
      </div>
    </form>
  );
}

function PasswordForm() {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<Status>(null);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setStatus(null);
    if (!current) return setStatus({ kind: "error", text: "Enter your current password." });
    if (next.length < 10) return setStatus({ kind: "error", text: "New password must be at least 10 characters." });
    if (!/[A-Za-z]/.test(next) || !/[0-9]/.test(next)) return setStatus({ kind: "error", text: "New password needs at least one letter and one number." });
    if (next !== confirm) return setStatus({ kind: "error", text: "Passwords do not match." });
    setSaving(true);
    try {
      await api.changePassword(current, next);
      setCurrent("");
      setNext("");
      setConfirm("");
      setStatus({ kind: "ok", text: "Password updated." });
    } catch (err) {
      setStatus({ kind: "error", text: errorText(err) });
    } finally {
      setSaving(false);
    }
  }

  return (
    <form onSubmit={onSubmit} className={`flex flex-col gap-4 p-6 ${tileClass}`}>
      <h2 className="text-lg font-bold">Change password</h2>
      <StatusNote status={status} />
      <label className="block text-sm text-muted">
        Current password
        <input type="password" autoComplete="current-password" className={`${inputClass} mt-1`} value={current} onChange={(e) => setCurrent(e.target.value)} />
      </label>
      <label className="block text-sm text-muted">
        New password
        <input type="password" autoComplete="new-password" className={`${inputClass} mt-1`} value={next} onChange={(e) => setNext(e.target.value)} />
      </label>
      <label className="block text-sm text-muted">
        Confirm new password
        <input type="password" autoComplete="new-password" className={`${inputClass} mt-1`} value={confirm} onChange={(e) => setConfirm(e.target.value)} />
      </label>
      <div>
        <button type="submit" disabled={saving} className={buttonClass}>{saving ? "Updating…" : "Update password"}</button>
      </div>
    </form>
  );
}

const SECTIONS = [
  { id: "personal", label: "Personal details" },
  { id: "busy", label: "Busy mode" },
  { id: "password", label: "Change password" },
] as const;
type SectionId = (typeof SECTIONS)[number]["id"];

export default function ProfilePage() {
  const { state, reload } = useApi(() => api.me());
  const [section, setSection] = useState<SectionId>("personal");
  return (
    <div>
      <PageHeader title="Profile">Update your personal details, busy mode and password.</PageHeader>
      <div className="grid gap-4 md:grid-cols-[14rem_minmax(0,1fr)]">
        <nav aria-label="Profile sections" className={`flex gap-1 overflow-x-auto p-2 md:flex-col md:self-start ${tileClass}`}>
          {SECTIONS.map((s) => (
            <button
              key={s.id}
              type="button"
              onClick={() => setSection(s.id)}
              aria-current={section === s.id ? "page" : undefined}
              className={`whitespace-nowrap rounded-full px-4 py-2.5 text-left text-sm font-medium transition-colors ${
                section === s.id ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2 hover:text-text"
              }`}
            >
              {s.label}
            </button>
          ))}
        </nav>
        <div className="min-w-0">
          {section === "password" ? (
            <PasswordForm />
          ) : section === "busy" ? (
            <BusyModeForm />
          ) : (
            <>
              {state.status === "loading" && <Loading />}
              {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
              {state.status === "ready" && <DetailsForm user={state.data} />}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
