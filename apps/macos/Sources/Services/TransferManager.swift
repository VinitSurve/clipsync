import Foundation
import Combine
import AppKit

@MainActor
public final class TransferManager: ObservableObject {
    @Published public var transfers: [TransferRecord] = []
    @Published public var isTransferring: Bool = false
    @Published public var latestErrorMessage: String?

    private let googleDrive: GoogleDriveService
    private let crypto: ClipSyncCrypto
    private weak var syncService: WebSocketSyncService?
    private weak var clipboardMonitor: ClipboardMonitor?

    public let serverUrl: String
    public let deviceId: String
    public let token: String

    private var activeTasks: [String: Task<Void, Never>] = [:]
    private var acknowledgedTransferIds = Set<String>()

    public init(
        serverUrl: String,
        deviceId: String,
        token: String,
        crypto: ClipSyncCrypto,
        googleDrive: GoogleDriveService? = nil,
        syncService: WebSocketSyncService?,
        clipboardMonitor: ClipboardMonitor?
    ) {
        self.serverUrl = serverUrl
        self.deviceId = deviceId
        self.token = token
        self.crypto = crypto
        self.googleDrive = googleDrive ?? GoogleDriveService.shared
        self.syncService = syncService
        self.clipboardMonitor = clipboardMonitor

        setupWebSocketCallbacks()
    }

    private func setupWebSocketCallbacks() {
        guard let sync = syncService else { return }

        sync.onTransferAnnounced = { [weak self] manifest in
            Task { @MainActor [weak self] in
                await self?.handleRemoteTransferAnnounced(manifest: manifest)
            }
        }

        sync.onTransferProgressRelay = { [weak self] tid, dev, bytes, total, phase in
            Task { @MainActor [weak self] in
                guard let self = self else { return }
                if let idx = self.transfers.firstIndex(where: { $0.id == tid }) {
                    self.transfers[idx].bytesTransferred = bytes
                    if total > 0 {
                        self.transfers[idx].progress = Double(bytes) / Double(total)
                    }
                    self.transfers[idx].statusText = "\(phase.capitalized)..."
                }
            }
        }

        sync.onTransferDelivered = { [weak self] tid, recipient, allDelivered, driveFileId, shouldDelete in
            Task { @MainActor [weak self] in
                await self?.handleRemoteTransferDelivered(
                    transferId: tid,
                    recipientDeviceId: recipient,
                    allDelivered: allDelivered,
                    driveFileId: driveFileId,
                    shouldDelete: shouldDelete
                )
            }
        }

        sync.onTransferExpired = { [weak self] tid, driveFileId in
            Task { @MainActor [weak self] in
                await self?.handleRemoteTransferExpired(transferId: tid, driveFileId: driveFileId)
            }
        }

        sync.onTransferDeleted = { [weak self] tid, driveFileId in
            Task { @MainActor [weak self] in
                await self?.handleRemoteTransferDeleted(transferId: tid, driveFileId: driveFileId)
            }
        }
    }

    // -------------------------------------------------------------------------
    // Outgoing Transfers (Image & Files)
    // -------------------------------------------------------------------------

    public func handleLocalImage(data: Data, mimeType: String, fileName: String, sourceApp: String?) {
        guard googleDrive.authStatus.isConnected else {
            self.latestErrorMessage = "Google Drive is not connected. Connect in Settings to sync images."
            return
        }

        let transferId = UUID().uuidString
        let record = TransferRecord(
            id: transferId,
            fileName: fileName,
            fileSizeBytes: Int64(data.count),
            mimeType: mimeType,
            isOutgoing: true,
            status: .uploading,
            statusText: "Encrypting image..."
        )
        insertOrUpdateRecord(record)

        let task = Task { [weak self] in
            guard let self = self else { return }
            do {
                // 1. Encrypt binary payload locally with AES-256-GCM
                let (ciphertext, iv, tag, sha256Hex) = try self.crypto.encryptBinary(data)

                self.updateRecord(id: transferId) { r in
                    r.statusText = "Uploading to Google Drive..."
                }

                // 2. Upload ciphertext to temporary Google Drive storage
                let driveFileId = try await self.googleDrive.uploadEncryptedPayload(encryptedData: ciphertext) { [weak self] progress in
                    Task { @MainActor [weak self] in
                        self?.updateRecord(id: transferId) { r in
                            r.progress = progress
                            r.bytesTransferred = Int64(Double(ciphertext.count) * progress)
                        }
                    }
                }

                // 3. Create TransferManifest
                let manifest = TransferManifest(
                    transferId: transferId,
                    senderDeviceId: self.deviceId,
                    fileName: fileName,
                    fileSizeBytes: Int64(ciphertext.count),
                    mimeType: mimeType,
                    sha256Hash: sha256Hex,
                    encryptionIv: iv,
                    encryptionTag: tag,
                    medium: .googleDrive,
                    storageReference: driveFileId,
                    driveFileId: driveFileId,
                    status: .ready
                )

                // 4. Register on Cloudflare backend
                try await self.registerTransferOnServer(manifest: manifest)

                // 5. Broadcast announcement over WebSocket
                self.syncService?.sendTransferInit(manifest: manifest)

                self.updateRecord(id: transferId) { r in
                    r.status = .ready
                    r.driveFileId = driveFileId
                    r.progress = 1.0
                    r.statusText = "Uploaded — Waiting for peer devices"
                }

            } catch {
                self.updateRecord(id: transferId) { r in
                    r.status = .failed
                    r.statusText = "Upload error: \(error.localizedDescription)"
                }
                self.latestErrorMessage = error.localizedDescription
            }
            self.activeTasks.removeValue(forKey: transferId)
        }
        activeTasks[transferId] = task
    }

    public func handleLocalFiles(urls: [URL], sourceApp: String?) {
        guard googleDrive.authStatus.isConnected else {
            self.latestErrorMessage = "Google Drive is not connected. Connect in Settings to sync files."
            return
        }

        let transferId = UUID().uuidString
        let fileManager = FileManager.default

        let task = Task { [weak self] in
            guard let self = self else { return }
            do {
                let payloadData: Data
                let displayFileName: String
                let mimeType: String

                var isDir: ObjCBool = false
                let isSingleFile = urls.count == 1 && fileManager.fileExists(atPath: urls[0].path, isDirectory: &isDir) && !isDir.boolValue

                if isSingleFile {
                    // Single file
                    let url = urls[0]
                    displayFileName = url.lastPathComponent
                    mimeType = self.mimeType(for: url)
                    payloadData = try Data(contentsOf: url)
                } else {
                    // Multiple files or Directory -> archive into a temporary zip
                    let formatter = DateFormatter()
                    formatter.dateFormat = "yyyyMMdd_HHmmss"
                    displayFileName = urls.count == 1 ? "\(urls[0].lastPathComponent).zip" : "Archive_\(formatter.string(from: Date())).zip"
                    mimeType = "application/zip"

                    let tempZipURL = fileManager.temporaryDirectory.appendingPathComponent(displayFileName)
                    try? fileManager.removeItem(at: tempZipURL)

                    payloadData = try self.createZipArchive(from: urls, destinationURL: tempZipURL)
                }

                guard Int64(payloadData.count) <= MAX_TRANSFER_BYTES else {
                    throw GoogleDriveService.GoogleDriveError.fileTooLarge(payloadData.count)
                }

                let record = TransferRecord(
                    id: transferId,
                    fileName: displayFileName,
                    fileSizeBytes: Int64(payloadData.count),
                    mimeType: mimeType,
                    isOutgoing: true,
                    status: .uploading,
                    statusText: "Encrypting file(s)..."
                )
                self.insertOrUpdateRecord(record)

                // 1. Encrypt binary payload locally with AES-256-GCM
                let (ciphertext, iv, tag, sha256Hex) = try self.crypto.encryptBinary(payloadData)

                self.updateRecord(id: transferId) { r in
                    r.statusText = "Uploading to Google Drive..."
                }

                // 2. Upload ciphertext to temporary Google Drive storage
                let driveFileId = try await self.googleDrive.uploadEncryptedPayload(encryptedData: ciphertext) { [weak self] progress in
                    Task { @MainActor [weak self] in
                        self?.updateRecord(id: transferId) { r in
                            r.progress = progress
                            r.bytesTransferred = Int64(Double(ciphertext.count) * progress)
                        }
                    }
                }

                // 3. Create TransferManifest
                let manifest = TransferManifest(
                    transferId: transferId,
                    senderDeviceId: self.deviceId,
                    fileName: displayFileName,
                    fileSizeBytes: Int64(ciphertext.count),
                    mimeType: mimeType,
                    sha256Hash: sha256Hex,
                    encryptionIv: iv,
                    encryptionTag: tag,
                    medium: .googleDrive,
                    storageReference: driveFileId,
                    driveFileId: driveFileId,
                    status: .ready
                )

                // 4. Register on Cloudflare backend
                try await self.registerTransferOnServer(manifest: manifest)

                // 5. Broadcast announcement over WebSocket
                self.syncService?.sendTransferInit(manifest: manifest)

                self.updateRecord(id: transferId) { r in
                    r.status = .ready
                    r.driveFileId = driveFileId
                    r.progress = 1.0
                    r.statusText = "Uploaded — Waiting for peer devices"
                }

            } catch {
                self.updateRecord(id: transferId) { r in
                    r.status = .failed
                    r.statusText = "Transfer error: \(error.localizedDescription)"
                }
                self.latestErrorMessage = error.localizedDescription
            }
            self.activeTasks.removeValue(forKey: transferId)
        }
        activeTasks[transferId] = task
    }

    // -------------------------------------------------------------------------
    // Incoming Transfers (Download, Decrypt, Verify & Deliver)
    // -------------------------------------------------------------------------

    public func handleRemoteTransferAnnounced(manifest: TransferManifest) async {
        // Skip if initiated by ourselves
        guard manifest.senderDeviceId != deviceId else { return }

        // Skip if already processed or acknowledged
        guard !acknowledgedTransferIds.contains(manifest.transferId) else { return }
        acknowledgedTransferIds.insert(manifest.transferId)

        guard let driveFileId = manifest.driveFileId ?? manifest.storageReference else { return }

        let record = TransferRecord(
            id: manifest.transferId,
            fileName: manifest.fileName,
            fileSizeBytes: manifest.fileSizeBytes,
            mimeType: manifest.mimeType,
            isOutgoing: false,
            status: .downloading,
            statusText: "Downloading encrypted payload...",
            driveFileId: driveFileId
        )
        insertOrUpdateRecord(record)

        do {
            // 1. Download encrypted payload from Google Drive
            let ciphertext = try await googleDrive.downloadEncryptedPayload(driveFileId: driveFileId) { [weak self] progress in
                Task { @MainActor [weak self] in
                    self?.updateRecord(id: manifest.transferId) { r in
                        r.progress = progress
                        r.bytesTransferred = Int64(Double(manifest.fileSizeBytes) * progress)
                    }
                }
            }

            self.updateRecord(id: manifest.transferId) { r in
                r.statusText = "Verifying & Decrypting..."
            }

            // 2. Decrypt with AES-256-GCM and verify SHA-256 integrity
            let decryptedData = try crypto.decryptBinary(
                ciphertext: ciphertext,
                iv: manifest.encryptionIv,
                tag: manifest.encryptionTag,
                expectedSha256: manifest.sha256Hash
            )

            // 3. Deliver to macOS clipboard and Downloads folder
            let localURL = try deliverPayload(data: decryptedData, fileName: manifest.fileName, mimeType: manifest.mimeType)

            self.updateRecord(id: manifest.transferId) { r in
                r.status = .delivered
                r.progress = 1.0
                r.localURL = localURL
                r.statusText = "Delivered to Clipboard & Downloads"
            }

            // 4. Send ACK to Cloudflare server and WebSocket
            let ackResp = try await sendAckToServer(transferId: manifest.transferId)
            self.syncService?.sendTransferAck(transferId: manifest.transferId)

            // 5. If all devices have received, delete immediately from Drive!
            if ackResp.allDelivered || ackResp.shouldDelete {
                _ = await googleDrive.deleteFile(driveFileId: driveFileId)
                await deleteTransferOnServer(transferId: manifest.transferId)
                self.updateRecord(id: manifest.transferId) { r in
                    r.statusText = "Delivered & Purged from Drive"
                }
            }

        } catch {
            self.updateRecord(id: manifest.transferId) { r in
                r.status = .failed
                r.statusText = "Delivery failed: \(error.localizedDescription)"
            }
            self.latestErrorMessage = "Failed to receive '\(manifest.fileName)': \(error.localizedDescription)"
        }
    }

    private func deliverPayload(data: Data, fileName: String, mimeType: String) throws -> URL {
        let fileManager = FileManager.default
        let downloadsDir = fileManager.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? fileManager.temporaryDirectory

        var destinationURL = downloadsDir.appendingPathComponent(fileName)
        // If file exists, avoid overwriting by appending timestamp
        if fileManager.fileExists(atPath: destinationURL.path) {
            let baseName = destinationURL.deletingPathExtension().lastPathComponent
            let ext = destinationURL.pathExtension
            let timestamp = Int(Date().timeIntervalSince1970)
            let uniqueName = ext.isEmpty ? "\(baseName)_\(timestamp)" : "\(baseName)_\(timestamp).\(ext)"
            destinationURL = downloadsDir.appendingPathComponent(uniqueName)
        }

        try data.write(to: destinationURL)

        if mimeType.starts(with: "image/") {
            // Write to clipboard as image
            clipboardMonitor?.setRemoteClipboardImage(data: data)
        } else {
            // Write to clipboard as Finder file URL
            clipboardMonitor?.setRemoteClipboardFiles(urls: [destinationURL])
        }

        return destinationURL
    }

    // -------------------------------------------------------------------------
    // Delivery & Cleanup Handlers (Three-Tier Cleanup)
    // -------------------------------------------------------------------------

    public func handleRemoteTransferDelivered(
        transferId: String,
        recipientDeviceId: String,
        allDelivered: Bool,
        driveFileId: String?,
        shouldDelete: Bool
    ) async {
        if allDelivered || shouldDelete {
            // Purge from Google Drive immediately!
            if let dfid = driveFileId {
                _ = await googleDrive.deleteFile(driveFileId: dfid)
            }
            await deleteTransferOnServer(transferId: transferId)

            updateRecord(id: transferId) { r in
                r.status = .delivered
                r.statusText = "Delivered to all peers & Purged from Drive"
            }
        }
    }

    public func handleRemoteTransferExpired(transferId: String, driveFileId: String?) async {
        if let dfid = driveFileId {
            _ = await googleDrive.deleteFile(driveFileId: dfid)
        }
        updateRecord(id: transferId) { r in
            r.status = .expired
            r.statusText = "Expired after 24h (Purged from Drive)"
        }
    }

    public func handleRemoteTransferDeleted(transferId: String, driveFileId: String?) async {
        if let dfid = driveFileId {
            _ = await googleDrive.deleteFile(driveFileId: dfid)
        }
        updateRecord(id: transferId) { r in
            r.status = .cancelled
            r.statusText = "Cancelled & Purged from Drive"
        }
    }

    public func cancelTransfer(recordId: String) {
        if let task = activeTasks[recordId] {
            task.cancel()
            activeTasks.removeValue(forKey: recordId)
        }

        guard let record = transfers.first(where: { $0.id == recordId }) else { return }

        Task { @MainActor in
            if let dfid = record.driveFileId {
                _ = await googleDrive.deleteFile(driveFileId: dfid)
            }
            await deleteTransferOnServer(transferId: recordId)
            syncService?.sendTransferCancel(transferId: recordId, reason: "Cancelled by user")

            updateRecord(id: recordId) { r in
                r.status = .cancelled
                r.statusText = "Cancelled"
            }
        }
    }

    public func retryTransfer(recordId: String) {
        guard let record = transfers.first(where: { $0.id == recordId }) else { return }

        if record.isOutgoing, let localURL = record.localURL {
            handleLocalFiles(urls: [localURL], sourceApp: nil)
        } else {
            // Incoming retry: re-fetch from pending list
            Task { @MainActor in
                await syncPendingTransfersOnStartupOrReconnect()
            }
        }
    }

    // -------------------------------------------------------------------------
    // Offline Recovery & Garbage Collection Sweep
    // -------------------------------------------------------------------------

    public func syncPendingTransfersOnStartupOrReconnect() async {
        guard !token.isEmpty else { return }

        do {
            guard let url = URL(string: "\(cleanServerUrl)/v1/transfers/pending") else { return }
            var req = URLRequest(url: url)
            req.timeoutInterval = 15.0
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

            let (data, response) = try await URLSession.shared.data(for: req)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else { return }

            let pendingResp = try JSONDecoder().decode(PendingTransfersResponse.self, from: data)

            for manifest in pendingResp.transfers {
                if manifest.senderDeviceId != deviceId && !acknowledgedTransferIds.contains(manifest.transferId) {
                    await handleRemoteTransferAnnounced(manifest: manifest)
                }
            }
        } catch {
            print("[ClipSync] syncPendingTransfers error: \(error)")
        }
    }

    public func sweepExpiredAndOrphanedFiles() async {
        if googleDrive.authStatus.isConnected {
            let swept = await googleDrive.sweepOrphanedFiles(olderThanSeconds: 24 * 3600)
            if swept > 0 {
                print("[ClipSync] Swept \(swept) orphaned Drive files older than 24h")
            }
        }
    }

    // -------------------------------------------------------------------------
    // REST API Helpers
    // -------------------------------------------------------------------------

    private var cleanServerUrl: String {
        let trimmed = serverUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }

    private func registerTransferOnServer(manifest: TransferManifest) async throws {
        guard let url = URL(string: "\(cleanServerUrl)/v1/transfers/init") else { return }
        var req = URLRequest(url: url)
        req.timeoutInterval = 15.0
        req.httpMethod = "POST"
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")

        let reqPayload = InitTransferRequest(manifest: manifest)
        req.httpBody = try JSONEncoder().encode(reqPayload)

        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let err = String(data: data, encoding: .utf8) ?? ""
            throw GoogleDriveService.GoogleDriveError.networkError("Init transfer failed: \(err)")
        }
    }

    private func sendAckToServer(transferId: String) async throws -> AckTransferResponse {
        guard let url = URL(string: "\(cleanServerUrl)/v1/transfers/\(transferId)/ack") else {
            throw GoogleDriveService.GoogleDriveError.networkError("Invalid ACK URL")
        }
        var req = URLRequest(url: url)
        req.timeoutInterval = 15.0
        req.httpMethod = "POST"
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")

        let body = AckTransferRequest(recipientDeviceId: deviceId)
        req.httpBody = try JSONEncoder().encode(body)

        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            let err = String(data: data, encoding: .utf8) ?? ""
            throw GoogleDriveService.GoogleDriveError.networkError("ACK failed: \(err)")
        }

        return try JSONDecoder().decode(AckTransferResponse.self, from: data)
    }

    private func deleteTransferOnServer(transferId: String) async {
        guard let url = URL(string: "\(cleanServerUrl)/v1/transfers/\(transferId)") else { return }
        var req = URLRequest(url: url)
        req.timeoutInterval = 15.0
        req.httpMethod = "DELETE"
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        _ = try? await URLSession.shared.data(for: req)
    }

    // -------------------------------------------------------------------------
    // Utilities (Zip Archiving & Record State)
    // -------------------------------------------------------------------------

    private func createZipArchive(from urls: [URL], destinationURL: URL) throws -> Data {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        var arguments = ["-c", "-k", "--sequesterRsrc"]
        for u in urls {
            arguments.append(u.path)
        }
        arguments.append(destinationURL.path)
        process.arguments = arguments

        try process.run()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            throw GoogleDriveService.GoogleDriveError.networkError("Archive creation failed with code \(process.terminationStatus)")
        }

        return try Data(contentsOf: destinationURL)
    }

    private func mimeType(for url: URL) -> String {
        let ext = url.pathExtension.lowercased()
        switch ext {
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "gif": return "image/gif"
        case "webp": return "image/webp"
        case "pdf": return "application/pdf"
        case "zip": return "application/zip"
        case "tar", "gz": return "application/gzip"
        case "txt": return "text/plain"
        default: return "application/octet-stream"
        }
    }

    private func insertOrUpdateRecord(_ record: TransferRecord) {
        if let idx = transfers.firstIndex(where: { $0.id == record.id }) {
            transfers[idx] = record
        } else {
            transfers.insert(record, at: 0)
        }
        if transfers.count > 50 {
            transfers.removeLast()
        }
    }

    private func updateRecord(id: String, mutate: (inout TransferRecord) -> Void) {
        if let idx = transfers.firstIndex(where: { $0.id == id }) {
            mutate(&transfers[idx])
        }
    }
}
