"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { QRCodeSVG } from "qrcode.react";
import { BrandTile, StatusChip } from "@/components/integrations";
import { buttonClass, ErrorState, Loading, Notice, secondaryButtonClass } from "@/components/ui";
import { api, ApiError, type AppDetail, type ConnectionStatusValue } from "@/lib/api";
import { getIntegrationsHub } from "@/lib/signalr";
import { useApi } from "@/lib/useApi";

const PAIRING_STEPS = [
  "Open WhatsApp on your phone.",
  "Go to Settings, then Linked Devices.",
  "Tap Link a Device.",
  "Scan the QR code shown below.",
];

function ConnectPanel({ detail, onChanged }: { detail: AppDetail; onChanged: () => void }) {
  const [connectionId, setConnectionId] = useState(detail.connection?.connectionId ?? null);
  const [status, setStatus] = useState<ConnectionStatusValue>(detail.connection?.status ?? "NotConnected");
  const [qr, setQr] = useState<string | null>(null);
  const [accountIdentifier, setAccountIdentifier] = useState<string | null>(detail.connection?.accountIdentifier ?? null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [addedToDashboard, setAddedToDashboard] = useState(false);
  const subscribedConnectionId = useRef<string | null>(null);

  const refreshQr = useCallback(async (id: string) => {
    try {
      const result = await api.getQr(id);
      if (result.qr) setQr(result.qr);
    } catch {
      /* QR not ready yet — the next poll or a SignalR push will deliver it */
    }
  }, []);

  // SignalR delivers both the QR and the eventual "Connected" transition the moment they happen, but
  // this poll is the fallback for either one if that push is ever missed or the hub can't connect —
  // without it, a dropped SignalR event leaves the page stuck on "Connecting" forever even after the
  // phone has actually finished pairing on the backend.
  useEffect(() => {
    if (!connectionId || status !== "Connecting") return;
    const poll = async () => {
      const result = await api.getConnectionStatus(connectionId).catch(() => null);
      if (result?.status === "Connected") {
        setStatus("Connected");
        setQr(null);
        setAccountIdentifier(result.accountIdentifier ?? null);
        return;
      }
      if (result?.status === "Error" || result?.status === "Disconnected") {
        setStatus(result.status);
        return;
      }
      await refreshQr(connectionId);
    };
    const interval = setInterval(() => void poll(), 2500);
    return () => clearInterval(interval);
  }, [connectionId, status, refreshQr]);

  useEffect(() => {
    if (!connectionId || subscribedConnectionId.current === connectionId) return;
    subscribedConnectionId.current = connectionId;

    const hub = getIntegrationsHub();
    const onQr = (payload: { connectionId: string; qr?: string }) => {
      if (payload.connectionId === connectionId && payload.qr) {
        setQr(payload.qr);
        setStatus("Connecting");
      }
    };
    const onConnected = (payload: { connectionId: string; accountIdentifier?: string }) => {
      if (payload.connectionId !== connectionId) return;
      setStatus("Connected");
      setQr(null);
      setAccountIdentifier(payload.accountIdentifier ?? null);
    };
    const onDisconnected = (payload: { connectionId: string }) => {
      if (payload.connectionId === connectionId) setStatus("Disconnected");
    };
    const onFailed = (payload: { connectionId: string; error?: string }) => {
      if (payload.connectionId === connectionId) {
        setStatus("Error");
        setError(payload.error ?? "The connection failed.");
      }
    };

    hub.on("WhatsAppQrGenerated", onQr);
    hub.on("WhatsAppConnected", onConnected);
    hub.on("WhatsAppDisconnected", onDisconnected);
    hub.on("WhatsAppConnectionFailed", onFailed);
    if (hub.state === "Disconnected") hub.start().catch(() => void 0);

    return () => {
      hub.off("WhatsAppQrGenerated", onQr);
      hub.off("WhatsAppConnected", onConnected);
      hub.off("WhatsAppDisconnected", onDisconnected);
      hub.off("WhatsAppConnectionFailed", onFailed);
    };
  }, [connectionId]);

  async function connect() {
    setError(null);
    setBusy(true);
    try {
      const result = await api.connectWhatsApp();
      setConnectionId(result.connectionId);
      setStatus("Connecting");
      await refreshQr(result.connectionId);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not start the connection. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  async function reconnect() {
    if (!connectionId) return;
    setError(null);
    setBusy(true);
    try {
      await api.reconnectApp(connectionId);
      setStatus("Connecting");
      setQr(null);
      await refreshQr(connectionId);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not reconnect. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  async function disconnect() {
    if (!connectionId) return;
    if (!confirm("Disconnect WhatsApp? You'll need to scan a new QR code to reconnect.")) return;
    setError(null);
    setBusy(true);
    try {
      await api.disconnectApp(connectionId);
      setStatus("Disconnected");
      setQr(null);
      setAccountIdentifier(null);
      onChanged();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not disconnect. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  async function addToDashboard() {
    if (!connectionId) return;
    setError(null);
    try {
      await api.addToDashboard(connectionId);
      setAddedToDashboard(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not add to dashboard.");
    }
  }

  return (
    <div>
      <div className="flex items-center gap-4">
        <BrandTile icon={detail.catalog.icon} color={detail.catalog.color} size={56} />
        <div>
          <h1 className="text-2xl font-extrabold">{detail.catalog.name}</h1>
          <p className="text-sm text-muted">{detail.catalog.description}</p>
        </div>
      </div>

      <div className="mt-4 flex items-center gap-2">
        <span className="text-sm font-semibold text-muted">Status:</span>
        <StatusChip status={status} />
      </div>

      {error && <p role="alert" className="mt-4 rounded-[1.25rem] bg-danger-bg p-4 text-sm text-danger">{error}</p>}

      {status === "NotConnected" && (
        <div className="mt-5">
          <Notice>
            WhatsApp pairing uses Baileys, an unofficial WhatsApp Web protocol implementation — not an official
            WhatsApp Business API. WhatsApp can change its protocol at any time and break compatibility, and
            unofficial automation carries some risk of account restriction. Only connect an account you own or
            are authorized to manage.
          </Notice>
          <button onClick={connect} disabled={busy} className={`${buttonClass} w-full`}>
            {busy ? "Starting…" : "Connect WhatsApp"}
          </button>
        </div>
      )}

      {status === "Connecting" && (
        <div className="mt-5 flex flex-col items-center gap-4 text-center">
          {qr ? (
            <div className="rounded-2xl bg-white p-4">
              <QRCodeSVG value={qr} size={280} />
            </div>
          ) : (
            <Loading label="Generating QR code" />
          )}
          <ol className="w-full max-w-sm list-inside list-decimal space-y-1 text-left text-sm text-muted">
            {PAIRING_STEPS.map((step) => (
              <li key={step}>{step}</li>
            ))}
          </ol>
          <p className="text-xs text-muted">The code refreshes automatically if it expires before you scan it.</p>
        </div>
      )}

      {status === "Connected" && (
        <div className="mt-5 space-y-4">
          <p className="text-sm text-muted">Connected account: <span className="font-semibold text-text">{accountIdentifier ?? "unknown"}</span></p>
          <div className="flex flex-wrap gap-2">
            {!addedToDashboard ? (
              <button onClick={addToDashboard} className={`${buttonClass} px-5 py-2.5`}>Add to Dashboard</button>
            ) : (
              <span className="inline-flex items-center rounded-full bg-lime px-4 py-2 text-sm font-semibold text-on-tint">Added to Dashboard ✓</span>
            )}
            <button onClick={disconnect} disabled={busy} className={`${secondaryButtonClass} px-5 py-2.5`}>Disconnect</button>
          </div>
        </div>
      )}

      {(status === "Disconnected" || status === "Error") && (
        <div className="mt-5">
          <button onClick={reconnect} disabled={busy} className={`${buttonClass} w-full`}>
            {busy ? "Reconnecting…" : "Reconnect"}
          </button>
        </div>
      )}
    </div>
  );
}

/** Self-contained: fetches its own WhatsApp connection detail, so it can be dropped into a modal or a page. */
export function WhatsAppConnectPanel() {
  const { state, reload } = useApi(() => api.appDetail("whatsapp"));
  return (
    <>
      {state.status === "loading" && <Loading />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && <ConnectPanel detail={state.data} onChanged={reload} />}
    </>
  );
}
