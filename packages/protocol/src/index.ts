/**
 * ClipSync Wire Protocol & Schema
 * Version: 1
 */

export const PROTOCOL_VERSION = 1;
export const ITEM_TYPES = ["text", "image", "file"] as const;
export type SyncItemType = (typeof ITEM_TYPES)[number];

export const DEVICE_PLATFORMS = ["macos", "windows", "android", "web", "cli"] as const;
export type DevicePlatform = (typeof DEVICE_PLATFORMS)[number];

// -----------------------------------------------------------------------------
// Large File & Image Transfer (Google Drive & P2P Direct)
// -----------------------------------------------------------------------------

export const MAX_TRANSFER_BYTES = 100 * 1024 * 1024; // 100 MB strict limit
export const MAX_TRANSFER_RETENTION_MS = 24 * 60 * 60 * 1000; // 24 hours strict retention

export const TRANSFER_MEDIUMS = ["p2p_direct", "google_drive"] as const;
export type TransferMedium = (typeof TRANSFER_MEDIUMS)[number];

export const TRANSFER_STATUSES = [
  "pending",
  "uploading",
  "ready",
  "downloading",
  "delivered",
  "expired",
  "failed",
  "cancelled",
] as const;
export type TransferStatus = (typeof TRANSFER_STATUSES)[number];

export interface TransferManifest {
  version: number;
  transferId: string; // UUID
  senderDeviceId: string;
  fileName: string;
  fileSizeBytes: number; // Max 100 MB
  mimeType: string;
  sha256Hash: string; // Hex string (64 chars)
  encryptionIv: string; // Base64url 12 bytes
  encryptionTag: string; // Base64url 16 bytes
  medium: TransferMedium;
  storageReference?: string; // Drive fileId, URL, or local direct endpoint
  driveFileId?: string; // Google Drive file ID if medium === "google_drive"
  createdAt: string; // ISO 8601
  expiresAt: string; // ISO 8601 (strictly <= createdAt + 24 hours)
  status: TransferStatus;
  deliveredTo?: string[]; // Device IDs that have successfully received & decrypted
}

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

export interface WsTransferInitMessage {
  type: "transfer:init";
  manifest: TransferManifest;
}

export interface WsTransferProgressMessage {
  type: "transfer:progress";
  transferId: string;
  bytesTransferred: number;
  totalBytes: number;
  phase: "uploading" | "downloading";
}

export interface WsTransferAckMessage {
  type: "transfer:ack";
  transferId: string;
  recipientDeviceId: string;
}

export interface WsTransferCleanupMessage {
  type: "transfer:cleanup";
  transferId: string;
  driveFileId?: string;
  reason: "delivered" | "expired" | "cancelled";
}

export interface WsTransferCancelMessage {
  type: "transfer:cancel";
  transferId: string;
  reason?: string;
}

export type WsClientMessage =
  | WsAuthMessage
  | WsSyncItemMessage
  | WsAckMessage
  | WsPingMessage
  | WsHistoryRequestMessage
  | WsStatusMessage
  | WsTransferInitMessage
  | WsTransferProgressMessage
  | WsTransferAckMessage
  | WsTransferCleanupMessage
  | WsTransferCancelMessage;

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

export interface WsTransferAnnouncedMessage {
  type: "transfer:announced";
  manifest: TransferManifest;
}

export interface WsTransferProgressRelayMessage {
  type: "transfer:progress_relay";
  transferId: string;
  deviceId: string;
  bytesTransferred: number;
  totalBytes: number;
  phase: "uploading" | "downloading";
}

export interface WsTransferDeliveredMessage {
  type: "transfer:delivered";
  transferId: string;
  recipientDeviceId: string;
  allDelivered: boolean;
  driveFileId?: string;
  shouldDelete: boolean;
}

export interface WsTransferExpiredMessage {
  type: "transfer:expired";
  transferId: string;
  driveFileId?: string;
  reason: "retention_exceeded" | "missed_deadline";
}

export interface WsTransferDeletedMessage {
  type: "transfer:deleted";
  transferId: string;
  driveFileId?: string;
  reason: string;
}

export type WsServerMessage =
  | WsConnectedMessage
  | WsSyncBroadcastMessage
  | WsSyncAckMessage
  | WsDeviceJoinedMessage
  | WsDeviceLeftMessage
  | WsDeviceRevokedMessage
  | WsErrorMessage
  | WsPongMessage
  | WsTransferAnnouncedMessage
  | WsTransferProgressRelayMessage
  | WsTransferDeliveredMessage
  | WsTransferExpiredMessage
  | WsTransferDeletedMessage;

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
  qrDataUrl?: string;
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

export interface InitTransferRequest {
  manifest: TransferManifest;
}

export interface InitTransferResponse {
  ok: boolean;
  manifest: TransferManifest;
  duplicate?: boolean;
}

export interface AckTransferRequest {
  recipientDeviceId?: string;
}

export interface AckTransferResponse {
  ok: boolean;
  transferId: string;
  allDelivered: boolean;
  shouldDelete: boolean;
  driveFileId?: string;
}

export interface PendingTransfersResponse {
  transfers: TransferManifest[];
}

export interface CleanupTransferResponse {
  ok: boolean;
  transferId: string;
  driveFileId?: string;
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

export function isTransferManifest(value: unknown): value is TransferManifest {
  if (!value || typeof value !== "object") return false;
  const m = value as Partial<TransferManifest>;
  return (
    m.version === PROTOCOL_VERSION &&
    typeof m.transferId === "string" &&
    typeof m.senderDeviceId === "string" &&
    typeof m.fileName === "string" &&
    typeof m.fileSizeBytes === "number" &&
    typeof m.mimeType === "string" &&
    typeof m.sha256Hash === "string" &&
    typeof m.encryptionIv === "string" &&
    typeof m.encryptionTag === "string" &&
    typeof m.medium === "string" &&
    TRANSFER_MEDIUMS.includes(m.medium as TransferMedium) &&
    typeof m.createdAt === "string" &&
    typeof m.expiresAt === "string" &&
    typeof m.status === "string" &&
    TRANSFER_STATUSES.includes(m.status as TransferStatus)
  );
}

export function validateTransferManifest(
  value: unknown
): { valid: true; manifest: TransferManifest } | { valid: false; error: string } {
  if (!value || typeof value !== "object") {
    return { valid: false, error: "TransferManifest must be a non-null object" };
  }
  const m = value as Partial<TransferManifest>;
  if (m.version !== PROTOCOL_VERSION) {
    return { valid: false, error: `Invalid protocol version: expected ${PROTOCOL_VERSION}, got ${m.version}` };
  }
  if (!m.transferId || typeof m.transferId !== "string") {
    return { valid: false, error: "transferId must be a non-empty string" };
  }
  if (!m.senderDeviceId || typeof m.senderDeviceId !== "string") {
    return { valid: false, error: "senderDeviceId must be a non-empty string" };
  }
  if (!m.fileName || typeof m.fileName !== "string") {
    return { valid: false, error: "fileName must be a non-empty string" };
  }
  if (typeof m.fileSizeBytes !== "number" || m.fileSizeBytes <= 0) {
    return { valid: false, error: "fileSizeBytes must be a positive number" };
  }
  if (m.fileSizeBytes > MAX_TRANSFER_BYTES) {
    return {
      valid: false,
      error: `fileSizeBytes exceeds maximum limit of ${MAX_TRANSFER_BYTES} bytes (100 MB)`,
    };
  }
  if (!m.mimeType || typeof m.mimeType !== "string") {
    return { valid: false, error: "mimeType must be a non-empty string" };
  }
  if (!m.sha256Hash || typeof m.sha256Hash !== "string" || m.sha256Hash.length !== 64) {
    return { valid: false, error: "sha256Hash must be a 64-character hex string" };
  }
  if (!m.encryptionIv || typeof m.encryptionIv !== "string") {
    return { valid: false, error: "encryptionIv must be a non-empty string" };
  }
  if (!m.encryptionTag || typeof m.encryptionTag !== "string") {
    return { valid: false, error: "encryptionTag must be a non-empty string" };
  }
  if (!m.medium || !TRANSFER_MEDIUMS.includes(m.medium as TransferMedium)) {
    return { valid: false, error: `medium must be one of: ${TRANSFER_MEDIUMS.join(", ")}` };
  }
  if (!m.createdAt || isNaN(Date.parse(m.createdAt))) {
    return { valid: false, error: "createdAt must be a valid ISO 8601 timestamp" };
  }
  if (!m.expiresAt || isNaN(Date.parse(m.expiresAt))) {
    return { valid: false, error: "expiresAt must be a valid ISO 8601 timestamp" };
  }
  const createdTime = new Date(m.createdAt).getTime();
  const expiresTime = new Date(m.expiresAt).getTime();
  if (expiresTime <= createdTime) {
    return { valid: false, error: "expiresAt must be in the future relative to createdAt" };
  }
  if (expiresTime - createdTime > MAX_TRANSFER_RETENTION_MS + 60000) {
    // 60s tolerance for clock drift
    return {
      valid: false,
      error: `expiresAt exceeds maximum retention of 24 hours (${MAX_TRANSFER_RETENTION_MS} ms)`,
    };
  }
  if (!m.status || !TRANSFER_STATUSES.includes(m.status as TransferStatus)) {
    return { valid: false, error: `status must be one of: ${TRANSFER_STATUSES.join(", ")}` };
  }
  return { valid: true, manifest: m as TransferManifest };
}

export function makeTransferManifest(params: Omit<TransferManifest, "version">): TransferManifest {
  const manifest: TransferManifest = {
    version: PROTOCOL_VERSION,
    ...params,
  };
  const val = validateTransferManifest(manifest);
  if (!val.valid) throw new TypeError(`Invalid TransferManifest: ${val.error}`);
  return manifest;
}

