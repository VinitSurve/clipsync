using System;
using System.Text.Json.Serialization;

namespace ClipSync.Windows.Models;

public static class TransferConstants
{
    public const long MaxTransferBytes = 100 * 1024 * 1024; // 100 MB strict limit
    public const int MaxTransferRetentionHours = 24; // 24 hours strict retention
    public static readonly TimeSpan MaxTransferRetention = TimeSpan.FromHours(24);
}

public static class TransferMedium
{
    public const string GoogleDrive = "google_drive";
    public const string P2PDirect = "p2p_direct";
}

public static class TransferStatus
{
    public const string Pending = "pending";
    public const string Uploading = "uploading";
    public const string Ready = "ready";
    public const string Downloading = "downloading";
    public const string Delivered = "delivered";
    public const string Expired = "expired";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public record TransferManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("transferId")] string TransferId,
    [property: JsonPropertyName("senderDeviceId")] string SenderDeviceId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("fileSizeBytes")] long FileSizeBytes,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sha256Hash")] string Sha256Hash,
    [property: JsonPropertyName("encryptionIv")] string EncryptionIv,
    [property: JsonPropertyName("encryptionTag")] string EncryptionTag,
    [property: JsonPropertyName("medium")] string Medium,
    [property: JsonPropertyName("storageReference")] string? StorageReference,
    [property: JsonPropertyName("driveFileId")] string? DriveFileId,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("deliveredTo")] string[]? DeliveredTo = null
);

public record InitTransferRequest(
    [property: JsonPropertyName("manifest")] TransferManifest Manifest
);

public record InitTransferResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("manifest")] TransferManifest Manifest,
    [property: JsonPropertyName("duplicate")] bool? Duplicate = null
);

public record AckTransferRequest(
    [property: JsonPropertyName("recipientDeviceId")] string RecipientDeviceId
);

public record AckTransferResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("transferId")] string TransferId,
    [property: JsonPropertyName("allDelivered")] bool AllDelivered,
    [property: JsonPropertyName("shouldDelete")] bool ShouldDelete,
    [property: JsonPropertyName("driveFileId")] string? DriveFileId = null
);

public record PendingTransfersResponse(
    [property: JsonPropertyName("transfers")] TransferManifest[] Transfers
);

public record CleanupTransferResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("transferId")] string TransferId,
    [property: JsonPropertyName("driveFileId")] string? DriveFileId = null
);

public sealed class TransferRecord
{
    public string Id { get; init; }
    public string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public string MimeType { get; init; }
    public bool IsOutgoing { get; init; }
    public double Progress { get; set; } // 0.0 to 1.0
    public long BytesTransferred { get; set; }
    public string Status { get; set; } // TransferStatus
    public string StatusText { get; set; }
    public string? DriveFileId { get; set; }
    public string? LocalPath { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public byte[]? OriginalPayload { get; set; } // Preserved for outgoing retry if needed

    public bool IsComplete => Status is TransferStatus.Delivered or TransferStatus.Expired or TransferStatus.Failed or TransferStatus.Cancelled;
    public bool CanCancel => Status is TransferStatus.Pending or TransferStatus.Uploading or TransferStatus.Ready or TransferStatus.Downloading;
    public bool CanRetry => Status is TransferStatus.Failed or TransferStatus.Cancelled;

    public string FormattedSize => FormatByteCount(FileSizeBytes);

    public TransferRecord(
        string id,
        string fileName,
        long fileSizeBytes,
        string mimeType,
        bool isOutgoing,
        double progress = 0.0,
        long bytesTransferred = 0,
        string status = TransferStatus.Pending,
        string statusText = "Pending",
        string? driveFileId = null,
        string? localPath = null,
        DateTime? createdAt = null,
        DateTime? expiresAt = null,
        byte[]? originalPayload = null)
    {
        Id = id;
        FileName = fileName;
        FileSizeBytes = fileSizeBytes;
        MimeType = mimeType;
        IsOutgoing = isOutgoing;
        Progress = progress;
        BytesTransferred = bytesTransferred;
        Status = status;
        StatusText = statusText;
        DriveFileId = driveFileId;
        LocalPath = localPath;
        CreatedAt = createdAt ?? DateTime.UtcNow;
        ExpiresAt = expiresAt ?? DateTime.UtcNow.Add(TransferConstants.MaxTransferRetention);
        OriginalPayload = originalPayload;
    }

    public static string FormatByteCount(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.#} {suffixes[i]}";
    }
}
