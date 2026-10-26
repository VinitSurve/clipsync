import { test, describe, before, after } from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import express from "express";
import cors from "cors";
import { WebSocket } from "ws";
import { createStorage } from "../dist/db.js";
import { LocalDiskStorageService } from "../dist/storage.js";
import { WebSocketRelayManager } from "../dist/websocket.js";
import { createRouter } from "../dist/routes.js";
import { deriveKey, encryptText, decryptText, computeSha256 } from "@clipsync/crypto";
import { makeSyncItem } from "@clipsync/protocol";
import { ClipSyncClient } from "@clipsync/sync-client";

import fs from "node:fs";
import path from "node:path";

describe("ClipSync Server Integration Tests", () => {
  let server;
  let baseUrl;
  let wsUrl;
  let storage;
  const testStorePath = path.resolve(process.cwd(), `.test-store-${Date.now()}.json`);

  before(async () => {
    if (fs.existsSync(testStorePath)) fs.unlinkSync(testStorePath);
    const app = express();
    server = http.createServer(app);
    app.use(cors());
    app.use(express.json());

    storage = await createStorage(undefined); // Using FileStorageAdapter
    // Set custom isolated file
    storage["filePath"] = testStorePath;
    const storageService = new LocalDiskStorageService("http://127.0.0.1:8788");
    const relay = new WebSocketRelayManager(server, storage);
    const router = createRouter(storage, storageService, relay, "http://127.0.0.1:8788");
    app.use(router);

    await new Promise((resolve) => {
      server.listen(8788, "127.0.0.1", resolve);
    });

    baseUrl = "http://127.0.0.1:8788";
    wsUrl = "ws://127.0.0.1:8788";
  });

  after(async () => {
    await storage.close();
    await new Promise((resolve) => server.close(resolve));
    if (fs.existsSync(testStorePath)) fs.unlinkSync(testStorePath);
    if (fs.existsSync(path.resolve(process.cwd(), ".clipsync-store.json"))) {
      try { fs.unlinkSync(path.resolve(process.cwd(), ".clipsync-store.json")); } catch {}
    }
  });

  test("GET /health returns status 200 and protocol info", async () => {
    const res = await fetch(`${baseUrl}/health`);
    assert.equal(res.status, 200);
    const json = await res.json();
    assert.equal(json.ok, true);
    assert.equal(json.protocolVersion, 1);
  });

  let deviceA;
  let deviceB;

  test("POST /v1/devices/register registers primary device (MacBook Air)", async () => {
    const res = await fetch(`${baseUrl}/v1/devices/register`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        label: "MacBook Air M5",
        platform: "macos",
      }),
    });
    assert.equal(res.status, 201);
    deviceA = await res.json();
    assert.ok(deviceA.deviceId);
    assert.ok(deviceA.token);
    assert.ok(deviceA.syncSalt);
    assert.equal(deviceA.label, "MacBook Air M5");
  });

  test("Pairing flow: Device A creates session, Device B pairs with 6-digit code", async () => {
    // 1. Device A creates pairing session
    const sessionRes = await fetch(`${baseUrl}/v1/auth/pairing/session`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${deviceA.token}`,
      },
    });
    assert.equal(sessionRes.status, 201);
    const session = await sessionRes.json();
    assert.ok(session.code);
    assert.ok(session.qrDataUrl);
    assert.equal(session.code.length, 6);

    // 2. Device B (Windows PC) claims pairing code
    const pairRes = await fetch(`${baseUrl}/v1/auth/pair`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        code: session.code,
        label: "Windows 10 PC",
        platform: "windows",
      }),
    });
    assert.equal(pairRes.status, 201);
    deviceB = await pairRes.json();
    assert.ok(deviceB.deviceId);
    assert.ok(deviceB.token);
    assert.equal(deviceB.groupId, deviceA.groupId);
    assert.equal(deviceB.syncSalt, deviceA.syncSalt);
  });

  test("GET /v1/devices lists paired devices", async () => {
    const res = await fetch(`${baseUrl}/v1/devices`, {
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.devices.length, 2);
    const labels = body.devices.map((d) => d.label);
    assert.ok(labels.includes("MacBook Air M5"));
    assert.ok(labels.includes("Windows 10 PC"));
  });

  test("End-to-End WebSocket sync: Mac sends encrypted text -> Windows receives and decrypts", async () => {
    const syncSecret = "my-secure-master-clipboard-key!";
    const key = deriveKey(syncSecret, deviceA.syncSalt);

    const wsA = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(deviceA.token)}`);
    const wsB = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(deviceB.token)}`);

    await Promise.all([
      new Promise((res) => wsA.on("open", res)),
      new Promise((res) => wsB.on("open", res)),
    ]);

    const originalText = "git commit -m 'Implement universal clipboard sync'";
    const envelope = encryptText(originalText, key);

    const testItem = makeSyncItem({
      itemId: "test-clip-item-001",
      type: "text",
      sourceDeviceId: deviceA.deviceId,
      payload: JSON.stringify(envelope),
    });

    const bReceivedPromise = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("Timeout waiting for sync on Device B")), 4000);
      wsB.on("message", (raw) => {
        const msg = JSON.parse(raw.toString());
        if (msg.type === "sync:item") {
          clearTimeout(timeout);
          resolve(msg);
        }
      });
    });

    // Send from Mac (wsA)
    wsA.send(JSON.stringify({ type: "sync:item", item: testItem }));

    const receivedMsg = await bReceivedPromise;
    assert.equal(receivedMsg.item.itemId, "test-clip-item-001");
    assert.equal(receivedMsg.item.sourceDeviceId, deviceA.deviceId);
    assert.ok(receivedMsg.sequenceNumber > 0);

    // Decrypt on Windows (Device B)
    const decrypted = decryptText(receivedMsg.item.payload, key);
    assert.equal(decrypted, originalText);

    wsA.close();
    wsB.close();
  });

  test("GET /v1/history retrieves encrypted clipboard history", async () => {
    const res = await fetch(`${baseUrl}/v1/history`, {
      headers: { authorization: `Bearer ${deviceB.token}` },
    });
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.ok(body.items.length >= 1);
    assert.equal(body.items[0].itemId, "test-clip-item-001");
  });

  test("Blob storage URL generation for files/images", async () => {
    const uploadRes = await fetch(`${baseUrl}/v1/storage/upload-url`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${deviceA.token}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({
        fileName: "screenshot.png",
        mimeType: "image/png",
        sizeBytes: 1024,
        sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      }),
    });
    assert.equal(uploadRes.status, 200);
    const uploadData = await uploadRes.json();
    assert.ok(uploadData.uploadUrl);
    assert.ok(uploadData.storageRef);

    // Test download URL
    const downloadRes = await fetch(`${baseUrl}/v1/storage/download-url/${encodeURIComponent(uploadData.storageRef)}`, {
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    assert.equal(downloadRes.status, 200);
    const downloadData = await downloadRes.json();
    assert.ok(downloadData.downloadUrl);
  });

  test("Complete E2E binary image upload, sync, download and integrity check", async () => {
    const syncSecret = "my-secure-master-clipboard-key!";
    const key = deriveKey(syncSecret, deviceA.syncSalt);

    const clientA = new ClipSyncClient({
      serverUrl: baseUrl,
      deviceId: deviceA.deviceId,
      token: deviceA.token,
      syncKey: key,
    });

    const clientB = new ClipSyncClient({
      serverUrl: baseUrl,
      deviceId: deviceB.deviceId,
      token: deviceB.token,
      syncKey: key,
    });

    const readyPromise = Promise.all([
      new Promise((resolve) => clientA.on("ready", resolve)),
      new Promise((resolve) => clientB.on("ready", resolve)),
    ]);

    clientA.connect();
    clientB.connect();

    await readyPromise;

    // Raw image bytes simulation
    const rawImageBytes = Buffer.from("PNG_IMAGE_DATA_0123456789_ABCDEF", "utf8");
    const rawSha = computeSha256(rawImageBytes);

    const receivedPromise = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("Timeout waiting for binary sync item")), 4000);
      clientB.on("sync:item", ({ item }) => {
        if (item.type === "image") {
          clearTimeout(timeout);
          resolve(item);
        }
      });
    });

    const item = await clientA.broadcastBinaryFile(rawImageBytes, "test-screenshot.png", "image/png", "image");
    assert.ok(item);
    assert.equal(item.type, "image");

    const receivedItem = await receivedPromise;
    assert.equal(receivedItem.itemId, item.itemId);
    assert.equal(receivedItem.metadata.fileName, "test-screenshot.png");

    // Download and decrypt using clientB
    const envelope = JSON.parse(receivedItem.payload);
    const downloadedBytes = await clientB.fetchAndDecryptBinaryFile(
      receivedItem.storageRef,
      envelope.iv,
      envelope.tag,
      rawSha
    );

    assert.deepEqual(Buffer.from(downloadedBytes), rawImageBytes);

    clientA.disconnect();
    clientB.disconnect();
  });
});
