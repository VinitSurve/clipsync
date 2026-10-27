import { test, describe, before, after } from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { unstable_dev } from "wrangler";
import { WebSocket } from "ws";
import { deriveKey, encryptText, decryptText } from "@clipsync/crypto";
import {
  makeSyncItem,
  MAX_TRANSFER_BYTES,
  MAX_TRANSFER_RETENTION_MS,
  PROTOCOL_VERSION,
} from "@clipsync/protocol";

import fs from "node:fs";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, "..");
const testPersistDir = path.resolve(projectRoot, `.test-wrangler-state-${Date.now()}`);

describe("ClipSync Cloudflare Worker Integration Tests", () => {
  let worker;
  let baseUrl;
  let wsUrl;

  before(async () => {
    if (fs.existsSync(testPersistDir)) {
      fs.rmSync(testPersistDir, { recursive: true, force: true });
    }
    fs.mkdirSync(testPersistDir, { recursive: true });

    // Start local Cloudflare worker via wrangler unstable_dev with isolated persistence
    worker = await unstable_dev(path.resolve(projectRoot, "src/index.ts"), {
      config: path.resolve(projectRoot, "wrangler.jsonc"),
      experimental: { disableExperimentalWarning: true },
      ip: "127.0.0.1",
      persistTo: testPersistDir,
    });

    baseUrl = `http://${worker.address}:${worker.port}`;
    wsUrl = `ws://${worker.address}:${worker.port}`;
  });

  after(async () => {
    if (worker) {
      await worker.stop();
    }
    if (fs.existsSync(testPersistDir)) {
      fs.rmSync(testPersistDir, { recursive: true, force: true });
    }
  });

  test("GET /health returns status 200 and protocol info", async () => {
    const res = await fetch(`${baseUrl}/health`);
    assert.equal(res.status, 200);
    const json = await res.json();
    assert.equal(json.ok, true);
    assert.equal(json.service, "clipsync-cloudflare-worker");
    assert.equal(json.protocolVersion, 1);
  });

  test("Path normalization: handles redundant slashes gracefully (e.g. //health)", async () => {
    const res = await fetch(`${baseUrl}//health`);
    assert.equal(res.status, 200);
    const json = await res.json();
    assert.equal(json.ok, true);
  });

  let deviceA;
  let deviceB;

  test("POST /v1/devices/register registers primary device (MacBook Air M5)", async () => {
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
    assert.equal(deviceA.platform, "macos");
    assert.equal(deviceA.groupId, "clipsync-default-group");
  });

  test("Authentication: reject invalid Bearer token on protected routes", async () => {
    const res = await fetch(`${baseUrl}/v1/devices`, {
      headers: { authorization: "Bearer invalid-token-xyz" },
    });
    assert.equal(res.status, 401);
  });

  test("Authentication: accept valid Bearer token on protected routes", async () => {
    const res = await fetch(`${baseUrl}/v1/devices`, {
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.devices.length, 1);
    assert.equal(body.devices[0].deviceId, deviceA.deviceId);
    assert.equal(body.devices[0].isCurrent, true);
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
    assert.ok(session.sessionId);
    assert.ok(session.code);
    assert.ok(session.qrData);
    assert.match(session.code, /^\d{6}$/);

    // 2. Device B (Windows 10 PC) claims pairing code
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

    // 3. Trying to claim the same code again fails
    const rePairRes = await fetch(`${baseUrl}/v1/auth/pair`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        code: session.code,
        label: "Attacker Device",
        platform: "android",
      }),
    });
    assert.equal(rePairRes.status, 400);

    // 4. Verify both devices are now listed
    const devListRes = await fetch(`${baseUrl}/v1/devices`, {
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    const devList = await devListRes.json();
    assert.equal(devList.devices.length, 2);
    const labels = devList.devices.map((d) => d.label);
    assert.ok(labels.includes("MacBook Air M5"));
    assert.ok(labels.includes("Windows 10 PC"));
  });

  test("WebSocket: reject unauthorized connection without token", async () => {
    const ws = new WebSocket(`${wsUrl}/v1/events`);
    const closed = await new Promise((resolve) => {
      ws.on("error", () => {});
      ws.on("unexpected-response", (_req, res) => {
        resolve(res.statusCode);
      });
      ws.on("close", (code) => resolve(code));
    });
    assert.ok(closed === 401 || closed === 1008);
  });

  test("End-to-End WebSocket text sync: Mac sends encrypted text -> Windows receives and decrypts", async () => {
    const syncSecret = "my-super-secret-master-sync-passphrase!";
    const key = deriveKey(syncSecret, deviceA.syncSalt);

    const wsA = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(deviceA.token)}`);
    const wsB = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(deviceB.token)}`);

    const connectionPromises = [
      new Promise((res, rej) => {
        wsA.once("message", (raw) => {
          const msg = JSON.parse(raw.toString());
          if (msg.type === "connected") res(msg);
          else rej(new Error(`Expected connected, got: ${raw}`));
        });
        wsA.once("error", rej);
      }),
      new Promise((res, rej) => {
        wsB.once("message", (raw) => {
          const msg = JSON.parse(raw.toString());
          if (msg.type === "connected") res(msg);
          else rej(new Error(`Expected connected, got: ${raw}`));
        });
        wsB.once("error", rej);
      }),
    ];

    await Promise.all(connectionPromises);

    const originalText = "Hello from MacBook Air M5 to Windows 10 PC via Cloudflare Durable Objects!";
    const envelope = encryptText(originalText, key);

    const testItem = makeSyncItem({
      itemId: "cf-test-item-001",
      type: "text",
      sourceDeviceId: deviceA.deviceId,
      payload: JSON.stringify(envelope),
    });

    const bReceivedPromise = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("Timeout waiting for sync:item on Device B")), 5000);
      wsB.on("message", (raw) => {
        try {
          const msg = JSON.parse(raw.toString());
          if (msg.type === "sync:item" && msg.item.itemId === "cf-test-item-001") {
            clearTimeout(timeout);
            resolve(msg);
          }
        } catch (e) {
          reject(e);
        }
      });
    });

    const aAckPromise = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("Timeout waiting for sync:ack on Device A")), 5000);
      wsA.on("message", (raw) => {
        try {
          const msg = JSON.parse(raw.toString());
          if (msg.type === "sync:ack" && msg.itemId === "cf-test-item-001") {
            clearTimeout(timeout);
            resolve(msg);
          }
        } catch (e) {
          reject(e);
        }
      });
    });

    // Device A sends the encrypted sync item
    wsA.send(JSON.stringify({ type: "sync:item", item: testItem }));

    const [ackMsg, bReceivedMsg] = await Promise.all([aAckPromise, bReceivedPromise]);

    assert.equal(ackMsg.type, "sync:ack");
    assert.equal(ackMsg.itemId, "cf-test-item-001");
    assert.ok(ackMsg.sequenceNumber > 0);

    assert.equal(bReceivedMsg.type, "sync:item");
    assert.equal(bReceivedMsg.item.itemId, "cf-test-item-001");
    assert.equal(bReceivedMsg.item.sourceDeviceId, deviceA.deviceId);

    // Device B decrypts payload using the shared key
    const receivedEnvelope = JSON.parse(bReceivedMsg.item.payload);
    const decryptedText = decryptText(receivedEnvelope, key);
    assert.equal(decryptedText, originalText);

    wsA.close();
    wsB.close();
  });

  test("History API: retrieve synced items and clear history", async () => {
    const histRes = await fetch(`${baseUrl}/v1/history`, {
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    assert.equal(histRes.status, 200);
    const hist = await histRes.json();
    assert.ok(Array.isArray(hist.items));
    assert.ok(hist.items.length >= 1);
    assert.equal(hist.items[0].itemId, "cf-test-item-001");

    // Clear history
    const clearRes = await fetch(`${baseUrl}/v1/history/clear`, {
      method: "POST",
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    assert.equal(clearRes.status, 200);

    const emptyHistRes = await fetch(`${baseUrl}/v1/history`, {
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    const emptyHist = await emptyHistRes.json();
    assert.equal(emptyHist.items.length, 0);
  });

  test("Device Revocation: revoking a device severs authorization and access", async () => {
    // Device A revokes Device B
    const revokeRes = await fetch(`${baseUrl}/v1/devices/${deviceB.deviceId}`, {
      method: "DELETE",
      headers: { authorization: `Bearer ${deviceA.token}` },
    });
    assert.equal(revokeRes.status, 200);

    // Device B tries to query devices -> 401
    const testRes = await fetch(`${baseUrl}/v1/devices`, {
      headers: { authorization: `Bearer ${deviceB.token}` },
    });
    assert.equal(testRes.status, 401);

    // Device B tries to connect WebSocket -> rejected
    const ws = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(deviceB.token)}`);
    const rejected = await new Promise((resolve) => {
      ws.on("error", () => {});
      ws.on("unexpected-response", (_req, res) => resolve(res.statusCode));
      ws.on("close", (code) => resolve(code));
    });
    assert.ok(rejected === 401 || rejected === 1008);
  });

  describe("Phase 2A: Temporary Storage & Transfer Lifecycle", () => {
    let transferDeviceA;
    let transferDeviceB;

    before(async () => {
      // Revoke leftover deviceA from previous tests to isolate Phase 2A mesh
      if (deviceA?.deviceId) {
        await fetch(`${baseUrl}/v1/devices/${deviceA.deviceId}`, {
          method: "DELETE",
          headers: { authorization: `Bearer ${deviceA.token}` },
        });
      }

      // Register Device A (Mac)
      const resA = await fetch(`${baseUrl}/v1/devices/register`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ label: "MacBook Air Phase 2A", platform: "macos" }),
      });
      transferDeviceA = await resA.json();

      // Pair Device B (Windows)
      const sessRes = await fetch(`${baseUrl}/v1/auth/pairing/session`, {
        method: "POST",
        headers: { authorization: `Bearer ${transferDeviceA.token}` },
      });
      const session = await sessRes.json();

      const resB = await fetch(`${baseUrl}/v1/auth/pair`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          code: session.code,
          label: "Windows 10 PC Phase 2A",
          platform: "windows",
        }),
      });
      transferDeviceB = await resB.json();
    });

    test("POST /v1/transfers/init rejects files exceeding 100 MB limit", async () => {
      const oversizedManifest = {
        version: PROTOCOL_VERSION,
        transferId: "over-100mb-" + Date.now(),
        senderDeviceId: transferDeviceA.deviceId,
        fileName: "giant_archive.zip",
        fileSizeBytes: MAX_TRANSFER_BYTES + 1024, // 100 MB + 1 KB
        mimeType: "application/zip",
        sha256Hash: "1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff",
        encryptionIv: "YWJjZGVmZ2hpams=",
        encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
        medium: "google_drive",
        driveFileId: "drive_oversized_123",
        createdAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + 3600000).toISOString(),
        status: "ready",
      };

      const res = await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ manifest: oversizedManifest }),
      });
      assert.equal(res.status, 400);
      const json = await res.json();
      assert.match(json.error, /exceeds maximum limit of 104857600 bytes/);
    });

    test("POST /v1/transfers/init rejects retention exceeding 24 hours", async () => {
      const now = Date.now();
      const longRetentionManifest = {
        version: PROTOCOL_VERSION,
        transferId: "retention-over-24h-" + Date.now(),
        senderDeviceId: transferDeviceA.deviceId,
        fileName: "photo.jpg",
        fileSizeBytes: 2 * 1024 * 1024,
        mimeType: "image/jpeg",
        sha256Hash: "aaaabbbbccccddddeeeeffff0000111122223333444455556666777788889999",
        encryptionIv: "YWJjZGVmZ2hpams=",
        encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
        medium: "google_drive",
        driveFileId: "drive_photo_123",
        createdAt: new Date(now).toISOString(),
        expiresAt: new Date(now + 25 * 60 * 60 * 1000).toISOString(), // 25 hours
        status: "ready",
      };

      const res = await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ manifest: longRetentionManifest }),
      });
      assert.equal(res.status, 400);
      const json = await res.json();
      assert.match(json.error, /exceeds maximum retention of 24 hours/);
    });

    let liveTransferId;
    const liveDriveFileId = "drive_enc_blob_uuid_777";

    test("POST /v1/transfers/init registers valid transfer and broadcasts transfer:announced", async () => {
      liveTransferId = "transfer-" + Date.now();

      const wsB = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(transferDeviceB.token)}`);
      await new Promise((resolve) => wsB.once("open", resolve));

      const announcementPromise = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("Timeout waiting for transfer:announced")), 5000);
        wsB.on("message", (raw) => {
          try {
            const msg = JSON.parse(raw.toString());
            if (msg.type === "transfer:announced" && msg.manifest.transferId === liveTransferId) {
              clearTimeout(timeout);
              resolve(msg);
            }
          } catch (e) {
            reject(e);
          }
        });
      });

      const validManifest = {
        version: PROTOCOL_VERSION,
        transferId: liveTransferId,
        senderDeviceId: transferDeviceA.deviceId,
        fileName: "design_mockup.png",
        fileSizeBytes: 5 * 1024 * 1024, // 5 MB
        mimeType: "image/png",
        sha256Hash: "222233334444555566667777888899990000aaaabbbbccccddddeeeeffff1111",
        encryptionIv: "YWJjZGVmZ2hpams=",
        encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
        medium: "google_drive",
        storageReference: liveDriveFileId,
        driveFileId: liveDriveFileId,
        createdAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + 2 * 60 * 60 * 1000).toISOString(), // 2 hours
        status: "ready",
      };

      const res = await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ manifest: validManifest }),
      });
      assert.equal(res.status, 201);
      const json = await res.json();
      assert.equal(json.ok, true);
      assert.equal(json.duplicate, false);
      assert.equal(json.manifest.transferId, liveTransferId);

      const announced = await announcementPromise;
      assert.equal(announced.manifest.transferId, liveTransferId);
      assert.equal(announced.manifest.driveFileId, liveDriveFileId);

      wsB.close();
    });

    test("Duplicate transfer detection: re-init with same content returns existing manifest", async () => {
      const duplicateAttempt = {
        version: PROTOCOL_VERSION,
        transferId: "new-transfer-id-" + Date.now(),
        senderDeviceId: transferDeviceA.deviceId,
        fileName: "design_mockup.png",
        fileSizeBytes: 5 * 1024 * 1024,
        mimeType: "image/png",
        sha256Hash: "222233334444555566667777888899990000aaaabbbbccccddddeeeeffff1111", // same hash
        encryptionIv: "YWJjZGVmZ2hpams=",
        encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
        medium: "google_drive",
        driveFileId: "different_drive_id",
        createdAt: new Date().toISOString(),
        expiresAt: new Date(Date.now() + 3600000).toISOString(),
        status: "ready",
      };

      const res = await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ manifest: duplicateAttempt }),
      });
      assert.equal(res.status, 200);
      const json = await res.json();
      assert.equal(json.ok, true);
      assert.equal(json.duplicate, true);
      assert.equal(json.manifest.transferId, liveTransferId);
    });

    test("GET /v1/transfers/pending returns active transfers for offline devices", async () => {
      const res = await fetch(`${baseUrl}/v1/transfers/pending`, {
        headers: { authorization: `Bearer ${transferDeviceB.token}` },
      });
      assert.equal(res.status, 200);
      const json = await res.json();
      assert.ok(Array.isArray(json.transfers));
      const found = json.transfers.find((t) => t.transferId === liveTransferId);
      assert.ok(found);
      assert.equal(found.fileName, "design_mockup.png");
    });

    test("POST /v1/transfers/:id/ack marks transfer delivered and signals immediate Drive deletion", async () => {
      const wsA = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(transferDeviceA.token)}`);
      await new Promise((resolve) => wsA.once("open", resolve));

      const deliveredPromise = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("Timeout waiting for transfer:delivered")), 5000);
        wsA.on("message", (raw) => {
          try {
            const msg = JSON.parse(raw.toString());
            if (msg.type === "transfer:delivered" && msg.transferId === liveTransferId) {
              clearTimeout(timeout);
              resolve(msg);
            }
          } catch (e) {
            reject(e);
          }
        });
      });

      const ackRes = await fetch(`${baseUrl}/v1/transfers/${encodeURIComponent(liveTransferId)}/ack`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceB.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ recipientDeviceId: transferDeviceB.deviceId }),
      });
      assert.equal(ackRes.status, 200);
      const ackJson = await ackRes.json();
      assert.equal(ackJson.ok, true);
      assert.equal(ackJson.allDelivered, true);
      assert.equal(ackJson.shouldDelete, true);
      assert.equal(ackJson.driveFileId, liveDriveFileId);

      const deliveredMsg = await deliveredPromise;
      assert.equal(deliveredMsg.transferId, liveTransferId);
      assert.equal(deliveredMsg.recipientDeviceId, transferDeviceB.deviceId);
      assert.equal(deliveredMsg.allDelivered, true);
      assert.equal(deliveredMsg.shouldDelete, true);
      assert.equal(deliveredMsg.driveFileId, liveDriveFileId);

      wsA.close();
    });

    test("Multi-device offline retention: file stays in Drive until offline Android device reconnects and ACKs", async () => {
      // 1. Pair Device C (Android) into the mesh
      const sessRes = await fetch(`${baseUrl}/v1/auth/pairing/session`, {
        method: "POST",
        headers: { authorization: `Bearer ${transferDeviceA.token}` },
      });
      const session = await sessRes.json();

      const resC = await fetch(`${baseUrl}/v1/auth/pair`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          code: session.code,
          label: "Pixel 9 Pro (Android)",
          platform: "android",
        }),
      });
      assert.equal(resC.status, 201);
      const transferDeviceC = await resC.json();

      // 2. Mac initiates a new transfer
      const multiTransferId = "multi-transfer-" + Date.now();
      const multiDriveId = "drive_multi_device_file_456";

      const initRes = await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({
          manifest: {
            version: PROTOCOL_VERSION,
            transferId: multiTransferId,
            senderDeviceId: transferDeviceA.deviceId,
            fileName: "project_archive.tar.gz",
            fileSizeBytes: 20 * 1024 * 1024, // 20 MB
            mimeType: "application/gzip",
            sha256Hash: "555566667777888899990000aaaabbbbccccddddeeeeffff1111222233334444",
            encryptionIv: "YWJjZGVmZ2hpams=",
            encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
            medium: "google_drive",
            driveFileId: multiDriveId,
            createdAt: new Date().toISOString(),
            expiresAt: new Date(Date.now() + 12 * 60 * 60 * 1000).toISOString(), // 12 hours
            status: "ready",
          },
        }),
      });
      assert.equal(initRes.status, 201);

      // 3. Windows (Device B) is online, receives, and ACKs
      const ackResB = await fetch(`${baseUrl}/v1/transfers/${encodeURIComponent(multiTransferId)}/ack`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceB.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ recipientDeviceId: transferDeviceB.deviceId }),
      });
      assert.equal(ackResB.status, 200);
      const ackJsonB = await ackResB.json();

      // CRITICAL REQUIREMENT VERIFICATION:
      // Since Android (Device C) is still offline, allDelivered MUST be false and shouldDelete MUST be false!
      assert.equal(ackJsonB.allDelivered, false);
      assert.equal(ackJsonB.shouldDelete, false);

      // Verify transfer remains available in pending transfers
      const pendingRes1 = await fetch(`${baseUrl}/v1/transfers/pending`, {
        headers: { authorization: `Bearer ${transferDeviceC.token}` },
      });
      const pendingJson1 = await pendingRes1.json();
      assert.ok(pendingJson1.transfers.some((t) => t.transferId === multiTransferId));

      // 4. Android (Device C) reconnects and ACKs
      const ackResC = await fetch(`${baseUrl}/v1/transfers/${encodeURIComponent(multiTransferId)}/ack`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceC.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({ recipientDeviceId: transferDeviceC.deviceId }),
      });
      assert.equal(ackResC.status, 200);
      const ackJsonC = await ackResC.json();

      // ALL registered peer devices have now delivered:
      // allDelivered becomes true and immediate Drive deletion is signaled!
      assert.equal(ackJsonC.allDelivered, true);
      assert.equal(ackJsonC.shouldDelete, true);
      assert.equal(ackJsonC.driveFileId, multiDriveId);

      // Transfer is now delivered, so pending transfers no longer lists it
      const pendingRes2 = await fetch(`${baseUrl}/v1/transfers/pending`, {
        headers: { authorization: `Bearer ${transferDeviceC.token}` },
      });
      const pendingJson2 = await pendingRes2.json();
      assert.ok(!pendingJson2.transfers.some((t) => t.transferId === multiTransferId));
    });

    test("24-Hour Expiry Sweep: expired transfers transition to expired and broadcast transfer:expired", async () => {
      const expiredTransferId = "expired-transfer-" + Date.now();
      const expiredDriveFileId = "drive_expired_file_999";
      const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
      const oneMinuteAgo = new Date(Date.now() - 60 * 1000).toISOString();

      const wsB = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(transferDeviceB.token)}`);
      await new Promise((resolve) => wsB.once("open", resolve));

      const expiredPromise = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("Timeout waiting for transfer:expired")), 5000);
        wsB.on("message", (raw) => {
          try {
            const msg = JSON.parse(raw.toString());
            if (msg.type === "transfer:expired" && msg.transferId === expiredTransferId) {
              clearTimeout(timeout);
              resolve(msg);
            }
          } catch (e) {
            reject(e);
          }
        });
      });

      // Init transfer that was created 24h ago and expired 1 minute ago
      const res = await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({
          manifest: {
            version: PROTOCOL_VERSION,
            transferId: expiredTransferId,
            senderDeviceId: transferDeviceA.deviceId,
            fileName: "stale_clipboard_file.dat",
            fileSizeBytes: 1024,
            mimeType: "application/octet-stream",
            sha256Hash: "33334444555566667777888899990000aaaabbbbccccddddeeeeffff11112222",
            encryptionIv: "YWJjZGVmZ2hpams=",
            encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
            medium: "google_drive",
            driveFileId: expiredDriveFileId,
            createdAt: twentyFourHoursAgo,
            expiresAt: oneMinuteAgo,
            status: "ready",
          },
        }),
      });
      assert.equal(res.status, 201);

      // Trigger the sweep
      const sweepRes = await fetch(`${baseUrl}/v1/transfers/sweep`, {
        method: "POST",
        headers: { authorization: `Bearer ${transferDeviceA.token}` },
      });
      assert.equal(sweepRes.status, 200);

      const expiredMsg = await expiredPromise;
      assert.equal(expiredMsg.transferId, expiredTransferId);
      assert.equal(expiredMsg.driveFileId, expiredDriveFileId);

      wsB.close();
    });

    test("DELETE /v1/transfers/:id cleans up orphaned/cancelled transfers and broadcasts transfer:deleted", async () => {
      const cancelTransferId = "cancel-transfer-" + Date.now();
      const cancelDriveId = "drive_cancel_file_888";

      const wsB = new WebSocket(`${wsUrl}/v1/events?token=${encodeURIComponent(transferDeviceB.token)}`);
      await new Promise((resolve) => wsB.once("open", resolve));

      const deletedPromise = new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("Timeout waiting for transfer:deleted")), 5000);
        wsB.on("message", (raw) => {
          try {
            const msg = JSON.parse(raw.toString());
            if (msg.type === "transfer:deleted" && msg.transferId === cancelTransferId) {
              clearTimeout(timeout);
              resolve(msg);
            }
          } catch (e) {
            reject(e);
          }
        });
      });

      // Init transfer
      await fetch(`${baseUrl}/v1/transfers/init`, {
        method: "POST",
        headers: {
          authorization: `Bearer ${transferDeviceA.token}`,
          "content-type": "application/json",
        },
        body: JSON.stringify({
          manifest: {
            version: PROTOCOL_VERSION,
            transferId: cancelTransferId,
            senderDeviceId: transferDeviceA.deviceId,
            fileName: "cancelled_transfer.dat",
            fileSizeBytes: 2048,
            mimeType: "application/octet-stream",
            sha256Hash: "4444555566667777888899990000aaaabbbbccccddddeeeeffff111122223333",
            encryptionIv: "YWJjZGVmZ2hpams=",
            encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
            medium: "google_drive",
            driveFileId: cancelDriveId,
            createdAt: new Date().toISOString(),
            expiresAt: new Date(Date.now() + 3600000).toISOString(),
            status: "ready",
          },
        }),
      });

      // Delete transfer
      const delRes = await fetch(`${baseUrl}/v1/transfers/${encodeURIComponent(cancelTransferId)}`, {
        method: "DELETE",
        headers: { authorization: `Bearer ${transferDeviceA.token}` },
      });
      assert.equal(delRes.status, 200);
      const delJson = await delRes.json();
      assert.equal(delJson.ok, true);
      assert.equal(delJson.transferId, cancelTransferId);
      assert.equal(delJson.driveFileId, cancelDriveId);

      const deletedMsg = await deletedPromise;
      assert.equal(deletedMsg.transferId, cancelTransferId);
      assert.equal(deletedMsg.driveFileId, cancelDriveId);

      wsB.close();
    });
  });
});

