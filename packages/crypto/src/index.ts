import crypto from "node:crypto";
import { type EncryptedPayloadEnvelope, PROTOCOL_VERSION } from "@clipsync/protocol";

const ALGORITHM = "aes-256-gcm";
const IV_LENGTH = 12; // 96 bits recommended for GCM
const TAG_LENGTH = 16; // 128 bits auth tag
const PBKDF2_ITERATIONS = 100000;
const PBKDF2_DIGEST = "sha256";
const KEY_LENGTH = 32; // 256 bits

/**
 * Generate a cryptographically secure random salt in base64url
 */
export function generateSalt(bytes = 16): string {
  return crypto.randomBytes(bytes).toString("base64url");
}

/**
 * Derive a 256-bit AES key from a passphrase/secret and salt using PBKDF2-SHA256
 */
export function deriveKey(passphrase: string, salt: string | Buffer): Buffer {
  const saltBuf = typeof salt === "string" ? Buffer.from(salt, "utf8") : salt;
  return crypto.pbkdf2Sync(passphrase, saltBuf, PBKDF2_ITERATIONS, KEY_LENGTH, PBKDF2_DIGEST);
}

/**
 * Encrypt a UTF-8 string using AES-256-GCM.
 * Returns an EncryptedPayloadEnvelope with base64url encoded strings.
 */
export function encryptText(plainText: string, key: Buffer): EncryptedPayloadEnvelope {
  if (key.length !== KEY_LENGTH) {
    throw new Error(`Invalid key length: expected ${KEY_LENGTH} bytes, got ${key.length}`);
  }
  const iv = crypto.randomBytes(IV_LENGTH);
  const cipher = crypto.createCipheriv(ALGORITHM, key, iv);
  const ciphertextBuf = Buffer.concat([cipher.update(plainText, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();

  return {
    version: PROTOCOL_VERSION,
    iv: iv.toString("base64url"),
    ciphertext: ciphertextBuf.toString("base64url"),
    tag: tag.toString("base64url"),
  };
}

/**
 * Decrypt an EncryptedPayloadEnvelope back to a UTF-8 string.
 */
export function decryptText(envelopeOrSerialized: EncryptedPayloadEnvelope | string, key: Buffer): string {
  if (key.length !== KEY_LENGTH) {
    throw new Error(`Invalid key length: expected ${KEY_LENGTH} bytes, got ${key.length}`);
  }

  let envelope: EncryptedPayloadEnvelope;
  if (typeof envelopeOrSerialized === "string") {
    try {
      envelope = JSON.parse(envelopeOrSerialized);
    } catch {
      throw new Error("Failed to parse envelope JSON");
    }
  } else {
    envelope = envelopeOrSerialized;
  }

  const iv = Buffer.from(envelope.iv, "base64url");
  const ciphertext = Buffer.from(envelope.ciphertext, "base64url");
  const tag = Buffer.from(envelope.tag, "base64url");

  if (iv.length !== IV_LENGTH) throw new Error("Invalid IV length in payload envelope");
  if (tag.length !== TAG_LENGTH) throw new Error("Invalid tag length in payload envelope");

  const decipher = crypto.createDecipheriv(ALGORITHM, key, iv);
  decipher.setAuthTag(tag);
  const decryptedBuf = Buffer.concat([decipher.update(ciphertext), decipher.final()]);

  return decryptedBuf.toString("utf8");
}

/**
 * Encrypt arbitrary binary bytes (images, files) with AES-256-GCM.
 */
export function encryptBinary(
  data: Uint8Array,
  key: Buffer
): {
  ciphertext: Buffer;
  iv: string;
  tag: string;
  sha256: string;
} {
  if (key.length !== KEY_LENGTH) {
    throw new Error(`Invalid key length: expected ${KEY_LENGTH} bytes, got ${key.length}`);
  }
  const iv = crypto.randomBytes(IV_LENGTH);
  const cipher = crypto.createCipheriv(ALGORITHM, key, iv);
  const ciphertext = Buffer.concat([cipher.update(Buffer.from(data)), cipher.final()]);
  const tag = cipher.getAuthTag();
  const sha256 = crypto.createHash("sha256").update(Buffer.from(data)).digest("hex");

  return {
    ciphertext,
    iv: iv.toString("base64url"),
    tag: tag.toString("base64url"),
    sha256,
  };
}

/**
 * Decrypt binary bytes with AES-256-GCM.
 */
export function decryptBinary(ciphertext: Buffer, key: Buffer, ivBase64Url: string, tagBase64Url: string): Buffer {
  if (key.length !== KEY_LENGTH) {
    throw new Error(`Invalid key length: expected ${KEY_LENGTH} bytes, got ${key.length}`);
  }
  const iv = Buffer.from(ivBase64Url, "base64url");
  const tag = Buffer.from(tagBase64Url, "base64url");
  const decipher = crypto.createDecipheriv(ALGORITHM, key, iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]);
}

/**
 * Compute standard hex SHA-256 digest of content
 */
export function computeSha256(content: string | Uint8Array): string {
  const hash = crypto.createHash("sha256");
  if (typeof content === "string") {
    hash.update(content, "utf8");
  } else {
    hash.update(Buffer.from(content));
  }
  return hash.digest("hex");
}
