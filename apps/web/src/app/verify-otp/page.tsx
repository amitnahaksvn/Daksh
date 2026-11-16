"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { AppearanceControls } from "@/components/ThemeToggle";
import { Orb } from "@/components/Orb";
import { buttonClass, inputClass, Notice, tileClass } from "@/components/ui";
import { api, ApiError } from "@/lib/api";

function VerifyOtpForm() {
  const router = useRouter();
  const email = useSearchParams().get("email") ?? "";
  const [otp, setOtp] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await api.verifyOtp(email, otp);
      router.push("/login?verified=1");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Something went wrong. Please try again.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className={`p-6 ${tileClass}`}>
      <h1 className="mb-4 text-2xl font-extrabold">Verify your email</h1>
      <Notice>
        We sent a 6-digit verification code to <strong>{email || "your email"}</strong>. It expires in 10 minutes.
      </Notice>
      {error && <p role="alert" className="mb-4 rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{error}</p>}
      <form onSubmit={onSubmit} className="space-y-3">
        <input
          required
          inputMode="numeric"
          pattern="[0-9]{6}"
          maxLength={6}
          placeholder="6-digit code"
          className={`${inputClass} text-center text-lg tracking-[0.5em]`}
          value={otp}
          onChange={(e) => setOtp(e.target.value.replace(/\D/g, "").slice(0, 6))}
        />
        <button type="submit" disabled={submitting || otp.length !== 6} className={`${buttonClass} w-full`}>
          {submitting ? "Verifying…" : "Verify"}
        </button>
      </form>
      <p className="mt-4 text-center text-sm text-muted">
        Entered the wrong email? <Link href="/register" className="font-semibold text-text underline">Register again</Link>
      </p>
    </section>
  );
}

export default function VerifyOtpPage() {
  return (
    <main className="mx-auto w-full max-w-md p-3 py-10 md:p-4 md:py-16">
      <div className="mb-3 flex items-center justify-between px-2">
        <Link href="/" className="flex items-center gap-2.5 text-xl font-extrabold tracking-tight"><Orb size={26} />Daksh</Link>
        <AppearanceControls className="justify-end" />
      </div>
      <Suspense fallback={null}>
        <VerifyOtpForm />
      </Suspense>
    </main>
  );
}
