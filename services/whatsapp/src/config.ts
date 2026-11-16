function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required (set it in services/whatsapp/.env).`);
  return value;
}

export const config = {
  port: Number(process.env.PORT ?? 4100),
  internalServiceKey: required("INTERNAL_SERVICE_KEY"),
  mongoConnectionString: required("MONGO_CONNECTION_STRING"),
  mongoDatabase: required("MONGO_DATABASE"),
  // AES key(s) matching Daksh.Api's Security:Encryption:Keys — this service encrypts/decrypts the
  // WhatsApp session material it alone owns, using the same shared-secret convention.
  encryptionKeys: JSON.parse(process.env.ENCRYPTION_KEYS_JSON ?? "{}") as Record<string, string>,
  activeEncryptionKeyId: required("ACTIVE_ENCRYPTION_KEY_ID"),
  // Where this service calls back into the .NET API to report connection events.
  dakshApiBaseUrl: process.env.DAKSH_API_BASE_URL ?? "http://localhost:5080",
};
