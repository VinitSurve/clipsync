import crypto from "node:crypto";
import { Router, type Request, type Response, type NextFunction } from "express";
import QRCode from "qrcode";
import type { StorageAdapter, DeviceRecord } from "./db.js";
import { hashToken } from "./db.js";
import type { StorageService, LocalDiskStorageService } from "./storage.js";
import type { WebSocketRelayManager } from "./websocket.js";
import {
  type RegisterDeviceRequest,
  type ClaimPairingRequest,
  type UploadUrlRequest,
  validateSyncItem,
} from "@clipsync/protocol";
import { generateSalt } from "@clipsync/crypto";

export interface AuthenticatedRequest extends Request {
  device?: DeviceRecord;
}

export function createRouter(
  storage: StorageAdapter,
  storageService: StorageService,
  relayManager: WebSocketRelayManager,
  serverOrigin: string
): Router {
  const router = Router();

  // Authentication middleware
  async function requireAuth(req: AuthenticatedRequest, res: Response, next: NextFunction) {
    const authHeader = req.header("authorization") ?? "";
    const [scheme, token] = authHeader.split(" ");
    if (scheme !== "Bearer" || !token) {
      return res.status(401).json({ error: "Missing or invalid authorization header" });
    }

    const device = await storage.getDeviceByToken(token);
    if (!device || device.isRevoked) {
      return res.status(401).json({ error: "Unauthorized or revoked device" });
    }

    req.device = device;
    next();
  }

  // Health check
  router.get("/health", (_req, res) => {
    res.json({
      ok: true,
      service: "clipsync-server",
      version: "1.0.0",
      protocolVersion: 1,
      timestamp: new Date().toISOString(),
    });
  });

  // Direct device registration (for initial device setup or bootstrap)
  router.post("/v1/devices/register", async (req: Request, res: Response) => {
    const body = req.body as RegisterDeviceRequest;
    const label = typeof body?.label === "string" ? body.label.slice(0, 80) : "Unnamed Device";
    const platform = body?.platform ?? "macos";
    const groupId = typeof body?.groupId === "string" && body.groupId.trim() ? body.groupId.trim() : "clipsync-default-group";
    const syncSalt = generateSalt(16);

    const deviceId = crypto.randomUUID();
    const token = crypto.randomBytes(32).toString("base64url");
    const tokenHash = hashToken(token);

    const deviceRecord: DeviceRecord = {
      deviceId,
      tokenHash,
      label,
      platform,
      groupId,
      syncSalt,
      publicKey: body?.publicKey,
      createdAt: new Date().toISOString(),
      lastSeen: new Date().toISOString(),
      isRevoked: false,
    };

    await storage.createDevice(deviceRecord);

    res.status(201).json({
      deviceId,
      token,
      label,
      platform,
      groupId,
      syncSalt,
    });
  });

  // Create a device pairing session (generates 6-digit code + QR code URL/SVG)
  router.post("/v1/auth/pairing/session", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    const sessionId = crypto.randomUUID();
    // 6-digit numeric code
    const code = crypto.randomInt(100000, 999999).toString();
    const expiresAt = new Date(Date.now() + 10 * 60 * 1000).toISOString(); // 10 minutes

    await storage.createPairingSession({
      sessionId,
      code,
      groupId: device.groupId,
      syncSalt: device.syncSalt,
      expiresAt,
      claimed: false,
      createdAt: new Date().toISOString(),
    });

    // Deep-link format for native mobile/desktop scanner
    const pairingUrl = `clipsync://pair?code=${code}&server=${encodeURIComponent(serverOrigin)}&salt=${encodeURIComponent(device.syncSalt)}`;
    const qrDataUrl = await QRCode.toDataURL(pairingUrl);

    res.status(201).json({
      sessionId,
      code,
      qrData: pairingUrl,
      qrDataUrl,
      expiresAt,
      syncSalt: device.syncSalt,
    });
  });

  // Claim pairing session on a new device (Mac, Windows, or Android)
  router.post("/v1/auth/pair", async (req: Request, res: Response) => {
    const body = req.body as ClaimPairingRequest;
    if (!body?.code) {
      return res.status(400).json({ error: "Missing pairing code" });
    }

    const session = await storage.claimPairingSession(body.code.trim());
    if (!session) {
      return res.status(400).json({ error: "Invalid, expired, or already used pairing code" });
    }

    const deviceId = crypto.randomUUID();
    const token = crypto.randomBytes(32).toString("base64url");
    const tokenHash = hashToken(token);
    const label = body.label?.slice(0, 80) ?? "New Device";
    const platform = body.platform ?? "android";

    const newDevice: DeviceRecord = {
      deviceId,
      tokenHash,
      label,
      platform,
      groupId: session.groupId,
      syncSalt: session.syncSalt,
      publicKey: body.publicKey,
      createdAt: new Date().toISOString(),
      lastSeen: new Date().toISOString(),
      isRevoked: false,
    };

    await storage.createDevice(newDevice);

    res.status(201).json({
      deviceId,
      token,
      groupId: session.groupId,
      label,
      platform,
      syncSalt: session.syncSalt,
    });
  });

  // List all devices in current group
  router.get("/v1/devices", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    const devices = await storage.listDevices(device.groupId);
    res.json({
      devices: devices.map((d) => ({
        ...d,
        isCurrent: d.deviceId === device.deviceId,
      })),
    });
  });

  // Revoke device
  router.delete("/v1/devices/:deviceId", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const currentDevice = req.device!;
    const targetId = String(req.params.deviceId);

    const target = await storage.getDeviceById(targetId);
    if (!target || target.groupId !== currentDevice.groupId) {
      return res.status(404).json({ error: "Device not found in group" });
    }

    await storage.revokeDevice(targetId);
    relayManager.revokeDeviceSockets(targetId);

    res.json({ ok: true, revokedDeviceId: targetId });
  });

  // Query clipboard history
  router.get("/v1/history", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    const afterSeq = Number(req.query.afterSequence ?? 0);
    const limit = Number(req.query.limit ?? 50);
    const type = req.query.type as any;
    const pinnedOnly = req.query.pinnedOnly === "true";

    const result = await storage.getHistory(device.groupId, {
      afterSequence: isNaN(afterSeq) ? 0 : afterSeq,
      limit: isNaN(limit) ? 50 : limit,
      type,
      pinnedOnly,
    });

    res.json(result);
  });

  // Delete specific history item
  router.delete("/v1/history/:itemId", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    const itemId = String(req.params.itemId);
    const deleted = await storage.deleteItem(device.groupId, itemId);
    res.json({ ok: deleted });
  });

  // Clear all history for group
  router.post("/v1/history/clear", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    await storage.clearHistory(device.groupId);
    res.json({ ok: true });
  });

  // HTTP Sync submission fallback (e.g. if WebSocket is unavailable or for background tasks)
  router.post("/v1/sync", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    const validation = validateSyncItem(req.body);
    if (!validation.valid) {
      return res.status(400).json({ error: validation.error });
    }

    const item = validation.item;
    item.sourceDeviceId = device.deviceId;

    const stored = await storage.saveItem(device.groupId, item);

    // Broadcast to connected WebSocket clients in group
    relayManager.broadcastToGroup(
      device.groupId,
      {
        type: "sync:item",
        item: stored,
        sequenceNumber: stored.sequenceNumber,
      },
      device.deviceId
    );

    res.status(202).json({ accepted: true, itemId: item.itemId, sequenceNumber: stored.sequenceNumber });
  });

  // Request presigned upload URL for binary blobs (images/files)
  router.post("/v1/storage/upload-url", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const device = req.device!;
    const body = req.body as UploadUrlRequest;

    if (!body?.fileName || !body?.mimeType || !body?.sizeBytes || !body?.sha256) {
      return res.status(400).json({ error: "Missing required fields: fileName, mimeType, sizeBytes, sha256" });
    }

    const maxFileSize = 50 * 1024 * 1024; // 50MB
    if (body.sizeBytes > maxFileSize) {
      return res.status(400).json({ error: "File exceeds 50MB maximum size limit" });
    }

    const result = await storageService.generateUploadUrl(
      device.groupId,
      body.fileName,
      body.mimeType,
      body.sizeBytes,
      body.sha256
    );

    res.json(result);
  });

  // Request presigned download URL for binary blob
  router.get("/v1/storage/download-url/:key", requireAuth, async (req: AuthenticatedRequest, res: Response) => {
    const key = decodeURIComponent(String(req.params.key));
    const result = await storageService.generateDownloadUrl(key);
    res.json(result);
  });

  // Local storage upload handler (for disk storage fallback)
  router.put("/v1/storage/local-upload", async (req: Request, res: Response) => {
    const key = req.query.key as string;
    if (!key) return res.status(400).send("Missing key");

    const chunks: Buffer[] = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      const data = Buffer.concat(chunks);
      (storageService as LocalDiskStorageService).saveBlob(key, data);
      res.status(200).send("OK");
    });
  });

  // Local storage download handler (for disk storage fallback)
  router.get("/v1/storage/local-download", async (req: Request, res: Response) => {
    const key = req.query.key as string;
    if (!key) return res.status(400).send("Missing key");

    const data = (storageService as LocalDiskStorageService).getBlob(key);
    if (!data) return res.status(404).send("Blob not found");

    res.setHeader("Content-Type", "application/octet-stream");
    res.send(data);
  });

  return router;
}
