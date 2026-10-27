import Foundation

public let MAX_TRANSFER_BYTES: Int64 = 100 * 1024 * 1024 // 100 MB strict limit
public let MAX_TRANSFER_RETENTION_SECONDS: TimeInterval = 24 * 60 * 60 // 24 hours strict retention

public enum TransferMedium: String, Codable {
    case p2pDirect = "p2p_direct"
    case googleDrive = "google_drive"
}

public enum TransferStatus: String, Codable {
    case pending = "pending"
    case uploading = "uploading"
    case ready = "ready"
    case downloading = "downloading"
    case delivered = "delivered"
    case expired = "expired"
    case failed = "failed"
    case cancelled = "cancelled"
}

public struct TransferManifest: Codable, Identifiable {
    public var id: String { transferId }
    public let version: Int
    public let transferId: String
    public var senderDeviceId: String
    public let fileName: String
    public let fileSizeBytes: Int64
    public let mimeType: String
    public let sha256Hash: String
    public let encryptionIv: String
    public let encryptionTag: String
    public let medium: String
    public var storageReference: String?
    public var driveFileId: String?
    public let createdAt: String
    public let expiresAt: String
    public var status: String
    public var deliveredTo: [String]?

    public init(
        version: Int = 1,
        transferId: String = UUID().uuidString,
        senderDeviceId: String,
        fileName: String,
        fileSizeBytes: Int64,
        mimeType: String,
        sha256Hash: String,
        encryptionIv: String,
        encryptionTag: String,
        medium: TransferMedium = .googleDrive,
        storageReference: String? = nil,
        driveFileId: String? = nil,
        createdAt: String = ISO8601DateFormatter().string(from: Date()),
        expiresAt: String = ISO8601DateFormatter().string(from: Date().addingTimeInterval(MAX_TRANSFER_RETENTION_SECONDS)),
        status: TransferStatus = .ready,
        deliveredTo: [String]? = []
    ) {
        self.version = version
        self.transferId = transferId
        self.senderDeviceId = senderDeviceId
        self.fileName = fileName
        self.fileSizeBytes = fileSizeBytes
        self.mimeType = mimeType
        self.sha256Hash = sha256Hash
        self.encryptionIv = encryptionIv
        self.encryptionTag = encryptionTag
        self.medium = medium.rawValue
        self.storageReference = storageReference
        self.driveFileId = driveFileId ?? storageReference
        self.createdAt = createdAt
        self.expiresAt = expiresAt
        self.status = status.rawValue
        self.deliveredTo = deliveredTo
    }
}

public struct InitTransferRequest: Codable {
    public let manifest: TransferManifest

    public init(manifest: TransferManifest) {
        self.manifest = manifest
    }
}

public struct InitTransferResponse: Codable {
    public let ok: Bool
    public let manifest: TransferManifest
    public let duplicate: Bool?
}

public struct AckTransferRequest: Codable {
    public let recipientDeviceId: String

    public init(recipientDeviceId: String) {
        self.recipientDeviceId = recipientDeviceId
    }
}

public struct AckTransferResponse: Codable {
    public let ok: Bool
    public let transferId: String
    public let allDelivered: Bool
    public let shouldDelete: Bool
    public let driveFileId: String?
}

public struct PendingTransfersResponse: Codable {
    public let transfers: [TransferManifest]
}

public struct CleanupTransferResponse: Codable {
    public let ok: Bool
    public let transferId: String
    public let driveFileId: String?
}

public struct TransferRecord: Identifiable, Equatable {
    public let id: String
    public let fileName: String
    public let fileSizeBytes: Int64
    public let mimeType: String
    public let isOutgoing: Bool
    public var progress: Double // 0.0 to 1.0
    public var bytesTransferred: Int64
    public var status: TransferStatus
    public var statusText: String
    public var driveFileId: String?
    public var localURL: URL?
    public let createdAt: Date
    public let expiresAt: Date

    public var isComplete: Bool {
        status == .delivered || status == .expired || status == .failed || status == .cancelled
    }

    public var canCancel: Bool {
        status == .pending || status == .uploading || status == .ready || status == .downloading
    }

    public var canRetry: Bool {
        status == .failed || status == .cancelled
    }

    public var formattedSize: String {
        ByteCountFormatter.string(fromByteCount: fileSizeBytes, countStyle: .file)
    }

    public init(
        id: String,
        fileName: String,
        fileSizeBytes: Int64,
        mimeType: String,
        isOutgoing: Bool,
        progress: Double = 0.0,
        bytesTransferred: Int64 = 0,
        status: TransferStatus = .pending,
        statusText: String = "Pending",
        driveFileId: String? = nil,
        localURL: URL? = nil,
        createdAt: Date = Date(),
        expiresAt: Date = Date().addingTimeInterval(MAX_TRANSFER_RETENTION_SECONDS)
    ) {
        self.id = id
        self.fileName = fileName
        self.fileSizeBytes = fileSizeBytes
        self.mimeType = mimeType
        self.isOutgoing = isOutgoing
        self.progress = progress
        self.bytesTransferred = bytesTransferred
        self.status = status
        self.statusText = statusText
        self.driveFileId = driveFileId
        self.localURL = localURL
        self.createdAt = createdAt
        self.expiresAt = expiresAt
    }
}
