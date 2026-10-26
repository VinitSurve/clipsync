import { EventEmitter } from "node:events";
import WebSocket from "ws";
import {
  type SyncItem,
  type SyncItemType,
  type WsServerMessage,
  type WsClientMessage,
  type DeviceInfo,
  makeSyncItem,
} from "@clipsync/protocol";
import { encryptText, decryptText, computeSha256 } from "@clipsync/crypto";

export interface SyncClientConfig {
  serverUrl: string; // e.g. "http://localhost:8787" or "ws://localhost:8787"
  deviceId: string;
  token: string;
  syncKey: Buffer; // 32-byte derived AES key
  autoReconnect?: boolean;
}

export type ConnectionState = "disconnected" | "connecting" | "connected" | "paused";

export class ClipSyncClient extends EventEmitter {
  private config: SyncClientConfig;
  private ws: WebSocket | null = null;
  private state: ConnectionState = "disconnected";
  private isPaused = false;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private pingInterval: NodeJS.Timeout | null = null;
  private reconnectAttempt = 0;
  private lastSequence = 0;

  // Sync Loop & Deduplication state
  private seenItemIds = new Set<string>();
  private lastReceivedHash = "";
  private lastSentHash = "";
  private outbox: SyncItem[] = [];

  constructor(config: SyncClientConfig) {
    super();
    this.config = config;
  }

  public getState(): ConnectionState {
    return this.isPaused ? "paused" : this.state;
  }

  public getDeviceId(): string {
    return this.config.deviceId;
  }

  public isSyncPaused(): boolean {
    return this.isPaused;
  }

  public pauseSync(): void {
    this.isPaused = true;
    this.emit("status", "paused");
  }

  public resumeSync(): void {
    this.isPaused = false;
    this.emit("status", this.state);
    this.flushOutbox();
  }

  public connect(): void {
    if (this.state === "connected" || this.state === "connecting") return;
    this.setState("connecting");

    const wsUrl = this.config.serverUrl
      .replace(/^http:\/\//, "ws://")
      .replace(/^https:\/\//, "wss://");
    const endpoint = `${wsUrl.replace(/\/$/, "")}/v1/events?token=${encodeURIComponent(this.config.token)}`;

    try {
      this.ws = new WebSocket(endpoint);

      this.ws.on("open", () => {
        this.reconnectAttempt = 0;
        this.setState("connected");
        this.startHeartbeat();
        this.flushOutbox();

        // Request any events missed while disconnected
        if (this.lastSequence > 0) {
          this.sendMessage({
            type: "history:request",
            afterSequence: this.lastSequence,
          });
        }
      });

      this.ws.on("message", (data: WebSocket.RawData) => {
        this.handleMessage(data.toString());
      });

      this.ws.on("close", () => {
        this.cleanupSocket();
        this.setState("disconnected");
        this.scheduleReconnect();
      });

      this.ws.on("error", (err: Error) => {
        this.emit("error", err);
      });
    } catch (err) {
      this.cleanupSocket();
      this.setState("disconnected");
      this.scheduleReconnect();
    }
  }

  public disconnect(): void {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.config.autoReconnect = false;
    this.cleanupSocket();
    this.setState("disconnected");
  }

  /**
   * Broadcast a clipboard item to all connected devices.
   * Handles client-side encryption and loop detection recording.
   */
  public broadcastText(text: string, metadata?: { sourceApp?: string }): SyncItem | null {
    if (this.isPaused) return null;
    if (!text || text.trim().length === 0) return null;

    const contentHash = computeSha256(text);

    // Prevent echoing: if this text matches the one we just received from remote, ignore
    if (contentHash === this.lastReceivedHash) {
      return null;
    }

    // Don't send identical item back-to-back if we already sent it
    if (contentHash === this.lastSentHash) {
      return null;
    }

    const envelope = encryptText(text, this.config.syncKey);
    const itemId = `item-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;

    const syncItem = makeSyncItem({
      itemId,
      type: "text",
      sourceDeviceId: this.config.deviceId,
      payload: JSON.stringify(envelope),
      metadata: {
        size: Buffer.byteLength(text, "utf8"),
        sha256: contentHash,
        previewSnippet: text.slice(0, 100),
        sourceApp: metadata?.sourceApp,
      },
    });

    this.lastSentHash = contentHash;
    this.seenItemIds.add(itemId);

    if (this.state === "connected" && this.ws?.readyState === WebSocket.OPEN) {
      this.sendMessage({ type: "sync:item", item: syncItem });
    } else {
      // Buffer in outbox if offline
      this.outbox.push(syncItem);
      if (this.outbox.length > 50) this.outbox.shift();
    }

    return syncItem;
  }

  /**
   * Encrypt and upload an image or file to object storage, then broadcast its reference.
   */
  public async broadcastBinaryFile(
    fileBytes: Uint8Array,
    fileName: string,
    mimeType: string,
    type: "image" | "file" = "file"
  ): Promise<SyncItem | null> {
    if (this.isPaused) return null;

    const { ciphertext, iv, tag, sha256 } = (await import("@clipsync/crypto")).encryptBinary(fileBytes, this.config.syncKey);

    // 1. Get presigned upload URL
    const uploadRes = await fetch(`${this.config.serverUrl.replace(/\/$/, "")}/v1/storage/upload-url`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${this.config.token}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({
        fileName,
        mimeType,
        sizeBytes: ciphertext.length,
        sha256,
      }),
    });

    if (!uploadRes.ok) {
      throw new Error(`Failed to get upload URL: ${uploadRes.statusText}`);
    }

    const { uploadUrl, storageRef } = (await uploadRes.json()) as { uploadUrl: string; storageRef: string };

    // 2. Upload encrypted bytes directly to storage
    const putRes = await fetch(uploadUrl, {
      method: "PUT",
      headers: {
        "content-type": mimeType,
      },
      body: new Uint8Array(ciphertext),
    });

    if (!putRes.ok) {
      throw new Error(`Storage upload failed: ${putRes.statusText}`);
    }

    // 3. Broadcast lightweight reference over WebSocket
    const itemId = `blob-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
    const syncItem = makeSyncItem({
      itemId,
      type,
      sourceDeviceId: this.config.deviceId,
      storageRef,
      payload: JSON.stringify({ version: 1, iv, ciphertext: "", tag, sha256 }),
      metadata: {
        size: fileBytes.length,
        fileName,
        mimeType,
        sha256,
        previewSnippet: `[${type.toUpperCase()}] ${fileName}`,
      },
    });

    this.seenItemIds.add(itemId);

    if (this.state === "connected" && this.ws?.readyState === WebSocket.OPEN) {
      this.sendMessage({ type: "sync:item", item: syncItem });
    } else {
      this.outbox.push(syncItem);
    }

    return syncItem;
  }

  /**
   * Download and decrypt a binary file or image from object storage.
   */
  public async fetchAndDecryptBinaryFile(
    storageRef: string,
    iv: string,
    tag: string,
    expectedSha256?: string
  ): Promise<Uint8Array> {
    const downloadRes = await fetch(
      `${this.config.serverUrl.replace(/\/$/, "")}/v1/storage/download-url/${encodeURIComponent(storageRef)}`,
      {
        headers: {
          authorization: `Bearer ${this.config.token}`,
        },
      }
    );

    if (!downloadRes.ok) {
      throw new Error(`Failed to get download URL: ${downloadRes.statusText}`);
    }

    const { downloadUrl } = (await downloadRes.json()) as { downloadUrl: string };
    const blobRes = await fetch(downloadUrl);
    if (!blobRes.ok) {
      throw new Error(`Failed to fetch blob: ${blobRes.statusText}`);
    }

    const encryptedArrayBuffer = await blobRes.arrayBuffer();
    const encryptedBytes = Buffer.from(encryptedArrayBuffer);

    const decrypted = (await import("@clipsync/crypto")).decryptBinary(
      encryptedBytes,
      this.config.syncKey,
      iv,
      tag
    );

    if (expectedSha256) {
      const actualHash = (await import("@clipsync/crypto")).computeSha256(decrypted);
      if (actualHash !== expectedSha256) {
        throw new Error("SHA-256 integrity verification failed for downloaded blob");
      }
    }

    return decrypted;
  }

  /**
   * Handle incoming raw WebSocket frame
   */
  private handleMessage(raw: string): void {
    let msg: WsServerMessage;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }

    switch (msg.type) {
      case "connected": {
        this.lastSequence = msg.lastSequence;
        this.emit("ready", { deviceId: msg.deviceId, groupId: msg.groupId });
        break;
      }

      case "sync:item": {
        this.handleIncomingSyncItem(msg.item, msg.sequenceNumber);
        break;
      }

      case "sync:ack": {
        this.emit("ack", { itemId: msg.itemId, sequenceNumber: msg.sequenceNumber });
        break;
      }

      case "device:joined": {
        this.emit("device:joined", msg.device);
        break;
      }

      case "device:left": {
        this.emit("device:left", msg.deviceId);
        break;
      }

      case "device:revoked": {
        if (msg.deviceId === this.config.deviceId) {
          this.emit("revoked");
          this.disconnect();
        }
        break;
      }

      case "pong":
        break;

      case "error": {
        this.emit("error", new Error(`Server error [${msg.code}]: ${msg.message}`));
        break;
      }
    }
  }

  private handleIncomingSyncItem(item: SyncItem, sequenceNumber: number): void {
    // 1. Acknowledge sequence number to server
    this.lastSequence = Math.max(this.lastSequence, sequenceNumber);
    this.sendMessage({
      type: "ack",
      itemId: item.itemId,
      sequenceNumber,
    });

    // 2. Loop prevention: Ignore echo from self
    if (item.sourceDeviceId === this.config.deviceId) {
      return;
    }

    // 3. Deduplication: Check if already processed
    if (this.seenItemIds.has(item.itemId)) {
      return;
    }
    this.seenItemIds.add(item.itemId);

    // Keep memory bounded
    if (this.seenItemIds.size > 2000) {
      const firstEntries = Array.from(this.seenItemIds).slice(0, 500);
      for (const id of firstEntries) this.seenItemIds.delete(id);
    }

    if (this.isPaused) return;

    // 4. Decrypt payload if text; for binary files, emit item with storage reference
    if (item.type === "image" || item.type === "file") {
      this.emit("sync:item", {
        item,
        decryptedText: undefined,
        sequenceNumber,
      });
      return;
    }

    try {
      const plainText = decryptText(item.payload, this.config.syncKey);
      const hash = computeSha256(plainText);

      // Record as received so our local clipboard observer won't re-broadcast it
      this.lastReceivedHash = hash;

      this.emit("sync:item", {
        item,
        decryptedText: plainText,
        sequenceNumber,
      });
    } catch (err) {
      this.emit("error", new Error(`Decryption failed for item ${item.itemId}: ${(err as Error).message}`));
    }
  }

  private sendMessage(msg: WsClientMessage): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(msg));
    }
  }

  private flushOutbox(): void {
    if (this.isPaused || this.state !== "connected" || !this.ws) return;
    while (this.outbox.length > 0) {
      const item = this.outbox.shift();
      if (item) {
        this.sendMessage({ type: "sync:item", item });
      }
    }
  }

  private startHeartbeat(): void {
    if (this.pingInterval) clearInterval(this.pingInterval);
    this.pingInterval = setInterval(() => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.sendMessage({ type: "ping" });
      }
    }, 20000);
  }

  private scheduleReconnect(): void {
    if (this.config.autoReconnect === false) return;
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);

    // Exponential backoff: 1s, 2s, 4s, 8s, up to 15s + jitter
    const delay = Math.min(1000 * Math.pow(1.8, this.reconnectAttempt), 15000) + Math.random() * 500;
    this.reconnectAttempt++;

    this.reconnectTimer = setTimeout(() => {
      this.connect();
    }, delay);
  }

  private cleanupSocket(): void {
    if (this.pingInterval) clearInterval(this.pingInterval);
    if (this.ws) {
      this.ws.removeAllListeners();
      try {
        this.ws.close();
      } catch {}
      this.ws = null;
    }
  }

  private setState(newState: ConnectionState): void {
    this.state = newState;
    this.emit("status", this.isPaused ? "paused" : newState);
  }
}
