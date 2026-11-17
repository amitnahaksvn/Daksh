"use client";
import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "@/lib/api";
import { buttonClass, tileClass } from "@/components/ui";

export function callbackError(reason: string | null) {
  switch (reason) {
    case "consent_denied": return "Google sign-in was cancelled.";
    case "oauth_not_configured": return "Gmail OAuth is not configured on the API.";
    case "refresh_token_missing": return "Google did not return offline access. Retry and approve the requested permissions.";
    case "profile_lookup": return "Daksh could not read the Gmail account profile.";
    case "google_unavailable": return "Google could not be reached. Please try again.";
    default: return "Gmail could not be connected. Please try again.";
  }
}

/** "Continue with Google": opens Google sign-in in a popup; the popup reports back via postMessage. */
export function GmailConnectCard({ onConnected, initialError }: { onConnected: () => void; initialError?: string | null }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(initialError ?? null);
  const popupRef = useRef<Window | null>(null);

  useEffect(() => {
    function onMessage(event: MessageEvent) {
      if (event.origin !== window.location.origin || event.data?.type !== "daksh-gmail-oauth") return;
      setBusy(false);
      if (event.data.result === "connected") onConnected();
      else setError(callbackError(String(event.data.result ?? "")));
    }
    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  }, [onConnected]);

  // Stop the spinner if the user closes the popup without finishing.
  useEffect(() => {
    if (!busy) return;
    const timer = window.setInterval(() => {
      if (popupRef.current?.closed) { setBusy(false); window.clearInterval(timer); }
    }, 600);
    return () => window.clearInterval(timer);
  }, [busy]);

  async function connect() {
    setError(null);
    setBusy(true);
    // Open synchronously on click so the browser does not block the popup.
    const width = 520, height = 680;
    const left = Math.max(0, window.screenX + (window.outerWidth - width) / 2);
    const top = Math.max(0, window.screenY + (window.outerHeight - height) / 2);
    const popup = window.open("about:blank", "daksh-gmail-oauth", `popup=yes,width=${width},height=${height},left=${left},top=${top}`);
    popupRef.current = popup;
    try {
      const result = await api.connectGmail();
      if (popup && !popup.closed) popup.location.href = result.authorizationUrl;
      else window.location.assign(result.authorizationUrl); // popup blocked: fall back to a full redirect
    } catch (err) {
      popup?.close();
      setError(err instanceof ApiError ? err.message : "Could not start Google sign-in.");
      setBusy(false);
    }
  }

  return (
    <section className={`p-6 ${tileClass}`}>
      <h2 className="text-xl font-bold">Connect your Gmail account</h2>
      <p className="mt-2 text-sm text-muted">
        Continue to Google to sign in and approve Gmail access. Daksh never asks for or stores your Google password.
      </p>
      {error && <p role="alert" className="mt-3 rounded-[1.25rem] bg-danger-bg p-3 text-sm text-danger">{error}</p>}
      <button onClick={connect} disabled={busy} className={`${buttonClass} mt-5`}>
        {busy ? "Waiting for Google…" : "Continue with Google"}
      </button>
    </section>
  );
}
