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

export type IntegrationCredentialDoc = {
  connectionId: string;
  credentialKey: string;
  encryptedValue: string;
  encryptionVersion: string;
  createdAt: Date;
  updatedAt: Date;
};

/** Same collection/shape the .NET API's IntegrationCredential documents. */
export function credentialsCollection(db: Db) {
  return db.collection<IntegrationCredentialDoc>("integrationCredentials");
}
