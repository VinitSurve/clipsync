import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import pg from "pg";
import type {
  DevicePlatform,
  DeviceInfo,
  StoredSyncItem,
  SyncItem,
  HistoryQueryOptions,
} from "@clipsync/protocol";

export interface DeviceRecord {
  deviceId: string;
  tokenHash: string;
  label: string;
  platform: DevicePlatform;
  groupId: string;
  syncSalt: string;
  publicKey?: string;
  createdAt: string;
  lastSeen: string;
  isRevoked: boolean;
}

export interface PairingSessionRecord {
  sessionId: string;
  code: string;
  groupId: string;
  syncSalt: string;
  expiresAt: string;
  claimed: boolean;
  createdAt: string;
}

export interface StorageAdapter {
  init(): Promise<void>;
  close(): Promise<void>;
  createDevice(device: DeviceRecord): Promise<void>;
  getDeviceById(deviceId: string): Promise<DeviceRecord | null>;
  getDeviceByToken(plainToken: string): Promise<DeviceRecord | null>;
  listDevices(groupId: string): Promise<DeviceInfo[]>;
  updateDeviceLastSeen(deviceId: string): Promise<void>;
  revokeDevice(deviceId: string): Promise<boolean>;

  createPairingSession(session: PairingSessionRecord): Promise<void>;
  getPairingSession(code: string): Promise<PairingSessionRecord | null>;
  claimPairingSession(code: string): Promise<PairingSessionRecord | null>;

  saveItem(groupId: string, item: SyncItem): Promise<StoredSyncItem>;
  getHistory(groupId: string, options?: HistoryQueryOptions): Promise<{ items: StoredSyncItem[]; lastSequence: number; hasMore: boolean }>;
  deleteItem(groupId: string, itemId: string): Promise<boolean>;
  clearHistory(groupId: string): Promise<void>;
}

export function hashToken(plainToken: string): string {
  return crypto.createHash("sha256").update(plainToken, "utf8").digest("hex");
}

/**
 * File-backed / In-Memory persistence adapter (default for local dev)
 */
class FileStorageAdapter implements StorageAdapter {
  private filePath: string;
  private devices = new Map<string, DeviceRecord>();
  private pairingSessions = new Map<string, PairingSessionRecord>();
  private itemsByGroup = new Map<string, StoredSyncItem[]>();
  private sequencesByGroup = new Map<string, number>();

  constructor(filePath?: string) {
    this.filePath = filePath ?? path.resolve(process.cwd(), ".clipsync-store.json");
  }

  async init(): Promise<void> {
    try {
      if (fs.existsSync(this.filePath)) {
        const raw = fs.readFileSync(this.filePath, "utf8");
        const data = JSON.parse(raw);
        if (Array.isArray(data.devices)) {
          for (const d of data.devices) this.devices.set(d.deviceId, d);
        }
        if (data.itemsByGroup && typeof data.itemsByGroup === "object") {
          for (const [gid, items] of Object.entries(data.itemsByGroup)) {
            this.itemsByGroup.set(gid, items as StoredSyncItem[]);
            const maxSeq = (items as StoredSyncItem[]).reduce((max, it) => Math.max(max, it.sequenceNumber ?? 0), 0);
            this.sequencesByGroup.set(gid, maxSeq);
          }
        }
      }
    } catch {
      // Clean fallback if file is corrupted
    }
  }

  private persist(): void {
    try {
      const data = {
        devices: Array.from(this.devices.values()),
        itemsByGroup: Object.fromEntries(this.itemsByGroup.entries()),
      };
      fs.writeFileSync(this.filePath, JSON.stringify(data, null, 2), "utf8");
    } catch {
      // Continue even if disk write fails
    }
  }

  async close(): Promise<void> {
    this.persist();
  }

  async createDevice(device: DeviceRecord): Promise<void> {
    this.devices.set(device.deviceId, device);
    this.persist();
  }

  async getDeviceById(deviceId: string): Promise<DeviceRecord | null> {
    const dev = this.devices.get(deviceId);
    if (!dev || dev.isRevoked) return null;
    return dev;
  }

  async getDeviceByToken(plainToken: string): Promise<DeviceRecord | null> {
    const targetHash = hashToken(plainToken);
    for (const dev of this.devices.values()) {
      if (dev.isRevoked) continue;
      if (crypto.timingSafeEqual(Buffer.from(dev.tokenHash), Buffer.from(targetHash))) {
        return dev;
      }
    }
    return null;
  }

  async listDevices(groupId: string): Promise<DeviceInfo[]> {
    const result: DeviceInfo[] = [];
    for (const dev of this.devices.values()) {
      if (dev.groupId === groupId && !dev.isRevoked) {
        result.push({
          deviceId: dev.deviceId,
          label: dev.label,
          platform: dev.platform,
          lastSeen: dev.lastSeen,
        });
      }
    }
    return result;
  }

  async updateDeviceLastSeen(deviceId: string): Promise<void> {
    const dev = this.devices.get(deviceId);
    if (dev) {
      dev.lastSeen = new Date().toISOString();
    }
  }

  async revokeDevice(deviceId: string): Promise<boolean> {
    const dev = this.devices.get(deviceId);
    if (!dev) return false;
    dev.isRevoked = true;
    this.persist();
    return true;
  }

  async createPairingSession(session: PairingSessionRecord): Promise<void> {
    this.pairingSessions.set(session.code, session);
  }

  async getPairingSession(code: string): Promise<PairingSessionRecord | null> {
    const session = this.pairingSessions.get(code);
    if (!session || session.claimed) return null;
    if (Date.parse(session.expiresAt) < Date.now()) {
      this.pairingSessions.delete(code);
      return null;
    }
    return session;
  }

  async claimPairingSession(code: string): Promise<PairingSessionRecord | null> {
    const session = await this.getPairingSession(code);
    if (!session) return null;
    session.claimed = true;
    return session;
  }

  async saveItem(groupId: string, item: SyncItem): Promise<StoredSyncItem> {
    const currentSeq = (this.sequencesByGroup.get(groupId) ?? 0) + 1;
    this.sequencesByGroup.set(groupId, currentSeq);

    const stored: StoredSyncItem = {
      ...item,
      sequenceNumber: currentSeq,
      groupId,
    };

    const list = this.itemsByGroup.get(groupId) ?? [];
    // Deduplicate by itemId
    const existingIndex = list.findIndex((i) => i.itemId === item.itemId);
    if (existingIndex >= 0) {
      list[existingIndex] = stored;
    } else {
      list.push(stored);
      // Retain max 500 items in history per group
      if (list.length > 500) list.shift();
    }
    this.itemsByGroup.set(groupId, list);
    this.persist();
    return stored;
  }

  async getHistory(
    groupId: string,
    options?: HistoryQueryOptions
  ): Promise<{ items: StoredSyncItem[]; lastSequence: number; hasMore: boolean }> {
    const list = this.itemsByGroup.get(groupId) ?? [];
    const afterSeq = options?.afterSequence ?? 0;
    const limit = Math.min(options?.limit ?? 50, 100);

    let filtered = list.filter((i) => i.sequenceNumber > afterSeq);
    if (options?.type) {
      filtered = filtered.filter((i) => i.type === options.type);
    }
    if (options?.pinnedOnly) {
      filtered = filtered.filter((i) => Boolean(i.metadata?.isPinned));
    }

    const items = filtered.slice(0, limit);
    const lastSeq = this.sequencesByGroup.get(groupId) ?? 0;
    const hasMore = filtered.length > limit;

    return { items, lastSequence: lastSeq, hasMore };
  }

  async deleteItem(groupId: string, itemId: string): Promise<boolean> {
    const list = this.itemsByGroup.get(groupId);
    if (!list) return false;
    const initialLen = list.length;
    const filtered = list.filter((i) => i.itemId !== itemId);
    this.itemsByGroup.set(groupId, filtered);
    this.persist();
    return filtered.length < initialLen;
  }

  async clearHistory(groupId: string): Promise<void> {
    this.itemsByGroup.set(groupId, []);
    this.persist();
  }
}

/**
 * PostgreSQL Storage Adapter
 */
class PostgresStorageAdapter implements StorageAdapter {
  private pool: pg.Pool;

  constructor(connectionString: string) {
    this.pool = new pg.Pool({ connectionString, max: 10 });
  }

  async init(): Promise<void> {
    const client = await this.pool.connect();
    try {
      await client.query(`
        CREATE TABLE IF NOT EXISTS devices (
          device_id VARCHAR(64) PRIMARY KEY,
          token_hash VARCHAR(64) NOT NULL,
          label VARCHAR(128) NOT NULL,
          platform VARCHAR(32) NOT NULL,
          group_id VARCHAR(128) NOT NULL,
          sync_salt VARCHAR(64) NOT NULL,
          public_key TEXT,
          created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
          last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
          is_revoked BOOLEAN NOT NULL DEFAULT FALSE
        );

        CREATE TABLE IF NOT EXISTS pairing_sessions (
          code VARCHAR(32) PRIMARY KEY,
          session_id VARCHAR(64) NOT NULL,
          group_id VARCHAR(128) NOT NULL,
          sync_salt VARCHAR(64) NOT NULL,
          expires_at TIMESTAMPTZ NOT NULL,
          claimed BOOLEAN NOT NULL DEFAULT FALSE,
          created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS clipboard_items (
          item_id VARCHAR(64) PRIMARY KEY,
          group_id VARCHAR(128) NOT NULL,
          source_device_id VARCHAR(64) NOT NULL,
          type VARCHAR(32) NOT NULL,
          version INT NOT NULL,
          created_at TIMESTAMPTZ NOT NULL,
          payload TEXT NOT NULL,
          metadata JSONB,
          storage_ref TEXT,
          sequence_number BIGSERIAL NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_clipboard_items_group_seq 
          ON clipboard_items (group_id, sequence_number);
      `);
    } finally {
      client.release();
    }
  }

  async close(): Promise<void> {
    await this.pool.end();
  }

  async createDevice(device: DeviceRecord): Promise<void> {
    await this.pool.query(
      `INSERT INTO devices (device_id, token_hash, label, platform, group_id, sync_salt, public_key, created_at, last_seen, is_revoked)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
       ON CONFLICT (device_id) DO UPDATE SET last_seen = EXCLUDED.last_seen`,
      [
        device.deviceId,
        device.tokenHash,
        device.label,
        device.platform,
        device.groupId,
        device.syncSalt,
        device.publicKey ?? null,
        device.createdAt,
        device.lastSeen,
        device.isRevoked,
      ]
    );
  }

  async getDeviceById(deviceId: string): Promise<DeviceRecord | null> {
    const res = await this.pool.query(
      `SELECT device_id as "deviceId", token_hash as "tokenHash", label, platform, group_id as "groupId",
              sync_salt as "syncSalt", public_key as "publicKey", created_at as "createdAt",
              last_seen as "lastSeen", is_revoked as "isRevoked"
       FROM devices WHERE device_id = $1 AND is_revoked = FALSE`,
      [deviceId]
    );
    return res.rows[0] ?? null;
  }

  async getDeviceByToken(plainToken: string): Promise<DeviceRecord | null> {
    const hash = hashToken(plainToken);
    const res = await this.pool.query(
      `SELECT device_id as "deviceId", token_hash as "tokenHash", label, platform, group_id as "groupId",
              sync_salt as "syncSalt", public_key as "publicKey", created_at as "createdAt",
              last_seen as "lastSeen", is_revoked as "isRevoked"
       FROM devices WHERE token_hash = $1 AND is_revoked = FALSE`,
      [hash]
    );
    return res.rows[0] ?? null;
  }

  async listDevices(groupId: string): Promise<DeviceInfo[]> {
    const res = await this.pool.query(
      `SELECT device_id as "deviceId", label, platform, last_seen as "lastSeen"
       FROM devices WHERE group_id = $1 AND is_revoked = FALSE ORDER BY last_seen DESC`,
      [groupId]
    );
    return res.rows;
  }

  async updateDeviceLastSeen(deviceId: string): Promise<void> {
    await this.pool.query(`UPDATE devices SET last_seen = NOW() WHERE device_id = $1`, [deviceId]);
  }

  async revokeDevice(deviceId: string): Promise<boolean> {
    const res = await this.pool.query(`UPDATE devices SET is_revoked = TRUE WHERE device_id = $1`, [deviceId]);
    return (res.rowCount ?? 0) > 0;
  }

  async createPairingSession(session: PairingSessionRecord): Promise<void> {
    await this.pool.query(
      `INSERT INTO pairing_sessions (code, session_id, group_id, sync_salt, expires_at, claimed, created_at)
       VALUES ($1, $2, $3, $4, $5, $6, $7)`,
      [session.code, session.sessionId, session.groupId, session.syncSalt, session.expiresAt, session.claimed, session.createdAt]
    );
  }

  async getPairingSession(code: string): Promise<PairingSessionRecord | null> {
    const res = await this.pool.query(
      `SELECT code, session_id as "sessionId", group_id as "groupId", sync_salt as "syncSalt",
              expires_at as "expiresAt", claimed, created_at as "createdAt"
       FROM pairing_sessions WHERE code = $1 AND claimed = FALSE AND expires_at > NOW()`,
      [code]
    );
    return res.rows[0] ?? null;
  }

  async claimPairingSession(code: string): Promise<PairingSessionRecord | null> {
    const res = await this.pool.query(
      `UPDATE pairing_sessions SET claimed = TRUE 
       WHERE code = $1 AND claimed = FALSE AND expires_at > NOW()
       RETURNING code, session_id as "sessionId", group_id as "groupId", sync_salt as "syncSalt",
                 expires_at as "expiresAt", claimed, created_at as "createdAt"`,
      [code]
    );
    return res.rows[0] ?? null;
  }

  async saveItem(groupId: string, item: SyncItem): Promise<StoredSyncItem> {
    const res = await this.pool.query(
      `INSERT INTO clipboard_items (item_id, group_id, source_device_id, type, version, created_at, payload, metadata, storage_ref)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
       ON CONFLICT (item_id) DO UPDATE SET payload = EXCLUDED.payload
       RETURNING sequence_number as "sequenceNumber"`,
      [
        item.itemId,
        groupId,
        item.sourceDeviceId,
        item.type,
        item.version,
        item.createdAt,
        item.payload,
        item.metadata ? JSON.stringify(item.metadata) : null,
        item.storageRef ?? null,
      ]
    );

    return {
      ...item,
      sequenceNumber: Number(res.rows[0].sequenceNumber),
      groupId,
    };
  }

  async getHistory(
    groupId: string,
    options?: HistoryQueryOptions
  ): Promise<{ items: StoredSyncItem[]; lastSequence: number; hasMore: boolean }> {
    const afterSeq = options?.afterSequence ?? 0;
    const limit = Math.min(options?.limit ?? 50, 100);

    const res = await this.pool.query(
      `SELECT item_id as "itemId", group_id as "groupId", source_device_id as "sourceDeviceId",
              type, version, created_at as "createdAt", payload, metadata, storage_ref as "storageRef",
              sequence_number as "sequenceNumber"
       FROM clipboard_items
       WHERE group_id = $1 AND sequence_number > $2
       ORDER BY sequence_number ASC
       LIMIT $3`,
      [groupId, afterSeq, limit + 1]
    );

    const rows = res.rows.map((r) => ({
      ...r,
      sequenceNumber: Number(r.sequenceNumber),
      metadata: typeof r.metadata === "string" ? JSON.parse(r.metadata) : r.metadata,
    }));

    const hasMore = rows.length > limit;
    const items = hasMore ? rows.slice(0, limit) : rows;

    const maxSeqRes = await this.pool.query(
      `SELECT COALESCE(MAX(sequence_number), 0) as "maxSeq" FROM clipboard_items WHERE group_id = $1`,
      [groupId]
    );
    const lastSequence = Number(maxSeqRes.rows[0].maxSeq);

    return { items, lastSequence, hasMore };
  }

  async deleteItem(groupId: string, itemId: string): Promise<boolean> {
    const res = await this.pool.query(`DELETE FROM clipboard_items WHERE group_id = $1 AND item_id = $2`, [groupId, itemId]);
    return (res.rowCount ?? 0) > 0;
  }

  async clearHistory(groupId: string): Promise<void> {
    await this.pool.query(`DELETE FROM clipboard_items WHERE group_id = $1`, [groupId]);
  }
}

/**
 * Factory creating storage adapter based on DATABASE_URL
 */
export async function createStorage(dbUrl?: string): Promise<StorageAdapter> {
  if (dbUrl && dbUrl.startsWith("postgres://")) {
    try {
      const pgAdapter = new PostgresStorageAdapter(dbUrl);
      await pgAdapter.init();
      return pgAdapter;
    } catch (err) {
      console.warn("PostgreSQL connection failed, falling back to file-backed storage:", (err as Error).message);
    }
  }

  const fileAdapter = new FileStorageAdapter();
  await fileAdapter.init();
  return fileAdapter;
}
