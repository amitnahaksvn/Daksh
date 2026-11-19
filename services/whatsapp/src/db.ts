import { MongoClient, type Db } from "mongodb";
import { config } from "./config.js";

let client: MongoClient | undefined;
let db: Db | undefined;

export async function getDb(): Promise<Db> {
  if (db) return db;
  client = new MongoClient(config.mongoConnectionString);
  await client.connect();
  db = client.db(config.mongoDatabase);
  return db;
}

export async function closeDb(): Promise<void> {
  await client?.close();
}

export type IntegrationCredentialDoc = {
  connectionId: string;
  credentialKey: string;
  encryptedValue: string;
  encryptionVersion: string;
  createdAt: Date;
  updatedAt: Date;
};

/** Same collection/shape the .NET API's IntegrationCredential model documents — this service is its only writer. */
export function credentialsCollection(db: Db) {
  return db.collection<IntegrationCredentialDoc>("integrationCredentials");
}

export type WhatsAppReaction = { by: string; emoji: string };

export type WhatsAppMessageDoc = {
  connectionId: string;
  messageId: string;
  chatId: string;
  chatName?: string;
  /** The sender's own JID within a group (Baileys' message.key.participant) — needed to reconstruct a
   *  valid key when reacting to or quoting a group message later. Undefined for 1:1 chats. */
  participant?: string;
  fromMe: boolean;
  /** True only for a reply Daksha's AI generated and sent on its own (auto-reply) — never for anything
   *  typed by the account owner through Manage, so the UI can style the two differently. */
  isAiGenerated?: boolean;
  text: string;
  /** True once the photo/sticker/video/voice note/document bytes are stored in the whatsappMedia collection. */
  hasMedia?: boolean;
  /** What kind of attachment the message carries, even before its bytes have been downloaded. */
  mediaKind?: "image" | "sticker" | "video" | "audio" | "document";
  mimeType?: string;
  fileName?: string;
  mediaSeconds?: number;
  voiceNote?: boolean;
  /** Outgoing messages only. 1 pending, 2 sent, 3 delivered, 4 read, 5 played. */
  status?: number;
  /** Deleted for everyone by the sender (the text is replaced by a tombstone and any stored media is erased). */
  deleted?: boolean;
  deletedAt?: Date;
  edited?: boolean;
  /** Hidden from Daksha only ("delete for me"); it still exists in WhatsApp. */
  hidden?: boolean;
  starred?: boolean;
  /** We already told WhatsApp this incoming message was read, so we do not send the receipt twice. */
  readSent?: boolean;
  quotedMessageId?: string;
  quotedText?: string;
  reactions: WhatsAppReaction[];
  timestamp: Date;
  createdAt: Date;
};

export type WhatsAppContactDoc = {
  connectionId: string;
  contactId: string;
  lid?: string;
  /** The phone-number id of the same person (the other half of the lid mapping). */
  phoneNumber?: string;
  /** The name saved in the phone's address book (or the group subject for a @g.us id). */
  name?: string;
  /** The contact's own self-chosen WhatsApp display name. */
  notify?: string;
  /** True when WhatsApp reports a business profile for this contact. */
  isBusiness?: boolean;
  updatedAt: Date;
};

/** Contact/group display names, so the chat list can show a person's name rather than a raw WhatsApp id. */
export function contactsCollection(db: Db) {
  return db.collection<WhatsAppContactDoc>("whatsappContacts");
}

/** This service's local record of messages, used only to answer "what did I just receive" for a connection
 *  — the .NET API never writes here, it just relays the WhatsAppMessageReceived event this service reports. */
export function messagesCollection(db: Db) {
  return db.collection<WhatsAppMessageDoc>("whatsappMessages");
}
