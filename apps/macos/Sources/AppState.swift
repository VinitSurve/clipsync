import Foundation
import Combine
import AppKit

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

    @Published public var connectionState: WebSocketSyncService.ConnectionState = .disconnected
    @Published public var isPaused: Bool = false
    @Published public var history: [DisplayClipboardItem] = []
    @Published public var devices: [DeviceInfo] = []
    @Published public var activePairingCode: String?
    @Published public var pairingQrDataUrl: String?
    @Published public var statusMessage: String = "Ready"

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

    public init() {
        let savedUrl = UserDefaults.standard.string(forKey: Self.serverUrlKey) ?? "https://clipsync-server-cloudflare.survevinit56.workers.dev"
        let savedPass = UserDefaults.standard.string(forKey: Self.syncPassphraseKey) ?? "clipsync-master-sync-passphrase"
        self.serverUrl = savedUrl
        self.syncPassphrase = savedPass
        bootstrapDevice()
    }

    public func bootstrapDevice() {
        Task {
            await connectToServer()
        }
    }

    public func connectToServer() async {
        let targetUrl = cleanServerUrl
        guard !targetUrl.isEmpty, let _ = URL(string: targetUrl) else {
            self.statusMessage = "Invalid Server URL"
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
            if await validateCredentials(serverUrl: targetUrl, token: token) {
                self.deviceId = deviceId
                self.token = token
                self.syncSalt = salt
                initServicesAndConnect()
                await self.refreshDevices()
                self.statusMessage = "Connected to ClipSync"
                return
            }
        }

        // Otherwise register clean device credentials on this server
        await registerFreshDevice(serverUrl: targetUrl)
    }

    private func validateCredentials(serverUrl: String, token: String) async -> Bool {
        guard let url = URL(string: "\(serverUrl)/v1/devices") else { return false }
        var req = URLRequest(url: url)
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        do {
            let (_, response) = try await URLSession.shared.data(for: req)
            if let http = response as? HTTPURLResponse, http.statusCode == 200 {
                return true
            }
        } catch {}
        return false
    }

    private func registerFreshDevice(serverUrl: String) async {
        do {
            self.statusMessage = "Registering device on server..."
            guard let url = URL(string: "\(serverUrl)/v1/devices/register") else {
                self.statusMessage = "Invalid URL: \(serverUrl)/v1/devices/register"
                return
            }
            var req = URLRequest(url: url)
            req.httpMethod = "POST"
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let body: [String: Any] = [
                "label": "MacBook Air M5",
                "platform": "macos"
            ]
            req.httpBody = try JSONSerialization.data(withJSONObject: body)

            let (data, response) = try await URLSession.shared.data(for: req)
            guard let http = response as? HTTPURLResponse else {
                self.statusMessage = "Registration failed: No server response"
                return
            }
            guard (200...299).contains(http.statusCode) else {
                let errText = String(data: data, encoding: .utf8) ?? ""
                self.statusMessage = "Registration failed (HTTP \(http.statusCode)): \(errText)"
                return
            }

            let resp = try JSONDecoder().decode(RegisterDeviceResponse.self, from: data)

            self.deviceId = resp.deviceId
            self.token = resp.token
            self.syncSalt = resp.syncSalt

            UserDefaults.standard.set(resp.token, forKey: tokenKey)
            UserDefaults.standard.set(resp.deviceId, forKey: deviceIdKey)
            UserDefaults.standard.set(resp.syncSalt, forKey: syncSaltKey)

            initServicesAndConnect()
            await self.refreshDevices()
            self.statusMessage = "Connected to ClipSync"
        } catch {
            self.statusMessage = "Registration error: \(error.localizedDescription)"
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

        sync.onStateChange = { [weak self] state in
            DispatchQueue.main.async {
                self?.connectionState = state
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

        monitor.onNewLocalCopy = { [weak self] text, app in
            DispatchQueue.main.async {
                self?.syncService?.broadcastText(text, sourceApp: app)
                self?.addItemToHistory(text: text, sourceApp: app, isRemote: false)
                self?.statusMessage = "Copied & Synced"
            }
        }

        monitor.start()
        sync.connect()
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
                req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

                let (data, response) = try await URLSession.shared.data(for: req)
                guard let http = response as? HTTPURLResponse else {
                    self.statusMessage = "Pairing failed: Invalid server response"
                    return
                }

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
                print("[ClipSync] createPairingSession error: \(error)")
            }
        }
    }

    public func refreshDevices() async {
        guard !token.isEmpty else { return }
        do {
            guard let url = URL(string: "\(cleanServerUrl)/v1/devices") else { return }
            var req = URLRequest(url: url)
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

            let (data, response) = try await URLSession.shared.data(for: req)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                return
            }
            let resp = try JSONDecoder().decode(DeviceListResponse.self, from: data)
            self.devices = resp.devices
        } catch {
            print("[ClipSync] refreshDevices error: \(error)")
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
