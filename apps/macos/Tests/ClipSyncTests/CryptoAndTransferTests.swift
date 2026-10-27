import XCTest
@testable import ClipSync

final class CryptoAndTransferTests: XCTestCase {
    var crypto: ClipSyncCrypto!

    override func setUpWithError() throws {
        try super.setUpWithError()
        crypto = try ClipSyncCrypto(passphrase: "test-master-sync-passphrase", salt: "test-salt-123456")
    }

    func testBinaryEncryptionAndDecryption() throws {
        // Generate 64 KB of dummy image data
        var originalBytes = [UInt8](repeating: 0, count: 64 * 1024)
        for i in 0..<originalBytes.count {
            originalBytes[i] = UInt8(i % 256)
        }
        let originalData = Data(originalBytes)

        // Encrypt binary
        let (ciphertext, iv, tag, sha256Hex) = try crypto.encryptBinary(originalData)

        XCTAssertFalse(ciphertext.isEmpty)
        XCTAssertFalse(iv.isEmpty)
        XCTAssertFalse(tag.isEmpty)
        XCTAssertEqual(sha256Hex.count, 64)

        // Decrypt binary with integrity check
        let decryptedData = try crypto.decryptBinary(ciphertext: ciphertext, iv: iv, tag: tag, expectedSha256: sha256Hex)
        XCTAssertEqual(decryptedData, originalData)
    }

    func testTamperedCiphertextRejection() throws {
        let originalData = "Important confidential payload".data(using: .utf8)!
        let (ciphertext, iv, tag, sha256Hex) = try crypto.encryptBinary(originalData)

        // Tamper 1 byte in ciphertext
        var tamperedCiphertext = ciphertext
        tamperedCiphertext[0] ^= 0xFF

        XCTAssertThrowsError(try crypto.decryptBinary(ciphertext: tamperedCiphertext, iv: iv, tag: tag, expectedSha256: sha256Hex))
    }

    func testTamperedTagRejection() throws {
        let originalData = "Secure clipboard image content".data(using: .utf8)!
        let (ciphertext, iv, tag, sha256Hex) = try crypto.encryptBinary(originalData)

        // Tamper tag
        let tamperedTag = "AAAAAAAA" + String(tag.dropFirst(8))

        XCTAssertThrowsError(try crypto.decryptBinary(ciphertext: ciphertext, iv: iv, tag: tamperedTag, expectedSha256: sha256Hex))
    }

    func testIntegrityCheckMismatchRejection() throws {
        let originalData = "Verify SHA-256 mismatch".data(using: .utf8)!
        let (ciphertext, iv, tag, _) = try crypto.encryptBinary(originalData)

        let wrongSha256 = "0000000000000000000000000000000000000000000000000000000000000000"

        XCTAssertThrowsError(try crypto.decryptBinary(ciphertext: ciphertext, iv: iv, tag: tag, expectedSha256: wrongSha256)) { error in
            guard let cryptoErr = error as? ClipSyncCrypto.CryptoError else {
                XCTFail("Expected CryptoError, got \(error)")
                return
            }
            XCTAssertEqual(cryptoErr, .integrityCheckFailed)
        }
    }

    func testTransferManifestInitialization() {
        let manifest = TransferManifest(
            senderDeviceId: "mac-device-test-1",
            fileName: "screenshot.png",
            fileSizeBytes: 2 * 1024 * 1024,
            mimeType: "image/png",
            sha256Hash: "1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff",
            encryptionIv: "YWJjZGVmZ2hpams=",
            encryptionTag: "MTIzNDU2Nzg5MDEyMzQ1Ng==",
            medium: .googleDrive,
            driveFileId: "drive_file_12345"
        )

        XCTAssertEqual(manifest.version, 1)
        XCTAssertEqual(manifest.fileName, "screenshot.png")
        XCTAssertEqual(manifest.fileSizeBytes, 2 * 1024 * 1024)
        XCTAssertEqual(manifest.medium, "google_drive")
        XCTAssertEqual(manifest.driveFileId, "drive_file_12345")
        XCTAssertEqual(manifest.status, "ready")
        XCTAssertFalse(manifest.transferId.isEmpty)
    }

    func testTransferRecordProperties() {
        let record = TransferRecord(
            id: "transfer-123",
            fileName: "archive.zip",
            fileSizeBytes: 15 * 1024 * 1024,
            mimeType: "application/zip",
            isOutgoing: true,
            status: .uploading,
            statusText: "Uploading to Drive"
        )

        XCTAssertTrue(record.canCancel)
        XCTAssertFalse(record.canRetry)
        XCTAssertFalse(record.isComplete)
        XCTAssertEqual(record.formattedSize, "15 MB")
    }

    func testBase64URLConversionRoundTrip() {
        let testBytes: [UInt8] = [0, 1, 2, 250, 253, 254, 255, 63, 62, 43, 47, 61]
        let original = Data(testBytes)

        let encoded = ClipSyncCrypto.toBase64URL(original)
        XCTAssertFalse(encoded.contains("+"))
        XCTAssertFalse(encoded.contains("/"))
        XCTAssertFalse(encoded.contains("="))

        let decoded = ClipSyncCrypto.fromBase64URL(encoded)
        XCTAssertEqual(decoded, original)
    }
}
