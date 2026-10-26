using System;
using System.Text.Json.Serialization;

namespace ClipSync.Windows.Models;

public record EncryptedPayloadEnvelope(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("iv")] string Iv,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("salt")] string? Salt = null
);

public record SyncItemMetadata(
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("mimeType")] string? MimeType = null,
    [property: JsonPropertyName("fileName")] string? FileName = null,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("previewSnippet")] string? PreviewSnippet = null,
    [property: JsonPropertyName("isPinned")] bool? IsPinned = null,
    [property: JsonPropertyName("sourceApp")] string? SourceApp = null
);

public record SyncItem(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sourceDeviceId")] string SourceDeviceId,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("metadata")] SyncItemMetadata? Metadata = null,
    [property: JsonPropertyName("storageRef")] string? StorageRef = null
);

public record DeviceInfo(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("lastSeen")] string? LastSeen = null,
    [property: JsonPropertyName("isCurrent")] bool? IsCurrent = null
);

public record RegisterDeviceResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("syncSalt")] string SyncSalt
);

public record PairingSessionResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("qrData")] string QrData,
    [property: JsonPropertyName("qrDataUrl")] string? QrDataUrl,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("syncSalt")] string SyncSalt
);

public record ClaimPairingResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("syncSalt")] string SyncSalt
);

public record DeviceListResponse(
    [property: JsonPropertyName("devices")] DeviceInfo[] Devices
);
