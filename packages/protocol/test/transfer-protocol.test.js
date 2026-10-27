import { test, describe } from "node:test";
import assert from "node:assert/strict";
import {
  MAX_TRANSFER_BYTES,
  MAX_TRANSFER_RETENTION_MS,
  validateTransferManifest,
  makeTransferManifest,
  PROTOCOL_VERSION,
} from "../dist/index.js";

describe("ClipSync Transfer Protocol Invariants", () => {
  const validBase = {
    transferId: "11111111-2222-3333-4444-555555555555",
    senderDeviceId: "device-mac-1",
    fileName: "screenshot.png",
    fileSizeBytes: 1024 * 1024, // 1 MB
    mimeType: "image/png",
    sha256Hash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    encryptionIv: "YWJjZGVmZ2hpams=",
    encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
    medium: "google_drive",
    driveFileId: "drive_file_abc123",
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 2 * 60 * 60 * 1000).toISOString(), // 2 hours
    status: "ready",
  };

  test("valid manifest passes validation", () => {
    const res = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
    });
    assert.equal(res.valid, true);
    if (res.valid) {
      assert.equal(res.manifest.transferId, validBase.transferId);
      assert.equal(res.manifest.fileSizeBytes, validBase.fileSizeBytes);
    }
  });

  test("makeTransferManifest attaches PROTOCOL_VERSION", () => {
    const manifest = makeTransferManifest(validBase);
    assert.equal(manifest.version, PROTOCOL_VERSION);
    assert.equal(manifest.fileName, "screenshot.png");
  });

  test("enforces strict 100 MB max limit", () => {
    // 100 MB exactly is allowed
    const atLimit = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      fileSizeBytes: MAX_TRANSFER_BYTES,
    });
    assert.equal(atLimit.valid, true);

    // 100 MB + 1 byte must be rejected
    const overLimit = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      fileSizeBytes: MAX_TRANSFER_BYTES + 1,
    });
    assert.equal(overLimit.valid, false);
    assert.match(overLimit.error, /exceeds maximum limit of 104857600 bytes/);
  });

  test("enforces strict 24-hour maximum retention", () => {
    const now = Date.now();
    // 24 hours is allowed
    const validRetention = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + MAX_TRANSFER_RETENTION_MS).toISOString(),
    });
    assert.equal(validRetention.valid, true);

    // 25 hours must be rejected
    const excessiveRetention = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + 25 * 60 * 60 * 1000).toISOString(),
    });
    assert.equal(excessiveRetention.valid, false);
    assert.match(excessiveRetention.error, /exceeds maximum retention of 24 hours/);
  });

  test("rejects expiresAt in the past relative to createdAt", () => {
    const now = Date.now();
    const pastExpires = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now - 1000).toISOString(),
    });
    assert.equal(pastExpires.valid, false);
    assert.match(pastExpires.error, /must be in the future/);
  });

  test("rejects invalid sha256 length", () => {
    const badHash = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      sha256Hash: "not-64-chars",
    });
    assert.equal(badHash.valid, false);
    assert.match(badHash.error, /64-character hex string/);
  });

  test("rejects unknown medium", () => {
    const badMedium = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      medium: "dropbox",
    });
    assert.equal(badMedium.valid, false);
    assert.match(badMedium.error, /medium must be one of/);
  });

  test("rejects unknown status", () => {
    const badStatus = validateTransferManifest({
      version: PROTOCOL_VERSION,
      ...validBase,
      status: "deleted_forever",
    });
    assert.equal(badStatus.valid, false);
    assert.match(badStatus.error, /status must be one of/);
  });
});
