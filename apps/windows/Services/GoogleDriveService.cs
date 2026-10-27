using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipSync.Windows.Crypto;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Services;

public enum GoogleDriveAuthStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed class GoogleDriveService
{
    private const string Scope = "https://www.googleapis.com/auth/drive.file";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string DriveFilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string ResumableUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable";

    private readonly SecureTokenStorage _tokenStorage;
    private readonly HttpClient _httpClient;

    private string? _accessToken;
    private DateTime? _accessTokenExpiresAt;

    public GoogleDriveAuthStatus Status { get; private set; } = GoogleDriveAuthStatus.Disconnected;
    public string? AccountEmail { get; private set; }
    public string? LastErrorMessage { get; private set; }

    public event Action? OnAuthStatusChanged;

    public bool IsConnected => Status == GoogleDriveAuthStatus.Connected;

    public string? ClientId
    {
        get => _tokenStorage.GetToken("google_client_id");
        set
        {
            if (!string.IsNullOrEmpty(value))
                _tokenStorage.SaveToken("google_client_id", value.Trim());
            else
                _tokenStorage.DeleteToken("google_client_id");
        }
    }

    public string? ClientSecret
    {
        get => _tokenStorage.GetToken("google_client_secret");
        set
        {
            if (!string.IsNullOrEmpty(value))
                _tokenStorage.SaveToken("google_client_secret", value.Trim());
            else
                _tokenStorage.DeleteToken("google_client_secret");
        }
    }

    public bool HasClientSecret => !string.IsNullOrWhiteSpace(ClientSecret);


    public string? ProjectId => _tokenStorage.GetToken("google_project_id");



    public string? ClientIdPrefix
    {
        get
        {
            if (string.IsNullOrEmpty(ClientId)) return null;
            return ClientId.Length > 16 ? ClientId[..16] + "..." : ClientId;
        }
    }

    public (string? ClientId, string? ClientSecret, string? ProjectId) ImportCredentialsFile(string path)
    {
        string? oldClientId = ClientId;
        var result = _tokenStorage.AutoImportCredentialsJson(path);
        bool clientIdChanged = !string.IsNullOrEmpty(result.ClientId) &&
                               !string.Equals(result.ClientId, oldClientId, StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(result.ClientId))
        {
            ClientId = result.ClientId;
        }
        if (!string.IsNullOrEmpty(result.ClientSecret))
        {
            ClientSecret = result.ClientSecret;
        }

        // If client ID changed, old tokens from previous client are invalid
        if (clientIdChanged)
        {
            Disconnect();
        }

        OnAuthStatusChanged?.Invoke();
        return result;
    }

    public GoogleDriveService(SecureTokenStorage? tokenStorage = null, HttpClient? httpClient = null)
    {
        _tokenStorage = tokenStorage ?? new SecureTokenStorage();
        _httpClient = httpClient ?? new HttpClient();

        // Check for existing credentials or auto-discover if not set or mismatched
        string? storedProjectId = _tokenStorage.GetToken("google_project_id");
        if (string.IsNullOrEmpty(_tokenStorage.GetToken("google_client_id")) ||
            (!string.IsNullOrEmpty(storedProjectId) && !storedProjectId.Contains("clipsync", StringComparison.OrdinalIgnoreCase)))
        {
            _tokenStorage.AutoImportCredentialsJson();
        }

        // Check for existing refresh token
        string? refreshToken = _tokenStorage.GetToken("google_refresh_token");
        if (!string.IsNullOrEmpty(refreshToken))
        {
            AccountEmail = _tokenStorage.GetToken("google_account_email");
            Status = GoogleDriveAuthStatus.Connected;
        }
        else
        {
            Status = GoogleDriveAuthStatus.Disconnected;
        }
    }


    // -------------------------------------------------------------------------
    // OAuth 2.0 PKCE Flow via Local Loopback
    // -------------------------------------------------------------------------

    public async Task<bool> StartAuthorizationAsync(CancellationToken ct = default)
    {
        string? clientId = ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Status = GoogleDriveAuthStatus.Error;
            LastErrorMessage = "Google Client ID is required in Settings.";
            OnAuthStatusChanged?.Invoke();
            return false;
        }

        Status = GoogleDriveAuthStatus.Connecting;
        LastErrorMessage = null;
        OnAuthStatusChanged?.Invoke();

        HttpListener? listener = null;
        try
        {
            // 1. Find free port for loopback redirect
            int port = GetRandomUnusedPort();
            string redirectUri = $"http://127.0.0.1:{port}/oauth2redirect";
            string listenerPrefix = $"http://127.0.0.1:{port}/oauth2redirect/";

            LogOAuthDiagnostic("StartAuthorization", $"ClientId configured: {!string.IsNullOrWhiteSpace(clientId)}, HasClientSecret: {HasClientSecret}, Port: {port}");

            listener = new HttpListener();
            listener.Prefixes.Add(listenerPrefix);
            listener.Start();

            // 2. Generate PKCE code verifier and challenge
            byte[] verifierBytes = new byte[32];
            RandomNumberGenerator.Fill(verifierBytes);
            string codeVerifier = ClipSyncCrypto.ToBase64Url(verifierBytes);

            byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
            string codeChallenge = ClipSyncCrypto.ToBase64Url(challengeHash);

            // 3. Build authorization URL
            string authUrl = $"{AuthEndpoint}?" +
                $"client_id={Uri.EscapeDataString(clientId)}&" +
                $"response_type=code&" +
                $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
                $"scope={Uri.EscapeDataString(Scope)}&" +
                $"code_challenge={Uri.EscapeDataString(codeChallenge)}&" +
                $"code_challenge_method=S256&" +
                $"access_type=offline&" +
                $"prompt=select_account%20consent";

            // 4. Launch browser
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Status = GoogleDriveAuthStatus.Error;
                LastErrorMessage = $"Failed to launch system browser: {ex.Message}";
                OnAuthStatusChanged?.Invoke();
                return false;
            }

            // 5. Wait for callback with 120s timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            var contextTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(-1, timeoutCts.Token));

            if (completedTask != contextTask)
            {
                Status = GoogleDriveAuthStatus.Error;
                LastErrorMessage = "Authorization timed out. Please try again.";
                OnAuthStatusChanged?.Invoke();
                return false;
            }

            var context = await contextTask;
            var request = context.Request;
            var response = context.Response;

            string? code = request.QueryString["code"];
            string? error = request.QueryString["error"];
            string? errorDescription = request.QueryString["error_description"];

            // Send friendly HTML response to browser
            string responseHtml;
            if (!string.IsNullOrEmpty(code))
            {
                responseHtml = "<html><head><title>ClipSync</title></head><body style=\"font-family:'Segoe UI',sans-serif; text-align:center; padding: 48px; background:#f6f8fa;\"><div style=\"max-width:480px; margin:0 auto; background:white; padding:32px; border-radius:8px; box-shadow:0 2px 8px rgba(0,0,0,0.1);\">" +
                               "<h2 style=\"color:#107c41;\">ClipSync Authorized!</h2><p>Google Drive is now securely connected to ClipSync. You can close this tab and return to the application.</p></div></body></html>";
            }
            else
            {
                responseHtml = $"<html><head><title>ClipSync</title></head><body style=\"font-family:'Segoe UI',sans-serif; text-align:center; padding: 48px; background:#f6f8fa;\"><div style=\"max-width:480px; margin:0 auto; background:white; padding:32px; border-radius:8px; box-shadow:0 2px 8px rgba(0,0,0,0.1);\">" +
                               $"<h2 style=\"color:#c42b1c;\">Authorization Rejected</h2><p>{WebUtility.HtmlEncode(errorDescription ?? error ?? "Unknown error")}</p></div></body></html>";
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
            response.ContentLength64 = buffer.Length;
            response.ContentType = "text/html; charset=utf-8";
            await response.OutputStream.WriteAsync(buffer, ct);
            response.OutputStream.Close();

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            {
                Status = GoogleDriveAuthStatus.Error;
                LastErrorMessage = errorDescription ?? error ?? "Authorization code missing.";
                OnAuthStatusChanged?.Invoke();
                return false;
            }

            // 6. Exchange code for tokens
            return await ExchangeCodeForTokensAsync(code, codeVerifier, redirectUri, ct);
        }
        catch (OperationCanceledException)
        {
            Status = GoogleDriveAuthStatus.Disconnected;
            LastErrorMessage = "Authorization was cancelled.";
            OnAuthStatusChanged?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            Status = GoogleDriveAuthStatus.Error;
            LastErrorMessage = $"Authorization error: {ex.Message}";
            OnAuthStatusChanged?.Invoke();
            return false;
        }
        finally
        {
            try
            {
                listener?.Stop();
                listener?.Close();
            }
            catch { }
        }
    }

    private async Task<bool> ExchangeCodeForTokensAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        try
        {
            string cleanClientId = ClientId!.Trim();
            var dict = new System.Collections.Generic.Dictionary<string, string>
            {
                ["client_id"] = cleanClientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            };

            string? secret = ClientSecret;
            bool hasSecret = !string.IsNullOrWhiteSpace(secret);
            if (hasSecret)
            {
                dict["client_secret"] = secret!.Trim();
            }

            string clientPrefix = cleanClientId.Length > 12 ? cleanClientId[..12] : cleanClientId;
            LogOAuthDiagnostic("TokenExchangeAttempt", $"ClientId prefix: {clientPrefix}..., HasClientSecret: {hasSecret}, RedirectUri: {redirectUri}");

            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(dict)
            };

            using var res = await _httpClient.SendAsync(req, ct);
            string json = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                string parsedError = ParseOAuthError(json, res.StatusCode);
                LogOAuthDiagnostic("TokenExchangeFailed", $"HTTP {(int)res.StatusCode} - {parsedError}");
                Status = GoogleDriveAuthStatus.Error;
                LastErrorMessage = parsedError;
                OnAuthStatusChanged?.Invoke();
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("access_token", out var accessProp))
            {
                LogOAuthDiagnostic("TokenExchangeFailed", "Access token property missing in Google JSON response");
                Status = GoogleDriveAuthStatus.Error;
                LastErrorMessage = "Access token missing in Google response.";
                OnAuthStatusChanged?.Invoke();
                return false;
            }

            _accessToken = accessProp.GetString();
            int expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
            _accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

            bool hasRefresh = root.TryGetProperty("refresh_token", out var refreshProp) && !string.IsNullOrEmpty(refreshProp.GetString());
            if (hasRefresh)
            {
                _tokenStorage.SaveToken("google_refresh_token", refreshProp.GetString()!);
            }

            LogOAuthDiagnostic("TokenExchangeSuccess", $"Access token received (expires in {expiresIn}s), Refresh token saved: {hasRefresh}");

            // Fetch user profile email
            AccountEmail = await FetchUserEmailAsync(_accessToken, ct);
            if (!string.IsNullOrEmpty(AccountEmail))
            {
                _tokenStorage.SaveToken("google_account_email", AccountEmail);
            }

            Status = GoogleDriveAuthStatus.Connected;
            LastErrorMessage = null;
            OnAuthStatusChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Status = GoogleDriveAuthStatus.Error;
            LastErrorMessage = $"Token exchange error: {ex.Message}";
            OnAuthStatusChanged?.Invoke();
            return false;
        }
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        // Return existing token if valid for at least 60 seconds
        if (!string.IsNullOrEmpty(_accessToken) && _accessTokenExpiresAt.HasValue && _accessTokenExpiresAt.Value > DateTime.UtcNow.AddSeconds(60))
        {
            return _accessToken;
        }

        string? refreshToken = _tokenStorage.GetToken("google_refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
        {
            Status = GoogleDriveAuthStatus.Disconnected;
            OnAuthStatusChanged?.Invoke();
            throw new InvalidOperationException("Not connected to Google Drive. Please connect in Settings.");
        }

        string? clientId = ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Missing Google Client ID.");
        }

        var dict = new System.Collections.Generic.Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        string? secret = ClientSecret;
        if (!string.IsNullOrEmpty(secret))
        {
            dict["client_secret"] = secret.Trim();
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(dict)
        };

        using var res = await _httpClient.SendAsync(req, ct);
        string json = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // Token revoked or expired (e.g. 7-day testing mode expiry)
            Disconnect();
            Status = GoogleDriveAuthStatus.Error;
            LastErrorMessage = "Google Drive authorization expired or was revoked. Please reconnect in Settings.";
            OnAuthStatusChanged?.Invoke();
            throw new InvalidOperationException(LastErrorMessage);
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to refresh Google token (HTTP {res.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        int expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        _accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

        // Update refresh token if rotated by Google
        if (root.TryGetProperty("refresh_token", out var newRefresh) && !string.IsNullOrEmpty(newRefresh.GetString()))
        {
            _tokenStorage.SaveToken("google_refresh_token", newRefresh.GetString()!);
        }

        return _accessToken!;
    }

    private async Task<string?> FetchUserEmailAsync(string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;

        try
        {
            // 1. Query Drive API /about endpoint (works natively with drive.file scope)
            using var driveReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
            driveReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var driveRes = await _httpClient.SendAsync(driveReq, ct);
            if (driveRes.IsSuccessStatusCode)
            {
                string json = await driveRes.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("user", out var userProp) &&
                    userProp.TryGetProperty("emailAddress", out var emailProp))
                {
                    return emailProp.GetString();
                }
            }
        }
        catch { }

        try
        {
            // 2. Fallback to oauth2 userinfo endpoint
            using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var res = await _httpClient.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("email", out var emailProp))
                {
                    return emailProp.GetString();
                }
            }
        }
        catch { }

        return null;
    }

    public void Disconnect()
    {
        _tokenStorage.DeleteToken("google_refresh_token");
        _tokenStorage.DeleteToken("google_account_email");
        _tokenStorage.DeleteToken("google_project_id");
        _accessToken = null;
        _accessTokenExpiresAt = null;
        AccountEmail = null;
        Status = GoogleDriveAuthStatus.Disconnected;
        LastErrorMessage = null;
        OnAuthStatusChanged?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Google Drive File Operations (Resumable Upload, Download, Delete, Sweep)
    // -------------------------------------------------------------------------

    public async Task<string> UploadEncryptedPayloadAsync(
        byte[] encryptedData,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (encryptedData.Length > TransferConstants.MaxTransferBytes)
        {
            throw new ArgumentException($"Payload size ({encryptedData.Length} bytes) exceeds 100 MB limit.");
        }

        string token = await GetValidAccessTokenAsync(ct);

        // 1. Initiate Resumable Upload Session
        using var initReq = new HttpRequestMessage(HttpMethod.Post, ResumableUploadEndpoint);
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        initReq.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        initReq.Headers.Add("X-Upload-Content-Length", encryptedData.Length.ToString());

        // Obfuscate file name to UUID only - zero plaintext metadata in Google Drive!
        var metadata = new
        {
            name = $"clipsync_{Guid.NewGuid():N}.enc",
            description = "ClipSync temporary encrypted clipboard transfer (auto-deleted within 24h)"
        };
        initReq.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");

        using var initRes = await _httpClient.SendAsync(initReq, ct);
        if (initRes.StatusCode != HttpStatusCode.OK || initRes.Headers.Location == null)
        {
            string err = await initRes.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Failed to initiate Google Drive upload (HTTP {initRes.StatusCode}): {err}");
        }

        Uri uploadUri = initRes.Headers.Location;

        // 2. Upload in 1 MB chunks to resumable session
        int totalBytes = encryptedData.Length;
        int chunkSize = 1024 * 1024; // 1 MB
        int offset = 0;

        while (offset < totalBytes)
        {
            ct.ThrowIfCancellationRequested();

            int nextOffset = Math.Min(offset + chunkSize, totalBytes);
            int count = nextOffset - offset;

            using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUri);
            var chunkContent = new ByteArrayContent(encryptedData, offset, count);
            chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            chunkContent.Headers.Add("Content-Range", $"bytes {offset}-{nextOffset - 1}/{totalBytes}");
            chunkReq.Content = chunkContent;

            using var chunkRes = await _httpClient.SendAsync(chunkReq, ct);
            offset = nextOffset;
            progress?.Report((double)offset / totalBytes);

            if (chunkRes.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                // Completed!
                string completeJson = await chunkRes.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(completeJson);
                return doc.RootElement.GetProperty("id").GetString()!;
            }
            else if ((int)chunkRes.StatusCode != 308) // 308 Resume Incomplete
            {
                string chunkErr = await chunkRes.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Chunk upload failed (HTTP {chunkRes.StatusCode}): {chunkErr}");
            }
        }

        throw new InvalidOperationException("Upload loop finished without receiving confirmation.");
    }

    public async Task<byte[]> DownloadEncryptedPayloadAsync(
        string driveFileId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string token = await GetValidAccessTokenAsync(ct);
        string cleanId = driveFileId.Trim();
        string url = $"{DriveFilesEndpoint}/{Uri.EscapeDataString(cleanId)}?alt=media";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string err = await res.Content.ReadAsStringAsync(ct);
            string parsedErr = ParseDriveError(err, res.StatusCode);
            throw new HttpRequestException(parsedErr);
        }

        long? totalBytes = res.Content.Headers.ContentLength;
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();

        byte[] buffer = new byte[64 * 1024];
        long totalRead = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                progress?.Report((double)totalRead / totalBytes.Value);
            }
        }

        progress?.Report(1.0);
        return ms.ToArray();
    }

    public async Task<bool> DeleteFileAsync(string driveFileId, CancellationToken ct = default)
    {
        try
        {
            string token = await GetValidAccessTokenAsync(ct);
            string url = $"{DriveFilesEndpoint}/{Uri.EscapeDataString(driveFileId)}";

            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await _httpClient.SendAsync(req, ct);
            // 204 No Content or 404 Not Found (already deleted) both count as success
            return res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> SweepOrphanedFilesAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        try
        {
            string token = await GetValidAccessTokenAsync(ct);
            string query = "'me' in owners and trashed = false";
            string url = $"{DriveFilesEndpoint}?q={Uri.EscapeDataString(query)}&fields=files(id,name,createdTime)";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return 0;

            string json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var filesProp)) return 0;

            DateTime cutoff = DateTime.UtcNow.Subtract(olderThan);
            int deletedCount = 0;

            foreach (var file in filesProp.EnumerateArray())
            {
                string? id = file.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                string? name = file.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                string? createdStr = file.TryGetProperty("createdTime", out var crProp) ? crProp.GetString() : null;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !name.StartsWith("clipsync_") || string.IsNullOrEmpty(createdStr))
                {
                    continue;
                }

                if (DateTime.TryParse(createdStr, out var created) && created.ToUniversalTime() < cutoff)
                {
                    if (await DeleteFileAsync(id, ct))
                    {
                        deletedCount++;
                    }
                }
            }

            return deletedCount;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetRandomUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string ParseDriveError(string rawJson, HttpStatusCode statusCode)
        => TransferHelpers.ParseDriveError(rawJson, statusCode);

    private static string ParseOAuthError(string json, HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errProp))
            {
                string err = errProp.GetString() ?? "";
                string desc = root.TryGetProperty("error_description", out var descProp) ? descProp.GetString() ?? "" : "";
                if (err == "invalid_request" && desc.Contains("client_secret is missing"))
                {
                    return "invalid_request: client_secret is missing. (Google Cloud Desktop App clients require client_secret. Paste your client secret or click 'Import JSON...' in Settings.)";
                }
                return $"{err}: {desc}".TrimEnd(':', ' ');
            }
        }
        catch { }

        return $"HTTP {statusCode}";
    }

    private void LogOAuthDiagnostic(string action, string detail)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        string line = $"[{timestamp}] [OAuth] {action}: {detail}";
        Console.WriteLine(line);

        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSync");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "oauth_diagnostics.log");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { }
    }
}
