import Foundation
import AuthenticationServices
import CryptoKit
import Combine

public enum GoogleDriveAuthStatus: Equatable {
    case disconnected
    case connecting
    case connected(accountEmail: String?)
    case error(String)

    public var isConnected: Bool {
        if case .connected = self { return true }
        return false
    }

    public var displayText: String {
        switch self {
        case .disconnected:
            return "Not Connected"
        case .connecting:
            return "Connecting..."
        case .connected(let email):
            return "Connected (\(email ?? "Google Account"))"
        case .error(let msg):
            return "Error: \(msg)"
        }
    }
}

@MainActor
public final class GoogleDriveService: NSObject, ObservableObject {
    public static let shared = GoogleDriveService()

    private static let clientIdKey = "clipsync.google.clientId"
    private static let userEmailKey = "clipsync.google.userEmail"
    private static let googleProjectIdKey = "clipsync.google.projectId"
    private static let keychainRefreshTokenKey = "clipsync.google.refreshToken"
    private static let keychainClientSecretKey = "clipsync.google.clientSecret"

    private let scope = "https://www.googleapis.com/auth/drive.file"

    @Published public var authStatus: GoogleDriveAuthStatus = .disconnected
    @Published public private(set) var projectId: String?
    @Published public var clientId: String {
        didSet {
            UserDefaults.standard.set(clientId, forKey: Self.clientIdKey)
        }
    }

    public var clientIdPrefix: String? {
        let trimmed = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        return String(trimmed.prefix(16)) + "..."
    }

    private var accessToken: String?
    private var accessTokenExpiresAt: Date?
    private var authSession: ASWebAuthenticationSession?

    override public init() {
        let savedClientId = UserDefaults.standard.string(forKey: Self.clientIdKey) ?? ""
        self.clientId = savedClientId
        self.projectId = UserDefaults.standard.string(forKey: Self.googleProjectIdKey) ?? "clipsync-512808"
        super.init()


        // Auto-discover credentials if available
        _ = loadClientSecretIfAvailable()

        // Check if we have a refresh token in Keychain
        if let _ = KeychainHelper.shared.readString(key: Self.keychainRefreshTokenKey) {
            let savedEmail = UserDefaults.standard.string(forKey: Self.userEmailKey)
            self.authStatus = .connected(accountEmail: savedEmail)
        } else {
            self.authStatus = .disconnected
        }
    }

    public var hasClientSecret: Bool {
        loadClientSecretIfAvailable() != nil
    }

    public func loadClientSecretIfAvailable() -> String? {
        // 1. Check Keychain
        if let secret = KeychainHelper.shared.readString(key: Self.keychainClientSecretKey), !secret.isEmpty {
            return secret
        }

        // 2. Check Application Support
        let appSupportDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ClipSync")
        let credFile = appSupportDir.appendingPathComponent("credentials.json")
        if FileManager.default.fileExists(atPath: credFile.path) {
            if let secret = parseAndSaveCredentials(at: credFile) {
                return secret
            }
        }

        return nil
    }

    @discardableResult
    public func importCredentialsFile(from url: URL) -> Bool {
        guard let secret = parseAndSaveCredentials(at: url) else {
            return false
        }
        return !secret.isEmpty
    }

    private func parseAndSaveCredentials(at url: URL) -> String? {
        guard let data = try? Data(contentsOf: url),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let clientDict = (json["installed"] ?? json["web"]) as? [String: Any] else {
            return nil
        }

        if let credClientId = clientDict["client_id"] as? String, !credClientId.isEmpty {
            self.clientId = credClientId.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        if let credProjectId = clientDict["project_id"] as? String ?? json["project_id"] as? String, !credProjectId.isEmpty {
            UserDefaults.standard.set(credProjectId, forKey: Self.googleProjectIdKey)
            self.projectId = credProjectId
            print("[OAuth] Loaded project_id: \(credProjectId)")
        }

        if let credSecret = clientDict["client_secret"] as? String, !credSecret.isEmpty {
            _ = KeychainHelper.shared.save(key: Self.keychainClientSecretKey, string: credSecret)
            print("[OAuth] Loaded client_secret and securely saved to Keychain")
            return credSecret
        }


        return nil
    }

    public func setClientId(_ newId: String) {
        self.clientId = newId.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // -------------------------------------------------------------------------
    // OAuth 2.0 PKCE Flow
    // -------------------------------------------------------------------------

    private var pendingCodeVerifier: String?
    private var pendingRedirectUri: String?

    public func startAuthorization() {
        let trimmedClientId = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedClientId.isEmpty else {
            self.authStatus = .error("Google Client ID is required in Settings")
            return
        }

        self.authStatus = .connecting

        // 1. Generate PKCE verifier & challenge
        let codeVerifier = generateCodeVerifier()
        self.pendingCodeVerifier = codeVerifier
        let codeChallenge = generateCodeChallenge(from: codeVerifier)

        // 2. Derive reverse client ID scheme
        // Google Desktop App and iOS OAuth credentials require reverse-DNS format:
        // com.googleusercontent.apps.<client-id-prefix>:/oauth2redirect
        let clientPrefix = trimmedClientId.replacingOccurrences(of: ".apps.googleusercontent.com", with: "")
        let callbackScheme = "com.googleusercontent.apps.\(clientPrefix)"
        let redirectUri = "\(callbackScheme):/oauth2redirect"
        self.pendingRedirectUri = redirectUri

        print("[OAuth] Starting Google authorization")
        print("[OAuth] Client ID: \(trimmedClientId)")
        print("[OAuth] Derived callback scheme: \(callbackScheme)")
        print("[OAuth] Using redirect_uri: \(redirectUri)")

        var components = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")!
        components.queryItems = [
            URLQueryItem(name: "client_id", value: trimmedClientId),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "redirect_uri", value: redirectUri),
            URLQueryItem(name: "scope", value: scope),
            URLQueryItem(name: "code_challenge", value: codeChallenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "access_type", value: "offline"),
            URLQueryItem(name: "prompt", value: "select_account consent")
        ]

        guard let authUrl = components.url else {
            self.authStatus = .error("Failed to construct Google Auth URL")
            print("[OAuth] Failed to construct Google Auth URL from components")
            return
        }

        print("[OAuth] Launching ASWebAuthenticationSession with callbackScheme: \(callbackScheme)")

        // 3. Launch ASWebAuthenticationSession
        authSession = ASWebAuthenticationSession(url: authUrl, callbackURLScheme: callbackScheme) { [weak self] callbackUrl, error in
            Task { @MainActor in
                guard let self = self else { return }

                if let error = error as? ASWebAuthenticationSessionError, error.code == .canceledLogin {
                    print("[OAuth] User cancelled login session")
                    self.authStatus = .disconnected
                    return
                }

                if let error = error {
                    print("[OAuth] ASWebAuthenticationSession returned error: \(error.localizedDescription)")
                    self.authStatus = .error("Sign in failed: \(error.localizedDescription)")
                    return
                }

                guard let callbackUrl = callbackUrl else {
                    print("[OAuth] Callback URL is nil")
                    self.authStatus = .error("Callback URL missing")
                    return
                }

                print("[OAuth] Received callback URL with scheme: \(callbackUrl.scheme ?? "nil"), path: \(callbackUrl.path)")
                await self.handleCallback(callbackUrl: callbackUrl, codeVerifier: codeVerifier, redirectUri: redirectUri)
            }
        }

        authSession?.presentationContextProvider = self
        authSession?.prefersEphemeralWebBrowserSession = false
        authSession?.start()
    }

    public func handleCallbackURL(_ url: URL) {
        Task { @MainActor in
            guard let verifier = pendingCodeVerifier, let redirectUri = pendingRedirectUri else {
                return
            }
            await handleCallback(callbackUrl: url, codeVerifier: verifier, redirectUri: redirectUri)
        }
    }

    private func handleCallback(callbackUrl: URL, codeVerifier: String, redirectUri: String) async {
        guard let urlComponents = URLComponents(url: callbackUrl, resolvingAgainstBaseURL: false) else {
            print("[OAuth] Failed to parse callback URL into components")
            self.authStatus = .error("Failed to parse callback URL")
            return
        }

        if let oauthError = urlComponents.queryItems?.first(where: { $0.name == "error" })?.value {
            let errorDesc = urlComponents.queryItems?.first(where: { $0.name == "error_description" })?.value ?? oauthError
            print("[OAuth] Google returned OAuth error: \(oauthError), description: \(errorDesc)")
            self.authStatus = .error("Google authorization rejected: \(errorDesc)")
            return
        }

        guard let code = urlComponents.queryItems?.first(where: { $0.name == "code" })?.value, !code.isEmpty else {
            print("[OAuth] Authorization code missing in callback URL query items")
            self.authStatus = .error("Authorization code missing in callback")
            return
        }

        print("[OAuth] Authorization code received. Exchanging for tokens...")
        await self.exchangeCodeForTokens(code: code, codeVerifier: codeVerifier, redirectUri: redirectUri)
    }

    public func disconnect() {
        _ = KeychainHelper.shared.delete(key: Self.keychainRefreshTokenKey)
        UserDefaults.standard.removeObject(forKey: Self.userEmailKey)
        self.accessToken = nil
        self.accessTokenExpiresAt = nil
        self.authStatus = .disconnected
        print("[OAuth] Disconnected Google Drive and removed token from Keychain")
    }

    public static func formEncode(_ dict: [String: String]) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return dict.map { key, val in
            let encKey = key.addingPercentEncoding(withAllowedCharacters: allowed) ?? key
            let encVal = val.addingPercentEncoding(withAllowedCharacters: allowed) ?? val
            return "\(encKey)=\(encVal)"
        }.joined(separator: "&")
    }

    private func exchangeCodeForTokens(code: String, codeVerifier: String, redirectUri: String) async {
        do {
            guard let url = URL(string: "https://oauth2.googleapis.com/token") else { return }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

            let cleanClientId = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
            var params: [String: String] = [
                "client_id": cleanClientId,
                "code": code,
                "code_verifier": codeVerifier,
                "grant_type": "authorization_code",
                "redirect_uri": redirectUri
            ]

            let secret = loadClientSecretIfAvailable()
            if let secret = secret, !secret.isEmpty {
                params["client_secret"] = secret
                print("[OAuth] Exchanging code with client_id, code_verifier, and client_secret (Desktop client mode)")
            } else {
                print("[OAuth] Exchanging code with client_id and code_verifier (Secretless PKCE / iOS client mode)")
            }

            request.httpBody = Self.formEncode(params).data(using: .utf8)

            print("[OAuth] Sending token exchange request to https://oauth2.googleapis.com/token with redirect_uri: \(redirectUri)")

            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                print("[OAuth] No HTTP response from Google token server")
                self.authStatus = .error("No response from Google token server")
                return
            }

            guard http.statusCode == 200 else {
                print("[OAuth] Token exchange failed with HTTP \(http.statusCode)")
                var errorSummary = "HTTP \(http.statusCode)"
                if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                    if let err = json["error"] as? String {
                        let desc = json["error_description"] as? String ?? ""
                        errorSummary = "\(err): \(desc)"
                        print("[OAuth] Server error detail: \(errorSummary)")
                    }
                }
                self.authStatus = .error("Token exchange failed (\(errorSummary))")
                return
            }

            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let access = json["access_token"] as? String else {
                print("[OAuth] Invalid JSON or missing access_token in Google token response")
                self.authStatus = .error("Invalid token response from Google")
                return
            }

            self.accessToken = access
            let expiresIn = (json["expires_in"] as? Double) ?? 3600
            self.accessTokenExpiresAt = Date().addingTimeInterval(expiresIn)

            if let refresh = json["refresh_token"] as? String {
                _ = KeychainHelper.shared.save(key: Self.keychainRefreshTokenKey, string: refresh)
                print("[OAuth] Refresh token securely saved to macOS Keychain")
            } else {
                print("[OAuth] Existing refresh token remains active in Keychain")
            }

            // Fetch user profile email
            let email = await fetchUserEmail(accessToken: access)
            if let email = email {
                UserDefaults.standard.set(email, forKey: Self.userEmailKey)
                print("[OAuth] Successfully authorized Google Account: \(email)")
            }
            self.authStatus = .connected(accountEmail: email)

            // Clear pending verifiers
            self.pendingCodeVerifier = nil
            self.pendingRedirectUri = nil

        } catch {
            print("[OAuth] Token exchange network error: \(error.localizedDescription)")
            self.authStatus = .error("Token exchange error: \(error.localizedDescription)")
        }
    }

    public func getValidAccessToken() async throws -> String {
        // Return existing token if valid for at least 60 more seconds
        if let token = accessToken, let expires = accessTokenExpiresAt, expires > Date().addingTimeInterval(60) {
            return token
        }

        // Otherwise refresh using refresh token from Keychain
        guard let refreshToken = KeychainHelper.shared.readString(key: Self.keychainRefreshTokenKey), !refreshToken.isEmpty else {
            self.authStatus = .disconnected
            throw GoogleDriveError.notAuthorized
        }

        guard !clientId.isEmpty else {
            throw GoogleDriveError.missingClientId
        }

        guard let url = URL(string: "https://oauth2.googleapis.com/token") else {
            throw GoogleDriveError.networkError("Invalid token URL")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        var params: [String: String] = [
            "client_id": clientId,
            "refresh_token": refreshToken,
            "grant_type": "refresh_token"
        ]

        if let secret = loadClientSecretIfAvailable(), !secret.isEmpty {
            params["client_secret"] = secret
        }

        request.httpBody = Self.formEncode(params).data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw GoogleDriveError.networkError("No response from Google token server")
        }

        if http.statusCode == 400 || http.statusCode == 401 {
            // Token revoked or expired (e.g. 7-day testing mode expiration)
            let errString = String(data: data, encoding: .utf8) ?? ""
            disconnect()
            self.authStatus = .error("Authorization expired or revoked. Please reconnect in Settings.")
            throw GoogleDriveError.authorizationExpired(errString)
        }

        guard http.statusCode == 200,
              let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let newAccess = json["access_token"] as? String else {
            throw GoogleDriveError.networkError("Failed to parse refreshed access token")
        }

        self.accessToken = newAccess
        let expiresIn = (json["expires_in"] as? Double) ?? 3600
        self.accessTokenExpiresAt = Date().addingTimeInterval(expiresIn)

        // Save new refresh token if Google rotated it
        if let newRefresh = json["refresh_token"] as? String {
            _ = KeychainHelper.shared.save(key: Self.keychainRefreshTokenKey, string: newRefresh)
        }

        return newAccess
    }

    private func fetchUserEmail(accessToken: String) async -> String? {
        guard let url = URL(string: "https://www.googleapis.com/oauth2/v2/userinfo") else { return nil }
        var req = URLRequest(url: url)
        req.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        do {
            let (data, response) = try await URLSession.shared.data(for: req)
            if let http = response as? HTTPURLResponse, http.statusCode == 200,
               let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] {
                return json["email"] as? String
            }
        } catch {}
        return nil
    }

    // -------------------------------------------------------------------------
    // Google Drive File Operations (Resumable Upload, Download, Delete, Sweep)
    // -------------------------------------------------------------------------

    public func uploadEncryptedPayload(
        encryptedData: Data,
        progress: @escaping (Double) -> Void
    ) async throws -> String {
        guard encryptedData.count <= MAX_TRANSFER_BYTES else {
            throw GoogleDriveError.fileTooLarge(encryptedData.count)
        }

        let token = try await getValidAccessToken()

        // 1. Initiate Resumable Upload Session
        guard let initUrl = URL(string: "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable") else {
            throw GoogleDriveError.networkError("Invalid upload URL")
        }

        var initReq = URLRequest(url: initUrl)
        initReq.httpMethod = "POST"
        initReq.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        initReq.setValue("application/json; charset=UTF-8", forHTTPHeaderField: "Content-Type")
        initReq.setValue("application/octet-stream", forHTTPHeaderField: "X-Upload-Content-Type")
        initReq.setValue("\(encryptedData.count)", forHTTPHeaderField: "X-Upload-Content-Length")

        // Obfuscate file name to UUID only — zero plaintext metadata in Google Drive!
        let metadata: [String: Any] = [
            "name": "clipsync_\(UUID().uuidString).enc",
            "description": "ClipSync temporary encrypted clipboard transfer (auto-deleted within 24h)"
        ]
        initReq.httpBody = try JSONSerialization.data(withJSONObject: metadata)

        let (_, initRes) = try await URLSession.shared.data(for: initReq)
        guard let httpInit = initRes as? HTTPURLResponse,
              httpInit.statusCode == 200,
              let location = httpInit.value(forHTTPHeaderField: "Location"),
              let uploadUrl = URL(string: location) else {
            throw GoogleDriveError.uploadFailed("Failed to initiate resumable upload session")
        }

        // 2. Upload Binary Chunks to Resumable URL
        let totalBytes = encryptedData.count
        let chunkSize = 1024 * 1024 // 1 MB chunk
        var offset = 0

        while offset < totalBytes {
            let nextOffset = min(offset + chunkSize, totalBytes)
            let chunk = encryptedData.subdata(in: offset..<nextOffset)

            var chunkReq = URLRequest(url: uploadUrl)
            chunkReq.httpMethod = "PUT"
            chunkReq.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
            chunkReq.setValue("bytes \(offset)-\(nextOffset - 1)/\(totalBytes)", forHTTPHeaderField: "Content-Range")
            chunkReq.httpBody = chunk

            let (chunkData, chunkRes) = try await URLSession.shared.data(for: chunkReq)
            guard let httpChunk = chunkRes as? HTTPURLResponse else {
                throw GoogleDriveError.uploadFailed("No response for upload chunk at offset \(offset)")
            }

            offset = nextOffset
            progress(Double(offset) / Double(totalBytes))

            if httpChunk.statusCode == 200 || httpChunk.statusCode == 201 {
                // Completed! Parse file ID
                guard let json = try JSONSerialization.jsonObject(with: chunkData) as? [String: Any],
                      let fileId = json["id"] as? String else {
                    throw GoogleDriveError.uploadFailed("Missing file id in upload completion response")
                }
                return fileId
            } else if httpChunk.statusCode != 308 {
                // 308 Resume Incomplete is expected for partial chunks
                let errBody = String(data: chunkData, encoding: .utf8) ?? ""
                throw GoogleDriveError.uploadFailed("Chunk upload failed (HTTP \(httpChunk.statusCode)): \(errBody)")
            }
        }

        throw GoogleDriveError.uploadFailed("Upload loop ended without confirmation")
    }

    public func downloadEncryptedPayload(
        driveFileId: String,
        progress: @escaping (Double) -> Void
    ) async throws -> Data {
        let token = try await getValidAccessToken()

        guard let url = URL(string: "https://www.googleapis.com/drive/v3/files/\(driveFileId)?alt=media") else {
            throw GoogleDriveError.networkError("Invalid download URL")
        }

        var req = URLRequest(url: url)
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse else {
            throw GoogleDriveError.downloadFailed("No response from Google Drive")
        }

        guard http.statusCode == 200 else {
            let err = String(data: data, encoding: .utf8) ?? ""
            if http.statusCode == 404 {
                throw GoogleDriveError.fileNotFound(driveFileId)
            }
            throw GoogleDriveError.downloadFailed("Download failed (HTTP \(http.statusCode)): \(err)")
        }

        progress(1.0)
        return data
    }

    public func deleteFile(driveFileId: String) async -> Bool {
        guard let token = try? await getValidAccessToken(),
              let url = URL(string: "https://www.googleapis.com/drive/v3/files/\(driveFileId)") else {
            return false
        }

        var req = URLRequest(url: url)
        req.httpMethod = "DELETE"
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        do {
            let (_, response) = try await URLSession.shared.data(for: req)
            if let http = response as? HTTPURLResponse {
                // 204 No Content or 404 Not Found (already deleted) both count as success
                return http.statusCode == 204 || http.statusCode == 404
            }
        } catch {}
        return false
    }

    public func sweepOrphanedFiles(olderThanSeconds: TimeInterval = 24 * 3600) async -> Int {
        guard let token = try? await getValidAccessToken() else { return 0 }

        // Query only files created by ClipSync under 'me' in owners
        let query = "'me' in owners and trashed = false"
        guard let encodedQuery = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let url = URL(string: "https://www.googleapis.com/drive/v3/files?q=\(encodedQuery)&fields=files(id,name,createdTime)") else {
            return 0
        }

        var req = URLRequest(url: url)
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        var deletedCount = 0
        do {
            let (data, response) = try await URLSession.shared.data(for: req)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200,
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let files = json["files"] as? [[String: Any]] else {
                return 0
            }

            let cutoff = Date().addingTimeInterval(-olderThanSeconds)
            let isoFormatter = ISO8601DateFormatter()
            isoFormatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]

            for file in files {
                guard let id = file["id"] as? String,
                      let name = file["name"] as? String,
                      name.starts(with: "clipsync_"),
                      let createdStr = file["createdTime"] as? String,
                      let createdDate = isoFormatter.date(from: createdStr) ?? ISO8601DateFormatter().date(from: createdStr) else {
                    continue
                }

                if createdDate < cutoff {
                    if await deleteFile(driveFileId: id) {
                        deletedCount += 1
                    }
                }
            }
        } catch {}
        return deletedCount
    }

    // -------------------------------------------------------------------------
    // PKCE Helpers
    // -------------------------------------------------------------------------

    private func generateCodeVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return ClipSyncCrypto.toBase64URL(Data(bytes))
    }

    private func generateCodeChallenge(from verifier: String) -> String {
        guard let data = verifier.data(using: .utf8) else { return "" }
        let digest = SHA256.hash(data: data)
        return ClipSyncCrypto.toBase64URL(Data(digest))
    }

    public enum GoogleDriveError: Error, LocalizedError {
        case notAuthorized
        case missingClientId
        case fileTooLarge(Int)
        case authorizationExpired(String)
        case uploadFailed(String)
        case downloadFailed(String)
        case fileNotFound(String)
        case networkError(String)

        public var errorDescription: String? {
            switch self {
            case .notAuthorized:
                return "Google Drive is not connected. Please connect in Settings."
            case .missingClientId:
                return "Google Client ID is missing. Please enter your Client ID in Settings."
            case .fileTooLarge(let size):
                return "File size (\(ByteCountFormatter.string(fromByteCount: Int64(size), countStyle: .file))) exceeds 100 MB limit."
            case .authorizationExpired(let msg):
                return "Google authorization expired: \(msg)"
            case .uploadFailed(let msg):
                return "Google Drive upload failed: \(msg)"
            case .downloadFailed(let msg):
                return "Google Drive download failed: \(msg)"
            case .fileNotFound(let id):
                return "File \(id) was already deleted or expired from Google Drive."
            case .networkError(let msg):
                return "Network error: \(msg)"
            }
        }
    }
}

extension GoogleDriveService: ASWebAuthenticationPresentationContextProviding {
    public func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        NSApplication.shared.windows.first { $0.isVisible } ?? NSWindow()
    }
}
