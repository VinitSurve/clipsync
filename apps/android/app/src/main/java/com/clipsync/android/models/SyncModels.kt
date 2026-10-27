package com.clipsync.android.models

import com.google.gson.annotations.SerializedName

data class EncryptedPayloadEnvelope(
    @SerializedName("version") val version: Int = 1,
    @SerializedName("iv") val iv: String,
    @SerializedName("ciphertext") val ciphertext: String,
    @SerializedName("tag") val tag: String,
    @SerializedName("salt") val salt: String? = null
)

data class SyncItemMetadata(
    @SerializedName("size") val size: Int,
    @SerializedName("mimeType") val mimeType: String? = null,
    @SerializedName("fileName") val fileName: String? = null,
    @SerializedName("sha256") val sha256: String? = null,
    @SerializedName("previewSnippet") val previewSnippet: String? = null,
    @SerializedName("isPinned") val isPinned: Boolean? = null,
    @SerializedName("sourceApp") val sourceApp: String? = null
)

data class SyncItem(
    @SerializedName("version") val version: Int = 1,
    @SerializedName("itemId") val itemId: String,
    @SerializedName("type") val type: String = "text",
    @SerializedName("sourceDeviceId") val sourceDeviceId: String,
    @SerializedName("createdAt") val createdAt: String,
    @SerializedName("payload") val payload: String,
    @SerializedName("metadata") val metadata: SyncItemMetadata? = null,
    @SerializedName("storageRef") val storageRef: String? = null
)

data class DeviceInfo(
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("label") val label: String,
    @SerializedName("platform") val platform: String,
    @SerializedName("lastSeen") val lastSeen: String? = null,
    @SerializedName("isCurrent") val isCurrent: Boolean? = null
)

data class DeviceListResponse(
    @SerializedName("devices") val devices: List<DeviceInfo>
)

data class PairingSessionResponse(
    @SerializedName("sessionId") val sessionId: String,
    @SerializedName("code") val code: String,
    @SerializedName("qrData") val qrData: String? = null,
    @SerializedName("expiresAt") val expiresAt: String? = null,
    @SerializedName("syncSalt") val syncSalt: String? = null
)

data class RegisterDeviceResponse(
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("token") val token: String,
    @SerializedName("label") val label: String,
    @SerializedName("platform") val platform: String,
    @SerializedName("groupId") val groupId: String,
    @SerializedName("syncSalt") val syncSalt: String
)

data class ClaimPairingResponse(
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("token") val token: String,
    @SerializedName("groupId") val groupId: String,
    @SerializedName("label") val label: String,
    @SerializedName("platform") val platform: String,
    @SerializedName("syncSalt") val syncSalt: String
)

