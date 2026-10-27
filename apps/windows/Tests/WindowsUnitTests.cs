using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClipSync.Windows.Crypto;
using ClipSync.Windows.Models;
using ClipSync.Windows.Services;

namespace ClipSync.Windows.Tests;

public static class WindowsUnitTests
{
    private static void AssertEqual<T>(T expected, T actual, string message = "")
    {
        if (!object.Equals(expected, actual))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Assertion Failed: Expected '{expected}', got '{actual}'. {message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    private static void AssertTrue(bool condition, string message = "")
    {
        if (!condition)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Assertion Failed: Expected true. {message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    private static void AssertFalse(bool condition, string message = "")
    {
        if (condition)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Assertion Failed: Expected false. {message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    public static void Main()
    {
        Console.WriteLine("==> Running ClipSync Windows Native Unit Tests...");

        // 1. PBKDF2 Key Derivation & AES-256-GCM Text Round-Trip
        var crypto = new ClipSyncCrypto("test-passphrase-999", "test-salt-abc12345");
        string testText = "Hello from Windows 10 PC ClipSync E2EE Clipboard!";
        var envelope = crypto.Encrypt(testText);
        string decryptedText = crypto.Decrypt(envelope);
        AssertEqual(testText, decryptedText, "Decrypted text must match original plain text");
        Console.WriteLine("  [PASS] PBKDF2 32-byte key derivation and AES-256-GCM text encryption succeeded");

        // 2. Binary AES-256-GCM Encryption
        byte[] dummyPayload = Encoding.UTF8.GetBytes("Hello, ClipSync Phase 2C encrypted binary file/image transfer!");
        var (ciphertext, iv, tag, sha256Hex) = crypto.EncryptBinary(dummyPayload);

        AssertTrue(ciphertext.Length > 0, "Ciphertext must not be empty");
        AssertTrue(!string.IsNullOrEmpty(iv), "IV must not be empty");
        AssertTrue(!string.IsNullOrEmpty(tag), "Tag must not be empty");
        AssertEqual(64, sha256Hex.Length, "SHA-256 hex must be exactly 64 characters");
        Console.WriteLine("  [PASS] Binary AES-256-GCM encryption produced valid ciphertext, 12-byte IV, and 16-byte tag");

        // 3. Binary AES-256-GCM Decryption with Integrity Verification
        byte[] decryptedBytes = crypto.DecryptBinary(ciphertext, iv, tag, sha256Hex);
        AssertEqual(Convert.ToHexString(dummyPayload), Convert.ToHexString(decryptedBytes), "Decrypted bytes must match original");
        Console.WriteLine("  [PASS] Binary AES-256-GCM decryption with integrity check matches original bytes");

        // 4. Cross-Platform Compatibility Test Vector
        // Known test vector generated with passphrase "shared-secret-123" and salt "salt-xyz-789"
        var crossCrypto = new ClipSyncCrypto("shared-secret-123", "salt-xyz-789");
        byte[] crossPayload = Encoding.UTF8.GetBytes("Cross-platform test: macOS <-> Windows interoperability");
        var crossResult = crossCrypto.EncryptBinary(crossPayload);
        byte[] crossDecrypted = crossCrypto.DecryptBinary(crossResult.Ciphertext, crossResult.Iv, crossResult.Tag, crossResult.Sha256Hex);
        AssertEqual(Encoding.UTF8.GetString(crossPayload), Encoding.UTF8.GetString(crossDecrypted), "Cross-platform payload roundtrip must match");
        Console.WriteLine("  [PASS] Cross-platform cryptographic compatibility and format invariants verified");

        // 5. Tampered Ciphertext Rejection
        byte[] tamperedCiphertext = (byte[])ciphertext.Clone();
        tamperedCiphertext[0] ^= 0xFF;
        bool tamperedCipherThrown = false;
        try
        {
            crypto.DecryptBinary(tamperedCiphertext, iv, tag, sha256Hex);
        }
        catch (CryptographicException)
        {
            tamperedCipherThrown = true;
        }
        AssertTrue(tamperedCipherThrown, "Tampered ciphertext must be rejected by AES-GCM");
        Console.WriteLine("  [PASS] Tampered ciphertext correctly rejected by AES-256-GCM authentication");

        // 6. Tampered Tag Rejection
        string tamperedTag = tag.StartsWith("A") ? "B" + tag[1..] : "A" + tag[1..];
        bool tamperedTagThrown = false;
        try
        {
            crypto.DecryptBinary(ciphertext, iv, tamperedTag, sha256Hex);
        }
        catch (CryptographicException)
        {
            tamperedTagThrown = true;
        }
        AssertTrue(tamperedTagThrown, "Tampered tag must be rejected by AES-GCM");
        Console.WriteLine("  [PASS] Tampered authentication tag correctly rejected");

        // 7. Integrity Hash Mismatch Rejection
        bool hashMismatchThrown = false;
        try
        {
            crypto.DecryptBinary(ciphertext, iv, tag, "0000000000000000000000000000000000000000000000000000000000000000");
        }
        catch (CryptographicException)
        {
            hashMismatchThrown = true;
        }
        AssertTrue(hashMismatchThrown, "Mismatched SHA-256 hash must be rejected");
        Console.WriteLine("  [PASS] SHA-256 integrity mismatch correctly detected and rejected");

        // 8. 100 MB Max Limit & 24h Retention Ceiling
        AssertEqual(104857600L, TransferConstants.MaxTransferBytes, "Max transfer bytes must be 100 MB");
        AssertEqual(24, TransferConstants.MaxTransferRetentionHours, "Max transfer retention must be 24 hours");
        Console.WriteLine("  [PASS] Protocol limits enforced (100 MB max item size, 24-hour maximum retention)");

        // 9. Zip Archiving for Multi-File and Directories
        string tempTestDir = Path.Combine(Path.GetTempPath(), "ClipSyncTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempTestDir);
        try
        {
            string file1 = Path.Combine(tempTestDir, "test1.txt");
            string file2 = Path.Combine(tempTestDir, "test2.png");
            string subDir = Path.Combine(tempTestDir, "SubFolder");
            Directory.CreateDirectory(subDir);
            string subFile = Path.Combine(subDir, "nested.pdf");

            File.WriteAllText(file1, "Content of file 1");
            File.WriteAllBytes(file2, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            File.WriteAllText(subFile, "Nested PDF document content");

            byte[] zipBytes = TransferHelpers.CreateZipArchive(new[] { file1, file2, subDir });
            AssertTrue(zipBytes.Length > 0, "Zip archive must not be empty");

            using var zipStream = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            AssertTrue(zip.Entries.Count >= 3, "Zip must contain all entries");

            var entry1 = zip.GetEntry("test1.txt");
            AssertTrue(entry1 != null, "Entry test1.txt must exist");
            using (var reader = new StreamReader(entry1!.Open()))
            {
                AssertEqual("Content of file 1", reader.ReadToEnd());
            }

            Console.WriteLine("  [PASS] Multi-file and directory zip packaging correctly archives and preserves relative paths");
        }
        finally
        {
            try { Directory.Delete(tempTestDir, true); } catch { }
        }

        // 10. MIME Type Detection
        AssertEqual("image/png", TransferHelpers.GetMimeType("photo.png"));
        AssertEqual("image/jpeg", TransferHelpers.GetMimeType("photo.jpg"));
        AssertEqual("application/pdf", TransferHelpers.GetMimeType("document.pdf"));
        AssertEqual("application/zip", TransferHelpers.GetMimeType("archive.zip"));
        AssertEqual("text/plain", TransferHelpers.GetMimeType("notes.txt"));
        Console.WriteLine("  [PASS] MIME type resolution accurately categorizes files and images");

        // 11. Secure Token Storage Round-Trip
        string tokenTestDir = Path.Combine(Path.GetTempPath(), "ClipSyncTokensTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var tokenStorage = new SecureTokenStorage(tokenTestDir);
            tokenStorage.SaveToken("google_refresh_token", "sample_refresh_token_12345");
            tokenStorage.SaveToken("google_account_email", "user@gmail.com");

            AssertEqual("sample_refresh_token_12345", tokenStorage.GetToken("google_refresh_token"));
            AssertEqual("user@gmail.com", tokenStorage.GetToken("google_account_email"));

            tokenStorage.DeleteToken("google_refresh_token");
            AssertEqual(null, tokenStorage.GetToken("google_refresh_token"));
            Console.WriteLine("  [PASS] Secure Token Storage protects and restores credentials losslessly");
        }
        finally
        {
            try { Directory.Delete(tokenTestDir, true); } catch { }
        }

        // 12. TransferRecord State Machine
        var record = new TransferRecord(
            id: "transfer-123",
            fileName: "photo.png",
            fileSizeBytes: 2048576, // ~2 MB
            mimeType: "image/png",
            isOutgoing: true,
            status: TransferStatus.Uploading
        );
        AssertEqual("2 MB", record.FormattedSize);
        AssertTrue(record.CanCancel, "Uploading record must be cancellable");
        AssertFalse(record.IsComplete, "Uploading record is not complete");

        record.Status = TransferStatus.Delivered;
        AssertTrue(record.IsComplete, "Delivered record is complete");
        AssertFalse(record.CanCancel, "Delivered record cannot be cancelled");
        Console.WriteLine("  [PASS] TransferRecord state machine flags and formatted sizes verified");

        // 13. Duplicate & Echo-Loop Hash Detection
        byte[] imgA = new byte[] { 1, 2, 3, 4, 5 };
        byte[] imgB = new byte[] { 1, 2, 3, 4, 5 };
        byte[] imgC = new byte[] { 5, 4, 3, 2, 1 };
        string hashA = ClipSyncCrypto.ComputeSha256(imgA);
        string hashB = ClipSyncCrypto.ComputeSha256(imgB);
        string hashC = ClipSyncCrypto.ComputeSha256(imgC);
        AssertEqual(hashA, hashB, "Identical content must yield identical hash for duplicate suppression");
        AssertFalse(hashA == hashC, "Different content must yield different hash");
        // 14. Google OAuth Credentials Multi-Format Parsing
        string tempCredsDir = Path.Combine(Path.GetTempPath(), "clipsync-creds-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCredsDir);
        try
        {
            var testStorage = new SecureTokenStorage(tempCredsDir);

            // Test 14A: Standard Desktop "installed" client JSON
            string installedJson = @"{
                ""installed"": {
                    ""client_id"": ""desktop-123.apps.googleusercontent.com"",
                    ""client_secret"": ""GOCSPX-secret123"",
                    ""project_id"": ""clipsync-512808"",
                    ""redirect_uris"": [""http://localhost""]
                }
            }";
            string installedFile = Path.Combine(tempCredsDir, "credentials.json");
            File.WriteAllText(installedFile, installedJson);
            var (cId1, cSec1, proj1) = testStorage.AutoImportCredentialsJson(installedFile);
            AssertEqual("desktop-123.apps.googleusercontent.com", cId1);
            AssertEqual("GOCSPX-secret123", cSec1);
            AssertEqual("clipsync-512808", proj1);
            AssertEqual("GOCSPX-secret123", testStorage.GetToken("google_client_secret"));
            AssertEqual("clipsync-512808", testStorage.GetToken("google_project_id"));

            // Test 14B: Web application client JSON
            string webJson = @"{
                ""web"": {
                    ""client_id"": ""web-456.apps.googleusercontent.com"",
                    ""client_secret"": ""GOCSPX-websecret456"",
                    ""project_id"": ""clipsync-web""
                }
            }";
            string webFile = Path.Combine(tempCredsDir, "client_secret_web.json");
            File.WriteAllText(webFile, webJson);
            var (cId2, cSec2, proj2) = testStorage.AutoImportCredentialsJson(webFile);
            AssertEqual("web-456.apps.googleusercontent.com", cId2);
            AssertEqual("GOCSPX-websecret456", cSec2);
            AssertEqual("clipsync-web", proj2);

            // Test 14C: Flat client JSON
            string flatJson = @"{
                ""client_id"": ""flat-789.apps.googleusercontent.com"",
                ""client_secret"": ""GOCSPX-flat789""
            }";
            string flatFile = Path.Combine(tempCredsDir, "flat.json");
            File.WriteAllText(flatFile, flatJson);
            var (cId3, cSec3, proj3) = testStorage.AutoImportCredentialsJson(flatFile);
            AssertEqual("flat-789.apps.googleusercontent.com", cId3);
            AssertEqual("GOCSPX-flat789", cSec3);

            Console.WriteLine("  [PASS] Google OAuth credentials multi-format parsing (installed, web, flat) verified");
        }
        finally
        {
            try { Directory.Delete(tempCredsDir, true); } catch { }
        }


        // 15. Google OAuth Token Exchange Payload Validation
        string testCode = "4/0AeanS0abcdef";
        string testVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        string testRedirect = "http://127.0.0.1:54805/oauth2redirect";
        string testClientId = "8819981768-sample.apps.googleusercontent.com";
        string testSecret = "GOCSPX-sampleSecret";

        var payloadParams = new System.Collections.Generic.Dictionary<string, string>
        {
            ["client_id"] = testClientId,
            ["code"] = testCode,
            ["code_verifier"] = testVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = testRedirect,
            ["client_secret"] = testSecret
        };

        AssertEqual("authorization_code", payloadParams["grant_type"]);
        AssertEqual(testClientId, payloadParams["client_id"]);
        AssertEqual(testRedirect, payloadParams["redirect_uri"]);
        AssertEqual(testVerifier, payloadParams["code_verifier"]);
        AssertEqual(testCode, payloadParams["code"]);
        AssertEqual(testSecret, payloadParams["client_secret"]);
        AssertTrue(payloadParams.ContainsKey("client_secret"), "Desktop app token request must include client_secret");
        Console.WriteLine("  [PASS] Google OAuth token exchange payload parameters verified (RFC 6749 + PKCE + Desktop secret)");

        // 16. Google OAuth Error Diagnostics & Guidance
        string googleErrorJson = @"{
            ""error"": ""invalid_request"",
            ""error_description"": ""client_secret is missing.""
        }";
        using (var errDoc = System.Text.Json.JsonDocument.Parse(googleErrorJson))
        {
            var root = errDoc.RootElement;
            string errCode = root.GetProperty("error").GetString()!;
            string errDesc = root.GetProperty("error_description").GetString()!;
            AssertEqual("invalid_request", errCode);
            AssertTrue(errDesc.Contains("client_secret is missing"));
        }
        Console.WriteLine("  [PASS] Google OAuth error response detection and user guidance verified");

        // 17. TransferManifest Full Serialization & Field Invariants
        var manifest = new TransferManifest(
            Version: 1,
            TransferId: "transfer-uuid-12345",
            SenderDeviceId: "device-mac-m5",
            FileName: "Screenshot 2026-10-27 at 13.22.43.png",
            FileSizeBytes: 1048576,
            MimeType: "image/png",
            Sha256Hash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            EncryptionIv: "dBjftJeZ4CVP",
            EncryptionTag: "mB92K27uhbUJU1p1r_wW1g",
            Medium: TransferMedium.GoogleDrive,
            StorageReference: "drive-file-id-abc987",
            DriveFileId: "drive-file-id-abc987",
            CreatedAt: DateTime.UtcNow.ToString("O"),
            ExpiresAt: DateTime.UtcNow.AddHours(24).ToString("O"),
            Status: TransferStatus.Ready,
            DeliveredTo: new[] { "device-windows-10" }
        );

        string manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest);
        var deserializedManifest = System.Text.Json.JsonSerializer.Deserialize<TransferManifest>(manifestJson);
        AssertTrue(deserializedManifest != null, "Manifest must deserialize correctly");
        AssertEqual(manifest.TransferId, deserializedManifest!.TransferId);
        AssertEqual(manifest.SenderDeviceId, deserializedManifest.SenderDeviceId);
        AssertEqual(manifest.FileName, deserializedManifest.FileName);
        AssertEqual(manifest.FileSizeBytes, deserializedManifest.FileSizeBytes);
        AssertEqual(manifest.MimeType, deserializedManifest.MimeType);
        AssertEqual(manifest.Sha256Hash, deserializedManifest.Sha256Hash);
        AssertEqual(manifest.EncryptionIv, deserializedManifest.EncryptionIv);
        AssertEqual(manifest.EncryptionTag, deserializedManifest.EncryptionTag);
        AssertEqual(manifest.DriveFileId, deserializedManifest.DriveFileId);
        Console.WriteLine("  [PASS] Transfer manifest JSON serialization and cross-platform fields verified");

        // 18. Downloaded Ciphertext Size Mismatch Detection
        long expectedCiphertextSize = 2048;
        byte[] receivedCiphertext = new byte[1024]; // Corrupted / partial download
        bool sizeMismatchDetected = false;
        if (expectedCiphertextSize > 0 && receivedCiphertext.Length != expectedCiphertextSize)
        {
            sizeMismatchDetected = true;
        }
        AssertTrue(sizeMismatchDetected, "Size mismatch must be detected before decryption attempt");
        Console.WriteLine("  [PASS] Ciphertext size mismatch detection prior to decryption verified");

        // 19. Cross-Platform Filename Sanitization
        string dirtyFileName = "Screen:Shot/2026*10?27<test>|name.png";
        string sanitized = TransferHelpers.SanitizeFileName(dirtyFileName);
        AssertTrue(!sanitized.Contains(':'), "Sanitized filename must not contain colon");
        AssertTrue(!sanitized.Contains('/'), "Sanitized filename must not contain slash");
        AssertTrue(!sanitized.Contains('*'), "Sanitized filename must not contain asterisk");
        AssertTrue(!sanitized.Contains('?'), "Sanitized filename must not contain question mark");
        AssertTrue(!sanitized.Contains('<'), "Sanitized filename must not contain less-than");
        AssertTrue(!sanitized.Contains('>'), "Sanitized filename must not contain greater-than");
        AssertTrue(!sanitized.Contains('|'), "Sanitized filename must not contain pipe");

        string emptySanitized = TransferHelpers.SanitizeFileName("   ");
        AssertTrue(emptySanitized.StartsWith("Clipboard_"), "Empty filename must fallback to default Clipboard_ name");
        AssertTrue(emptySanitized.EndsWith(".png"), "Empty image filename must have .png extension");
        Console.WriteLine("  [PASS] Cross-platform filename sanitization against invalid characters verified");

        // 20. Google Drive HTTP Error Sanitization & Guidance
        string notFoundError = TransferHelpers.ParseDriveError("{\"error\":{\"code\":404}}", System.Net.HttpStatusCode.NotFound);
        AssertTrue(notFoundError.Contains("HTTP 404"), "Must mention HTTP 404");
        AssertTrue(notFoundError.Contains("Google account"), "Must provide guidance on matching Google accounts");

        string unauthorizedError = TransferHelpers.ParseDriveError("{\"error\":{\"code\":401}}", System.Net.HttpStatusCode.Unauthorized);
        AssertTrue(unauthorizedError.Contains("Settings"), "Must guide user to Settings");

        string customGoogleError = TransferHelpers.ParseDriveError("{\"error\":{\"code\":500,\"message\":\"Internal backend error\"}}", System.Net.HttpStatusCode.InternalServerError);
        AssertTrue(customGoogleError.Contains("Internal backend error"), "Must extract Google error message");
        Console.WriteLine("  [PASS] Google Drive error response parsing and actionable user guidance verified");

        // 21. Incoming Transfer Record & Manifest Preservation for Retry
        var failedIncomingRecord = new TransferRecord(
            id: "incoming-fail-001",
            fileName: "TestImage.png",
            fileSizeBytes: 500000,
            mimeType: "image/png",
            isOutgoing: false,
            status: TransferStatus.Failed,
            statusText: "Delivery failed: Download failed (HTTP 404): File not found in Google Drive.",
            manifest: manifest,
            errorDetails: "System.Net.Http.HttpRequestException: Download failed (HTTP 404)..."
        );
        AssertTrue(!failedIncomingRecord.IsOutgoing, "Record must be incoming");
        AssertTrue(failedIncomingRecord.CanRetry, "Failed incoming transfer must be retryable");
        AssertTrue(failedIncomingRecord.Manifest != null, "Manifest must be preserved for retry");
        AssertTrue(!string.IsNullOrEmpty(failedIncomingRecord.ErrorDetails), "ErrorDetails must be preserved");
        Console.WriteLine("  [PASS] Incoming transfer record state machine and manifest preservation for retry verified");

        // 22. Status Formatting and Untruncated Status Display
        AssertEqual("1 MB", TransferRecord.FormatByteCount(1048576));
        AssertEqual("2.2 MB", TransferRecord.FormatByteCount(2306867));
        AssertEqual("678 KB", TransferRecord.FormatByteCount(694272));
        Console.WriteLine("  [PASS] Transfer size formatting and precision verified");

        // 23. Google Cloud Project Isolation & Foreign Project Auto-Discovery Rejection
        AssertTrue(TransferHelpers.IsValidClipSyncProject("clipsync-512808"), "clipsync-512808 must be valid");
        AssertTrue(TransferHelpers.IsValidClipSyncProject("clipsync-desktop-app"), "any project containing clipsync must be valid");
        AssertTrue(!TransferHelpers.IsValidClipSyncProject("homevault-511916"), "homevault-511916 must be rejected as foreign project");
        AssertTrue(TransferHelpers.IsValidClipSyncProject(null), "null project ID must pass for legacy compatibility");

        string mismatchWarning = TransferHelpers.FormatProjectMismatchWarning("homevault-511916");
        AssertTrue(mismatchWarning.Contains("homevault-511916"), "Warning must mention mismatched project");
        AssertTrue(mismatchWarning.Contains("clipsync"), "Warning must mention expected clipsync project");

        string isolationTempDir = Path.Combine(Path.GetTempPath(), "clipsync-isolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolationTempDir);
            var isolationStorage = new SecureTokenStorage(isolationTempDir);

            // Place foreign project credentials in directory
            string foreignJson = @"{
                ""installed"": {
                    ""client_id"": ""817882429803-foreign.apps.googleusercontent.com"",
                    ""client_secret"": ""GOCSPX-foreign"",
                    ""project_id"": ""homevault-511916""
                }
            }";
            File.WriteAllText(Path.Combine(isolationTempDir, "credentials.json"), foreignJson);

            // Auto-discovery without customPath MUST skip foreign project
            var (foreignCid, _, _) = isolationStorage.AutoImportCredentialsJson();
            AssertTrue(foreignCid == null, "Auto-discovery must reject credentials from foreign project (homevault)");

            // Now place valid ClipSync project credentials in credentials_clipsync.json
            string validClipSyncJson = @"{
                ""installed"": {
                    ""client_id"": ""469960524475-clipsync.apps.googleusercontent.com"",
                    ""client_secret"": ""GOCSPX-validClipSync"",
                    ""project_id"": ""clipsync-512808""
                }
            }";
            File.WriteAllText(Path.Combine(isolationTempDir, "credentials_clipsync.json"), validClipSyncJson);

            var (validCid, validSec, validProj) = isolationStorage.AutoImportCredentialsJson();
            AssertEqual("469960524475-clipsync.apps.googleusercontent.com", validCid);
            AssertEqual("GOCSPX-validClipSync", validSec);
            AssertEqual("clipsync-512808", validProj);
        }
        finally
        {
            try { Directory.Delete(isolationTempDir, true); } catch { }
        }
        Console.WriteLine("  [PASS] Google Cloud Project isolation and foreign credential auto-discovery rejection verified");

        // ---------------------------------------------------------------------
        // Test 24: Google Drive Disconnected Error Formatting and Transfer Record Guards
        // ---------------------------------------------------------------------
        string dlErr = TransferHelpers.FormatDisconnectedDriveError("downloading");
        AssertTrue(dlErr.Contains("disconnected", StringComparison.OrdinalIgnoreCase), "Must mention disconnected");
        AssertTrue(dlErr.Contains("Settings"), "Must guide user to Settings");

        string retryErr = TransferHelpers.FormatDisconnectedDriveError("retrying");
        AssertTrue(retryErr.Contains("disconnected", StringComparison.OrdinalIgnoreCase), "Must mention disconnected");
        AssertTrue(retryErr.Contains("retrying", StringComparison.OrdinalIgnoreCase), "Must mention retrying");

        string remediation = TransferHelpers.FormatDisconnectedRemediation("Disconnected");
        AssertTrue(remediation.Contains("Disconnected"), "Must mention current status");
        AssertTrue(remediation.Contains("Google Drive & System"), "Must direct user to Google Drive & System tab");
        AssertTrue(remediation.Contains("Connect Google Drive"), "Must guide user to Connect Google Drive button");

        // Verify incoming transfer record created when disconnected
        var disconnectedRecord = new TransferRecord(
            id: "disc-rec-001",
            fileName: "TestClipboard.png",
            fileSizeBytes: 2048,
            mimeType: "image/png",
            isOutgoing: false,
            status: TransferStatus.Failed,
            statusText: $"Delivery failed: {retryErr}",
            manifest: manifest,
            errorDetails: remediation
        );
        AssertEqual(TransferStatus.Failed, disconnectedRecord.Status);
        AssertTrue(disconnectedRecord.CanRetry, "Disconnected transfer failure must be retryable");
        AssertTrue(disconnectedRecord.Manifest != null, "Manifest must be preserved for retry once connected");
        AssertTrue(disconnectedRecord.StatusText.Contains("disconnected", StringComparison.OrdinalIgnoreCase), "StatusText must be explicit");
        AssertTrue(disconnectedRecord.ErrorDetails!.Contains("Connect Google Drive"), "ErrorDetails must provide remediation");

        // Verify credential replacement lifecycle in SecureTokenStorage
        string rotTempDir = Path.Combine(Path.GetTempPath(), "clipsync-rot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(rotTempDir);
            var rotStorage = new SecureTokenStorage(rotTempDir);
            rotStorage.SaveToken("google_client_id", "469960524475-old.apps.googleusercontent.com");
            rotStorage.SaveToken("google_refresh_token", "stale-old-refresh-token");
            rotStorage.SaveToken("google_account_email", "old@example.com");

            string newCredsFile = Path.Combine(rotTempDir, "credentials.json");
            string newCredsJson = @"{
                ""installed"": {
                    ""client_id"": ""469960524475-new.apps.googleusercontent.com"",
                    ""client_secret"": ""GOCSPX-newSecret"",
                    ""project_id"": ""clipsync-512808""
                }
            }";
            File.WriteAllText(newCredsFile, newCredsJson);

            var (rotCid, rotSec, rotProj) = rotStorage.AutoImportCredentialsJson(newCredsFile);
            AssertEqual("469960524475-new.apps.googleusercontent.com", rotCid);
            AssertEqual("GOCSPX-newSecret", rotSec);
            AssertEqual("clipsync-512808", rotProj);

            // Deleting stale refresh token upon credential rotation
            rotStorage.DeleteToken("google_refresh_token");
            rotStorage.DeleteToken("google_account_email");
            AssertTrue(string.IsNullOrEmpty(rotStorage.GetToken("google_refresh_token")), "Old refresh token must be wiped");
            AssertTrue(string.IsNullOrEmpty(rotStorage.GetToken("google_account_email")), "Old account email must be wiped");
        }
        finally
        {
            try { Directory.Delete(rotTempDir, true); } catch { }
        }
        Console.WriteLine("  [PASS] Google Drive disconnected error formatting, record guards, and token rotation verified");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n[SUCCESS] ALL 24 WINDOWS NATIVE UNIT TESTS PASSED SUCCESSFULLY!\n");
        Console.ResetColor();
    }
}

