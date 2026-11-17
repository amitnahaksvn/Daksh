import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { config } from "./config.js";

const IV_LENGTH = 12;
const TAG_LENGTH = 16;

/**
 * AES-256-GCM, format `<keyId>:<base64(iv + ciphertext + tag)>` — matches .NET's AesGcm defaults
 * (12-byte nonce, 16-byte tag) so the scheme is shared/compatible across services even though today
 * only this service ever reads or writes WhatsApp credential values.
 */
export function encrypt(plaintext: string): string {
  const keyId = config.activeEncryptionKeyId;
  const key = Buffer.from(config.encryptionKeys[keyId], "base64");
  const iv = randomBytes(IV_LENGTH);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ciphertext = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return `${keyId}:${Buffer.concat([iv, ciphertext, tag]).toString("base64")}`;
}

export function decrypt(encoded: string): string {
  const sep = encoded.indexOf(":");
  const keyId = encoded.slice(0, sep);
  const key = Buffer.from(config.encryptionKeys[keyId], "base64");
  const packed = Buffer.from(encoded.slice(sep + 1), "base64");
  const iv = packed.subarray(0, IV_LENGTH);
  const tag = packed.subarray(packed.length - TAG_LENGTH);
  const ciphertext = packed.subarray(IV_LENGTH, packed.length - TAG_LENGTH);
  const decipher = createDecipheriv("aes-256-gcm", key, iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
}
