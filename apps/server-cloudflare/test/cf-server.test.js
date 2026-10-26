import { test, describe, before, after } from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { unstable_dev } from "wrangler";
import { WebSocket } from "ws";
import { deriveKey, encryptText, decryptText } from "@clipsync/crypto";
import { makeSyncItem } from "@clipsync/protocol";

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
});
