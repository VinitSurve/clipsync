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
    @Published public var connectionState: WebSocketSyncService.ConnectionState = .disconnected
    @Published public var isPaused: Bool = false
    @Published public var history: [DisplayClipboardItem] = []
    @Published public var devices: [DeviceInfo] = []
    @Published public var activePairingCode: String?
    @Published public var pairingQrDataUrl: String?
    @Published public var statusMessage: String = "Ready"

    public var serverUrl: String = "http://localhost:8787"
    public var syncPassphrase: String = "clipsync-master-sync-passphrase"

    private var clipboardMonitor: ClipboardMonitor?
    private var syncService: WebSocketSyncService?
    private var crypto: ClipSyncCrypto?

    private var deviceId: String = ""
    private var token: String = ""
    private var syncSalt: String = ""

    public init() {
        bootstrapDevice()
    }

    public func bootstrapDevice() {
        Task {
            do {
                self.statusMessage = "Registering device..."
                // Bootstrap register macOS device
                guard let url = URL(string: "\(serverUrl)/v1/devices/register") else { return }
                var req = URLRequest(url: url)
                req.httpMethod = "POST"
                req.setValue("application/json", forHTTPHeaderField: "Content-Type")
                let body: [String: Any] = [
                    "label": "MacBook Air M5",
                    "platform": "macos"
                ]
                req.httpBody = try JSONSerialization.data(withJSONObject: body)

                let (data, _) = try await URLSession.shared.data(for: req)
                let resp = try JSONDecoder().decode(RegisterDeviceResponse.self, from: data)

                self.deviceId = resp.deviceId
                self.token = resp.token
                self.syncSalt = resp.syncSalt

                // Initialize Crypto
                self.crypto = try ClipSyncCrypto(passphrase: syncPassphrase, salt: syncSalt)

                // Initialize Services
                self.setupSync()
                await self.refreshDevices()
                self.statusMessage = "Connected to ClipSync"
            } catch {
                self.statusMessage = "Registration failed: \(error.localizedDescription)"
            }
        }
    }

    private func setupSync() {
        guard let crypto = self.crypto else { return }

        let monitor = ClipboardMonitor()
        self.clipboardMonitor = monitor

        let sync = WebSocketSyncService(
            serverUrl: serverUrl,
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
                guard let url = URL(string: "\(serverUrl)/v1/auth/pairing/session") else { return }
                var req = URLRequest(url: url)
                req.httpMethod = "POST"
                req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

                let (data, _) = try await URLSession.shared.data(for: req)
                let resp = try JSONDecoder().decode(PairingSessionResponse.self, from: data)

                self.activePairingCode = resp.code
                self.pairingQrDataUrl = resp.qrDataUrl
                self.statusMessage = "Pairing code: \(resp.code)"
            } catch {
                self.statusMessage = "Failed to create pairing session"
            }
        }
    }

    public func refreshDevices() async {
        guard !token.isEmpty else { return }
        do {
            guard let url = URL(string: "\(serverUrl)/v1/devices") else { return }
            var req = URLRequest(url: url)
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

            let (data, _) = try await URLSession.shared.data(for: req)
            let resp = try JSONDecoder().decode(DeviceListResponse.self, from: data)
            self.devices = resp.devices
        } catch {}
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
