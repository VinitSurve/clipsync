import { test, describe } from "node:test";
import assert from "node:assert/strict";
import { deriveKey, encryptText, decryptText, encryptBinary, decryptBinary, computeSha256 } from "../src/index.ts";

describe("packages/crypto", () => {
  const passphrase = "my-secret-sync-password-12345!";
  const salt = "test-salt-string-456";
  const key = deriveKey(passphrase, salt);

  test("derives a 32-byte key", () => {
    assert.equal(key.length, 32);
    // Key derivation should be deterministic given the same passphrase and salt
    const key2 = deriveKey(passphrase, salt);
    assert.deepEqual(key, key2);
  });

  test("encrypts and decrypts text accurately", () => {
    const originalText = "Hello from macOS! Testing ClipSync universal clipboard: 🚀 https://github.com";
    const envelope = encryptText(originalText, key);

    assert.ok(envelope.iv);
    assert.ok(envelope.ciphertext);
    assert.ok(envelope.tag);
    assert.notEqual(envelope.ciphertext, originalText);

    const decrypted = decryptText(envelope, key);
    assert.equal(decrypted, originalText);
  });

  test("fails decryption with wrong key or tampered ciphertext", () => {
    const originalText = "Sensitive code snippet";
    const envelope = encryptText(originalText, key);

    const wrongKey = deriveKey("wrong-password", salt);
    assert.throws(() => {
      decryptText(envelope, wrongKey);
    });

    // Tampered ciphertext
    const tampered = { ...envelope, ciphertext: envelope.ciphertext.slice(0, -2) + "==" };
    assert.throws(() => {
      decryptText(tampered, key);
    });
  });

  test("encrypts and decrypts binary data (image/file simulation)", () => {
    const binaryData = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x42, 0xff]);
    const { ciphertext, iv, tag, sha256 } = encryptBinary(binaryData, key);

    assert.equal(sha256, computeSha256(binaryData));
    assert.notDeepEqual(ciphertext, binaryData);

    const decrypted = decryptBinary(ciphertext, key, iv, tag);
    assert.deepEqual(decrypted, binaryData);
  });
});
