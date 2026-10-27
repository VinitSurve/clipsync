import Foundation
import Combine
import AppKit
import Network

public struct DisplayClipboardItem: Identifiable, Equatable {
    public let id: String
    public let text: String
    public let sourceApp: String?
    public let timestamp: Date
    public var isPinned: Bool
    public let isRemote: Bool
}

@MainActor
public final class AppState: ObservableObject {
    private static let serverUrlKey = "clipsync.serverUrl"
    private static let syncPassphraseKey = "clipsync.syncPassphrase"

    // Safe diagnostic logging (Never logs tokens or secrets)
    public static func logDiag(_ message: String) {
        let formatter = ISO8601DateFormatter()
        let timestamp = formatter.string(from: Date())
        print("[SyncServer] [\(timestamp)] \(message)")
    }
    private func logDiag(_ message: String) {
        Self.logDiag(message)
    }

    public typealias ValidationResult = DeviceValidationResult

    private var reconnectTimer: Timer?
    private var reconnectAttempts: Int = 0
    private let pathMonitor = NWPathMonitor()
    private let monitorQueue = DispatchQueue(label: "com.clipsync.networkmonitor")

    @Published public var connectionState: WebSocketSyncService.ConnectionState = .disconnected
    @Published public var isPaused: Bool = false
    @Published public var history: [DisplayClipboardItem] = []
    @Published public var devices: [DeviceInfo] = []
    @Published public var activePairingCode: String?
    @Published public var pairingQrDataUrl: String?
    @Published public var statusMessage: String = "Ready"

    @Published public var googleDrive: GoogleDriveService = .shared
    @Published public var transferManager: TransferManager?

    @Published public var serverUrl: String {
        didSet {
            UserDefaults.standard.set(serverUrl, forKey: Self.serverUrlKey)
        }
    }
    @Published public var syncPassphrase: String {
        didSet {
            UserDefaults.standard.set(syncPassphrase, forKey: Self.syncPassphraseKey)
        }
    }

    public var cleanServerUrl: String {
        let trimmed = serverUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }

    private var tokenKey: String { "clipsync.token.\(cleanServerUrl)" }
    private var deviceIdKey: String { "clipsync.deviceId.\(cleanServerUrl)" }
    private var syncSaltKey: String { "clipsync.syncSalt.\(cleanServerUrl)" }

    private var clipboardMonitor: ClipboardMonitor?
    private var syncService: WebSocketSyncService?
    private var crypto: ClipSyncCrypto?

    private var deviceId: String = ""
    private var token: String = ""
    private var syncSalt: String = ""

    @Published public var launchAtLogin: Bool = false

    private var launchAgentURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/com.clipsync.macos.plist")
    }

    private func checkLaunchAtLoginStatus() {
        self.launchAtLogin = FileManager.default.fileExists(atPath: launchAgentURL.path)
    }

    public func setLaunchAtLogin(_ enabled: Bool) {
        let fileManager = FileManager.default
        let agentPath = launchAgentURL.path
        let agentDir = launchAgentURL.deletingLastPathComponent().path

        if enabled {
            do {
                if !fileManager.fileExists(atPath: agentDir) {
                    try fileManager.createDirectory(atPath: agentDir, withIntermediateDirectories: true)
                }

                let executablePath: String
                if Bundle.main.bundlePath.hasSuffix(".app") {
                    executablePath = Bundle.main.executablePath ?? "/Applications/ClipSync.app/Contents/MacOS/ClipSync"
                } else {
                    executablePath = "/Applications/ClipSync.app/Contents/MacOS/ClipSync"
                }

                let plistContent = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.clipsync.macos</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>\(executablePath)</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>ProcessType</key>
                    <string>Interactive</string>
                </dict>
                </plist>
                """
                try plistContent.write(toFile: agentPath, atomically: true, encoding: .utf8)
                self.launchAtLogin = true
                self.statusMessage = "Launch at login enabled"
            } catch {
                self.statusMessage = "Could not enable auto-launch: \(error.localizedDescription)"
            }
        } else {
            do {
                if fileManager.fileExists(atPath: agentPath) {
                    try fileManager.removeItem(atPath: agentPath)
                }
                self.launchAtLogin = false
                self.statusMessage = "Launch at login disabled"
            } catch {
                self.statusMessage = "Could not disable auto-launch: \(error.localizedDescription)"
            }
        }
    }

    public init() {
        let savedUrl = UserDefaults.standard.string(forKey: Self.serverUrlKey) ?? "https://clipsync-server-cloudflare.survevinit56.workers.dev"
        let savedPass = UserDefaults.standard.string(forKey: Self.syncPassphraseKey) ?? "clipsync-master-sync-passphrase"
        self.serverUrl = savedUrl
        self.syncPassphrase = savedPass
        checkLaunchAtLoginStatus()
        setupNetworkMonitoring()
        bootstrapDevice()
    }

    private func setupNetworkMonitoring() {
        pathMonitor.pathUpdateHandler = { [weak self] path in
            DispatchQueue.main.async {
                guard let self = self else { return }
                let isOnline = path.status == .satisfied
                self.logDiag("Network interface status: \(isOnline ? "Satisfied (Online)" : "Unsatisfied (Offline)")")
                if isOnline && self.connectionState == .disconnected {
                    self.logDiag("Network available; triggering immediate reconnection...")
                    self.reconnectAttempts = 0
                    Task {
                        await self.connectToServer()
                    }
                }
            }
        }
        pathMonitor.start(queue: monitorQueue)
    }

    private func cancelAutoReconnect() {
        reconnectTimer?.invalidate()
        reconnectTimer = nil
    }

    private func scheduleAutoReconnect() {
        reconnectTimer?.invalidate()
        let delay = min(pow(2.0, Double(min(reconnectAttempts, 5))), 30.0) // 2s, 4s, 8s, 16s, 30s
        reconnectAttempts += 1
        logDiag("Scheduling auto-reconnect attempt #\(reconnectAttempts) in \(Int(delay))s...")
        reconnectTimer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self = self else { return }
                guard self.connectionState != .connected else { return }
                self.logDiag("Executing scheduled auto-reconnect attempt #\(self.reconnectAttempts)...")
                await self.connectToServer()
            }
        }
    }

    public func bootstrapDevice() {
        Task {
            await connectToServer()
        }
    }

    public func connectToServer() async {
        cancelAutoReconnect()
        let targetUrl = cleanServerUrl
        guard !targetUrl.isEmpty, let _ = URL(string: targetUrl) else {
            self.statusMessage = "Invalid Server URL"
            self.connectionState = .disconnected
            return
        }

        self.statusMessage = "Connecting to \(targetUrl)..."
        syncService?.disconnect()
        self.connectionState = .connecting
        self.activePairingCode = nil
        self.pairingQrDataUrl = nil

        // Check if we have cached token & deviceId for this specific server URL
        let cachedToken = UserDefaults.standard.string(forKey: tokenKey)
        let cachedDeviceId = UserDefaults.standard.string(forKey: deviceIdKey)
        let cachedSalt = UserDefaults.standard.string(forKey: syncSaltKey)

        if let token = cachedToken, let deviceId = cachedDeviceId, let salt = cachedSalt, !token.isEmpty {
            logDiag("Validating cached credentials against \(targetUrl)...")
            let validation = await validateCredentials(serverUrl: targetUrl, token: token)
            switch validation {
            case .valid:
                logDiag("Cached credentials verified successfully. Initializing sync services...")
                self.deviceId = deviceId
                self.token = token
                self.syncSalt = salt
                self.reconnectAttempts = 0
                initServicesAndConnect()
                await self.refreshDevices()
                self.statusMessage = "Connected to ClipSync"
                return

            case .invalidCredentials(let code):
                logDiag("Cached credentials rejected by server (HTTP \(code)). Clearing stale credentials...")
                UserDefaults.standard.removeObject(forKey: tokenKey)
                UserDefaults.standard.removeObject(forKey: deviceIdKey)
                UserDefaults.standard.removeObject(forKey: syncSaltKey)
                // Fall through to register clean device credentials on this server

            case .unreachable(let reason, let elapsed):
                logDiag("Server unreachable during credential validation (\(elapsed)ms): \(reason)")
                self.connectionState = .disconnected
                self.statusMessage = "Server unreachable (\(reason))"
                scheduleAutoReconnect()
                return
            }
        }

        // Otherwise register clean device credentials on this server
        await registerFreshDevice(serverUrl: targetUrl)
    }

    public func validateCredentials(serverUrl: String, token: String) async -> ValidationResult {
        guard let url = URL(string: "\(serverUrl)/v1/devices") else {
            return .unreachable(reason: "Invalid server URL", elapsedMs: 0)
        }
        var req = URLRequest(url: url)
        req.timeoutInterval = 10.0
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let start = Date()
        do {
            let (_, response) = try await URLSession.shared.data(for: req)
            let elapsed = Int(Date().timeIntervalSince(start) * 1000)
            guard let http = response as? HTTPURLResponse else {
                logDiag("GET /v1/devices -> Non-HTTP response in \(elapsed)ms")
                return .unreachable(reason: "Non-HTTP server response", elapsedMs: elapsed)
            }
            logDiag("GET /v1/devices -> HTTP \(http.statusCode) in \(elapsed)ms")
            if http.statusCode == 200 {
                return .valid
            } else if http.statusCode == 401 || http.statusCode == 403 {
                return .invalidCredentials(statusCode: http.statusCode)
            } else {
                return .unreachable(reason: "HTTP \(http.statusCode)", elapsedMs: elapsed)
            }
        } catch {
            let elapsed = Int(Date().timeIntervalSince(start) * 1000)
            logDiag("GET /v1/devices failed in \(elapsed)ms: \(error.localizedDescription)")
            return .unreachable(reason: error.localizedDescription, elapsedMs: elapsed)
        }
    }

    private func registerFreshDevice(serverUrl: String) async {
        self.statusMessage = "Registering device on server..."
        guard let url = URL(string: "\(serverUrl)/v1/devices/register") else {
            self.statusMessage = "Invalid URL: \(serverUrl)/v1/devices/register"
            self.connectionState = .disconnected
            return
        }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.timeoutInterval = 10.0
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: Any] = [
            "label": "MacBook Air M5",
            "platform": "macos"
        ]
        let start = Date()
        do {
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: req)
            let elapsed = Int(Date().timeIntervalSince(start) * 1000)
            guard let http = response as? HTTPURLResponse else {
                logDiag("POST /v1/devices/register -> Non-HTTP response in \(elapsed)ms")
                self.statusMessage = "Registration failed: No server response"
                self.connectionState = .disconnected
                scheduleAutoReconnect()
                return
            }
            logDiag("POST /v1/devices/register -> HTTP \(http.statusCode) in \(elapsed)ms")
            guard (200...299).contains(http.statusCode) else {
                let errText = String(data: data, encoding: .utf8) ?? ""
                self.statusMessage = "Registration failed (HTTP \(http.statusCode)): \(errText)"
                self.connectionState = .disconnected
                scheduleAutoReconnect()
                return
            }

            let resp = try JSONDecoder().decode(RegisterDeviceResponse.self, from: data)

            self.deviceId = resp.deviceId
            self.token = resp.token
            self.syncSalt = resp.syncSalt
            self.reconnectAttempts = 0

            UserDefaults.standard.set(resp.token, forKey: tokenKey)
            UserDefaults.standard.set(resp.deviceId, forKey: deviceIdKey)
            UserDefaults.standard.set(resp.syncSalt, forKey: syncSaltKey)

            initServicesAndConnect()
            await self.refreshDevices()
            self.statusMessage = "Connected to ClipSync"
        } catch {
            let elapsed = Int(Date().timeIntervalSince(start) * 1000)
            logDiag("POST /v1/devices/register failed in \(elapsed)ms: \(error.localizedDescription)")
            self.statusMessage = "Registration error (\(elapsed)ms): \(error.localizedDescription)"
            self.connectionState = .disconnected
            scheduleAutoReconnect()
        }
    }

    private func initServicesAndConnect() {
        do {
            self.crypto = try ClipSyncCrypto(passphrase: syncPassphrase, salt: syncSalt)
            self.setupSync()
        } catch {
            self.statusMessage = "Crypto error: \(error.localizedDescription)"
        }
    }

    private func setupSync() {
        guard let crypto = self.crypto else { return }

        clipboardMonitor?.stop()
        syncService?.disconnect()

        let monitor = ClipboardMonitor()
        self.clipboardMonitor = monitor

        let sync = WebSocketSyncService(
            serverUrl: cleanServerUrl,
            deviceId: deviceId,
            token: token,
            crypto: crypto
        )
        self.syncService = sync

        let manager = TransferManager(
            serverUrl: cleanServerUrl,
            deviceId: deviceId,
            token: token,
            crypto: crypto,
            googleDrive: googleDrive,
            syncService: sync,
            clipboardMonitor: monitor
        )
        self.transferManager = manager

        sync.onStateChange = { [weak self] state in
            DispatchQueue.main.async {
                self?.connectionState = state
                if state == .connected {
                    self?.statusMessage = "Connected to ClipSync"
                    Task { [weak self] in
                        await self?.transferManager?.syncPendingTransfersOnStartupOrReconnect()
                        await self?.transferManager?.sweepExpiredAndOrphanedFiles()
                    }
                } else if state == .disconnected {
                    if self?.statusMessage == "Connected to ClipSync" {
                        self?.statusMessage = "Disconnected from sync server"
                    }
                }
            }
        }

        sync.onRemoteItemReceived = { [weak self] item, text in
            DispatchQueue.main.async {
                self?.clipboardMonitor?.setRemoteClipboard(text: text)
                self?.addItemToHistory(text: text, sourceApp: item.metadata?.sourceApp, isRemote: true)
                self?.statusMessage = "Synced from remote"
            }
        }

        sync.onDeviceEvent = { [weak self] msg in
            DispatchQueue.main.async {
                self?.statusMessage = msg
                Task { [weak self] in
                    await self?.refreshDevices()
                }
            }
        }

        monitor.onClipboardEvent = { [weak self] event in
            DispatchQueue.main.async {
                guard let self = self else { return }
                switch event {
                case .text(let text, let app):
                    self.syncService?.broadcastText(text, sourceApp: app)
                    self.addItemToHistory(text: text, sourceApp: app, isRemote: false)
                    self.statusMessage = "Copied & Synced Text"

                case .image(let data, let mimeType, let fileName, let app):
                    self.transferManager?.handleLocalImage(data: data, mimeType: mimeType, fileName: fileName, sourceApp: app)
                    self.addItemToHistory(text: "🖼️ \(fileName)", sourceApp: app, isRemote: false)
                    self.statusMessage = "Uploading image: \(fileName)..."

                case .files(let urls, let app):
                    self.transferManager?.handleLocalFiles(urls: urls, sourceApp: app)
                    let name = urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files"
                    self.addItemToHistory(text: "📁 \(name)", sourceApp: app, isRemote: false)
                    self.statusMessage = "Uploading: \(name)..."

                case .error(let message):
                    self.statusMessage = message
                }
            }
        }

        monitor.start()
        sync.connect()
    }

    public func connectGoogleDrive() {
        googleDrive.startAuthorization()
    }

    public func disconnectGoogleDrive() {
        googleDrive.disconnect()
        statusMessage = "Google Drive disconnected"
    }

    public func cancelTransfer(recordId: String) {
        transferManager?.cancelTransfer(recordId: recordId)
    }

    public func retryTransfer(recordId: String) {
        transferManager?.retryTransfer(recordId: recordId)
    }

    public func sweepGoogleDrive() {
        Task {
            statusMessage = "Sweeping expired Drive files..."
            await transferManager?.sweepExpiredAndOrphanedFiles()
            statusMessage = "Drive sweep completed"
        }
    }

    public func togglePause() {
        isPaused.toggle()
        clipboardMonitor?.isPaused = isPaused
        if isPaused {
            syncService?.pause()
            statusMessage = "Sync paused"
        } else {
            syncService?.resume()
            statusMessage = "Sync resumed"
        }
    }

    public func createPairingSession() {
        Task {
            do {
                guard !token.isEmpty else {
                    self.statusMessage = "Pairing failed: Device not registered. Reconnecting..."
                    await connectToServer()
                    return
                }
                guard let url = URL(string: "\(cleanServerUrl)/v1/auth/pairing/session") else {
                    self.statusMessage = "Pairing failed: Invalid URL (\(cleanServerUrl))"
                    return
                }
                var req = URLRequest(url: url)
                req.httpMethod = "POST"
                req.timeoutInterval = 10.0
                req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

                let start = Date()
                let (data, response) = try await URLSession.shared.data(for: req)
                let elapsed = Int(Date().timeIntervalSince(start) * 1000)
                guard let http = response as? HTTPURLResponse else {
                    self.statusMessage = "Pairing failed: Invalid server response"
                    return
                }
                logDiag("POST /v1/auth/pairing/session -> HTTP \(http.statusCode) in \(elapsed)ms")

                if http.statusCode == 401 {
                    self.statusMessage = "Pairing failed: Token expired or rejected by server. Re-registering..."
                    UserDefaults.standard.removeObject(forKey: tokenKey)
                    await registerFreshDevice(serverUrl: cleanServerUrl)
                    if !token.isEmpty {
                        createPairingSession()
                    }
                    return
                }

                guard (200...299).contains(http.statusCode) else {
                    let errBody = String(data: data, encoding: .utf8) ?? ""
                    self.statusMessage = "Pairing failed (HTTP \(http.statusCode)): \(errBody)"
                    return
                }

                let resp = try JSONDecoder().decode(PairingSessionResponse.self, from: data)

                self.activePairingCode = resp.code
                self.pairingQrDataUrl = resp.qrDataUrl
                self.statusMessage = "Pairing code: \(resp.code)"
            } catch {
                self.statusMessage = "Failed to create pairing session: \(error.localizedDescription)"
                logDiag("createPairingSession error: \(error.localizedDescription)")
            }
        }
    }

    public func refreshDevices() async {
        guard !token.isEmpty else { return }
        do {
            guard let url = URL(string: "\(cleanServerUrl)/v1/devices") else { return }
            var req = URLRequest(url: url)
            req.timeoutInterval = 10.0
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

            let start = Date()
            let (data, response) = try await URLSession.shared.data(for: req)
            let elapsed = Int(Date().timeIntervalSince(start) * 1000)
            guard let http = response as? HTTPURLResponse else { return }
            logDiag("GET /v1/devices (refresh) -> HTTP \(http.statusCode) in \(elapsed)ms")
            guard (200...299).contains(http.statusCode) else { return }
            let resp = try JSONDecoder().decode(DeviceListResponse.self, from: data)
            self.devices = resp.devices
        } catch {
            logDiag("refreshDevices error: \(error.localizedDescription)")
        }
    }

    public func copyItemToClipboard(_ item: DisplayClipboardItem) {
        clipboardMonitor?.setRemoteClipboard(text: item.text)
        statusMessage = "Copied to clipboard"
    }

    public func togglePinItem(_ item: DisplayClipboardItem) {
        if let idx = history.firstIndex(where: { $0.id == item.id }) {
            history[idx].isPinned.toggle()
        }
    }

    public func clearHistory() {
        history.removeAll()
        statusMessage = "History cleared"
    }

    private func addItemToHistory(text: String, sourceApp: String?, isRemote: Bool) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let existingIdx = history.firstIndex(where: { $0.text == trimmed }) {
            let item = history.remove(at: existingIdx)
            history.insert(item, at: 0)
            return
        }

        let newItem = DisplayClipboardItem(
            id: UUID().uuidString,
            text: trimmed,
            sourceApp: sourceApp,
            timestamp: Date(),
            isPinned: false,
            isRemote: isRemote
        )
        history.insert(newItem, at: 0)

        if history.count > 100 {
            history.removeLast()
        }
    }
}
