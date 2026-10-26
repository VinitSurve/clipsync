import AppKit

public final class ClipboardMonitor {
    private let pasteboard = NSPasteboard.general
    private var lastChangeCount: Int
    private var timer: Timer?

    private var lastReceivedHash: String = ""
    private var lastSentHash: String = ""

    public var isPaused: Bool = false
    public var excludeSensitiveApps: Bool = true

    public var onNewLocalCopy: ((String, String?) -> Void)?

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

        // Check source application
        let frontmostApp = NSWorkspace.shared.frontmostApplication
        let bundleId = frontmostApp?.bundleIdentifier ?? ""

        if excludeSensitiveApps && sensitiveAppIdentifiers.contains(bundleId) {
            return
        }

        guard let text = pasteboard.string(forType: .string), !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return
        }

        let hash = ClipSyncCrypto.sha256Hex(text)

        // Prevent echo loop: if this text came from remote sync, do not re-broadcast
        if hash == lastReceivedHash {
            return
        }

        // Prevent duplicate broadcast if identical to last sent
        if hash == lastSentHash {
            return
        }

        lastSentHash = hash
        onNewLocalCopy?(text, frontmostApp?.localizedName)
    }

    /**
     * Called when a remote sync item arrives.
     * Sets pasteboard content and registers hash to prevent echo loops.
     */
    public func setRemoteClipboard(text: String) {
        let hash = ClipSyncCrypto.sha256Hex(text)
        lastReceivedHash = hash

        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        lastChangeCount = pasteboard.changeCount
    }
}
