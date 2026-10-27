import AppKit

public enum LocalClipboardEvent {
    case text(content: String, sourceApp: String?)
    case image(data: Data, mimeType: String, fileName: String, sourceApp: String?)
    case files(urls: [URL], sourceApp: String?)
    case error(message: String)
}

public final class ClipboardMonitor {
    private let pasteboard = NSPasteboard.general
    private var lastChangeCount: Int
    private var timer: Timer?

    private var lastReceivedHash: String = ""
    private var lastSentHash: String = ""

    public var isPaused: Bool = false
    public var excludeSensitiveApps: Bool = true

    public var onClipboardEvent: ((LocalClipboardEvent) -> Void)?
    public var onNewLocalCopy: ((String, String?) -> Void)? // Backwards compatibility for text sync

    private let sensitiveAppIdentifiers: Set<String> = [
        "com.agilebits.onepassword7",
        "com.1password.1password",
        "com.bitwarden.desktop",
        "com.apple.keychainaccess",
        "org.keepassxc.keepassxc",
        "com.dashlane.dashlane"
    ]

    public init() {
        self.lastChangeCount = pasteboard.changeCount
    }

    public func start() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.checkClipboard()
        }
    }

    public func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func checkClipboard() {
        guard !isPaused else { return }

        let currentChangeCount = pasteboard.changeCount
        guard currentChangeCount != lastChangeCount else { return }
        lastChangeCount = currentChangeCount

        // 1. Check source application
        let frontmostApp = NSWorkspace.shared.frontmostApplication
        let bundleId = frontmostApp?.bundleIdentifier ?? ""
        let appName = frontmostApp?.localizedName

        if excludeSensitiveApps && sensitiveAppIdentifiers.contains(bundleId) {
            return
        }

        // 2. Check for copied Files / Folders (Finder copy)
        if let fileUrls = pasteboard.readObjects(forClasses: [NSURL.self], options: [
            NSPasteboard.ReadingOptionKey.urlReadingFileURLsOnly: true
        ]) as? [URL], !fileUrls.isEmpty {
            handleFilesCopy(urls: fileUrls, appName: appName)
            return
        }

        // 3. Check for copied Image data (Screenshots, browser images, Preview copy)
        if let imageData = extractImageDataFromPasteboard() {
            handleImageCopy(data: imageData, appName: appName)
            return
        }

        // 4. Check for copied Text
        if let text = pasteboard.string(forType: .string), !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            handleTextCopy(text: text, appName: appName)
            return
        }
    }

    // -------------------------------------------------------------------------
    // Local Copy Handlers
    // -------------------------------------------------------------------------

    private func handleFilesCopy(urls: [URL], appName: String?) {
        let fileManager = FileManager.default
        var totalSize: Int64 = 0
        var validatedUrls: [URL] = []

        for url in urls {
            guard fileManager.fileExists(atPath: url.path) else { continue }

            var isDir: ObjCBool = false
            fileManager.fileExists(atPath: url.path, isDirectory: &isDir)

            let itemSize: Int64
            if isDir.boolValue {
                itemSize = calculateDirectorySize(at: url)
            } else {
                let attrs = try? fileManager.attributesOfItem(atPath: url.path)
                itemSize = (attrs?[.size] as? Int64) ?? 0
            }

            if itemSize > MAX_TRANSFER_BYTES {
                let sizeStr = ByteCountFormatter.string(fromByteCount: itemSize, countStyle: .file)
                onClipboardEvent?(.error(message: "'\(url.lastPathComponent)' exceeds 100 MB limit (\(sizeStr))"))
                return
            }

            totalSize += itemSize
            validatedUrls.append(url)
        }

        guard !validatedUrls.isEmpty else { return }

        if totalSize > MAX_TRANSFER_BYTES {
            let totalStr = ByteCountFormatter.string(fromByteCount: totalSize, countStyle: .file)
            onClipboardEvent?(.error(message: "Selected files exceed 100 MB limit (\(totalStr))"))
            return
        }

        // Build stable hash over paths and modification dates
        var hashInput = ""
        for u in validatedUrls {
            let modDate = (try? fileManager.attributesOfItem(atPath: u.path)[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
            hashInput += "\(u.path):\(modDate);"
        }
        let hash = ClipSyncCrypto.sha256Hex(hashInput)

        if hash == lastReceivedHash || hash == lastSentHash {
            return
        }

        lastSentHash = hash
        onClipboardEvent?(.files(urls: validatedUrls, sourceApp: appName))
    }

    private func handleImageCopy(data: Data, appName: String?) {
        guard Int64(data.count) <= MAX_TRANSFER_BYTES else {
            onClipboardEvent?(.error(message: "Image exceeds 100 MB limit"))
            return
        }

        let hash = ClipSyncCrypto.sha256Hex(data)

        if hash == lastReceivedHash || hash == lastSentHash {
            return
        }

        lastSentHash = hash
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd_HHmmss"
        let timestamp = formatter.string(from: Date())
        let fileName = "Clipboard_\(timestamp).png"

        onClipboardEvent?(.image(data: data, mimeType: "image/png", fileName: fileName, sourceApp: appName))
    }

    private func handleTextCopy(text: String, appName: String?) {
        let hash = ClipSyncCrypto.sha256Hex(text)

        if hash == lastReceivedHash || hash == lastSentHash {
            return
        }

        lastSentHash = hash
        onClipboardEvent?(.text(content: text, sourceApp: appName))
        onNewLocalCopy?(text, appName)
    }

    private func extractImageDataFromPasteboard() -> Data? {
        // Prefer standard PNG representation
        if let pngData = pasteboard.data(forType: .png) {
            return pngData
        }

        // Convert TIFF to PNG
        if let tiffData = pasteboard.data(forType: .tiff),
           let rep = NSBitmapImageRep(data: tiffData),
           let png = rep.representation(using: .png, properties: [:]) {
            return png
        }

        // Convert NSImage to PNG if available
        if pasteboard.canReadObject(forClasses: [NSImage.self], options: nil),
           let images = pasteboard.readObjects(forClasses: [NSImage.self], options: nil) as? [NSImage],
           let image = images.first,
           let tiff = image.tiffRepresentation,
           let rep = NSBitmapImageRep(data: tiff),
           let png = rep.representation(using: .png, properties: [:]) {
            return png
        }

        return nil
    }

    private func calculateDirectorySize(at url: URL) -> Int64 {
        let fileManager = FileManager.default
        var total: Int64 = 0
        if let enumerator = fileManager.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey], options: [.skipsHiddenFiles]) {
            for case let fileURL as URL in enumerator {
                if let size = try? fileURL.resourceValues(forKeys: [.fileSizeKey]).fileSize {
                    total += Int64(size)
                }
            }
        }
        return total
    }

    // -------------------------------------------------------------------------
    // Remote Clipboard Setters (with Echo-Loop Prevention)
    // -------------------------------------------------------------------------

    public func setRemoteClipboard(text: String) {
        let hash = ClipSyncCrypto.sha256Hex(text)
        lastReceivedHash = hash

        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        lastChangeCount = pasteboard.changeCount
    }

    public func setRemoteClipboardImage(data: Data) {
        let hash = ClipSyncCrypto.sha256Hex(data)
        lastReceivedHash = hash

        pasteboard.clearContents()
        pasteboard.setData(data, forType: .png)

        if let rep = NSBitmapImageRep(data: data),
           let tiff = rep.representation(using: .tiff, properties: [:]) {
            pasteboard.setData(tiff, forType: .tiff)
        }

        lastChangeCount = pasteboard.changeCount
    }

    public func setRemoteClipboardFiles(urls: [URL]) {
        let fileManager = FileManager.default
        var hashInput = ""
        for u in urls {
            let modDate = (try? fileManager.attributesOfItem(atPath: u.path)[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
            hashInput += "\(u.path):\(modDate);"
        }
        let hash = ClipSyncCrypto.sha256Hex(hashInput)
        lastReceivedHash = hash

        pasteboard.clearContents()
        pasteboard.writeObjects(urls as [NSPasteboardWriting])
        lastChangeCount = pasteboard.changeCount
    }
}
