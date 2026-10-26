# ClipSync Cloudflare Server (Phase 1)

Cloudflare Workers + Durable Objects backend for ClipSync universal clipboard synchronization.

## Highlights & Design

- **Runtime**: Cloudflare Workers with [Hono](https://hono.dev) framework for REST API routing.
- **Coordination & State**: Cloudflare Durable Objects backed by **Durable Object SQLite Storage**.
- **Real-Time Communication**: WebSocket Hibernation API (`ctx.acceptWebSocket(server)`). Idle WebSocket connections hibernate in memory, consuming **0 GB-seconds** of compute while keeping sockets open.
- **Security & Encryption**: End-to-end encrypted payloads using AES-256-GCM. The server never has access to plaintext clipboard data or decryption keys.
- **100% Free Plan Friendly**: Specifically designed to operate completely within Cloudflare's Free tier without requiring a credit card, debit card, or billing profile.
- **Compatibility**: Matches ClipSync's existing REST API and WebSocket wire protocol (`/health`, `/v1/devices/register`, `/v1/auth/pairing/session`, `/v1/auth/pair`, `/v1/devices`, `/v1/history`, `/v1/events`). Existing native apps (macOS, Windows, Android) can connect seamlessly.

---

## Cloudflare Free Tier Verification & Limitations

This Phase 1 implementation uses **only services available on Cloudflare's Free Plan without adding any payment method**:

| Feature / Service | Free Tier Allowance | ClipSync Usage / Impact |
| :--- | :--- | :--- |
| **Cloudflare Workers** | 100,000 requests/day<br>10 ms CPU time per request | Each clipboard sync is ~1 ms CPU. 100,000 requests/day is more than enough for personal multi-device use (~3,300 clipboard syncs/hour). |
| **Durable Objects** | 100,000 requests/day<br>13,000 GB-seconds/day (free tier) | Durable Object uses the WebSocket Hibernation API; hibernated connections consume 0 GB-seconds. |
| **Durable Object SQLite** | Built into Durable Objects on free tier | Retains up to 500 recent encrypted clipboard items and device pairing records. No external database or paid plan needed. |
| **Cloudflare R2** | *Not used in Phase 1* | Cloudflare R2 requires linking a credit/debit card to activate. To maintain a strict zero-card policy, Phase 1 handles encrypted text clipboard sync exclusively. |

---

## Local Development & Testing

### 1. Run Local Tests

Run the comprehensive integration test suite (covers health checks, device registration, authentication, 6-digit pairing codes, WebSocket upgrades, and end-to-end encrypted sync):

```bash
# Run tests for Cloudflare server
pnpm --filter @clipsync/server-cloudflare test

# Or run all monorepo test suites
pnpm test
```

### 2. Run Local Development Server with Wrangler

You can run the Cloudflare worker locally using Wrangler's local development runtime:

```bash
# From workspace root
pnpm dev:cloudflare

# Or directly in apps/server-cloudflare
cd apps/server-cloudflare
npx wrangler dev
```

The local development server will start at `http://localhost:8787` (or prompt for an alternative port if port 8787 is occupied by the Node server).

---

## Deploying to Cloudflare (Free Plan)

Follow these steps to deploy your private ClipSync server to Cloudflare for free without entering payment details.

### Step 1: Install & Login with Wrangler

If you don't already have Wrangler logged in to your free Cloudflare account:

```bash
cd apps/server-cloudflare
npx wrangler login
```

A browser window will open asking you to authorize Wrangler. Sign in with your free Cloudflare account. **Do not enter any payment method or credit card.**

### Step 2: Deploy the Worker

Deploy the Worker and its Durable Object migration:

```bash
npx wrangler deploy
```

Wrangler will output your live public worker URL, for example:
```text
Uploaded clipsync-server-cloudflare (x.xx sec)
Deployed clipsync-server-cloudflare triggers (x.xx sec)
  https://clipsync-server-cloudflare.<your-subdomain>.workers.dev
```

### Step 3: Verify the Live Deployment

Check the health endpoint in your terminal or browser:

```bash
curl https://clipsync-server-cloudflare.<your-subdomain>.workers.dev/health
```

Expected response:
```json
{
  "ok": true,
  "service": "clipsync-cloudflare-worker",
  "version": "1.0.0",
  "protocolVersion": 1,
  "timestamp": "..."
}
```

---

## Connecting Native Clients (macOS, Windows, Android)

Because the Cloudflare Worker conforms to the exact ClipSync wire protocol, you only need to point your apps to your Cloudflare URL instead of `http://localhost:8787`.

### macOS App
1. Open ClipSync on your Mac.
2. In the menu bar, open **Settings / Preferences**.
3. Change the Server URL from `http://localhost:8787` to:
   ```text
   https://clipsync-server-cloudflare.<your-subdomain>.workers.dev
   ```
4. Click **Register Device** or **Pair**.

### Windows PC App
1. Open ClipSync from the system tray.
2. Open **Settings**.
3. Set the Server URL to:
   ```text
   https://clipsync-server-cloudflare.<your-subdomain>.workers.dev
   ```
4. Enter your 6-digit pairing code generated from your Mac app (or vice-versa).

### Android App
1. Open ClipSync.
2. Enter the server URL or scan the pairing QR code from your Mac/Windows app.
3. Enter the 6-digit pairing code to pair instantly across different Wi-Fi / cellular networks.
