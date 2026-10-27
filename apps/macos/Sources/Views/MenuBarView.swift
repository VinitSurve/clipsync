import SwiftUI
import UniformTypeIdentifiers

public struct MenuBarView: View {
    @ObservedObject var appState: AppState
    @State private var selectedTab = 0

    public init(appState: AppState) {
        self.appState = appState
    }

    public var body: some View {
        VStack(spacing: 0) {
            // Header
            HStack {
                HStack(spacing: 6) {
                    Circle()
                        .fill(statusColor)
                        .frame(width: 8, height: 8)
                    Text("ClipSync")
                        .font(.headline)
                        .fontWeight(.semibold)
                }

                Spacer()

                Button(action: {
                    appState.togglePause()
                }) {
                    Image(systemName: appState.isPaused ? "play.circle.fill" : "pause.circle")
                        .font(.system(size: 14))
                        .foregroundColor(appState.isPaused ? .orange : .secondary)
                }
                .buttonStyle(.plain)
                .help(appState.isPaused ? "Resume Sync" : "Pause Sync")

                Button(action: {
                    NSApplication.shared.terminate(nil)
                }) {
                    Image(systemName: "power")
                        .font(.system(size: 13))
                        .foregroundColor(.secondary)
                }
                .buttonStyle(.plain)
                .help("Quit ClipSync")
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(Color(NSColor.windowBackgroundColor))

            Divider()

            // Tab Picker
            Picker("", selection: $selectedTab) {
                Text("Clipboard").tag(0)
                Text("Transfers").tag(1)
                Text("Devices").tag(2)
                Text("Settings").tag(3)
            }
            .pickerStyle(.segmented)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)

            Divider()

            // Content Area
            Group {
                if selectedTab == 0 {
                    historyView
                } else if selectedTab == 1 {
                    transfersView
                } else if selectedTab == 2 {
                    devicesView
                } else {
                    settingsView
                }
            }
            .frame(height: 330)

            Divider()

            // Bottom Status Bar
            HStack {
                Text(appState.statusMessage)
                    .font(.caption2)
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                Spacer()
                Text(appState.connectionState.rawValue)
                    .font(.caption2)
                    .foregroundColor(statusColor)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(Color(NSColor.controlBackgroundColor))
        }
        .frame(width: 360)
    }

    private var statusColor: Color {
        switch appState.connectionState {
        case .connected:
            return .green
        case .connecting:
            return .yellow
        case .paused:
            return .orange
        case .disconnected:
            return .red
        }
    }

    // -------------------------------------------------------------------------
    // Tab 0: Clipboard History
    // -------------------------------------------------------------------------

    private var historyView: some View {
        VStack(spacing: 0) {
            if appState.history.isEmpty {
                VStack(spacing: 8) {
                    Spacer()
                    Image(systemName: "clipboard")
                        .font(.system(size: 32))
                        .foregroundColor(.secondary.opacity(0.6))
                    Text("Clipboard is empty")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                    Text("Copied text, images, and files will sync across all your devices")
                        .font(.caption2)
                        .foregroundColor(.secondary.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 20)
                    Spacer()
                }
            } else {
                List {
                    ForEach(appState.history) { item in
                        HStack(alignment: .top, spacing: 8) {
                            Image(systemName: historyItemIcon(for: item.text))
                                .font(.system(size: 13))
                                .foregroundColor(.secondary)
                                .frame(width: 16)
                                .padding(.top, 2)

                            VStack(alignment: .leading, spacing: 4) {
                                Text(item.text)
                                    .font(.system(size: 12, design: .monospaced))
                                    .lineLimit(3)
                                    .frame(maxWidth: .infinity, alignment: .leading)

                                HStack(spacing: 6) {
                                    if item.isRemote {
                                        Label("Remote", systemImage: "arrow.down.circle")
                                            .font(.system(size: 9))
                                            .foregroundColor(.blue)
                                    }
                                    if let app = item.sourceApp {
                                        Text(app)
                                            .font(.system(size: 9))
                                            .foregroundColor(.secondary)
                                    }
                                    Spacer()
                                    Text(item.timestamp, style: .time)
                                        .font(.system(size: 9))
                                        .foregroundColor(.secondary)
                                }
                            }

                            Button(action: {
                                appState.copyItemToClipboard(item)
                            }) {
                                Image(systemName: "doc.on.doc")
                                    .font(.system(size: 11))
                            }
                            .buttonStyle(.borderless)
                            .help("Copy to Clipboard")
                        }
                        .padding(.vertical, 4)
                    }
                }
                .listStyle(.plain)

                HStack {
                    Spacer()
                    Button("Clear History") {
                        appState.clearHistory()
                    }
                    .font(.caption)
                    .buttonStyle(.plain)
                    .foregroundColor(.secondary)
                    .padding(8)
                }
            }
        }
    }

    private func historyItemIcon(for text: String) -> String {
        if text.starts(with: "🖼️") {
            return "photo"
        } else if text.starts(with: "📁") {
            return "doc"
        } else {
            return "text.quote"
        }
    }

    // -------------------------------------------------------------------------
    // Tab 1: Transfers (Images & Files)
    // -------------------------------------------------------------------------

    private var transfersView: some View {
        VStack(spacing: 0) {
            let records = appState.transferManager?.transfers ?? []
            if records.isEmpty {
                VStack(spacing: 8) {
                    Spacer()
                    Image(systemName: "arrow.up.arrow.down.circle")
                        .font(.system(size: 32))
                        .foregroundColor(.secondary.opacity(0.6))
                    Text("No active or recent transfers")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                    Text("Copy an image or file in Finder to sync across your devices via temporary encrypted Drive storage (max 100 MB).")
                        .font(.caption2)
                        .foregroundColor(.secondary.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 20)
                    Spacer()
                }
            } else {
                List {
                    ForEach(records) { record in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack(spacing: 8) {
                                Image(systemName: iconForMime(record.mimeType))
                                    .font(.system(size: 16))
                                    .foregroundColor(.accentColor)
                                    .frame(width: 20)

                                VStack(alignment: .leading, spacing: 2) {
                                    Text(record.fileName)
                                        .font(.system(size: 12, weight: .medium))
                                        .lineLimit(1)
                                    HStack(spacing: 4) {
                                        Text(record.formattedSize)
                                            .font(.caption2)
                                            .foregroundColor(.secondary)
                                        Text("•")
                                            .font(.caption2)
                                            .foregroundColor(.secondary)
                                        Text(record.isOutgoing ? "Outgoing" : "Incoming")
                                            .font(.caption2)
                                            .foregroundColor(record.isOutgoing ? .blue : .purple)
                                    }
                                }

                                Spacer()

                                statusBadge(for: record.status)
                            }

                            if record.status == .uploading || record.status == .downloading {
                                ProgressView(value: record.progress)
                                    .progressViewStyle(.linear)
                            }

                            HStack {
                                Text(record.statusText)
                                    .font(.system(size: 10))
                                    .foregroundColor(.secondary)
                                    .lineLimit(1)

                                Spacer()

                                if record.canCancel {
                                    Button("Cancel") {
                                        appState.cancelTransfer(recordId: record.id)
                                    }
                                    .font(.caption2)
                                    .buttonStyle(.borderless)
                                    .foregroundColor(.red)
                                } else if record.canRetry {
                                    Button("Retry") {
                                        appState.retryTransfer(recordId: record.id)
                                    }
                                    .font(.caption2)
                                    .buttonStyle(.borderless)
                                    .foregroundColor(.accentColor)
                                }

                                if let localURL = record.localURL, FileManager.default.fileExists(atPath: localURL.path) {
                                    Button(action: {
                                        NSWorkspace.shared.activateFileViewerSelecting([localURL])
                                    }) {
                                        Image(systemName: "folder")
                                            .font(.system(size: 11))
                                    }
                                    .buttonStyle(.borderless)
                                    .help("Show in Finder")
                                }
                            }
                        }
                        .padding(.vertical, 4)
                    }
                }
                .listStyle(.plain)
            }
        }
    }

    private func iconForMime(_ mime: String) -> String {
        if mime.starts(with: "image/") {
            return "photo"
        } else if mime == "application/zip" || mime == "application/gzip" {
            return "doc.zipper"
        } else if mime == "application/pdf" {
            return "doc.richtext"
        } else {
            return "doc"
        }
    }

    @ViewBuilder
    private func statusBadge(for status: TransferStatus) -> some View {
        switch status {
        case .ready:
            Text("Ready")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.blue.opacity(0.15))
                .foregroundColor(.blue)
                .cornerRadius(4)
        case .uploading, .downloading:
            Text(status == .uploading ? "Uploading" : "Downloading")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.yellow.opacity(0.2))
                .foregroundColor(.orange)
                .cornerRadius(4)
        case .delivered:
            Text("Delivered")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.green.opacity(0.15))
                .foregroundColor(.green)
                .cornerRadius(4)
        case .expired:
            Text("Expired (24h)")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.secondary.opacity(0.15))
                .foregroundColor(.secondary)
                .cornerRadius(4)
        case .failed:
            Text("Failed")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.red.opacity(0.15))
                .foregroundColor(.red)
                .cornerRadius(4)
        case .cancelled:
            Text("Cancelled")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.secondary.opacity(0.15))
                .foregroundColor(.secondary)
                .cornerRadius(4)
        case .pending:
            Text("Pending")
                .font(.system(size: 9, weight: .bold))
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(Color.secondary.opacity(0.15))
                .foregroundColor(.secondary)
                .cornerRadius(4)
        }
    }

    // -------------------------------------------------------------------------
    // Tab 2: Devices
    // -------------------------------------------------------------------------

    private var devicesView: some View {
        VStack(spacing: 12) {
            ScrollView {
                VStack(spacing: 8) {
                    ForEach(appState.devices) { dev in
                        HStack {
                            Image(systemName: deviceIcon(for: dev.platform))
                                .font(.system(size: 16))
                                .foregroundColor(.accentColor)
                                .frame(width: 24)

                            VStack(alignment: .leading, spacing: 2) {
                                Text(dev.label)
                                    .font(.system(size: 12, weight: .medium))
                                Text(dev.platform.capitalized)
                                    .font(.caption2)
                                    .foregroundColor(.secondary)
                            }

                            Spacer()

                            if dev.isCurrent == true {
                                Text("This Mac")
                                    .font(.system(size: 10))
                                    .padding(.horizontal, 6)
                                    .padding(.vertical, 2)
                                    .background(Color.secondary.opacity(0.2))
                                    .cornerRadius(4)
                            }
                        }
                        .padding(8)
                        .background(Color(NSColor.controlBackgroundColor))
                        .cornerRadius(6)
                    }
                }
                .padding(12)
            }

            Divider()

            VStack(spacing: 8) {
                if let code = appState.activePairingCode {
                    VStack(spacing: 6) {
                        HStack {
                            Text("Pairing Code")
                                .font(.caption)
                                .foregroundColor(.secondary)
                            Spacer()
                            Button("Dismiss") {
                                appState.activePairingCode = nil
                            }
                            .buttonStyle(.borderless)
                            .font(.caption2)
                        }
                        Text(code)
                            .font(.system(size: 26, weight: .bold, design: .monospaced))
                            .foregroundColor(.accentColor)
                        Text("Enter this 6-digit code on your Windows PC or Android phone")
                            .font(.caption2)
                            .foregroundColor(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .padding(10)
                    .background(Color.accentColor.opacity(0.12))
                    .cornerRadius(8)
                } else {
                    Button(action: {
                        appState.createPairingSession()
                    }) {
                        Label("Pair Another Device", systemImage: "qrcode")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.regular)
                }
            }
            .padding(.horizontal, 12)
            .padding(.bottom, 12)
        }
    }

    // -------------------------------------------------------------------------
    // Tab 3: Settings
    // -------------------------------------------------------------------------

    private var settingsView: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                // Server Connection
                VStack(alignment: .leading, spacing: 6) {
                    Text("Server Connection")
                        .font(.caption)
                        .bold()
                        .foregroundColor(.secondary)

                    TextField("Server URL", text: $appState.serverUrl)
                        .textFieldStyle(.roundedBorder)

                    HStack {
                        Button("Cloudflare") {
                            appState.serverUrl = "https://clipsync-server-cloudflare.survevinit56.workers.dev"
                            appState.bootstrapDevice()
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)

                        Button("Local Server") {
                            appState.serverUrl = "http://localhost:8787"
                            appState.bootstrapDevice()
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)

                        Spacer()

                        Button(action: {
                            appState.bootstrapDevice()
                        }) {
                            Label("Save & Connect", systemImage: "arrow.clockwise")
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                    }

                    SecureField("Master Sync Key", text: $appState.syncPassphrase)
                        .textFieldStyle(.roundedBorder)

                    Toggle("Start ClipSync at Login", isOn: Binding(
                        get: { appState.launchAtLogin },
                        set: { appState.setLaunchAtLogin($0) }
                    ))
                    .toggleStyle(.checkbox)
                    .help("Automatically launch ClipSync in the menu bar when your Mac turns on or logs in.")
                }

                Divider()

                // Google Drive Connection
                VStack(alignment: .leading, spacing: 6) {
                    Text("Google Drive (Temporary Encrypted Storage)")
                        .font(.caption)
                        .bold()
                        .foregroundColor(.secondary)

                    HStack {
                        Text("Status:")
                            .font(.caption)
                            .foregroundColor(.secondary)
                        Text(appState.googleDrive.authStatus.displayText)
                            .font(.caption)
                            .fontWeight(.medium)
                            .foregroundColor(appState.googleDrive.authStatus.isConnected ? .green : .orange)
                    }

                    Text("Used solely as temporary encrypted storage (max 100 MB). Files are encrypted locally and auto-deleted immediately upon delivery or at 24 hours. 100% free tier.")
                        .font(.caption2)
                        .foregroundColor(.secondary)

                    TextField("Google OAuth Client ID", text: $appState.googleDrive.clientId)
                        .textFieldStyle(.roundedBorder)

                    HStack(spacing: 8) {
                        if appState.googleDrive.authStatus.isConnected {
                            Button("Disconnect") {
                                appState.disconnectGoogleDrive()
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                            .foregroundColor(.red)

                            Button("Sweep Expired Files") {
                                appState.sweepGoogleDrive()
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        } else {
                            Button("Connect Google Drive") {
                                appState.connectGoogleDrive()
                            }
                            .buttonStyle(.borderedProminent)
                            .controlSize(.small)

                            Button("Import credentials.json...") {
                                let panel = NSOpenPanel()
                                panel.allowedContentTypes = [UTType.json]
                                panel.allowsMultipleSelection = false
                                panel.canChooseDirectories = false
                                panel.canCreateDirectories = false
                                if panel.runModal() == .OK, let url = panel.url {
                                    _ = appState.googleDrive.importCredentialsFile(from: url)
                                }
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                            .help("Import credentials.json for a Desktop app client if client_secret is required.")
                        }
                    }

                    if appState.googleDrive.hasClientSecret {
                        Text("✓ Client secret configured (Desktop App credentials)")
                            .font(.system(size: 10))
                            .foregroundColor(.secondary)
                    }
                }
            }
            .padding(12)
        }
    }

    private func deviceIcon(for platform: String) -> String {
        switch platform.lowercased() {
        case "macos":
            return "laptopcomputer"
        case "windows":
            return "desktopcomputer"
        case "android":
            return "candybarphone"
        default:
            return "externaldrive.connected.to.line.below"
        }
    }
}
