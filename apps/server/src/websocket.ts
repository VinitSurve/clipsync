import type { Server } from "node:http";
import { WebSocketServer, WebSocket } from "ws";
import type { RawData } from "ws";
import type { StorageAdapter, DeviceRecord } from "./db.js";
import {
  type WsClientMessage,
  type WsServerMessage,
  validateSyncItem,
} from "@clipsync/protocol";

interface AuthenticatedSocketContext {
  device: DeviceRecord;
  socket: WebSocket;
}

export class WebSocketRelayManager {
  private wss: WebSocketServer;
  private storage: StorageAdapter;
  // groupId -> Map<deviceId, Set<WebSocket>>
  private socketsByGroup = new Map<string, Map<string, Set<WebSocket>>>();

  constructor(server: Server, storage: StorageAdapter) {
    this.storage = storage;
    this.wss = new WebSocketServer({ noServer: true });

    server.on("upgrade", async (request, socket, head) => {
      const url = new URL(request.url ?? "/", `http://${request.headers.host ?? "localhost"}`);
      if (url.pathname !== "/v1/events") {
        socket.destroy();
        return;
      }

      const token = url.searchParams.get("token");
      if (!token) {
        socket.destroy();
        return;
      }

      const device = await this.storage.getDeviceByToken(token);
      if (!device || device.isRevoked) {
        socket.destroy();
        return;
      }

      this.wss.handleUpgrade(request, socket, head, (ws) => {
        this.handleConnection(ws, device);
      });
    });
  }

  private async handleConnection(ws: WebSocket, device: DeviceRecord): Promise<void> {
    const groupId = device.groupId;
    const deviceId = device.deviceId;

    if (!this.socketsByGroup.has(groupId)) {
      this.socketsByGroup.set(groupId, new Map());
    }
    const groupDevices = this.socketsByGroup.get(groupId)!;
    if (!groupDevices.has(deviceId)) {
      groupDevices.set(deviceId, new Set());
    }
    const deviceSockets = groupDevices.get(deviceId)!;
    const isFirstConnectionForDevice = deviceSockets.size === 0;
    deviceSockets.add(ws);

    // Update last seen
    await this.storage.updateDeviceLastSeen(deviceId);

    // Get current last sequence
    const history = await this.storage.getHistory(groupId, { limit: 1 });
    const lastSequence = history.lastSequence;

    // 1. Send connected greeting
    this.sendToSocket(ws, {
      type: "connected",
      deviceId,
      groupId,
      serverTime: new Date().toISOString(),
      lastSequence,
    });

    // 2. Notify other devices in group if newly online
    if (isFirstConnectionForDevice) {
      this.broadcastToGroup(
        groupId,
        {
          type: "device:joined",
          device: {
            deviceId: device.deviceId,
            label: device.label,
            platform: device.platform,
            lastSeen: new Date().toISOString(),
          },
        },
        deviceId // exclude self
      );
    }

    // Handle messages
    ws.on("message", async (raw: RawData) => {
      try {
        const msg = JSON.parse(raw.toString()) as WsClientMessage;
        await this.handleClientMessage(ws, device, msg);
      } catch (err) {
        this.sendToSocket(ws, {
          type: "error",
          code: "INVALID_MESSAGE",
          message: (err as Error).message,
        });
      }
    });

    // Handle close
    ws.on("close", () => {
      deviceSockets.delete(ws);
      if (deviceSockets.size === 0) {
        groupDevices.delete(deviceId);
        this.broadcastToGroup(groupId, { type: "device:left", deviceId }, deviceId);
      }
    });

    ws.on("error", () => {
      deviceSockets.delete(ws);
    });
  }

  private async handleClientMessage(ws: WebSocket, device: DeviceRecord, msg: WsClientMessage): Promise<void> {
    switch (msg.type) {
      case "ping": {
        this.sendToSocket(ws, { type: "pong", serverTime: new Date().toISOString() });
        break;
      }

      case "sync:item": {
        const validation = validateSyncItem(msg.item);
        if (!validation.valid) {
          this.sendToSocket(ws, {
            type: "error",
            code: "INVALID_ITEM",
            message: validation.error,
          });
          return;
        }

        const item = validation.item;
        // Verify sourceDeviceId matches authenticated device
        item.sourceDeviceId = device.deviceId;

        // Persist and obtain monotonic sequence number
        const stored = await this.storage.saveItem(device.groupId, item);

        // Acknowledge back to sender
        this.sendToSocket(ws, {
          type: "sync:ack",
          itemId: item.itemId,
          sequenceNumber: stored.sequenceNumber,
        });

        // Broadcast to all other devices in the same group
        this.broadcastToGroup(
          device.groupId,
          {
            type: "sync:item",
            item: stored,
            sequenceNumber: stored.sequenceNumber,
          },
          device.deviceId // do not echo back to sender
        );
        break;
      }

      case "history:request": {
        const afterSeq = msg.afterSequence ?? 0;
        const result = await this.storage.getHistory(device.groupId, {
          afterSequence: afterSeq,
          limit: msg.limit ?? 50,
        });

        for (const item of result.items) {
          this.sendToSocket(ws, {
            type: "sync:item",
            item,
            sequenceNumber: item.sequenceNumber,
          });
        }
        break;
      }

      case "ack": {
        // Client confirmed receipt of sequence number
        break;
      }

      case "presence:status": {
        // Can be forwarded to other devices if needed
        break;
      }
    }
  }

  public broadcastToGroup(groupId: string, message: WsServerMessage, excludeDeviceId?: string): void {
    const groupDevices = this.socketsByGroup.get(groupId);
    if (!groupDevices) return;

    const payload = JSON.stringify(message);
    for (const [deviceId, sockets] of groupDevices) {
      if (excludeDeviceId && deviceId === excludeDeviceId) continue;
      for (const socket of sockets) {
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(payload);
        }
      }
    }
  }

  public revokeDeviceSockets(deviceId: string): void {
    for (const groupDevices of this.socketsByGroup.values()) {
      const sockets = groupDevices.get(deviceId);
      if (sockets) {
        for (const socket of sockets) {
          this.sendToSocket(socket, {
            type: "device:revoked",
            deviceId,
          });
          socket.close();
        }
        groupDevices.delete(deviceId);
      }
    }
  }

  private sendToSocket(ws: WebSocket, message: WsServerMessage): void {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(message));
    }
  }
}
