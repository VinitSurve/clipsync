import SwiftUI

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
                Text("Devices").tag(1)
                Text("Settings").tag(2)
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
                    devicesView
                } else {
                    settingsView
                }
            }
            .frame(height: 300)

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
        .frame(width: 340)
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
                    Text("Copied items will sync across all your devices")
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
                    VStack(spacing: 4) {
                        Text("Pairing Code")
                            .font(.caption)
                            .foregroundColor(.secondary)
                        Text(code)
                            .font(.system(size: 24, weight: .bold, design: .monospaced))
                            .foregroundColor(.accentColor)
                        Text("Enter this code on your Windows PC or Android phone")
                            .font(.caption2)
                            .foregroundColor(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .padding(8)
                    .background(Color.accentColor.opacity(0.1))
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

    private var settingsView: some View {
        Form {
            Section(header: Text("General").font(.caption).bold()) {
                TextField("Server URL", text: $appState.serverUrl)
                    .textFieldStyle(.roundedBorder)

                SecureField("Master Sync Key", text: $appState.syncPassphrase)
                    .textFieldStyle(.roundedBorder)

                Toggle("Exclude Password Managers", isOn: .constant(true))
                    .toggleStyle(.checkbox)
                    .help("Prevents copying passwords from 1Password, Bitwarden, etc.")
            }
        }
        .padding(12)
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
