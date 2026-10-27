import { DurableObject } from "cloudflare:workers";
import {
  type SyncItem,
  type StoredSyncItem,
  type WsClientMessage,
  type WsServerMessage,
  type DeviceInfo,
  type RegisterDeviceResponse,
  type PairingSessionResponse,
  type ClaimPairingResponse,
  type TransferManifest,
  type InitTransferResponse,
  type AckTransferResponse,
  type PendingTransfersResponse,
  type CleanupTransferResponse,
  validateSyncItem,
  validateTransferManifest,
  PROTOCOL_VERSION,
} from "@clipsync/protocol";

export interface Env {
  CLIPSYNC_ROOM: DurableObjectNamespace<ClipSyncRoom>;
  SERVER_ORIGIN?: string;
  CLIPSYNC_DEV_TOKEN?: string;
}

interface SocketAttachment {
  deviceId: string;
  label: string;
  platform: string;
}

export async function hashToken(token: string): Promise<string> {
  const data = new TextEncoder().encode(token);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

export function generateRandomToken(bytes = 32): string {
  const array = new Uint8Array(bytes);
  crypto.getRandomValues(array);
  return Array.from(array)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

export function generateSalt(bytes = 16): string {
  const array = new Uint8Array(bytes);
  crypto.getRandomValues(array);
  return Array.from(array)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

export class ClipSyncRoom extends DurableObject<Env> {
  private sql: SqlStorage;
  private syncSalt: string = "";

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.initDatabase();
    this.ctx.blockConcurrencyWhile(async () => {
      await this.sweepExpiredTransfers();
    });
  }

  private initDatabase(): void {
    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS metadata (
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS devices (
        device_id TEXT PRIMARY KEY,
        token_hash TEXT NOT NULL,
        label TEXT NOT NULL,
        platform TEXT NOT NULL,
        sync_salt TEXT NOT NULL,
        public_key TEXT,
        created_at TEXT NOT NULL,
        last_seen TEXT NOT NULL,
        is_revoked INTEGER NOT NULL DEFAULT 0
      );

      CREATE TABLE IF NOT EXISTS pairing_sessions (
        code TEXT PRIMARY KEY,
        session_id TEXT NOT NULL,
        sync_salt TEXT NOT NULL,
        expires_at TEXT NOT NULL,
        claimed INTEGER NOT NULL DEFAULT 0,
        created_at TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS clipboard_items (
        item_id TEXT PRIMARY KEY,
        source_device_id TEXT NOT NULL,
        type TEXT NOT NULL,
        version INTEGER NOT NULL,
        created_at TEXT NOT NULL,
        payload TEXT NOT NULL,
        metadata TEXT,
        storage_ref TEXT,
        sequence_number INTEGER NOT NULL
      );

      CREATE INDEX IF NOT EXISTS idx_items_seq ON clipboard_items(sequence_number);

      CREATE TABLE IF NOT EXISTS file_transfers (
        transfer_id TEXT PRIMARY KEY,
        sender_device_id TEXT NOT NULL,
        file_name TEXT NOT NULL,
        file_size_bytes INTEGER NOT NULL,
        mime_type TEXT NOT NULL,
        sha256_hash TEXT NOT NULL,
        encryption_iv TEXT NOT NULL,
        encryption_tag TEXT NOT NULL,
        medium TEXT NOT NULL,
        storage_reference TEXT,
        drive_file_id TEXT,
        created_at TEXT NOT NULL,
        expires_at TEXT NOT NULL,
        status TEXT NOT NULL,
        delivered_to TEXT NOT NULL DEFAULT '[]',
        updated_at TEXT NOT NULL
      );

      CREATE INDEX IF NOT EXISTS idx_transfers_expires ON file_transfers(expires_at, status);
      CREATE INDEX IF NOT EXISTS idx_transfers_hash ON file_transfers(sha256_hash, status);
    `);

    // Ensure group syncSalt exists
    const saltRow = this.sql.exec("SELECT value FROM metadata WHERE key = 'sync_salt'").toArray()[0];
    if (saltRow) {
      this.syncSalt = saltRow.value as string;
    } else {
      this.syncSalt = generateSalt(16);
      this.sql.exec("INSERT INTO metadata (key, value) VALUES ('sync_salt', ?)", this.syncSalt);
    }
  }

  async alarm(): Promise<void> {
    await this.sweepExpiredTransfers();
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    // WebSocket upgrade route: /v1/events?token=...
    if (url.pathname === "/v1/events") {
      return this.handleWebSocketUpgrade(request);
    }

    // Internal REST routes
    if (url.pathname === "/internal/device/register" && request.method === "POST") {
      const body = (await request.json()) as any;
      return this.registerDevice(body);
    }

    if (url.pathname === "/internal/auth/pairing/session" && request.method === "POST") {
      return this.createPairingSession(request);
    }

    if (url.pathname === "/internal/auth/pair" && request.method === "POST") {
      const body = (await request.json()) as any;
      return this.claimPairing(body);
    }

    if (url.pathname === "/internal/devices" && request.method === "GET") {
      return this.listDevices(request);
    }

    if (url.pathname.startsWith("/internal/devices/") && request.method === "DELETE") {
      const deviceId = url.pathname.replace("/internal/devices/", "");
      return this.revokeDevice(request, deviceId);
    }

    if (url.pathname === "/internal/history" && request.method === "GET") {
      return this.getHistory(request, url);
    }

    if (url.pathname.startsWith("/internal/history/") && request.method === "DELETE") {
      const itemId = url.pathname.replace("/internal/history/", "");
      return this.deleteHistoryItem(request, itemId);
    }

    if (url.pathname === "/internal/history/clear" && request.method === "POST") {
      return this.clearHistory(request);
    }

    if (url.pathname === "/internal/sync" && request.method === "POST") {
      return this.httpSync(request);
    }

    // Transfers routes
    if (url.pathname === "/internal/transfers/init" && request.method === "POST") {
      return this.initTransfer(request);
    }

    if (url.pathname === "/internal/transfers/pending" && request.method === "GET") {
      return this.getPendingTransfers(request);
    }

    if (url.pathname.startsWith("/internal/transfers/") && url.pathname.endsWith("/ack") && request.method === "POST") {
      const transferId = url.pathname.replace("/internal/transfers/", "").replace("/ack", "");
      return this.ackTransfer(request, transferId);
    }

    if (url.pathname.startsWith("/internal/transfers/") && request.method === "DELETE") {
      const transferId = url.pathname.replace("/internal/transfers/", "");
      return this.cleanupTransfer(request, transferId);
    }

    if (url.pathname === "/internal/transfers/sweep" && request.method === "POST") {
      await this.sweepExpiredTransfers();
      return Response.json({ ok: true, sweptAt: new Date().toISOString() });
    }

    return new Response("Not found in Durable Object", { status: 404 });
  }

  // ---------------------------------------------------------------------------
  // WebSocket Handling (Hibernation API)
  // ---------------------------------------------------------------------------

  private async handleWebSocketUpgrade(request: Request): Promise<Response> {
    const upgradeHeader = request.headers.get("Upgrade");
    if (!upgradeHeader || upgradeHeader.toLowerCase() !== "websocket") {
      return new Response("Expected Upgrade: websocket", { status: 426 });
    }

    const url = new URL(request.url);
    const token = url.searchParams.get("token");
    if (!token) {
      return new Response("Unauthorized: missing token", { status: 401 });
    }

    const tokenHash = await hashToken(token);
    const rows = this.sql
      .exec("SELECT * FROM devices WHERE token_hash = ? AND is_revoked = 0", tokenHash)
      .toArray();

    if (rows.length === 0) {
      return new Response("Unauthorized: invalid or revoked token", { status: 401 });
    }

    const device = rows[0] as any;

    // Update last_seen
    this.sql.exec("UPDATE devices SET last_seen = ? WHERE device_id = ?", new Date().toISOString(), device.device_id);

    // Create WebSocket pair
    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];

    // Accept WebSocket with Hibernation API
    this.ctx.acceptWebSocket(server, [device.device_id]);

    const attachment: SocketAttachment = {
      deviceId: device.device_id as string,
      label: device.label as string,
      platform: device.platform as string,
    };
    server.serializeAttachment(attachment);

    // Query last sequence number
    const maxSeqRow = this.sql
      .exec("SELECT COALESCE(MAX(sequence_number), 0) as maxSeq FROM clipboard_items")
      .toArray()[0];
    const lastSequence = Number(maxSeqRow?.maxSeq ?? 0);

    // Send connected greeting
    const connectedMsg: WsServerMessage = {
      type: "connected",
      deviceId: device.device_id,
      groupId: "clipsync-default-group",
      serverTime: new Date().toISOString(),
      lastSequence,
    };
    server.send(JSON.stringify(connectedMsg));

    // Broadcast device:joined to other devices
    const joinMsg: WsServerMessage = {
      type: "device:joined",
      device: {
        deviceId: device.device_id,
        label: device.label,
        platform: device.platform,
        lastSeen: new Date().toISOString(),
      },
    };
    this.broadcast(JSON.stringify(joinMsg), server);

    return new Response(null, {
      status: 101,
      webSocket: client,
    });
  }

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const raw = typeof message === "string" ? message : new TextDecoder().decode(message);
    let msg: WsClientMessage;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }

    const attachment = ws.deserializeAttachment() as SocketAttachment | null;
    if (!attachment) return;

    switch (msg.type) {
      case "ping": {
        const pong: WsServerMessage = { type: "pong", serverTime: new Date().toISOString() };
        ws.send(JSON.stringify(pong));
        break;
      }

      case "sync:item": {
        const validation = validateSyncItem(msg.item);
        if (!validation.valid) {
          const errMsg: WsServerMessage = { type: "error", code: "INVALID_ITEM", message: validation.error };
          ws.send(JSON.stringify(errMsg));
          return;
        }

        const item = validation.item;
        item.sourceDeviceId = attachment.deviceId;

        // Atomic sequence increment
        const seqRow = this.sql
          .exec("SELECT COALESCE(MAX(sequence_number), 0) + 1 AS nextSeq FROM clipboard_items")
          .toArray()[0];
        const nextSeq = Number(seqRow.nextSeq);

        const metadataStr = item.metadata ? JSON.stringify(item.metadata) : null;

        this.sql.exec(
          `INSERT INTO clipboard_items (item_id, source_device_id, type, version, created_at, payload, metadata, storage_ref, sequence_number)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
           ON CONFLICT(item_id) DO UPDATE SET payload = excluded.payload`,
          item.itemId,
          item.sourceDeviceId,
          item.type,
          item.version,
          item.createdAt,
          item.payload,
          metadataStr,
          item.storageRef ?? null,
          nextSeq
        );

        // Retain latest 500 items in history
        this.sql.exec(`
          DELETE FROM clipboard_items 
          WHERE item_id NOT IN (
            SELECT item_id FROM clipboard_items ORDER BY sequence_number DESC LIMIT 500
          )
        `);

        // Send ACK back to sender
        const ackMsg: WsServerMessage = {
          type: "sync:ack",
          itemId: item.itemId,
          sequenceNumber: nextSeq,
        };
        ws.send(JSON.stringify(ackMsg));

        // Broadcast to all OTHER connected sockets in the room
        const storedItem: StoredSyncItem = {
          ...item,
          sequenceNumber: nextSeq,
          groupId: "clipsync-default-group",
        };
        const broadcastMsg: WsServerMessage = {
          type: "sync:item",
          item: storedItem,
          sequenceNumber: nextSeq,
        };
        this.broadcast(JSON.stringify(broadcastMsg), ws);
        break;
      }

      case "history:request": {
        const afterSeq = msg.afterSequence ?? 0;
        const limit = Math.min(msg.limit ?? 50, 100);

        const rows = this.sql
          .exec(
            `SELECT * FROM clipboard_items WHERE sequence_number > ? ORDER BY sequence_number ASC LIMIT ?`,
            afterSeq,
            limit
          )
          .toArray();

        for (const row of rows) {
          const item: StoredSyncItem = {
            version: Number(row.version),
            itemId: row.item_id as string,
            sourceDeviceId: row.source_device_id as string,
            type: row.type as any,
            createdAt: row.created_at as string,
            payload: row.payload as string,
            metadata: row.metadata ? JSON.parse(row.metadata as string) : undefined,
            storageRef: (row.storage_ref as string) || undefined,
            sequenceNumber: Number(row.sequence_number),
            groupId: "clipsync-default-group",
          };
          const histMsg: WsServerMessage = {
            type: "sync:item",
            item,
            sequenceNumber: item.sequenceNumber,
          };
          ws.send(JSON.stringify(histMsg));
        }
        break;
      }

      case "ack":
        break;

      case "presence:status":
        break;

      case "transfer:init": {
        const validation = validateTransferManifest(msg.manifest);
        if (!validation.valid) {
          const errMsg: WsServerMessage = { type: "error", code: "INVALID_TRANSFER", message: validation.error };
          ws.send(JSON.stringify(errMsg));
          return;
        }

        const manifest = validation.manifest;
        manifest.senderDeviceId = attachment.deviceId;
        if (!manifest.driveFileId && manifest.medium === "google_drive" && manifest.storageReference) {
          manifest.driveFileId = manifest.storageReference;
        }

        const now = new Date().toISOString();

        // Check duplicate
        const existingRow = this.sql.exec(
          `SELECT * FROM file_transfers WHERE (sha256_hash = ? OR transfer_id = ?) AND expires_at > ? AND status NOT IN ('expired', 'cancelled', 'failed') LIMIT 1`,
          manifest.sha256Hash,
          manifest.transferId,
          now
        ).toArray()[0];

        if (existingRow) {
          const existing = this.rowToManifest(existingRow);
          ws.send(JSON.stringify({ type: "transfer:announced", manifest: existing }));
          return;
        }

        this.sql.exec(
          `INSERT INTO file_transfers (
            transfer_id, sender_device_id, file_name, file_size_bytes, mime_type,
            sha256_hash, encryption_iv, encryption_tag, medium, storage_reference,
            drive_file_id, created_at, expires_at, status, delivered_to, updated_at
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          manifest.transferId,
          manifest.senderDeviceId,
          manifest.fileName,
          manifest.fileSizeBytes,
          manifest.mimeType,
          manifest.sha256Hash,
          manifest.encryptionIv,
          manifest.encryptionTag,
          manifest.medium,
          manifest.storageReference ?? null,
          manifest.driveFileId ?? null,
          manifest.createdAt,
          manifest.expiresAt,
          manifest.status,
          JSON.stringify(manifest.deliveredTo ?? []),
          now
        );

        await this.scheduleNextExpiryAlarm();

        // Broadcast to all other sockets in room
        const announceMsg: WsServerMessage = {
          type: "transfer:announced",
          manifest,
        };
        this.broadcast(JSON.stringify(announceMsg), ws);
        break;
      }

      case "transfer:progress": {
        const relayMsg: WsServerMessage = {
          type: "transfer:progress_relay",
          transferId: msg.transferId,
          deviceId: attachment.deviceId,
          bytesTransferred: msg.bytesTransferred,
          totalBytes: msg.totalBytes,
          phase: msg.phase,
        };
        this.broadcast(JSON.stringify(relayMsg), ws);
        break;
      }

      case "transfer:ack": {
        const row = this.sql.exec("SELECT * FROM file_transfers WHERE transfer_id = ?", msg.transferId).toArray()[0];
        if (!row) break;

        const deliveredTo: string[] = row.delivered_to ? JSON.parse(row.delivered_to as string) : [];
        if (!deliveredTo.includes(attachment.deviceId)) {
          deliveredTo.push(attachment.deviceId);
        }

        const peerRows = this.sql.exec(
          "SELECT device_id FROM devices WHERE is_revoked = 0 AND device_id != ?",
          row.sender_device_id
        ).toArray();

        const allDelivered = peerRows.length === 0 || peerRows.every((p: any) => deliveredTo.includes(p.device_id));
        const newStatus = allDelivered ? "delivered" : row.status;
        const now = new Date().toISOString();

        this.sql.exec(
          "UPDATE file_transfers SET delivered_to = ?, status = ?, updated_at = ? WHERE transfer_id = ?",
          JSON.stringify(deliveredTo),
          newStatus,
          now,
          msg.transferId
        );

        const driveFileId = (row.drive_file_id as string) || (row.storage_reference as string) || undefined;
        const deliveredMsg: WsServerMessage = {
          type: "transfer:delivered",
          transferId: msg.transferId,
          recipientDeviceId: attachment.deviceId,
          allDelivered,
          driveFileId,
          shouldDelete: allDelivered,
        };
        this.broadcast(JSON.stringify(deliveredMsg));
        break;
      }

      case "transfer:cleanup":
      case "transfer:cancel": {
        const row = this.sql.exec("SELECT * FROM file_transfers WHERE transfer_id = ?", msg.transferId).toArray()[0];
        if (!row) break;

        const now = new Date().toISOString();
        this.sql.exec(
          "UPDATE file_transfers SET status = 'cancelled', updated_at = ? WHERE transfer_id = ?",
          now,
          msg.transferId
        );

        const driveFileId = (row.drive_file_id as string) || (row.storage_reference as string) || undefined;
        const deletedMsg: WsServerMessage = {
          type: "transfer:deleted",
          transferId: msg.transferId,
          driveFileId,
          reason: msg.type === "transfer:cancel" ? (msg.reason || "cancelled") : (msg.reason || "cleanup_requested"),
        };
        this.broadcast(JSON.stringify(deletedMsg));
        break;
      }
    }
  }

  async webSocketClose(ws: WebSocket, _code: number, _reason: string, _wasClean: boolean): Promise<void> {
    const attachment = ws.deserializeAttachment() as SocketAttachment | null;
    if (attachment) {
      const leftMsg: WsServerMessage = {
        type: "device:left",
        deviceId: attachment.deviceId,
      };
      this.broadcast(JSON.stringify(leftMsg), ws);
    }
  }

  async webSocketError(_ws: WebSocket, error: unknown): Promise<void> {
    console.error("Durable Object WebSocket error:", error);
  }

  private broadcast(payload: string, excludeSocket?: WebSocket): void {
    for (const ws of this.ctx.getWebSockets()) {
      if (excludeSocket && ws === excludeSocket) continue;
      try {
        ws.send(payload);
      } catch {
        // Ignored, socket might be closing
      }
    }
  }

  // ---------------------------------------------------------------------------
  // REST Helpers
  // ---------------------------------------------------------------------------

  private async authenticateRequest(request: Request): Promise<any | null> {
    const authHeader = request.headers.get("authorization") ?? "";
    const [scheme, token] = authHeader.split(" ");
    if (scheme !== "Bearer" || !token) return null;

    const tokenHash = await hashToken(token);
    const rows = this.sql
      .exec("SELECT * FROM devices WHERE token_hash = ? AND is_revoked = 0", tokenHash)
      .toArray();

    return rows[0] ?? null;
  }

  private async registerDevice(body: any): Promise<Response> {
    const label = typeof body?.label === "string" ? body.label.slice(0, 80) : "Unnamed Device";
    const platform = body?.platform ?? "macos";
    const deviceId = crypto.randomUUID();
    const token = generateRandomToken(32);
    const tokenHash = await hashToken(token);
    const now = new Date().toISOString();

    this.sql.exec(
      `INSERT INTO devices (device_id, token_hash, label, platform, sync_salt, public_key, created_at, last_seen, is_revoked)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0)`,
      deviceId,
      tokenHash,
      label,
      platform,
      this.syncSalt,
      body?.publicKey ?? null,
      now,
      now
    );

    const resp: RegisterDeviceResponse = {
      deviceId,
      token,
      label,
      platform,
      groupId: "clipsync-default-group",
      syncSalt: this.syncSalt,
    };
    return Response.json(resp, { status: 201 });
  }

  private async createPairingSession(request: Request): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const sessionId = crypto.randomUUID();
    // 6-digit numeric pairing code (100000 - 999999)
    const array = new Uint32Array(1);
    crypto.getRandomValues(array);
    const code = (100000 + (array[0] % 900000)).toString();
    const expiresAt = new Date(Date.now() + 10 * 60 * 1000).toISOString();
    const now = new Date().toISOString();

    this.sql.exec(
      `INSERT INTO pairing_sessions (code, session_id, sync_salt, expires_at, claimed, created_at)
       VALUES (?, ?, ?, ?, 0, ?)`,
      code,
      sessionId,
      this.syncSalt,
      expiresAt,
      now
    );

    const origin = this.env.SERVER_ORIGIN ?? request.headers.get("x-public-origin") ?? new URL(request.url).origin;
    const pairingUrl = `clipsync://pair?code=${code}&server=${encodeURIComponent(origin)}&salt=${encodeURIComponent(this.syncSalt)}`;

    const resp: PairingSessionResponse = {
      sessionId,
      code,
      qrData: pairingUrl,
      expiresAt,
      syncSalt: this.syncSalt,
    };
    return Response.json(resp, { status: 201 });
  }

  private async claimPairing(body: any): Promise<Response> {
    const code = body?.code?.trim();
    if (!code) return Response.json({ error: "Missing pairing code" }, { status: 400 });

    const now = new Date().toISOString();
    const rows = this.sql
      .exec("SELECT * FROM pairing_sessions WHERE code = ? AND claimed = 0 AND expires_at > ?", code, now)
      .toArray();

    if (rows.length === 0) {
      return Response.json({ error: "Invalid or expired pairing code" }, { status: 400 });
    }

    // Mark claimed
    this.sql.exec("UPDATE pairing_sessions SET claimed = 1 WHERE code = ?", code);

    const label = body?.label?.slice(0, 80) ?? "New Paired Device";
    const platform = body?.platform ?? "android";
    const deviceId = crypto.randomUUID();
    const token = generateRandomToken(32);
    const tokenHash = await hashToken(token);

    this.sql.exec(
      `INSERT INTO devices (device_id, token_hash, label, platform, sync_salt, public_key, created_at, last_seen, is_revoked)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0)`,
      deviceId,
      tokenHash,
      label,
      platform,
      this.syncSalt,
      body?.publicKey ?? null,
      now,
      now
    );

    const resp: ClaimPairingResponse = {
      deviceId,
      token,
      groupId: "clipsync-default-group",
      label,
      platform,
      syncSalt: this.syncSalt,
    };
    return Response.json(resp, { status: 201 });
  }

  private async listDevices(request: Request): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const rows = this.sql.exec("SELECT * FROM devices WHERE is_revoked = 0 ORDER BY last_seen DESC").toArray();

    const devices: DeviceInfo[] = rows.map((r: any) => ({
      deviceId: r.device_id,
      label: r.label,
      platform: r.platform,
      lastSeen: r.last_seen,
      isCurrent: r.device_id === device.device_id,
    }));

    return Response.json({ devices });
  }

  private async revokeDevice(request: Request, deviceId: string): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    this.sql.exec("UPDATE devices SET is_revoked = 1 WHERE device_id = ?", deviceId);

    // Sever any active sockets for that device
    for (const ws of this.ctx.getWebSockets(deviceId)) {
      try {
        ws.send(JSON.stringify({ type: "device:revoked", deviceId }));
        ws.close(1008, "Device revoked");
      } catch {}
    }

    return Response.json({ ok: true, revokedDeviceId: deviceId });
  }

  private async getHistory(request: Request, url: URL): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const afterSeq = Number(url.searchParams.get("afterSequence") ?? 0);
    const limit = Math.min(Number(url.searchParams.get("limit") ?? 50), 100);

    const rows = this.sql
      .exec(
        `SELECT * FROM clipboard_items WHERE sequence_number > ? ORDER BY sequence_number ASC LIMIT ?`,
        afterSeq,
        limit + 1
      )
      .toArray();

    const hasMore = rows.length > limit;
    const itemsRows = hasMore ? rows.slice(0, limit) : rows;

    const items: StoredSyncItem[] = itemsRows.map((r: any) => ({
      version: Number(r.version),
      itemId: r.item_id,
      sourceDeviceId: r.source_device_id,
      type: r.type,
      createdAt: r.created_at,
      payload: r.payload,
      metadata: r.metadata ? JSON.parse(r.metadata) : undefined,
      storageRef: r.storage_ref || undefined,
      sequenceNumber: Number(r.sequence_number),
      groupId: "clipsync-default-group",
    }));

    const maxRow = this.sql.exec("SELECT COALESCE(MAX(sequence_number), 0) as maxSeq FROM clipboard_items").toArray()[0];
    const lastSequence = Number(maxRow?.maxSeq ?? 0);

    return Response.json({ items, lastSequence, hasMore });
  }

  private async deleteHistoryItem(request: Request, itemId: string): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    this.sql.exec("DELETE FROM clipboard_items WHERE item_id = ?", itemId);
    return Response.json({ ok: true });
  }

  private async clearHistory(request: Request): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    this.sql.exec("DELETE FROM clipboard_items");
    return Response.json({ ok: true });
  }

  private async httpSync(request: Request): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const body = (await request.json()) as any;
    const validation = validateSyncItem(body);
    if (!validation.valid) {
      return Response.json({ error: validation.error }, { status: 400 });
    }

    const item = validation.item;
    item.sourceDeviceId = device.device_id;

    const seqRow = this.sql
      .exec("SELECT COALESCE(MAX(sequence_number), 0) + 1 AS nextSeq FROM clipboard_items")
      .toArray()[0];
    const nextSeq = Number(seqRow.nextSeq);

    const metadataStr = item.metadata ? JSON.stringify(item.metadata) : null;

    this.sql.exec(
      `INSERT INTO clipboard_items (item_id, source_device_id, type, version, created_at, payload, metadata, storage_ref, sequence_number)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(item_id) DO UPDATE SET payload = excluded.payload`,
      item.itemId,
      item.sourceDeviceId,
      item.type,
      item.version,
      item.createdAt,
      item.payload,
      metadataStr,
      item.storageRef ?? null,
      nextSeq
    );

    const storedItem: StoredSyncItem = {
      ...item,
      sequenceNumber: nextSeq,
      groupId: "clipsync-default-group",
    };

    // Broadcast to connected WebSockets
    const broadcastMsg: WsServerMessage = {
      type: "sync:item",
      item: storedItem,
      sequenceNumber: nextSeq,
    };
    this.broadcast(JSON.stringify(broadcastMsg));

    return Response.json({ accepted: true, itemId: item.itemId, sequenceNumber: nextSeq }, { status: 202 });
  }

  // ---------------------------------------------------------------------------
  // Transfer Engine Helpers
  // ---------------------------------------------------------------------------

  private rowToManifest(row: any): TransferManifest {
    return {
      version: PROTOCOL_VERSION,
      transferId: row.transfer_id as string,
      senderDeviceId: row.sender_device_id as string,
      fileName: row.file_name as string,
      fileSizeBytes: Number(row.file_size_bytes),
      mimeType: row.mime_type as string,
      sha256Hash: row.sha256_hash as string,
      encryptionIv: row.encryption_iv as string,
      encryptionTag: row.encryption_tag as string,
      medium: row.medium as any,
      storageReference: (row.storage_reference as string) || undefined,
      driveFileId: (row.drive_file_id as string) || undefined,
      createdAt: row.created_at as string,
      expiresAt: row.expires_at as string,
      status: row.status as any,
      deliveredTo: row.delivered_to ? JSON.parse(row.delivered_to as string) : [],
    };
  }

  private async sweepExpiredTransfers(): Promise<void> {
    const now = new Date().toISOString();
    const expiredRows = this.sql
      .exec(
        `SELECT * FROM file_transfers WHERE expires_at <= ? AND status NOT IN ('delivered', 'expired', 'cancelled')`,
        now
      )
      .toArray();

    for (const row of expiredRows) {
      this.sql.exec(
        `UPDATE file_transfers SET status = 'expired', updated_at = ? WHERE transfer_id = ?`,
        now,
        row.transfer_id
      );

      const expiredMsg: WsServerMessage = {
        type: "transfer:expired",
        transferId: row.transfer_id as string,
        driveFileId: (row.drive_file_id as string) || (row.storage_reference as string) || undefined,
        reason: "retention_exceeded",
      };
      this.broadcast(JSON.stringify(expiredMsg));
    }

    await this.scheduleNextExpiryAlarm();
  }

  private async scheduleNextExpiryAlarm(): Promise<void> {
    const now = new Date().toISOString();
    const nextRow = this.sql
      .exec(
        `SELECT expires_at FROM file_transfers WHERE expires_at > ? AND status NOT IN ('delivered', 'expired', 'cancelled') ORDER BY expires_at ASC LIMIT 1`,
        now
      )
      .toArray()[0];

    if (nextRow && nextRow.expires_at) {
      const nextTime = new Date(nextRow.expires_at as string).getTime();
      const currentAlarm = await this.ctx.storage.getAlarm();
      if (currentAlarm === null || nextTime < currentAlarm) {
        await this.ctx.storage.setAlarm(nextTime);
      }
    }
  }

  private async initTransfer(request: Request): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const body = (await request.json()) as any;
    const manifestCandidate = body?.manifest || body;
    const validation = validateTransferManifest(manifestCandidate);
    if (!validation.valid) {
      return Response.json({ error: validation.error }, { status: 400 });
    }

    const manifest = validation.manifest;
    manifest.senderDeviceId = device.device_id;
    if (!manifest.driveFileId && manifest.medium === "google_drive" && manifest.storageReference) {
      manifest.driveFileId = manifest.storageReference;
    }

    const now = new Date().toISOString();

    const existingRow = this.sql
      .exec(
        `SELECT * FROM file_transfers WHERE (sha256_hash = ? OR transfer_id = ?) AND expires_at > ? AND status NOT IN ('expired', 'cancelled', 'failed') LIMIT 1`,
        manifest.sha256Hash,
        manifest.transferId,
        now
      )
      .toArray()[0];

    if (existingRow) {
      const existing = this.rowToManifest(existingRow);
      const resp: InitTransferResponse = {
        ok: true,
        manifest: existing,
        duplicate: true,
      };
      return Response.json(resp, { status: 200 });
    }

    this.sql.exec(
      `INSERT INTO file_transfers (
        transfer_id, sender_device_id, file_name, file_size_bytes, mime_type,
        sha256_hash, encryption_iv, encryption_tag, medium, storage_reference,
        drive_file_id, created_at, expires_at, status, delivered_to, updated_at
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      manifest.transferId,
      manifest.senderDeviceId,
      manifest.fileName,
      manifest.fileSizeBytes,
      manifest.mimeType,
      manifest.sha256Hash,
      manifest.encryptionIv,
      manifest.encryptionTag,
      manifest.medium,
      manifest.storageReference ?? null,
      manifest.driveFileId ?? null,
      manifest.createdAt,
      manifest.expiresAt,
      manifest.status,
      JSON.stringify(manifest.deliveredTo ?? []),
      now
    );

    await this.scheduleNextExpiryAlarm();

    const announcedMsg: WsServerMessage = {
      type: "transfer:announced",
      manifest,
    };
    this.broadcast(JSON.stringify(announcedMsg));

    const resp: InitTransferResponse = {
      ok: true,
      manifest,
      duplicate: false,
    };
    return Response.json(resp, { status: 201 });
  }

  private async getPendingTransfers(request: Request): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const now = new Date().toISOString();
    const rows = this.sql
      .exec(
        `SELECT * FROM file_transfers WHERE status IN ('ready', 'uploading', 'pending') AND expires_at > ? ORDER BY created_at DESC`,
        now
      )
      .toArray();

    const transfers = rows.map((r: any) => this.rowToManifest(r));
    const resp: PendingTransfersResponse = { transfers };
    return Response.json(resp, { status: 200 });
  }

  private async ackTransfer(request: Request, transferId: string): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    let recipientDeviceId = device.device_id as string;
    try {
      const body = (await request.json()) as any;
      if (body?.recipientDeviceId) recipientDeviceId = body.recipientDeviceId;
    } catch {}

    const row = this.sql.exec("SELECT * FROM file_transfers WHERE transfer_id = ?", transferId).toArray()[0];
    if (!row) {
      return Response.json({ error: "Transfer not found" }, { status: 404 });
    }

    const deliveredTo: string[] = row.delivered_to ? JSON.parse(row.delivered_to as string) : [];
    if (!deliveredTo.includes(recipientDeviceId)) {
      deliveredTo.push(recipientDeviceId);
    }

    const peerRows = this.sql
      .exec("SELECT device_id FROM devices WHERE is_revoked = 0 AND device_id != ?", row.sender_device_id)
      .toArray();

    const allDelivered = peerRows.length === 0 || peerRows.every((p: any) => deliveredTo.includes(p.device_id));
    const newStatus = allDelivered ? "delivered" : row.status;
    const now = new Date().toISOString();

    this.sql.exec(
      "UPDATE file_transfers SET delivered_to = ?, status = ?, updated_at = ? WHERE transfer_id = ?",
      JSON.stringify(deliveredTo),
      newStatus,
      now,
      transferId
    );

    const driveFileId = (row.drive_file_id as string) || (row.storage_reference as string) || undefined;
    const deliveredMsg: WsServerMessage = {
      type: "transfer:delivered",
      transferId,
      recipientDeviceId,
      allDelivered,
      driveFileId,
      shouldDelete: allDelivered,
    };
    this.broadcast(JSON.stringify(deliveredMsg));

    const resp: AckTransferResponse = {
      ok: true,
      transferId,
      allDelivered,
      shouldDelete: allDelivered,
      driveFileId,
    };
    return Response.json(resp, { status: 200 });
  }

  private async cleanupTransfer(request: Request, transferId: string): Promise<Response> {
    const device = await this.authenticateRequest(request);
    if (!device) return Response.json({ error: "Unauthorized" }, { status: 401 });

    const row = this.sql.exec("SELECT * FROM file_transfers WHERE transfer_id = ?", transferId).toArray()[0];
    if (!row) {
      return Response.json({ error: "Transfer not found" }, { status: 404 });
    }

    const now = new Date().toISOString();
    this.sql.exec(
      "UPDATE file_transfers SET status = 'cancelled', updated_at = ? WHERE transfer_id = ?",
      now,
      transferId
    );

    const driveFileId = (row.drive_file_id as string) || (row.storage_reference as string) || undefined;
    const deletedMsg: WsServerMessage = {
      type: "transfer:deleted",
      transferId,
      driveFileId,
      reason: "cleanup_requested",
    };
    this.broadcast(JSON.stringify(deletedMsg));

    const resp: CleanupTransferResponse = {
      ok: true,
      transferId,
      driveFileId,
    };
    return Response.json(resp, { status: 200 });
  }
}
