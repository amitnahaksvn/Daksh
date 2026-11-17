import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * Where each setting comes from, highest priority first — so the same code runs on a laptop and behind a
 * secret manager without changes:
 *
 *   1. Environment variable          — what Docker, Kubernetes, Azure/AWS/GCP and Vault agents inject.
 *   2. `<NAME>_FILE` environment var — path to a file holding the value (Docker/Kubernetes secret mounts,
 *                                      Vault Agent file templates). Trailing newline is trimmed.
 *   3. `.env` file                   — local development (loaded into the environment by loadEnv.ts).
 *   4. API appsettings (Integrations:Telegram) — local-development fallback so there is one place to
 *                                      configure the app credentials. Remove them from the file once a
 *                                      secret store provides them; nothing else needs to change.
 */
type ApiSettings = Record<string, unknown>;

function readApiSettings(): ApiSettings {
  const dir = fileURLToPath(new URL("../../api/Daksh.Api/", import.meta.url));
  let merged: ApiSettings = {};
  for (const file of ["appsettings.json", "appsettings.Development.json"]) {
    try {
      const json = JSON.parse(readFileSync(dir + file, "utf8")) as { Integrations?: { Telegram?: ApiSettings } };
      merged = { ...merged, ...json.Integrations?.Telegram };
    } catch { /* missing or unreadable — fall through */ }
  }
  return merged;
}
const apiSettings = readApiSettings();

function fromEnvOrFile(name: string): string | undefined {
  const direct = process.env[name];
  if (direct) return direct;
  const file = process.env[`${name}_FILE`];
  if (file) {
    try { return readFileSync(file, "utf8").trim(); } catch (err) {
      throw new Error(`${name}_FILE points to ${file}, which could not be read: ${(err as Error).message}`);
    }
  }
  return undefined;
}

/** A setting that may also come from the API's appsettings (`Integrations:Telegram:<apiKey>`). */
function setting(name: string, apiKey?: string): string | undefined {
  const found = fromEnvOrFile(name);
  if (found !== undefined) return found;
  const fallback = apiKey ? apiSettings[apiKey] : undefined;
  return fallback === undefined || fallback === null ? undefined : String(fallback);
}

function required(name: string, apiKey?: string): string {
  const value = setting(name, apiKey);
  if (!value) throw new Error(`${name} is required (environment variable, ${name}_FILE, or services/telegram/.env).`);
  return value;
}

export const config = {
  port: Number(setting("PORT") ?? 4200),
  // The shared secret with the .NET API (Integrations:Telegram:InternalServiceKey).
  internalServiceKey: required("INTERNAL_SERVICE_KEY", "InternalServiceKey"),
  mongoConnectionString: required("MONGO_CONNECTION_STRING"),
  mongoDatabase: required("MONGO_DATABASE"),
  encryptionKeys: JSON.parse(setting("ENCRYPTION_KEYS_JSON") ?? "{}") as Record<string, string>,
  activeEncryptionKeyId: required("ACTIVE_ENCRYPTION_KEY_ID"),
  // Telegram application credentials (my.telegram.org). Optional at startup so the service can boot and
  // report a clear "not configured" error to the user instead of crashing.
  apiId: Number(setting("TELEGRAM_API_ID", "ApiId") ?? 0),
  apiHash: setting("TELEGRAM_API_HASH", "ApiHash") ?? "",
  dakshApiBaseUrl: setting("DAKSH_API_BASE_URL") ?? "http://localhost:5080",
};

export const telegramConfigured = config.apiId > 0 && config.apiHash.length > 0 && config.apiHash !== "change-me";
