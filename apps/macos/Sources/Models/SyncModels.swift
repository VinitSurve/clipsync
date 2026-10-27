import Foundation

public struct EncryptedPayloadEnvelope: Codable {
    public let version: Int
    public let iv: String          // base64url 12 bytes
    public let ciphertext: String  // base64url
    public let tag: String         // base64url 16 bytes
    public let salt: String?

    public init(version: Int = 1, iv: String, ciphertext: String, tag: String, salt: String? = nil) {
        self.version = version
        self.iv = iv
        self.ciphertext = ciphertext
        self.tag = tag
        self.salt = salt
    }
}

public struct SyncItemMetadata: Codable {
    public let size: Int
    public let mimeType: String?
    public let fileName: String?
    public let sha256: String?
    public let previewSnippet: String?
    public var isPinned: Bool?
    public let sourceApp: String?

    public init(
        size: Int,
        mimeType: String? = nil,
        fileName: String? = nil,
        sha256: String? = nil,
        previewSnippet: String? = nil,
        isPinned: Bool? = nil,
        sourceApp: String? = nil
    ) {
        self.size = size
        self.mimeType = mimeType
        self.fileName = fileName
        self.sha256 = sha256
        self.previewSnippet = previewSnippet
        self.isPinned = isPinned
        self.sourceApp = sourceApp
    }
}

public struct SyncItem: Codable, Identifiable {
    public var id: String { itemId }
    public let version: Int
    public let itemId: String
    public let type: String // "text", "image", "file"
    public let sourceDeviceId: String
    public let createdAt: String
    public let payload: String // Encrypted envelope JSON string
    public let metadata: SyncItemMetadata?
    public let storageRef: String?

    public init(
        version: Int = 1,
        itemId: String,
        type: String = "text",
        sourceDeviceId: String,
        createdAt: String = ISO8601DateFormatter().string(from: Date()),
        payload: String,
        metadata: SyncItemMetadata? = nil,
        storageRef: String? = nil
    ) {
        self.version = version
        self.itemId = itemId
        self.type = type
        self.sourceDeviceId = sourceDeviceId
        self.createdAt = createdAt
        self.payload = payload
        self.metadata = metadata
        self.storageRef = storageRef
    }
}

public struct DeviceInfo: Codable, Identifiable {
    public var id: String { deviceId }
    public let deviceId: String
    public let label: String
    public let platform: String
    public let lastSeen: String?
    public let isCurrent: Bool?
}

public struct RegisterDeviceResponse: Codable {
    public let deviceId: String
    public let token: String
    public let label: String
    public let platform: String
    public let groupId: String
    public let syncSalt: String
}

public struct PairingSessionResponse: Codable {
    public let sessionId: String
    public let code: String
    public let qrData: String
    public let qrDataUrl: String?
    public let expiresAt: String
    public let syncSalt: String
}

public struct ClaimPairingResponse: Codable {
    public let deviceId: String
    public let token: String
    public let groupId: String
    public let label: String
    public let platform: String
    public let syncSalt: String
}

public struct DeviceListResponse: Codable {
    public let devices: [DeviceInfo]
}

public struct HistoryResponse: Codable {
    public let items: [SyncItem]
    public let lastSequence: Int
    public let hasMore: Bool
}

public enum DeviceValidationResult: Equatable {
    case valid
    case invalidCredentials(statusCode: Int)
    case unreachable(reason: String, elapsedMs: Int)
}
