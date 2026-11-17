"use client";
import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { QRCodeSVG } from "qrcode.react";
import { BrandTile, StatusChip } from "@/components/integrations";
import { buttonClass, ErrorState, inputClass, Loading, Notice } from "@/components/ui";
import { WhatsAppSettingsModal } from "@/components/WhatsAppSettingsModal";
import { api, ApiError, type AppDetail, type ConnectionStatusValue, type TelegramLoginState } from "@/lib/api";
import { setChatShown, useHiddenChatConnections } from "@/lib/chatVisibility";
import { useApi } from "@/lib/useApi";

const PAIRING_STEPS = [
  "Open Telegram on your phone.",
  "Go to Settings, then Devices.",
  "Tap Link Desktop Device.",
  "Scan the QR code shown below.",
];

function ConnectPanel({ detail, onChanged }: { detail: AppDetail; onChanged: () => void }) {
  const initial = detail.connection;
  const [connectionId, setConnectionId] = useState(initial?.connectionId ?? null);
  const [status, setStatus] = useState<ConnectionStatusValue>(initial?.status ?? "NotConnected");
  const [account, setAccount] = useState<string | null>(initial?.accountIdentifier ?? null);
  const [login, setLogin] = useState<TelegramLoginState | null>(null);
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const hiddenChats = useHiddenChatConnections();

  // While signing in, poll the Telegram service for the QR code, the password prompt and the final "connected".
  useEffect(() => {
    if (!connectionId || status !== "Connecting") return;
    let stopped = false;
    let restarted = false;
    const tick = async () => {
      const next = await api.telegramState(connectionId).catch(() => null);
      if (stopped || !next) return;
      setLogin(next);
      if (next.status === "Connected") {
        const detailNow = await api.getConnectionStatus(connectionId).catch(() => null);
        setAccount(detailNow?.accountIdentifier ?? null);
        setStatus("Connected");
        onChanged();
      } else if (next.status === "Error") {
        setStatus("Error");
        setError(next.error ?? "Telegram sign-in failed.");
      } else if ((next.status === "Disconnected" || next.status === "NotConnected") && !restarted) {
        // The Telegram service lost this sign-in (restart, or the page was reopened later): start a fresh one.
        restarted = true;
        await api.connectTelegram().catch(() => undefined);
      }
    };
    void tick();
    const timer = setInterval(() => void tick(), 2000);
    return () => { stopped = true; clearInterval(timer); };
  }, [connectionId, status, onChanged]);

  async function connect() {
    setError(null);
    setBusy(true);
    try {
      const result = await api.connectTelegram();
      setConnectionId(result.connectionId);
      setLogin(null);
      setStatus("Connecting");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not start the connection. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  async function submitPassword(e: FormEvent) {
    e.preventDefault();
    if (!connectionId) return;
    setBusy(true);
    setError(null);
    try {
      await api.telegramPassword(connectionId, password);
      setPassword("");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not send the password.");
    } finally {
      setBusy(false);
    }
  }

  async function disconnect() {
    if (!connectionId) return;
    if (!confirm("Disconnect Telegram? Daksha will be signed out of your account and you'll need to scan a new QR code to reconnect.")) return;
    setError(null);
    setBusy(true);
    try {
      await api.disconnectApp(connectionId);
      setStatus("Disconnected");
      setAccount(null);
      onChanged();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not disconnect. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center gap-4">
        <BrandTile icon={detail.catalog.icon} color={detail.catalog.color} size={56} />
        <div className="flex-1">
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-extrabold">{detail.catalog.name}</h1>
            <StatusChip status={status} />
          </div>
          <p className="text-sm text-muted">Read, answer and auto-reply to your Telegram chats.</p>
        </div>
        {status === "Connected" && connectionId && (
          <div className="flex shrink-0 flex-nowrap items-center gap-2 rounded-full border border-line bg-surface-2 py-1 pl-3 pr-1">
            <span className="flex items-center gap-1.5 whitespace-nowrap text-sm font-semibold">
              <span aria-hidden>✈️</span>
              {account || "Telegram"}
            </span>
            <span aria-hidden className="h-5 w-px shrink-0 bg-line" />
            <button
              type="button"
              role="switch"
              aria-checked={!hiddenChats.has(connectionId)}
              onClick={() => setChatShown(connectionId, hiddenChats.has(connectionId))}
              className={`whitespace-nowrap rounded-full px-3 py-1 text-sm font-semibold transition-opacity hover:opacity-90 ${
                !hiddenChats.has(connectionId) ? "bg-accent text-accent-contrast" : "border border-line bg-surface text-text"
              }`}
            >
              {!hiddenChats.has(connectionId) ? "In Chats ✓" : "Show in Chats"}
            </button>
            <button onClick={disconnect} disabled={busy} className="whitespace-nowrap rounded-full bg-danger-bg px-3 py-1 text-sm font-semibold text-danger transition-opacity hover:opacity-80 disabled:opacity-50">
              Disconnect
            </button>
          </div>
        )}
      </div>

      {error && <p role="alert" className="mt-4 rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{error}</p>}

      {(status === "NotConnected" || status === "Disconnected" || status === "Error") && (
        <div className="mt-5">
          <Notice>
            Daksha signs in to your own Telegram account, like any other Telegram app, by scanning a QR code. Your session is
            encrypted and Disconnect signs Daksha out of your account. Telegram can restrict accounts that automate heavily,
            so only connect an account you own, and keep auto-reply to chats where it makes sense.
          </Notice>
          <button onClick={connect} disabled={busy} className={`${buttonClass} w-full`}>
            {busy ? "Starting…" : status === "NotConnected" ? "Connect Telegram" : "Reconnect"}
          </button>
        </div>
      )}

      {status === "Connecting" && (
        <div className="mt-5 flex flex-col items-center gap-4 text-center">
          {login?.passwordRequired ? (
            <form onSubmit={submitPassword} className="flex w-full max-w-xs flex-col gap-3 text-left">
              <p className="text-sm font-semibold">Two-step verification</p>
              <p className="text-xs text-muted">Your account has a cloud password. Enter it to finish connecting.</p>
              <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="off" className={inputClass} placeholder="Telegram password" />
              {login.error && <p role="alert" className="text-sm text-danger">{login.error}</p>}
              <button type="submit" disabled={busy || !password} className={`${buttonClass} disabled:opacity-50`}>Continue</button>
            </form>
          ) : (
            <>
              {login?.qr ? (
                <div className="rounded-2xl bg-white p-4"><QRCodeSVG value={login.qr} size={280} /></div>
              ) : (
                <Loading label="Generating QR code" />
              )}
              <ol className="w-full max-w-sm list-inside list-decimal space-y-1 text-left text-sm text-muted">
                {PAIRING_STEPS.map((step) => <li key={step}>{step}</li>)}
              </ol>
              <p className="text-xs text-muted">If your account uses two-step verification, you&rsquo;ll be asked for your password next. The code refreshes automatically.</p>
            </>
          )}
        </div>
      )}

      {status === "Connected" && connectionId && (
        <div className="mt-4">
          <WhatsAppSettingsModal connectionId={connectionId} inline provider="telegram" />
        </div>
      )}
    </div>
  );
}

/** Self-contained: fetches its own Telegram connection detail, so it can sit in a modal or on a page. */
export function TelegramConnectPanel() {
  const { state, reload } = useApi(() => api.appDetail("telegram"));
  return (
    <>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && <ConnectPanel detail={state.data} onChanged={reload} />}
    </>
  );
}
