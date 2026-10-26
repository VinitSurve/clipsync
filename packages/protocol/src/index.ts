/**
 * ClipSync Wire Protocol & Schema
 * Version: 1
 */

export const PROTOCOL_VERSION = 1;
export const ITEM_TYPES = ["text", "image", "file"] as const;
export type SyncItemType = (typeof ITEM_TYPES)[number];

export const DEVICE_PLATFORMS = ["macos", "windows", "android", "web", "cli"] as const;
export type DevicePlatform = (typeof DEVICE_PLATFORMS)[number];

export interface SyncItemMetadata {
  size: number;
  mimeType?: string;
  fileName?: string;
  sha256?: string;
  previewSnippet?: string;
  isPinned?: boolean;
  sourceApp?: string;
}

export interface EncryptedPayloadEnvelope {
  version: number;
  iv: string; // base64url 12 bytes
  ciphertext: string; // base64url
  tag: string; // base64url 16 bytes
  salt?: string; // base64url salt
}

export interface SyncItem {
  version: number;
  itemId: string;
  type: SyncItemType;
  sourceDeviceId: string;
  createdAt: string; // ISO 8601
  payload: string; // Serialized JSON of EncryptedPayloadEnvelope or raw base64url envelope
  metadata?: SyncItemMetadata;
  storageRef?: string; // Object storage key for large payloads (e.g. files/images)
}

export interface StoredSyncItem extends SyncItem {
  sequenceNumber: number;
  groupId: string;
}

// -----------------------------------------------------------------------------
// WebSocket Messages (Client -> Server)
// -----------------------------------------------------------------------------

export interface WsAuthMessage {
  type: "auth";
  token: string;
  deviceId: string;
}

export interface WsSyncItemMessage {
  type: "sync:item";
  item: SyncItem;
}

export interface WsAckMessage {
  type: "ack";
  itemId: string;
  sequenceNumber: number;
}

export interface WsPingMessage {
  type: "ping";
}

export interface WsHistoryRequestMessage {
  type: "history:request";
  afterSequence?: number;
  limit?: number;
}

export interface WsStatusMessage {
  type: "presence:status";
  status: "active" | "idle" | "paused";
}

export type WsClientMessage =
  | WsAuthMessage
  | WsSyncItemMessage
  | WsAckMessage
  | WsPingMessage
  | WsHistoryRequestMessage
  | WsStatusMessage;

// -----------------------------------------------------------------------------
// WebSocket Messages (Server -> Client)
// -----------------------------------------------------------------------------

export interface DeviceInfo {
  deviceId: string;
  label: string;
  platform: DevicePlatform;
  lastSeen?: string;
  isCurrent?: boolean;
}

export interface WsConnectedMessage {
  type: "connected";
  deviceId: string;
  groupId: string;
  serverTime: string;
  lastSequence: number;
}

export interface WsSyncBroadcastMessage {
  type: "sync:item";
  item: SyncItem;
  sequenceNumber: number;
}

export interface WsSyncAckMessage {
  type: "sync:ack";
  itemId: string;
  sequenceNumber: number;
}

export interface WsDeviceJoinedMessage {
  type: "device:joined";
  device: DeviceInfo;
}

export interface WsDeviceLeftMessage {
  type: "device:left";
  deviceId: string;
}

export interface WsDeviceRevokedMessage {
  type: "device:revoked";
  deviceId: string;
}

export interface WsErrorMessage {
  type: "error";
  code: string;
  message: string;
}

export interface WsPongMessage {
  type: "pong";
  serverTime: string;
}

export type WsServerMessage =
  | WsConnectedMessage
  | WsSyncBroadcastMessage
  | WsSyncAckMessage
  | WsDeviceJoinedMessage
  | WsDeviceLeftMessage
  | WsDeviceRevokedMessage
  | WsErrorMessage
  | WsPongMessage;

// -----------------------------------------------------------------------------
// REST API Payloads
// -----------------------------------------------------------------------------

export interface RegisterDeviceRequest {
  label: string;
  platform: DevicePlatform;
  groupId?: string;
  publicKey?: string;
}

export interface RegisterDeviceResponse {
  deviceId: string;
  token: string;
  label: string;
  platform: DevicePlatform;
  groupId: string;
  syncSalt: string;
}

export interface PairingSessionResponse {
  sessionId: string;
  code: string; // 6-digit numeric pairing code (e.g. "849201")
  qrData: string; // clipsync://pair?code=849201&session=...&host=...
  expiresAt: string;
  syncSalt: string;
}

export interface ClaimPairingRequest {
  code: string;
  label: string;
  platform: DevicePlatform;
  publicKey?: string;
}

export interface ClaimPairingResponse {
  deviceId: string;
  token: string;
  groupId: string;
  label: string;
  platform: DevicePlatform;
  syncSalt: string;
}

export interface UploadUrlRequest {
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  sha256: string;
}

export interface UploadUrlResponse {
  uploadUrl: string;
  storageRef: string;
  expiresInSeconds: number;
}

export interface DownloadUrlResponse {
  downloadUrl: string;
  storageRef: string;
  expiresInSeconds: number;
}

export interface HistoryQueryOptions {
  afterSequence?: number;
  limit?: number;
  type?: SyncItemType;
  pinnedOnly?: boolean;
}

export interface HistoryResponse {
  items: StoredSyncItem[];
  lastSequence: number;
  hasMore: boolean;
}

// -----------------------------------------------------------------------------
// Validation & Helpers
// -----------------------------------------------------------------------------

export function isSyncItem(value: unknown): value is SyncItem {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Partial<SyncItem>;
  return (
    candidate.version === PROTOCOL_VERSION &&
    typeof candidate.itemId === "string" &&
    candidate.itemId.length > 0 &&
    typeof candidate.sourceDeviceId === "string" &&
    typeof candidate.type === "string" &&
    ITEM_TYPES.includes(candidate.type as SyncItemType) &&
    typeof candidate.createdAt === "string" &&
    typeof candidate.payload === "string"
  );
}

export function validateSyncItem(value: unknown): { valid: true; item: SyncItem } | { valid: false; error: string } {
  if (!value || typeof value !== "object") {
    return { valid: false, error: "SyncItem must be a non-null object" };
  }
  const item = value as Partial<SyncItem>;
  if (item.version !== PROTOCOL_VERSION) {
    return { valid: false, error: `Invalid protocol version: expected ${PROTOCOL_VERSION}, got ${item.version}` };
  }
  if (!item.itemId || typeof item.itemId !== "string") {
    return { valid: false, error: "itemId must be a non-empty string" };
  }
  if (!item.type || !ITEM_TYPES.includes(item.type as SyncItemType)) {
    return { valid: false, error: `type must be one of: ${ITEM_TYPES.join(", ")}` };
  }
  if (!item.sourceDeviceId || typeof item.sourceDeviceId !== "string") {
    return { valid: false, error: "sourceDeviceId must be a non-empty string" };
  }
  if (!item.createdAt || isNaN(Date.parse(item.createdAt))) {
    return { valid: false, error: "createdAt must be a valid ISO 8601 timestamp" };
  }
  if (typeof item.payload !== "string") {
    return { valid: false, error: "payload must be an encrypted string" };
  }
  return { valid: true, item: item as SyncItem };
}

export function makeSyncItem({
  itemId,
  type,
  sourceDeviceId,
  createdAt = new Date().toISOString(),
  payload,
  metadata,
  storageRef,
}: {
  itemId: string;
  type: SyncItemType;
  sourceDeviceId: string;
  createdAt?: string;
  payload: string;
  metadata?: SyncItemMetadata;
  storageRef?: string;
}): SyncItem {
  const item: SyncItem = {
    version: PROTOCOL_VERSION,
    itemId,
    type,
    sourceDeviceId,
    createdAt,
    payload,
    metadata,
    storageRef,
  };
  const validation = validateSyncItem(item);
  if (!validation.valid) throw new TypeError(`Invalid ClipSync item: ${validation.error}`);
  return item;
}

export function isEncryptedEnvelope(value: unknown): value is EncryptedPayloadEnvelope {
  if (!value || typeof value !== "object") return false;
  const env = value as Partial<EncryptedPayloadEnvelope>;
  return (
    typeof env.version === "number" &&
    typeof env.iv === "string" &&
    typeof env.ciphertext === "string" &&
    typeof env.tag === "string"
  );
}
