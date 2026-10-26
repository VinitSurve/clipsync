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
  validateSyncItem,
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
}
