import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { ClipSyncClient } from "../dist/index.js";
import { deriveKey } from "@clipsync/crypto";

describe("packages/sync-client", () => {
  const syncKey = deriveKey("test-secret", "test-salt");

  test("initializes in disconnected state and can be paused/resumed", () => {
    const client = new ClipSyncClient({
      serverUrl: "http://localhost:8787",
      deviceId: "dev-mac-001",
      token: "dummy-token",
      syncKey,
      autoReconnect: false,
    });

    assert.equal(client.getState(), "disconnected");
    assert.equal(client.isSyncPaused(), false);

    client.pauseSync();
    assert.equal(client.isSyncPaused(), true);
    assert.equal(client.getState(), "paused");

    // When paused, broadcastText returns null
    const res = client.broadcastText("Hello world");
    assert.equal(res, null);

    client.resumeSync();
    assert.equal(client.isSyncPaused(), false);
  });

  test("buffers broadcast items in outbox when offline", () => {
    const client = new ClipSyncClient({
      serverUrl: "http://localhost:8787",
      deviceId: "dev-mac-001",
      token: "dummy-token",
      syncKey,
      autoReconnect: false,
    });

    const item = client.broadcastText("Quick copy text");
    assert.ok(item);
    assert.equal(item.type, "text");
    assert.equal(item.sourceDeviceId, "dev-mac-001");
    assert.ok(item.payload);
  });

  test("prevents loop echoes: ignores duplicate text sent repeatedly", () => {
    const client = new ClipSyncClient({
      serverUrl: "http://localhost:8787",
      deviceId: "dev-mac-001",
      token: "dummy-token",
      syncKey,
      autoReconnect: false,
    });

    const item1 = client.broadcastText("Same snippet");
    assert.ok(item1);

    // Second immediate broadcast of exact same content should be skipped
    const item2 = client.broadcastText("Same snippet");
    assert.equal(item2, null);
  });
});
