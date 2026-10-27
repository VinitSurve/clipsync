using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipSync.Windows.Crypto;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Services;

public sealed class TransferManager
{
    private readonly string _serverUrl;
    private readonly string _deviceId;
    private readonly string _token;
    private readonly ClipSyncCrypto _crypto;
    private readonly GoogleDriveService _googleDrive;
    private readonly WebSocketSyncService _syncService;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly HttpClient _httpClient;

    private readonly List<TransferRecord> _transfers = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeTasks = new();
    private readonly HashSet<string> _acknowledgedTransferIds = new();

    public IReadOnlyList<TransferRecord> Transfers
    {
        get
        {
            lock (_lock)
            {
                return _transfers.ToArray();
            }
        }
    }

    public bool IsTransferring
    {
        get
        {
            lock (_lock)
            {
                return _transfers.Exists(t => t.Status is TransferStatus.Uploading or TransferStatus.Downloading);
            }
        }
    }

    public string? LatestErrorMessage { get; private set; }

    public event Action? OnTransfersChanged;
    public event Action<string>? OnTransferError;

    public TransferManager(
        string serverUrl,
        string deviceId,
        string token,
        ClipSyncCrypto crypto,
        GoogleDriveService googleDrive,
        WebSocketSyncService syncService,
        ClipboardMonitor clipboardMonitor,
        HttpClient? httpClient = null)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _deviceId = deviceId;
        _token = token;
        _crypto = crypto;
        _googleDrive = googleDrive;
        _syncService = syncService;
        _clipboardMonitor = clipboardMonitor;
        _httpClient = httpClient ?? new HttpClient();

        SetupWebSocketCallbacks();
        SetupClipboardCallbacks();
    }

    private void SetupWebSocketCallbacks()
    {
        _syncService.OnTransferAnnounced += manifest =>
        {
            _ = HandleRemoteTransferAnnouncedAsync(manifest);
        };

        _syncService.OnTransferProgressRelay += (tid, dev, bytes, total, phase) =>
        {
            UpdateRecord(tid, r =>
            {
                r.BytesTransferred = bytes;
                if (total > 0)
                {
                    r.Progress = (double)bytes / total;
                }
                r.StatusText = $"{char.ToUpperInvariant(phase[0])}{phase[1..]}...";
            });
        };

        _syncService.OnTransferDelivered += (tid, rec, allDelivered, driveFileId, shouldDelete) =>
        {
            _ = HandleRemoteTransferDeliveredAsync(tid, driveFileId, allDelivered || shouldDelete);
        };

        _syncService.OnTransferExpired += (tid, driveFileId) =>
        {
            _ = HandleRemoteTransferExpiredAsync(tid, driveFileId);
        };

        _syncService.OnTransferDeleted += (tid, driveFileId) =>
        {
            _ = HandleRemoteTransferDeletedAsync(tid, driveFileId);
        };
    }

    private void SetupClipboardCallbacks()
    {
        _clipboardMonitor.OnNewLocalImage += (data, mimeType, fileName) =>
        {
            HandleLocalImage(data, mimeType, fileName);
        };

        _clipboardMonitor.OnNewLocalFiles += paths =>
        {
            HandleLocalFiles(paths);
        };

        _clipboardMonitor.OnClipboardError += msg =>
        {
            LatestErrorMessage = msg;
            OnTransferError?.Invoke(msg);
        };
    }

    // -------------------------------------------------------------------------
    // Outgoing Transfers (Image & Files)
    // -------------------------------------------------------------------------

    public void HandleLocalImage(byte[] data, string mimeType, string fileName)
    {
        if (!_googleDrive.IsConnected)
        {
            LatestErrorMessage = "Google Drive is not connected. Connect in Settings to sync images.";
            OnTransferError?.Invoke(LatestErrorMessage);
            return;
        }

        if (data.Length > TransferConstants.MaxTransferBytes)
        {
            LatestErrorMessage = $"Image exceeds 100 MB limit ({TransferRecord.FormatByteCount(data.Length)}).";
            OnTransferError?.Invoke(LatestErrorMessage);
            return;
        }

        string transferId = Guid.NewGuid().ToString();
        var record = new TransferRecord(
            id: transferId,
            fileName: fileName,
            fileSizeBytes: data.Length,
            mimeType: mimeType,
            isOutgoing: true,
            status: TransferStatus.Uploading,
            statusText: "Encrypting image...",
            originalPayload: data
        );

        InsertOrUpdateRecord(record);

        var cts = new CancellationTokenSource();
        _activeTasks[transferId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // 1. Encrypt binary payload locally with AES-256-GCM
                var (ciphertext, iv, tag, sha256Hex) = _crypto.EncryptBinary(data);

                UpdateRecord(transferId, r =>
                {
                    r.StatusText = "Uploading to Google Drive...";
                });

                // 2. Upload ciphertext to temporary Google Drive storage
                var progress = new Progress<double>(p =>
                {
                    UpdateRecord(transferId, r =>
                    {
                        r.Progress = p;
                        r.BytesTransferred = (long)(ciphertext.Length * p);
                    });
                });

                string driveFileId = await _googleDrive.UploadEncryptedPayloadAsync(ciphertext, progress, cts.Token);

                // 3. Create TransferManifest
                var manifest = new TransferManifest(
                    Version: 1,
                    TransferId: transferId,
                    SenderDeviceId: _deviceId,
                    FileName: fileName,
                    FileSizeBytes: ciphertext.Length,
                    MimeType: mimeType,
                    Sha256Hash: sha256Hex,
                    EncryptionIv: iv,
                    EncryptionTag: tag,
                    Medium: TransferMedium.GoogleDrive,
                    StorageReference: driveFileId,
                    DriveFileId: driveFileId,
                    CreatedAt: DateTime.UtcNow.ToString("O"),
                    ExpiresAt: DateTime.UtcNow.Add(TransferConstants.MaxTransferRetention).ToString("O"),
                    Status: TransferStatus.Ready
                );

                // 4. Register on Cloudflare backend
                await RegisterTransferOnServerAsync(manifest, cts.Token);

                // 5. Broadcast announcement over WebSocket
                await _syncService.SendTransferInitAsync(manifest);

                UpdateRecord(transferId, r =>
                {
                    r.Status = TransferStatus.Ready;
                    r.DriveFileId = driveFileId;
                    r.Progress = 1.0;
                    r.StatusText = "Uploaded - Waiting for peer devices";
                });
            }
            catch (OperationCanceledException)
            {
                UpdateRecord(transferId, r =>
                {
                    r.Status = TransferStatus.Cancelled;
                    r.StatusText = "Transfer cancelled.";
                });
            }
            catch (Exception ex)
            {
                LatestErrorMessage = $"Upload error: {ex.Message}";
                UpdateRecord(transferId, r =>
                {
                    r.Status = TransferStatus.Failed;
                    r.StatusText = $"Upload error: {ex.Message}";
                });
                OnTransferError?.Invoke(LatestErrorMessage);
            }
            finally
            {
                _activeTasks.TryRemove(transferId, out _);
            }
        });
    }

    public void HandleLocalFiles(string[] paths)
    {
        if (!_googleDrive.IsConnected)
        {
            LatestErrorMessage = "Google Drive is not connected. Connect in Settings to sync files.";
            OnTransferError?.Invoke(LatestErrorMessage);
            return;
        }

        string transferId = Guid.NewGuid().ToString();

        var cts = new CancellationTokenSource();
        _activeTasks[transferId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] payloadBytes;
                string displayFileName;
                string mimeType;

                bool isSingleFile = paths.Length == 1 && File.Exists(paths[0]) && !Directory.Exists(paths[0]);

                if (isSingleFile)
                {
                    // Single file
                    string filePath = paths[0];
                    displayFileName = Path.GetFileName(filePath);
                    mimeType = GetMimeType(displayFileName);
                    payloadBytes = await File.ReadAllBytesAsync(filePath, cts.Token);
                }
                else
                {
                    // Multiple files or folder -> package into clean temporary zip archive
                    if (paths.Length == 1 && Directory.Exists(paths[0]))
                    {
                        displayFileName = $"{Path.GetFileName(paths[0].TrimEnd('\\', '/'))}.zip";
                    }
                    else
                    {
                        displayFileName = $"Archive_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
                    }

                    mimeType = "application/zip";
                    payloadBytes = CreateZipArchive(paths);
                }

                if (payloadBytes.Length > TransferConstants.MaxTransferBytes)
                {
                    throw new InvalidOperationException($"Payload size ({TransferRecord.FormatByteCount(payloadBytes.Length)}) exceeds 100 MB limit.");
                }

                var record = new TransferRecord(
                    id: transferId,
                    fileName: displayFileName,
                    fileSizeBytes: payloadBytes.Length,
                    mimeType: mimeType,
                    isOutgoing: true,
                    status: TransferStatus.Uploading,
                    statusText: "Encrypting file(s)...",
                    originalPayload: payloadBytes
                );
                InsertOrUpdateRecord(record);

                // 1. Encrypt binary payload locally with AES-256-GCM
                var (ciphertext, iv, tag, sha256Hex) = _crypto.EncryptBinary(payloadBytes);

                UpdateRecord(transferId, r =>
                {
                    r.StatusText = "Uploading to Google Drive...";
                });

                // 2. Upload ciphertext to temporary Google Drive storage
                var progress = new Progress<double>(p =>
                {
                    UpdateRecord(transferId, r =>
                    {
                        r.Progress = p;
                        r.BytesTransferred = (long)(ciphertext.Length * p);
                    });
                });

                string driveFileId = await _googleDrive.UploadEncryptedPayloadAsync(ciphertext, progress, cts.Token);

                // 3. Create TransferManifest
                var manifest = new TransferManifest(
                    Version: 1,
                    TransferId: transferId,
                    SenderDeviceId: _deviceId,
                    FileName: displayFileName,
                    FileSizeBytes: ciphertext.Length,
                    MimeType: mimeType,
                    Sha256Hash: sha256Hex,
                    EncryptionIv: iv,
                    EncryptionTag: tag,
                    Medium: TransferMedium.GoogleDrive,
                    StorageReference: driveFileId,
                    DriveFileId: driveFileId,
                    CreatedAt: DateTime.UtcNow.ToString("O"),
                    ExpiresAt: DateTime.UtcNow.Add(TransferConstants.MaxTransferRetention).ToString("O"),
                    Status: TransferStatus.Ready
                );

                // 4. Register on Cloudflare backend
                await RegisterTransferOnServerAsync(manifest, cts.Token);

                // 5. Broadcast announcement over WebSocket
                await _syncService.SendTransferInitAsync(manifest);

                UpdateRecord(transferId, r =>
                {
                    r.Status = TransferStatus.Ready;
                    r.DriveFileId = driveFileId;
                    r.Progress = 1.0;
                    r.StatusText = "Uploaded - Waiting for peer devices";
                });
            }
            catch (OperationCanceledException)
            {
                UpdateRecord(transferId, r =>
                {
                    r.Status = TransferStatus.Cancelled;
                    r.StatusText = "Transfer cancelled.";
                });
            }
            catch (Exception ex)
            {
                LatestErrorMessage = $"File transfer error: {ex.Message}";
                UpdateRecord(transferId, r =>
                {
                    r.Status = TransferStatus.Failed;
                    r.StatusText = $"Transfer error: {ex.Message}";
                });
                OnTransferError?.Invoke(LatestErrorMessage);
            }
            finally
            {
                _activeTasks.TryRemove(transferId, out _);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Incoming Transfers (Download, Decrypt, Verify & Deliver)
    // -------------------------------------------------------------------------

    public async Task HandleRemoteTransferAnnouncedAsync(TransferManifest manifest)
    {
        // Skip self-initiated
        if (manifest.SenderDeviceId == _deviceId) return;

        // Prevent duplicate concurrent downloads
        if (_activeTasks.ContainsKey(manifest.TransferId)) return;

        // Skip already delivered/acknowledged transfers
        lock (_acknowledgedTransferIds)
        {
            if (_acknowledgedTransferIds.Contains(manifest.TransferId)) return;
        }

        string? driveFileId = (manifest.DriveFileId ?? manifest.StorageReference)?.Trim();
        if (string.IsNullOrEmpty(driveFileId))
        {
            string missingErr = "Transfer manifest is missing Google Drive storage reference.";
            LogTransferDiagnostic(manifest.TransferId, "Failed", missingErr);
            var errRecord = new TransferRecord(
                id: manifest.TransferId,
                fileName: manifest.FileName,
                fileSizeBytes: manifest.FileSizeBytes,
                mimeType: manifest.MimeType,
                isOutgoing: false,
                status: TransferStatus.Failed,
                statusText: $"Delivery failed: {missingErr}",
                manifest: manifest,
                errorDetails: missingErr
            );
            InsertOrUpdateRecord(errRecord);
            return;
        }

        if (!_googleDrive.IsConnected)
        {
            string notConnErr = "Google Drive is disconnected. Connect in Settings before downloading.";
            LogTransferDiagnostic(manifest.TransferId, "DownloadSkipped", notConnErr);
            var errRecord = new TransferRecord(
                id: manifest.TransferId,
                fileName: manifest.FileName,
                fileSizeBytes: manifest.FileSizeBytes,
                mimeType: manifest.MimeType,
                isOutgoing: false,
                status: TransferStatus.Failed,
                statusText: $"Delivery failed: {notConnErr}",
                manifest: manifest,
                driveFileId: driveFileId,
                errorDetails: $"Google Drive auth status is {_googleDrive.Status}. Please open Settings -> 'Google Drive & System' tab, click 'Connect Google Drive', and sign in before downloading."
            );
            InsertOrUpdateRecord(errRecord);
            return;
        }

        var record = new TransferRecord(
            id: manifest.TransferId,
            fileName: manifest.FileName,
            fileSizeBytes: manifest.FileSizeBytes,
            mimeType: manifest.MimeType,
            isOutgoing: false,
            status: TransferStatus.Downloading,
            statusText: "Downloading encrypted payload...",
            driveFileId: driveFileId,
            manifest: manifest
        );
        InsertOrUpdateRecord(record);

        var cts = new CancellationTokenSource();
        _activeTasks[manifest.TransferId] = cts;
        LogTransferDiagnostic(manifest.TransferId, "DownloadStart", $"Starting download for '{manifest.FileName}' ({manifest.FileSizeBytes} bytes).");

        try
        {
            // 1. Download encrypted payload from Google Drive
            var progress = new Progress<double>(p =>
            {
                UpdateRecord(manifest.TransferId, r =>
                {
                    r.Progress = p;
                    r.BytesTransferred = (long)(manifest.FileSizeBytes * p);
                });
            });

            byte[] ciphertext = await _googleDrive.DownloadEncryptedPayloadAsync(driveFileId, progress, cts.Token);
            LogTransferDiagnostic(manifest.TransferId, "DownloadSuccess", $"Retrieved {ciphertext.Length} bytes from Google Drive.");

            // Verify downloaded byte count matches expected size before decrypting
            if (manifest.FileSizeBytes > 0 && ciphertext.Length != manifest.FileSizeBytes)
            {
                throw new InvalidDataException($"Ciphertext size mismatch: downloaded {ciphertext.Length} bytes, expected {manifest.FileSizeBytes} bytes.");
            }

            UpdateRecord(manifest.TransferId, r =>
            {
                r.StatusText = "Verifying & Decrypting...";
            });

            // 2. Decrypt with AES-256-GCM and verify SHA-256 integrity
            byte[] decryptedData = _crypto.DecryptBinary(
                ciphertext,
                manifest.EncryptionIv,
                manifest.EncryptionTag,
                manifest.Sha256Hash
            );
            LogTransferDiagnostic(manifest.TransferId, "DecryptSuccess", $"Decryption and SHA-256 integrity check verified ({decryptedData.Length} bytes).");

            // 3. Deliver to Windows clipboard and Downloads/ClipSync folder
            string localFilePath = DeliverPayload(decryptedData, manifest.FileName, manifest.MimeType);
            LogTransferDiagnostic(manifest.TransferId, "Delivered", $"Delivered to clipboard and saved to '{localFilePath}'.");

            UpdateRecord(manifest.TransferId, r =>
            {
                r.Status = TransferStatus.Delivered;
                r.Progress = 1.0;
                r.LocalPath = localFilePath;
                r.StatusText = "Delivered to Clipboard & Downloads";
                r.ErrorDetails = null;
            });

            // Mark as acknowledged only after decryption and delivery succeed!
            lock (_acknowledgedTransferIds)
            {
                _acknowledgedTransferIds.Add(manifest.TransferId);
            }

            // 4. Send ACK to Cloudflare server and WebSocket
            var ackResp = await SendAckToServerAsync(manifest.TransferId, cts.Token);
            await _syncService.SendTransferAckAsync(manifest.TransferId);

            // 5. If all devices have received, delete immediately from Drive!
            if (ackResp.AllDelivered || ackResp.ShouldDelete)
            {
                await _googleDrive.DeleteFileAsync(driveFileId, CancellationToken.None);
                await DeleteTransferOnServerAsync(manifest.TransferId);
                UpdateRecord(manifest.TransferId, r =>
                {
                    r.StatusText = "Delivered & Purged from Drive";
                });
                LogTransferDiagnostic(manifest.TransferId, "Purged", "Delivered to all peers; purged from Drive.");
            }
        }
        catch (OperationCanceledException)
        {
            LogTransferDiagnostic(manifest.TransferId, "Cancelled", "Transfer cancelled by user.");
            UpdateRecord(manifest.TransferId, r =>
            {
                r.Status = TransferStatus.Cancelled;
                r.StatusText = "Download cancelled.";
            });
        }
        catch (Exception ex)
        {
            lock (_acknowledgedTransferIds)
            {
                _acknowledgedTransferIds.Remove(manifest.TransferId);
            }

            string sanitizedErr = ex.Message;
            string errorDetails = ex.ToString();
            LatestErrorMessage = $"Failed to receive '{manifest.FileName}': {sanitizedErr}";
            LogTransferDiagnostic(manifest.TransferId, "DeliveryFailed", $"{sanitizedErr} ({ex.GetType().Name})");

            UpdateRecord(manifest.TransferId, r =>
            {
                r.Status = TransferStatus.Failed;
                r.StatusText = $"Delivery failed: {sanitizedErr}";
                r.ErrorDetails = errorDetails;
                r.Manifest = manifest;
            });
            OnTransferError?.Invoke(LatestErrorMessage);
        }
        finally
        {
            _activeTasks.TryRemove(manifest.TransferId, out _);
        }
    }

    private string DeliverPayload(byte[] data, string fileName, string mimeType)
    {
        string downloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "ClipSync"
        );
        Directory.CreateDirectory(downloadsDir);

        string safeFileName = SanitizeFileName(fileName);
        string destinationPath = Path.Combine(downloadsDir, safeFileName);

        // Prevent overwriting existing file by appending timestamp
        if (File.Exists(destinationPath))
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(safeFileName);
            string ext = Path.GetExtension(safeFileName);
            string uniqueName = $"{nameWithoutExt}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{ext}";
            destinationPath = Path.Combine(downloadsDir, uniqueName);
        }

        File.WriteAllBytes(destinationPath, data);

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            // Deliver to Windows clipboard as Image
            _clipboardMonitor.SetRemoteClipboardImage(data);
        }
        else
        {
            // Deliver to Windows clipboard as File Drop List
            _clipboardMonitor.SetRemoteClipboardFiles(new[] { destinationPath });
        }

        return destinationPath;
    }

    public static string SanitizeFileName(string fileName) => TransferHelpers.SanitizeFileName(fileName);

    // -------------------------------------------------------------------------
    // Offline Reconnection & Pending Transfers Fetch
    // -------------------------------------------------------------------------

    public async Task FetchPendingTransfersAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_serverUrl}/v1/transfers/pending");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return;

            string json = await res.Content.ReadAsStringAsync(ct);
            var resp = JsonSerializer.Deserialize<PendingTransfersResponse>(json);
            if (resp?.Transfers == null) return;

            foreach (var manifest in resp.Transfers)
            {
                if (manifest.SenderDeviceId != _deviceId)
                {
                    _ = HandleRemoteTransferAnnouncedAsync(manifest);
                }
            }
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // Three-Tier Cleanup Handlers
    // -------------------------------------------------------------------------

    public async Task HandleRemoteTransferDeliveredAsync(string transferId, string? driveFileId, bool shouldDelete)
    {
        if (shouldDelete)
        {
            if (!string.IsNullOrEmpty(driveFileId))
            {
                await _googleDrive.DeleteFileAsync(driveFileId);
            }
            await DeleteTransferOnServerAsync(transferId);

            UpdateRecord(transferId, r =>
            {
                r.Status = TransferStatus.Delivered;
                r.StatusText = "Delivered to all peers & Purged from Drive";
            });
        }
    }

    public async Task HandleRemoteTransferExpiredAsync(string transferId, string? driveFileId)
    {
        if (!string.IsNullOrEmpty(driveFileId))
        {
            await _googleDrive.DeleteFileAsync(driveFileId);
        }
        UpdateRecord(transferId, r =>
        {
            r.Status = TransferStatus.Expired;
            r.StatusText = "Expired after 24h (Purged from Drive)";
        });
    }

    public async Task HandleRemoteTransferDeletedAsync(string transferId, string? driveFileId)
    {
        if (!string.IsNullOrEmpty(driveFileId))
        {
            await _googleDrive.DeleteFileAsync(driveFileId);
        }
        UpdateRecord(transferId, r =>
        {
            r.Status = TransferStatus.Cancelled;
            r.StatusText = "Cancelled & Purged from Drive";
        });
    }

    public void CancelTransfer(string transferId)
    {
        if (_activeTasks.TryRemove(transferId, out var cts))
        {
            cts.Cancel();
        }

        TransferRecord? record;
        lock (_lock)
        {
            record = _transfers.Find(t => t.Id == transferId);
        }

        if (record == null) return;

        UpdateRecord(transferId, r =>
        {
            r.Status = TransferStatus.Cancelled;
            r.StatusText = "Cancelled by user";
        });

        _ = Task.Run(async () =>
        {
            if (!string.IsNullOrEmpty(record.DriveFileId))
            {
                await _googleDrive.DeleteFileAsync(record.DriveFileId);
            }
            await DeleteTransferOnServerAsync(transferId);
            await _syncService.SendTransferCancelAsync(transferId, "Cancelled by user");
        });
    }

    public void RetryTransfer(string transferId)
    {
        TransferRecord? record;
        lock (_lock)
        {
            record = _transfers.Find(t => t.Id == transferId);
        }

        if (record == null || !record.CanRetry) return;

        if (record.IsOutgoing && record.OriginalPayload != null)
        {
            if (record.MimeType.StartsWith("image/"))
            {
                HandleLocalImage(record.OriginalPayload, record.MimeType, record.FileName);
            }
            else
            {
                // Re-initiate file transfer
                string tempDir = Path.Combine(Path.GetTempPath(), "ClipSync", "Retries");
                Directory.CreateDirectory(tempDir);
                string retryFile = Path.Combine(tempDir, record.FileName);
                File.WriteAllBytes(retryFile, record.OriginalPayload);
                HandleLocalFiles(new[] { retryFile });
            }
        }
        else if (!record.IsOutgoing && record.Manifest != null)
        {
            if (!_googleDrive.IsConnected)
            {
                string notConnErr = "Google Drive is disconnected. Connect in Settings before retrying.";
                LogTransferDiagnostic(record.Id, "RetryFailed", notConnErr);
                record.Status = TransferStatus.Failed;
                record.StatusText = $"Delivery failed: {notConnErr}";
                record.ErrorDetails = $"Google Drive auth status is {_googleDrive.Status}. Please open Settings -> 'Google Drive & System' tab, click 'Connect Google Drive', and sign in before retrying.";
                OnTransfersChanged?.Invoke();
                return;
            }

            // Re-attempt incoming transfer
            LogTransferDiagnostic(record.Id, "Retry", $"User retrying incoming transfer for '{record.FileName}'.");
            record.Status = TransferStatus.Downloading;
            record.StatusText = "Retrying download...";
            record.ErrorDetails = null;
            OnTransfersChanged?.Invoke();

            _ = Task.Run(async () =>
            {
                await HandleRemoteTransferAnnouncedAsync(record.Manifest);
            });
        }
    }

    public static void LogTransferDiagnostic(string transferId, string action, string detail)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        string shortId = transferId.Length > 8 ? transferId[..8] : transferId;
        string line = $"[{timestamp}] [Transfer:{shortId}] {action}: {detail}";
        Console.WriteLine(line);

        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSync");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "transfers.log");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { }
    }

    public void ClearCompleted()
    {
        lock (_lock)
        {
            _transfers.RemoveAll(t => t.IsComplete);
        }
        OnTransfersChanged?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Server REST Helpers
    // -------------------------------------------------------------------------

    private async Task RegisterTransferOnServerAsync(TransferManifest manifest, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/v1/transfers/init");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var body = new { manifest };
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            string err = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Server rejected transfer (HTTP {res.StatusCode}): {err}");
        }
    }

    private async Task<AckTransferResponse> SendAckToServerAsync(string transferId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/v1/transfers/{Uri.EscapeDataString(transferId)}/ack");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var body = new { recipientDeviceId = _deviceId };
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            return new AckTransferResponse(false, transferId, false, false);
        }

        string json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AckTransferResponse>(json) 
            ?? new AckTransferResponse(true, transferId, false, false);
    }

    private async Task DeleteTransferOnServerAsync(string transferId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_serverUrl}/v1/transfers/{Uri.EscapeDataString(transferId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            await _httpClient.SendAsync(req);
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // Utility Helpers (Zip Archiving, MIME detection, Record updates)
    // -------------------------------------------------------------------------

    public static byte[] CreateZipArchive(string[] paths) => TransferHelpers.CreateZipArchive(paths);

    public static string GetMimeType(string fileName) => TransferHelpers.GetMimeType(fileName);

    private void InsertOrUpdateRecord(TransferRecord record)
    {
        lock (_lock)
        {
            int idx = _transfers.FindIndex(t => t.Id == record.Id);
            if (idx >= 0)
            {
                _transfers[idx] = record;
            }
            else
            {
                _transfers.Insert(0, record);
                if (_transfers.Count > 100)
                {
                    _transfers.RemoveAt(_transfers.Count - 1);
                }
            }
        }
        OnTransfersChanged?.Invoke();
    }

    private void UpdateRecord(string transferId, Action<TransferRecord> updateAction)
    {
        lock (_lock)
        {
            var record = _transfers.Find(t => t.Id == transferId);
            if (record != null)
            {
                updateAction(record);
            }
        }
        OnTransfersChanged?.Invoke();
    }
}
