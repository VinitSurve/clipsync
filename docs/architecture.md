# ClipSync architecture

## Components

1. **Native clients**
   - macOS: Swift menu-bar app (clipboard change monitoring through NSPasteboard; app-level history and permission UX).
   - Windows: C#/.NET tray app (clipboard notifications and Windows clipboard APIs).
   - Android: Kotlin app (ClipboardManager, foreground/user-visible operation where required by OS policy; avoid promising unrestricted background clipboard reads).
2. **Crypto and protocol package**: versioned event envelope, content encryption on-device, per-user/device key lifecycle, replay/idempotency IDs.
3. **Cloud API + WebSocket relay**: authentication, device membership, event fan-out, history metadata, signed upload/download authorization.
4. **PostgreSQL**: users, devices, pairings, item metadata, cursor/retention state.
5. **Object storage**: encrypted image/file bytes, multipart uploads, expiring signed URLs, checksums and size limits.

## Event flow

- Client detects a supported clipboard item and checks local opt-in/type/size rules.
- Client creates a unique item ID and encrypts content locally with authenticated encryption.
- Small text events can go through API/WebSocket; images/files upload encrypted bytes to object storage and publish only metadata.
- Receiving clients validate event version, sender/device membership, timestamp and item ID, decrypt locally, then apply clipboard update only if sync is enabled and the event is not a self-originated echo.
- Clients track cursors and fetch missed events after reconnect.

## Security requirements before public deployment

- TLS everywhere; no plaintext clipboard content at server.
- Proper account authentication and explicit device pairing/revocation.
- End-to-end encryption with authenticated encryption (e.g. AES-GCM) and secure key agreement/key backup design. Never store plaintext content keys in server DB.
- Short-lived signed object URLs; encrypted object contents; integrity hashes; file size/type limits.
- Do not sync passwords/one-time codes by default; add allow/deny rules and pause/auto-clear controls.
- Rate limits, abuse controls, audit events without content, durable storage, retention/delete controls, backups, and secret management.

## Suggested deployment

A managed container or app platform supporting long-lived WebSockets, PostgreSQL, and S3-compatible object storage. The cloud is the authority; devices do not depend on the Windows PC being online.
