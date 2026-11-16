import type { Db } from "mongodb";
import {
  BufferJSON,
  initAuthCreds,
  proto,
  type AuthenticationCreds,
  type AuthenticationState,
  type SignalDataTypeMap,
} from "@whiskeysockets/baileys";
import { credentialsCollection } from "./db.js";
import { decrypt, encrypt } from "./crypto.js";

const ENCRYPTION_VERSION = "aes-256-gcm-v1";

/**
 * Baileys' own docs recommend NOT using the filesystem (`useMultiFileAuthState`) for anything beyond a
 * toy bot, and to back it with a real database instead — this is that adapter, keyed by the Daksha
 * `connectionId` so a service restart restores every user's session from Mongo rather than local disk.
 * Each Signal key (and `creds` itself) is one `integrationCredentials` document, AES-256-GCM encrypted,
 * so updating one key never touches the others.
 */
async function readValue<T>(db: Db, connectionId: string, credentialKey: string): Promise<T | undefined> {
  const doc = await credentialsCollection(db).findOne({ connectionId, credentialKey });
  if (!doc) return undefined;
  return JSON.parse(decrypt(doc.encryptedValue), BufferJSON.reviver) as T;
}

async function writeValue(db: Db, connectionId: string, credentialKey: string, value: unknown): Promise<void> {
  const encryptedValue = encrypt(JSON.stringify(value, BufferJSON.replacer));
  await credentialsCollection(db).updateOne(
    { connectionId, credentialKey },
    { $set: { encryptedValue, encryptionVersion: ENCRYPTION_VERSION, updatedAt: new Date() }, $setOnInsert: { createdAt: new Date() } },
    { upsert: true },
  );
}

async function deleteValue(db: Db, connectionId: string, credentialKey: string): Promise<void> {
  await credentialsCollection(db).deleteOne({ connectionId, credentialKey });
}

export async function removeAllCredentials(db: Db, connectionId: string): Promise<void> {
  await credentialsCollection(db).deleteMany({ connectionId });
}

export async function useMongoAuthState(
  db: Db,
  connectionId: string,
): Promise<{ state: AuthenticationState; saveCreds: () => Promise<void> }> {
  const creds: AuthenticationCreds = (await readValue<AuthenticationCreds>(db, connectionId, "creds")) ?? initAuthCreds();

  return {
    state: {
      creds,
      keys: {
        get: async <T extends keyof SignalDataTypeMap>(type: T, ids: string[]) => {
          const data: { [id: string]: SignalDataTypeMap[T] } = {};
          await Promise.all(
            ids.map(async (id) => {
              let value = await readValue<SignalDataTypeMap[T]>(db, connectionId, `${type}-${id}`);
              if (type === "app-state-sync-key" && value) {
                value = proto.Message.AppStateSyncKeyData.fromObject(value as object) as unknown as SignalDataTypeMap[T];
              }
              if (value !== undefined) data[id] = value;
            }),
          );
          return data;
        },
        set: async (data) => {
          const tasks: Promise<void>[] = [];
          for (const category of Object.keys(data) as (keyof SignalDataTypeMap)[]) {
            const entries = data[category];
            if (!entries) continue;
            for (const id of Object.keys(entries)) {
              const value = entries[id];
              const credentialKey = `${category}-${id}`;
              tasks.push(value ? writeValue(db, connectionId, credentialKey, value) : deleteValue(db, connectionId, credentialKey));
            }
          }
          await Promise.all(tasks);
        },
      },
    },
    saveCreds: () => writeValue(db, connectionId, "creds", creds),
  };
}
