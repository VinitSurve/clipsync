# ClipSync — Security Architecture & Threat Model

ClipSync is designed around a **zero-knowledge cloud relay model**: the server facilitates discovery, real-time message fan-out, persistent history, and large binary storage, but **never has access to plaintext clipboard data or encryption keys**.

---

## 1. End-to-End Encryption (E2EE)

All clipboard contents (text, code, URLs, images, and files) are encrypted on the sending device before being transmitted over the wire or stored in cloud databases and object storage.

### Cipher Suite
- **Algorithm**: `AES-256-GCM` (Galois/Counter Mode) authenticated encryption.
- **Key Size**: 256 bits (32 bytes).
- **Nonce/IV**: 96 bits (12 bytes) cryptographically random per item. Nonces are never reused.
- **Authentication Tag**: 128 bits (16 bytes). Provides both confidentiality and tamper resistance.

### Key Derivation
- Master sync keys are derived on-device using `PBKDF2-HMAC-SHA256` with **100,000 iterations**.
- A per-group random cryptographic salt (16 bytes base64url) is generated upon initial setup and distributed to paired devices during device pairing.
- The master passphrase or derived symmetric key is **never transmitted to the server**.

---

## 2. Wire Protocol & Envelope Structure

Every sync item transmitted over WebSockets or stored in PostgreSQL contains an encrypted envelope:

```json
{
  "version": 1,
  "itemId": "item-171928374-a9b8c7",
  "type": "text",
  "sourceDeviceId": "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d",
  "createdAt": "2026-10-26T10:00:00.000Z",
  "payload": "{\"version\":1,\"iv\":\"...\",\"ciphertext\":\"...\",\"tag\":\"...\"}",
  "metadata": {
    "size": 42,
    "sha256": "4b227777d4dd1fc61c6f884f48641d02b4d121d3fd328cb08b5531fcacdabf8a",
    "previewSnippet": "...",
    "sourceApp": "VS Code"
  }
}
```

The server validates only message structure and authentication headers. Payloads remain opaque base64url ciphertexts.

---

## 3. Large File & Image Encryption (Object Storage)

Large binary payloads (images, documents, archives up to 50MB) are **never** streamed directly across WebSockets:

1. **Client-side Encryption**: The sending device encrypts raw bytes with `AES-256-GCM` and computes a `SHA-256` checksum of the unencrypted file.
2. **Authorized Presigned URL**: The client requests a short-lived presigned PUT URL (`/v1/storage/upload-url`) bound to the SHA-256 checksum and size.
3. **Direct Upload**: The client uploads the encrypted bytes directly to S3 or MinIO.
4. **Metadata Broadcast**: The client broadcasts a lightweight `SyncItem` referencing the `storageRef` and encryption IV/tag.
5. **Decryption & Verification**: Receiving clients request an authorized download URL (`/v1/storage/download-url/:key`), fetch the ciphertext, decrypt locally, and verify the SHA-256 digest against the metadata.

---

## 4. Device Pairing & Authentication

1. **Device Tokens**: Each device holds a high-entropy 256-bit secret token. The server stores only `SHA-256(token)` in PostgreSQL, preventing token leakage in case of database dumps.
2. **6-Digit Pairing & QR Codes**: Device pairing uses ephemeral 10-minute pairing sessions. Once claimed by a second device, the session is invalidated.
3. **Device Revocation**: When a device is revoked (`DELETE /v1/devices/:id`), the server terminates all active WebSockets for that device ID and permanently rejects future API calls.

---

## 5. Clipboard Loop & Echo Prevention

To prevent runaway copy loops:
- Every sync item carries the originating `sourceDeviceId`. The server suppresses broadcasting an item back to its author, and clients drop self-echoes.
- Clients maintain an in-memory hash cache of the last incoming item. When the client sets the native operating system pasteboard, the change listener detects that the hash matches the incoming clip and suppresses re-broadcasting.

---

## 6. Sensitive App Exclusion

The native clients (macOS, Windows, Android) include configurable exclusion filters that automatically ignore clips originating from password managers (such as 1Password, Bitwarden, KeepassXC, and Apple Keychain).
