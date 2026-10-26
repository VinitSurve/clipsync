import Foundation

public final class WebSocketSyncService: NSObject, URLSessionWebSocketDelegate {
    public enum ConnectionState: String {
        case disconnected = "Disconnected"
        case connecting = "Connecting..."
        case connected = "Connected"
        case paused = "Paused"
    }

    private var webSocketTask: URLSessionWebSocketTask?
    private var urlSession: URLSession!

    public let serverUrl: String
    public let deviceId: String
    public let token: String
    public let crypto: ClipSyncCrypto

    public private(set) var state: ConnectionState = .disconnected {
        didSet { onStateChange?(state) }
    }

    public var isPaused: Bool = false
    private var lastSequence: Int = 0
    private var pingTimer: Timer?
    private var reconnectTimer: Timer?
    private var reconnectAttempts: Int = 0

    // Deduplication Set
    private var seenItemIds = Set<String>()

    public var onStateChange: ((ConnectionState) -> Void)?
    public var onRemoteItemReceived: ((SyncItem, String) -> Void)?
    public var onDeviceEvent: ((String) -> Void)?

    public init(serverUrl: String, deviceId: String, token: String, crypto: ClipSyncCrypto) {
        self.serverUrl = serverUrl
        self.deviceId = deviceId
        self.token = token
        self.crypto = crypto
        super.init()
        self.urlSession = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
    }

    public func connect() {
        guard state != .connected && state != .connecting else { return }
        state = .connecting

        let wsScheme = serverUrl.starts(with: "https") ? "wss" : "ws"
        let hostAndPort = serverUrl
            .replacingOccurrences(of: "http://", with: "")
            .replacingOccurrences(of: "https://", with: "")
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))

        guard let url = URL(string: "\(wsScheme)://\(hostAndPort)/v1/events?token=\(token)") else {
            state = .disconnected
            return
        }

        webSocketTask = urlSession.webSocketTask(with: url)
        webSocketTask?.resume()

        listenForMessages()
        startPingTimer()
    }

    public func disconnect() {
        reconnectTimer?.invalidate()
        pingTimer?.invalidate()
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        state = .disconnected
    }

    public func pause() {
        isPaused = true
        state = .paused
    }

    public func resume() {
        isPaused = false
        if webSocketTask?.state == .running {
            state = .connected
        } else {
            connect()
        }
    }

    public func broadcastText(_ text: String, sourceApp: String? = nil) {
        guard !isPaused, state == .connected, let webSocket = webSocketTask else { return }

        do {
            let envelope = try crypto.encrypt(plainText: text)
            let encoder = JSONEncoder()
            let envelopeData = try encoder.encode(envelope)
            guard let envelopeString = String(data: envelopeData, encoding: .utf8) else { return }

            let itemId = "mac-\(UUID().uuidString)"
            let item = SyncItem(
                version: 1,
                itemId: itemId,
                type: "text",
                sourceDeviceId: deviceId,
                payload: envelopeString,
                metadata: SyncItemMetadata(
                    size: text.utf8.count,
                    sha256: ClipSyncCrypto.sha256Hex(text),
                    previewSnippet: String(text.prefix(100)),
                    sourceApp: sourceApp
                )
            )

            seenItemIds.insert(itemId)

            let payloadDict: [String: Any] = [
                "type": "sync:item",
                "item": [
                    "version": item.version,
                    "itemId": item.itemId,
                    "type": item.type,
                    "sourceDeviceId": item.sourceDeviceId,
                    "createdAt": item.createdAt,
                    "payload": item.payload,
                    "metadata": [
                        "size": item.metadata?.size ?? text.utf8.count,
                        "sha256": item.metadata?.sha256 ?? "",
                        "previewSnippet": item.metadata?.previewSnippet ?? "",
                        "sourceApp": item.metadata?.sourceApp ?? ""
                    ]
                ]
            ]

            let jsonData = try JSONSerialization.data(withJSONObject: payloadDict)
            guard let jsonString = String(data: jsonData, encoding: .utf8) else { return }

            webSocket.send(.string(jsonString)) { error in
                if let error = error {
                    print("[ClipSync] Send error: \(error)")
                }
            }
        } catch {
            print("[ClipSync] Encryption/broadcast error: \(error)")
        }
    }

    private func listenForMessages() {
        webSocketTask?.receive { [weak self] result in
            guard let self = self else { return }

            switch result {
            case .success(let message):
                switch message {
                case .string(let text):
                    self.handleIncomingFrame(text)
                case .data(let data):
                    if let text = String(data: data, encoding: .utf8) {
                        self.handleIncomingFrame(text)
                    }
                @unknown default:
                    break
                }
                self.listenForMessages()

            case .failure:
                self.handleDisconnect()
            }
        }
    }

    private func handleIncomingFrame(_ text: String) {
        guard let data = text.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = json["type"] as? String else {
            return
        }

        switch type {
        case "connected":
            self.state = isPaused ? .paused : .connected
            self.reconnectAttempts = 0
            if let seq = json["lastSequence"] as? Int {
                self.lastSequence = seq
            }

        case "sync:item":
            guard let itemDict = json["item"] as? [String: Any],
                  let seq = json["sequenceNumber"] as? Int else { return }

            self.lastSequence = max(self.lastSequence, seq)

            // Send ack
            if let itemId = itemDict["itemId"] as? String {
                sendAck(itemId: itemId, sequenceNumber: seq)
            }

            // Loop prevention: check sourceDeviceId
            let sourceId = itemDict["sourceDeviceId"] as? String ?? ""
            if sourceId == deviceId {
                return
            }

            // Deduplication
            if let itemId = itemDict["itemId"] as? String {
                if seenItemIds.contains(itemId) { return }
                seenItemIds.insert(itemId)
            }

            guard !isPaused else { return }

            // Decrypt
            if let payloadStr = itemDict["payload"] as? String,
               let payloadData = payloadStr.data(using: .utf8),
               let envelope = try? JSONDecoder().decode(EncryptedPayloadEnvelope.self, from: payloadData) {
                do {
                    let decrypted = try crypto.decrypt(envelope: envelope)
                    let syncItem = try JSONDecoder().decode(SyncItem.self, from: JSONSerialization.data(withJSONObject: itemDict))
                    onRemoteItemReceived?(syncItem, decrypted)
                } catch {
                    print("[ClipSync] Decryption failure: \(error)")
                }
            }

        case "device:joined":
            if let dev = json["device"] as? [String: Any], let label = dev["label"] as? String {
                onDeviceEvent?("\(label) connected")
            }

        case "device:left":
            onDeviceEvent?("A device disconnected")

        case "pong":
            break

        default:
            break
        }
    }

    private func sendAck(itemId: String, sequenceNumber: Int) {
        let ack: [String: Any] = [
            "type": "ack",
            "itemId": itemId,
            "sequenceNumber": sequenceNumber
        ]
        if let data = try? JSONSerialization.data(withJSONObject: ack),
           let str = String(data: data, encoding: .utf8) {
            webSocketTask?.send(.string(str)) { _ in }
        }
    }

    private func startPingTimer() {
        pingTimer?.invalidate()
        pingTimer = Timer.scheduledTimer(withTimeInterval: 20.0, repeats: true) { [weak self] _ in
            self?.webSocketTask?.send(.string("{\"type\":\"ping\"}")) { _ in }
        }
    }

    private func handleDisconnect() {
        state = .disconnected
        pingTimer?.invalidate()
        scheduleReconnect()
    }

    private func scheduleReconnect() {
        reconnectTimer?.invalidate()
        let delay = min(pow(1.8, Double(reconnectAttempts)), 15.0)
        reconnectAttempts += 1
        reconnectTimer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            self?.connect()
        }
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        state = isPaused ? .paused : .connected
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        handleDisconnect()
    }
}
