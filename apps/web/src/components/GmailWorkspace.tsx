"use client";
import { useCallback, useState, useSyncExternalStore } from "react";
import type { FormEvent } from "react";
import { api, ApiError, type GmailMessage, type GmailMessagePage, type GmailMessageSummary } from "@/lib/api";
import { useApi } from "@/lib/useApi";
import { buttonClass, Chip, EmptyState, inputClass, Loading, secondaryButtonClass, tileClass } from "@/components/ui";

function messageError(error: unknown, fallback: string) {
  return error instanceof ApiError ? error.message : fallback;
}

function callbackError(reason: string | null) {
  switch (reason) {
    case "consent_denied": return "Google sign-in was cancelled.";
    case "oauth_not_configured": return "Gmail OAuth is not configured on the API.";
    case "refresh_token_missing": return "Google did not return offline access. Retry and approve the requested permissions.";
    case "profile_lookup": return "Daksh could not read the Gmail account profile.";
    case "google_unavailable": return "Google could not be reached. Please try again.";
    default: return "Gmail could not be connected. Please try again.";
  }
}

function callbackSnapshot() {
  if (typeof window === "undefined") return "";
  const params = new URLSearchParams(window.location.search);
  if (params.get("status") === "connected") return "connected";
  return params.get("error") ?? "";
}

function subscribeToLocation(callback: () => void) {
  window.addEventListener("popstate", callback);
  return () => window.removeEventListener("popstate", callback);
}

function displayDate(value: string | null) {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

export function GmailWorkspace() {
  const { state: detail, reload } = useApi(() => api.appDetail("gmail"));
  const feedback = useSyncExternalStore(subscribeToLocation, callbackSnapshot, () => "");
  const connection = detail.status === "ready" ? detail.data.connection : null;
  const connected = connection?.status === "Connected";
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function connect() {
    setError(null);
    setBusy(true);
    try {
      const result = await api.connectGmail();
      window.location.assign(result.authorizationUrl);
    } catch (err) {
      setError(messageError(err, "Could not start Google sign-in."));
      setBusy(false);
    }
  }

  async function disconnect() {
    if (!window.confirm("Disconnect Gmail and revoke Daksh's Google access?")) return;
    setBusy(true);
    setError(null);
    try {
      await api.disconnectGmail();
      reload();
    } catch (err) {
      setError(messageError(err, "Could not disconnect Gmail."));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      {detail.status === "loading" && <Loading label="Loading Gmail connection" />}
      {detail.status === "error" && (
        <div role="alert" className="rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">
          <p>{detail.error.message}</p>
          <button onClick={reload} className="mt-2 font-semibold underline">Try again</button>
        </div>
      )}
      {error && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{error}</p>}
      {feedback === "connected" && <p role="status" className="rounded-[1.25rem] bg-lime p-4 text-sm text-on-tint">Gmail connected successfully.</p>}
      {feedback && feedback !== "connected" && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{callbackError(feedback)}</p>}

      {detail.status === "ready" && !connected && (
        <section className={`p-6 ${tileClass}`}>
          <h2 className="text-xl font-bold">Connect your Gmail account</h2>
          <p className="mt-2 text-sm text-muted">
            Continue to Google to sign in and approve Gmail access. Daksh never asks for or stores your Google password.
          </p>
          <button onClick={connect} disabled={busy} className={`${buttonClass} mt-5`}>
            {busy ? "Opening Google…" : "Continue with Google"}
          </button>
        </section>
      )}

      {connected && connection && (
        <>
          <section className={`flex flex-wrap items-center justify-between gap-3 p-5 ${tileClass}`}>
            <div>
              <p className="font-semibold">{connection.accountIdentifier}</p>
              <div className="mt-1 flex items-center gap-2"><Chip tone="lime">Connected</Chip><span className="text-xs text-muted">Gmail</span></div>
            </div>
            <button onClick={disconnect} disabled={busy} className={secondaryButtonClass}>Disconnect</button>
          </section>
          <GmailInbox connectionId={connection.connectionId} />
        </>
      )}
    </div>
  );
}

function GmailInbox({ connectionId }: { connectionId: string }) {
  const { state: initialPage } = useApi(() => api.gmailMessages("in:inbox"));
  const [page, setPage] = useState<GmailMessagePage | null>(null);
  const [query, setQuery] = useState("in:inbox");
  const [selected, setSelected] = useState<GmailMessage | null>(null);
  const [composeOpen, setComposeOpen] = useState(false);
  const [to, setTo] = useState("");
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [busy, setBusy] = useState(false);
  const [loadingMessages, setLoadingMessages] = useState(false);
  const [loadingMessageId, setLoadingMessageId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const messages = page?.messages ?? (initialPage.status === "ready" ? initialPage.data.messages : []);
  const nextPageToken = page ? page.nextPageToken : initialPage.status === "ready" ? initialPage.data.nextPageToken : null;

  const loadMessages = useCallback(async (search: string, pageToken?: string | null, append = false) => {
    setError(null);
    setLoadingMessages(true);
    try {
      const result = await api.gmailMessages(search, pageToken);
      setPage((current) => {
        const previous = current ?? (initialPage.status === "ready" ? initialPage.data : null);
        return append && previous ? { ...result, messages: [...previous.messages, ...result.messages] } : result;
      });
    } catch (err) {
      setError(messageError(err, "Could not load Gmail messages."));
    } finally {
      setLoadingMessages(false);
    }
  }, [initialPage]);

  async function search(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSelected(null);
    await loadMessages(query.trim() || "in:inbox");
  }

  async function openMessage(message: GmailMessageSummary) {
    setLoadingMessageId(message.id);
    setSelected(null);
    setError(null);
    try {
      setSelected(await api.gmailMessage(message.id));
    } catch (err) {
      setError(messageError(err, "Could not open this message."));
    } finally {
      setLoadingMessageId(null);
    }
  }

  async function send(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await api.sendGmailMessage({ to, subject, body });
      setTo("");
      setSubject("");
      setBody("");
      setComposeOpen(false);
      setNotice("Message sent.");
    } catch (err) {
      setError(messageError(err, "Could not send this message."));
    } finally {
      setBusy(false);
    }
  }

  async function addToDashboard() {
    setBusy(true);
    setError(null);
    try {
      await api.addToDashboard(connectionId);
      setNotice("Gmail added to your dashboard.");
    } catch (err) {
      setError(messageError(err, "Could not add Gmail to the dashboard."));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      {notice && <p role="status" className="rounded-[1.25rem] bg-lime p-4 text-sm text-on-tint">{notice}</p>}
      {error && <p role="alert" className="rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{error}</p>}
      <section className={`flex flex-wrap justify-end gap-2 p-4 ${tileClass}`}>
        <button onClick={() => setComposeOpen((open) => !open)} className={buttonClass}>Compose</button>
        <button onClick={addToDashboard} disabled={busy} className={secondaryButtonClass}>Add to Dashboard</button>
      </section>

      {composeOpen && (
        <form onSubmit={send} className={`space-y-3 p-5 ${tileClass}`}>
          <h2 className="font-bold">New message</h2>
          <label className="block text-sm font-medium">To
            <input value={to} onChange={(event) => setTo(event.target.value)} required className={`${inputClass} mt-1`} placeholder="name@example.com" />
          </label>
          <label className="block text-sm font-medium">Subject
            <input value={subject} onChange={(event) => setSubject(event.target.value)} required className={`${inputClass} mt-1`} />
          </label>
          <label className="block text-sm font-medium">Message
            <textarea value={body} onChange={(event) => setBody(event.target.value)} required rows={7} className={`${inputClass} mt-1 resize-y`} />
          </label>
          <button type="submit" disabled={busy} className={buttonClass}>{busy ? "Sending…" : "Send"}</button>
        </form>
      )}

      <form onSubmit={search} className={`flex flex-col gap-2 p-4 sm:flex-row ${tileClass}`}>
        <label htmlFor="gmail-query" className="sr-only">Search Gmail</label>
        <input id="gmail-query" value={query} onChange={(event) => setQuery(event.target.value)} className={inputClass} placeholder="Search Gmail (e.g. from:someone@example.com)" />
        <button type="submit" disabled={loadingMessages} className={secondaryButtonClass}>Search</button>
      </form>

      <div className="grid gap-4 lg:grid-cols-[minmax(16rem,0.8fr)_minmax(0,1.2fr)]">
        <section className={`overflow-hidden ${tileClass}`} aria-label="Gmail messages">
          {initialPage.status === "loading" && !page && <div className="p-4"><Loading label="Loading messages" /></div>}
          {initialPage.status === "error" && !page && <p role="alert" className="p-4 text-sm text-danger">{initialPage.error.message}</p>}
          {loadingMessages && <div className="p-4"><Loading label="Loading messages" /></div>}
          {!loadingMessages && initialPage.status === "ready" && messages.length === 0 && <div className="p-4"><EmptyState>No messages found.</EmptyState></div>}
          <ul className="divide-y divide-line">
            {messages.map((message) => (
              <li key={message.id}>
                <button
                  onClick={() => void openMessage(message)}
                  className="w-full p-4 text-left transition-colors hover:bg-surface-2"
                  aria-current={selected?.id === message.id ? "true" : undefined}
                >
                  <span className="flex items-start justify-between gap-2">
                    <span className="truncate text-sm font-semibold">{message.from || "(unknown sender)"}</span>
                    <span className="shrink-0 text-xs text-muted">{displayDate(message.date)}</span>
                  </span>
                  <span className="mt-1 block truncate text-sm">{message.subject || "(no subject)"}</span>
                  <span className="mt-1 block line-clamp-2 text-xs text-muted">{message.snippet}</span>
                  {loadingMessageId === message.id && <span className="mt-2 block text-xs text-muted">Opening…</span>}
                </button>
              </li>
            ))}
          </ul>
          {nextPageToken && (
            <button onClick={() => void loadMessages(query.trim() || "in:inbox", nextPageToken, true)} disabled={loadingMessages} className={`${secondaryButtonClass} m-3`}>
              Load more
            </button>
          )}
        </section>

        <section className={`min-h-64 p-5 ${tileClass}`} aria-label="Selected Gmail message">
          {selected ? (
            <>
              <button onClick={() => setSelected(null)} className="mb-3 text-sm font-semibold underline">Back to messages</button>
              <h2 className="break-words text-xl font-bold">{selected.subject || "(no subject)"}</h2>
              <p className="mt-2 break-words text-sm text-muted">From: {selected.from}</p>
              <p className="break-words text-sm text-muted">To: {selected.to}</p>
              <p className="mt-1 text-xs text-muted">{displayDate(selected.date)}</p>
              <pre className="mt-5 whitespace-pre-wrap break-words font-sans text-sm leading-6">{selected.body}</pre>
            </>
          ) : (
            <p className="text-sm text-muted">Select a message to read it.</p>
          )}
        </section>
      </div>
    </div>
  );
}
