import type { Db } from "mongodb";
import bigInt from "big-integer";
import { Api, TelegramClient } from "telegram";
import { StringSession } from "telegram/sessions/index.js";
import { NewMessage, type NewMessageEvent } from "telegram/events/index.js";
import { reportEvent } from "./apiClient.js";
import { config, telegramConfigured } from "./config.js";
import { decrypt, encrypt } from "./crypto.js";
import { credentialsCollection } from "./db.js";

type Status = "NotConnected" | "Connecting" | "Connected" | "Disconnected" | "Error";

type Entry = {
  client?: TelegramClient;
  status: Status;
  qr?: string;
  qrExpiresAt?: Date;
  /** The account has two-step verification on: the user must supply their cloud password. */
  passwordRequired: boolean;
  passwordResolver?: (password: string) => void;
  error?: string;
};

const SESSION_KEY = "telegramSession";
const sessions = new Map<string, Entry>();

function entryFor(connectionId: string): Entry {
  let entry = sessions.get(connectionId);
  if (!entry) {
    entry = { status: "NotConnected", passwordRequired: false };
    sessions.set(connectionId, entry);
  }
  return entry;
}

export function getState(connectionId: string) {
  const entry = sessions.get(connectionId);
  if (!entry) return { status: "Disconnected" as Status, qr: null, expiresAt: null, passwordRequired: false, error: null };
  const expired = entry.qrExpiresAt ? entry.qrExpiresAt.getTime() < Date.now() : false;
  return {
    status: entry.status,
    qr: expired ? null : (entry.qr ?? null),
    expiresAt: entry.qrExpiresAt?.toISOString() ?? null,
    passwordRequired: entry.passwordRequired,
    error: entry.error ?? null,
  };
}

async function saveSession(db: Db, connectionId: string, client: TelegramClient): Promise<void> {
  const encryptedValue = encrypt(String(client.session.save()));
  await credentialsCollection(db).updateOne(
    { connectionId, credentialKey: SESSION_KEY },
    { $set: { encryptedValue, encryptionVersion: "aes-256-gcm-v1", updatedAt: new Date() }, $setOnInsert: { createdAt: new Date() } },
    { upsert: true },
  );
}

async function loadSession(db: Db, connectionId: string): Promise<string | undefined> {
  const doc = await credentialsCollection(db).findOne({ connectionId, credentialKey: SESSION_KEY });
  return doc ? decrypt(doc.encryptedValue) : undefined;
}

function newClient(session: string): TelegramClient {
  const client = new TelegramClient(new StringSession(session), config.apiId, config.apiHash, { connectionRetries: 5, useWSS: false });
  client.setLogLevel("none" as never);
  return client;
}

function nameOf(entity: unknown): string | undefined {
  if (entity instanceof Api.User) return [entity.firstName, entity.lastName].filter(Boolean).join(" ") || entity.username || undefined;
  if (entity instanceof Api.Chat || entity instanceof Api.Channel) return entity.title;
  return undefined;
}

/** Broadcast channels are one-way feeds — not conversations Daksha should list or answer. */
function isBroadcast(entity: unknown): boolean {
  return entity instanceof Api.Channel && entity.broadcast === true;
}

async function reportMessage(connectionId: string, msg: Api.Message, history: boolean): Promise<void> {
  const text = msg.message;
  if (!text || !msg.id || msg.chatId === undefined) return;
  const chat = await msg.getChat().catch(() => undefined);
  if (isBroadcast(chat)) return;
  const sender = msg.out ? undefined : await msg.getSender().catch(() => undefined);
  await reportEvent({
    connectionId,
    eventType: "Message",
    messageId: String(msg.id),
    chatId: String(msg.chatId),
    chatName: nameOf(chat),
    isGroup: !(chat instanceof Api.User),
    fromMe: msg.out === true,
    senderName: msg.out ? "Me" : nameOf(sender),
    text,
    timestamp: new Date(msg.date * 1000).toISOString(),
    history,
  });
}

/** Seeds the chat list: pulls the most recent conversations so the screen isn't empty after connecting. It
 *  also warms GramJS's entity cache, which sending later relies on. */
async function backfill(connectionId: string, client: TelegramClient): Promise<void> {
  try {
    const dialogs = await client.getDialogs({ limit: 25 });
    for (const dialog of dialogs) {
      if (!dialog.entity || isBroadcast(dialog.entity)) continue;
      // One unreadable chat (e.g. a private channel you were removed from → CHANNEL_PRIVATE) must not stop the rest.
      try {
        const messages = await client.getMessages(dialog.entity, { limit: 15 });
        for (const m of [...messages].reverse()) await reportMessage(connectionId, m, true);
      } catch (err) {
        console.warn(`Telegram backfill skipped a chat for ${connectionId}: ${(err as Error).message}`);
      }
    }
  } catch (err) {
    console.error(`Telegram backfill failed for ${connectionId}:`, err);
  }
}

async function onConnected(db: Db, connectionId: string, entry: Entry, client: TelegramClient): Promise<void> {
  const me = (await client.getMe()) as Api.User;
  entry.status = "Connected";
  entry.qr = undefined;
  entry.passwordRequired = false;
  entry.error = undefined;
  await saveSession(db, connectionId, client);
  client.addEventHandler((event: NewMessageEvent) => void reportMessage(connectionId, event.message, false), new NewMessage({}));
  const account = me.username ? `@${me.username}` : [me.firstName, me.lastName].filter(Boolean).join(" ") || me.phone || "Telegram";
  await reportEvent({ connectionId, eventType: "Status", status: "Connected", accountIdentifier: account });
  void backfill(connectionId, client);
}

/** Starts a fresh QR sign-in (or resumes a stored session when there is one). */
export async function connect(db: Db, connectionId: string): Promise<void> {
  const entry = entryFor(connectionId);
  if (entry.status === "Connected" || (entry.status === "Connecting" && entry.client)) return;
  if (!telegramConfigured) {
    entry.status = "Error";
    entry.error = "Telegram is not configured on the server (TELEGRAM_API_ID / TELEGRAM_API_HASH).";
    await reportEvent({ connectionId, eventType: "Status", status: "Error", error: entry.error });
    return;
  }

  entry.status = "Connecting";
  entry.error = undefined;
  entry.passwordRequired = false;
  const stored = await loadSession(db, connectionId);
  const client = newClient(stored ?? "");
  entry.client = client;

  void (async () => {
    try {
      await client.connect();
      if (stored && (await client.checkAuthorization())) {
        await onConnected(db, connectionId, entry, client);
        return;
      }
      await client.signInUserWithQrCode(
        { apiId: config.apiId, apiHash: config.apiHash },
        {
          qrCode: async ({ token, expires }) => {
            entry.qr = `tg://login?token=${token.toString("base64url")}`;
            entry.qrExpiresAt = new Date(expires * 1000);
          },
          password: async () => {
            entry.passwordRequired = true;
            entry.qr = undefined;
            return new Promise<string>((resolve) => { entry.passwordResolver = resolve; });
          },
          onError: async (err) => {
            // A wrong cloud password lets the user try again; anything else ends this attempt.
            if (entry.passwordRequired && /PASSWORD/i.test(err.message)) {
              entry.error = "That password is not correct.";
              return false;
            }
            entry.error = err.message;
            return true;
          },
        },
      );
      await onConnected(db, connectionId, entry, client);
    } catch (err) {
      entry.status = "Error";
      entry.error = err instanceof Error ? err.message : "Telegram sign-in failed.";
      entry.qr = undefined;
      await reportEvent({ connectionId, eventType: "Status", status: "Error", error: entry.error });
      await client.disconnect().catch(() => undefined);
      entry.client = undefined;
    }
  })();
}

export function submitPassword(connectionId: string, password: string): boolean {
  const entry = sessions.get(connectionId);
  if (!entry?.passwordResolver) return false;
  entry.error = undefined;
  entry.passwordResolver(password);
  entry.passwordResolver = undefined;
  return true;
}

export async function reconnect(db: Db, connectionId: string): Promise<void> {
  await teardown(connectionId, false);
  await connect(db, connectionId);
}

async function teardown(connectionId: string, logOut: boolean): Promise<void> {
  const entry = sessions.get(connectionId);
  if (entry?.client) {
    try {
      if (logOut) await entry.client.invoke(new Api.auth.LogOut());
    } catch { /* already invalid */ }
    await entry.client.disconnect().catch(() => undefined);
    await entry.client.destroy().catch(() => undefined);
  }
  sessions.delete(connectionId);
}

export async function disconnect(db: Db, connectionId: string): Promise<void> {
  await teardown(connectionId, true);
  await credentialsCollection(db).deleteMany({ connectionId, credentialKey: SESSION_KEY });
  await reportEvent({ connectionId, eventType: "Status", status: "Disconnected" });
}

export async function sendMessage(connectionId: string, chatId: string, text: string): Promise<{ messageId: string; timestamp: string }> {
  const entry = sessions.get(connectionId);
  if (!entry?.client || entry.status !== "Connected") throw new Error("Telegram is not connected.");
  const sent = await entry.client.sendMessage(bigInt(chatId) as never, { message: text });
  return { messageId: String(sent.id), timestamp: new Date(sent.date * 1000).toISOString() };
}

export async function restoreSessionsFromDatabase(db: Db): Promise<void> {
  if (!telegramConfigured) {
    console.warn("TELEGRAM_API_ID / TELEGRAM_API_HASH not set — Telegram sign-in is disabled until they are.");
    return;
  }
  const docs = await credentialsCollection(db).find({ credentialKey: SESSION_KEY }).toArray();
  for (const doc of docs) {
    try { await connect(db, doc.connectionId); } catch (err) { console.error(`Could not restore Telegram session ${doc.connectionId}:`, err); }
  }
}
