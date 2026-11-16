import type { Boom } from "@hapi/boom";
import type { Db } from "mongodb";
import { pino } from "pino";
import {
  DisconnectReason,
  fetchLatestBaileysVersion,
  makeCacheableSignalKeyStore,
  makeWASocket,
  type WASocket,
} from "@whiskeysockets/baileys";
import { removeAllCredentials, useMongoAuthState } from "./authStateMongo.js";
import { reportEvent } from "./apiClient.js";

export type ConnectionStatus = "Connecting" | "Connected" | "Disconnected" | "Error";

type SessionEntry = {
  sock: WASocket | null;
  status: ConnectionStatus;
  qr?: string;
  qrExpiresAt?: Date;
  accountIdentifier?: string;
  lastError?: string;
  reconnectAttempts: number;
  reconnectTimer?: NodeJS.Timeout;
  /** True while a socket is actively being created, so a second concurrent connect/reconnect call for
   *  the same connectionId waits on this instead of opening a second WhatsApp session. */
  starting: Promise<void> | null;
};

const QR_LIFETIME_MS = 20_000; // Baileys reissues a fresh QR roughly this often until it's scanned.
const MAX_RECONNECT_ATTEMPTS = 10;
const logger = pino({ level: process.env.BAILEYS_LOG_LEVEL ?? "warn" });

const sessions = new Map<string, SessionEntry>();

function entryFor(connectionId: string): SessionEntry {
  let entry = sessions.get(connectionId);
  if (!entry) {
    entry = { sock: null, status: "Disconnected", reconnectAttempts: 0, starting: null };
    sessions.set(connectionId, entry);
  }
  return entry;
}

async function startSocket(db: Db, connectionId: string): Promise<void> {
  const entry = entryFor(connectionId);
  if (entry.starting) return entry.starting; // another caller is already starting this exact session
  // A second makeWASocket() on top of an already-open one isn't harmless — WhatsApp treats it as a
  // conflicting device and force-logs the real session out. If this one is live, there's nothing to do.
  if (entry.status === "Connected" && entry.sock) return;

  entry.starting = (async () => {
    entry.status = "Connecting";
    await reportEvent(connectionId, "WhatsAppConnectionStarted", { status: "Connecting" });

    const { state, saveCreds } = await useMongoAuthState(db, connectionId);
    const { version } = await fetchLatestBaileysVersion();
    const sock = makeWASocket({
      version,
      auth: { creds: state.creds, keys: makeCacheableSignalKeyStore(state.keys, logger) },
      logger,
      syncFullHistory: false,
    });
    entry.sock = sock;

    sock.ev.on("creds.update", saveCreds);
    sock.ev.on("connection.update", (update) => void onConnectionUpdate(db, connectionId, entry, update));
  })();

  try {
    await entry.starting;
  } finally {
    entry.starting = null;
  }
}

async function onConnectionUpdate(
  db: Db,
  connectionId: string,
  entry: SessionEntry,
  update: { connection?: "open" | "connecting" | "close"; lastDisconnect?: { error?: Error }; qr?: string },
): Promise<void> {
  const { connection, lastDisconnect, qr } = update;

  if (qr) {
    entry.qr = qr;
    entry.qrExpiresAt = new Date(Date.now() + QR_LIFETIME_MS);
    entry.status = "Connecting";
    await reportEvent(connectionId, "WhatsAppQrGenerated", { status: "Connecting", qr, qrExpiresAt: entry.qrExpiresAt });
    // Baileys doesn't emit a distinct "expired" event; schedule our own so UIs relying on this service's
    // GET /qr (rather than just the SignalR push) can tell a stale code apart from a fresh one.
    setTimeout(() => {
      if (entry.qr === qr && entry.status !== "Connected") void reportEvent(connectionId, "WhatsAppQrExpired", { status: entry.status });
    }, QR_LIFETIME_MS);
  }

  if (connection === "open") {
    entry.status = "Connected";
    entry.qr = undefined;
    entry.qrExpiresAt = undefined;
    entry.reconnectAttempts = 0;
    entry.accountIdentifier = entry.sock?.user?.id;
    entry.lastError = undefined;
    await reportEvent(connectionId, "WhatsAppConnected", { status: "Connected", accountIdentifier: entry.accountIdentifier });
  }

  if (connection === "close") {
    const statusCode = (lastDisconnect?.error as Boom | undefined)?.output?.statusCode;
    const loggedOut = statusCode === DisconnectReason.loggedOut;

    if (loggedOut) {
      entry.status = "Disconnected";
      entry.sock = null;
      await removeAllCredentials(db, connectionId);
      await reportEvent(connectionId, "WhatsAppDisconnected", { status: "Disconnected" });
      return;
    }

    entry.reconnectAttempts += 1;
    if (entry.reconnectAttempts > MAX_RECONNECT_ATTEMPTS) {
      entry.status = "Error";
      entry.lastError = "Could not reconnect to WhatsApp after repeated attempts.";
      await reportEvent(connectionId, "WhatsAppConnectionFailed", { status: "Error", error: entry.lastError });
      return;
    }

    entry.status = "Connecting";
    const delayMs = Math.min(1000 * 2 ** entry.reconnectAttempts, 30_000);
    if (entry.reconnectTimer) clearTimeout(entry.reconnectTimer);
    entry.reconnectTimer = setTimeout(() => void startSocket(db, connectionId), delayMs);
  }
}

/** Starts (or resumes, idempotently) a session for this connectionId. */
export async function connect(db: Db, connectionId: string): Promise<void> {
  await startSocket(db, connectionId);
}

export function getQr(connectionId: string): { qr: string | null; expiresAt: Date | null; status: ConnectionStatus } {
  const entry = sessions.get(connectionId);
  if (!entry) return { qr: null, expiresAt: null, status: "Disconnected" };
  const expired = entry.qrExpiresAt ? entry.qrExpiresAt.getTime() < Date.now() : false;
  return { qr: expired ? null : (entry.qr ?? null), expiresAt: entry.qrExpiresAt ?? null, status: entry.status };
}

export function getStatus(connectionId: string): { status: ConnectionStatus; accountIdentifier?: string; lastError?: string } {
  const entry = sessions.get(connectionId);
  if (!entry) return { status: "Disconnected" };
  return { status: entry.status, accountIdentifier: entry.accountIdentifier, lastError: entry.lastError };
}

export async function reconnect(db: Db, connectionId: string): Promise<void> {
  const entry = entryFor(connectionId);
  entry.reconnectAttempts = 0;
  if (entry.sock) {
    try {
      entry.sock.end(undefined);
    } catch {
      /* already closed */
    }
    entry.sock = null;
  }
  await startSocket(db, connectionId);
}

/** Logs the WhatsApp account out (invalidating the session on WhatsApp's side) and clears stored credentials. */
export async function disconnect(db: Db, connectionId: string): Promise<void> {
  const entry = sessions.get(connectionId);
  if (entry?.reconnectTimer) clearTimeout(entry.reconnectTimer);
  if (entry?.sock) {
    try {
      await entry.sock.logout();
    } catch {
      /* best-effort; session is being torn down either way */
    }
  }
  await removeAllCredentials(db, connectionId);
  sessions.delete(connectionId);
  await reportEvent(connectionId, "WhatsAppDisconnected", { status: "Disconnected" });
}

/** Called once at startup: resumes every connection this process previously held an active WhatsApp session for. */
export async function restoreSessionsFromDatabase(db: Db): Promise<void> {
  const connectionIds = await db.collection("integrationCredentials").distinct("connectionId", { credentialKey: "creds" });
  await Promise.all(
    connectionIds.map(async (connectionId: string) => {
      try {
        await connect(db, connectionId);
      } catch (err) {
        logger.error({ err, connectionId }, "Failed to restore WhatsApp session on startup");
      }
    }),
  );
}
