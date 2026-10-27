using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipSync.Windows.Services;

public sealed class SecureTokenStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClipSync-Token-Storage-v1");
    private readonly string? _customDir;
    private readonly string _storageDir;
    private readonly string _storageFile;

    public SecureTokenStorage(string? customDir = null)
    {
        _customDir = customDir;
        if (!string.IsNullOrEmpty(customDir))
        {
            _storageDir = customDir;
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _storageDir = Path.Combine(localAppData, "ClipSync");
        }

        Directory.CreateDirectory(_storageDir);
        _storageFile = Path.Combine(_storageDir, "tokens.dat");
    }


    public void SaveToken(string key, string value)
    {
        var dict = LoadAllTokens();
        dict[key] = value;
        SaveAllTokens(dict);
    }

    public string? GetToken(string key)
    {
        var dict = LoadAllTokens();
        return dict.TryGetValue(key, out var val) ? val : null;
    }

    public void DeleteToken(string key)
    {
        var dict = LoadAllTokens();
        if (dict.Remove(key))
        {
            SaveAllTokens(dict);
        }
    }

    public void ClearAll()
    {
        try
        {
            if (File.Exists(_storageFile))
            {
                File.Delete(_storageFile);
            }
        }
        catch { }
    }

    private System.Collections.Generic.Dictionary<string, string> LoadAllTokens()
    {
        try
        {
            if (!File.Exists(_storageFile))
            {
                return new System.Collections.Generic.Dictionary<string, string>();
            }

            byte[] encryptedBytes = File.ReadAllBytes(_storageFile);
            if (encryptedBytes.Length == 0)
            {
                return new System.Collections.Generic.Dictionary<string, string>();
            }

            byte[] plainBytes;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                // Cross-platform test fallback: simple obfuscation
                plainBytes = UnprotectFallback(encryptedBytes);
            }

            string json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json) 
                ?? new System.Collections.Generic.Dictionary<string, string>();
        }
        catch
        {
            return new System.Collections.Generic.Dictionary<string, string>();
        }
    }

    private void SaveAllTokens(System.Collections.Generic.Dictionary<string, string> dict)
    {
        try
        {
            string json = JsonSerializer.Serialize(dict);
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);
            byte[] encryptedBytes;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                // Cross-platform test fallback
                encryptedBytes = ProtectFallback(plainBytes);
            }

            File.WriteAllBytes(_storageFile, encryptedBytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SecureStorage] Failed to save tokens: {ex.Message}");
        }
    }

    private static byte[] ProtectFallback(byte[] plain)
    {
        byte[] copy = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            copy[i] = (byte)(plain[i] ^ Entropy[i % Entropy.Length]);
        }
        return copy;
    }

    private static byte[] UnprotectFallback(byte[] encrypted)
    {
        return ProtectFallback(encrypted);
    }

    public (string? ClientId, string? ClientSecret, string? ProjectId) AutoImportCredentialsJson(string? customPath = null)
    {
        var candidatePaths = new List<string>();
        bool isManualSelection = !string.IsNullOrEmpty(customPath);

        if (isManualSelection)
        {
            candidatePaths.Add(customPath!);
        }
        else if (!string.IsNullOrEmpty(_customDir))
        {
            // In custom directory mode (e.g. isolated unit tests), restrict scan solely to custom directory
            candidatePaths.Add(Path.Combine(_storageDir, "credentials.json"));
            candidatePaths.Add(Path.Combine(_storageDir, "credentials_clipsync.json"));
        }
        else
        {
            // Auto-discovery: Scan candidate locations
            // 1. Local AppData storage dir: %LOCALAPPDATA%\ClipSync\credentials.json or credentials_clipsync.json
            candidatePaths.Add(Path.Combine(_storageDir, "credentials.json"));
            candidatePaths.Add(Path.Combine(_storageDir, "credentials_clipsync.json"));


            // 2. Roaming AppData: %APPDATA%\ClipSync\credentials.json
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                {
                    candidatePaths.Add(Path.Combine(appData, "ClipSync", "credentials.json"));
                    candidatePaths.Add(Path.Combine(appData, "ClipSync", "credentials_clipsync.json"));
                }
            }
            catch { }

            // 3. User Downloads folder: prioritize files with 'clipsync' in the name
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    string downloadsDir = Path.Combine(userProfile, "Downloads");
                    candidatePaths.Add(Path.Combine(downloadsDir, "credentials_clipsync.json"));

                    if (Directory.Exists(downloadsDir))
                    {
                        foreach (var f in Directory.GetFiles(downloadsDir, "*clipsync*.json"))
                        {
                            candidatePaths.Add(f);
                        }
                        // Also inspect standard credentials.json and client_secret_*.json,
                        // but strictly validate project_id below to reject foreign projects.
                        candidatePaths.Add(Path.Combine(downloadsDir, "credentials.json"));
                        foreach (var f in Directory.GetFiles(downloadsDir, "client_secret_*.json"))
                        {
                            candidatePaths.Add(f);
                        }
                    }
                }
            }
            catch { }

            // 4. Current working directory / repository root
            try
            {
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "credentials_clipsync.json"));
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "credentials.json"));
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "apps", "windows", "credentials.json"));
            }
            catch { }

            // 5. Application base directory (where ClipSync.exe is installed)
            try
            {
                candidatePaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credentials_clipsync.json"));
                candidatePaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credentials.json"));
            }
            catch { }
        }

        foreach (string path in candidatePaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                string content = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                JsonElement clientElement = default;
                if (root.TryGetProperty("installed", out var installed))
                {
                    clientElement = installed;
                }
                else if (root.TryGetProperty("web", out var web))
                {
                    clientElement = web;
                }
                else if (root.TryGetProperty("client_id", out _))
                {
                    clientElement = root;
                }

                if (clientElement.ValueKind != JsonValueKind.Undefined)
                {
                    string? clientId = clientElement.TryGetProperty("client_id", out var idProp) ? idProp.GetString()?.Trim() : null;
                    string? clientSecret = clientElement.TryGetProperty("client_secret", out var secretProp) ? secretProp.GetString()?.Trim() : null;
                    string? projectId = clientElement.TryGetProperty("project_id", out var projProp) 
                        ? projProp.GetString()?.Trim() 
                        : (root.TryGetProperty("project_id", out var rootProjProp) ? rootProjProp.GetString()?.Trim() : null);

                    // If auto-discovery (not manual file selection), strictly require that project_id
                    // or filename contains 'clipsync'. NEVER silently import unrelated projects (e.g. 'homevault').
                    if (!isManualSelection)
                    {
                        string fileName = Path.GetFileName(path).ToLowerInvariant();
                        bool isClipSyncName = fileName.Contains("clipsync");
                        bool isClipSyncProject = !string.IsNullOrEmpty(projectId) && projectId.Contains("clipsync", StringComparison.OrdinalIgnoreCase);

                        if (!isClipSyncName && !isClipSyncProject)
                        {
                            Console.WriteLine($"[SecureStorage] Skipping non-ClipSync credentials file from '{path}' (Project: '{projectId ?? "unknown"}').");
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(clientId))
                    {
                        SaveToken("google_client_id", clientId);
                    }
                    if (!string.IsNullOrEmpty(clientSecret))
                    {
                        SaveToken("google_client_secret", clientSecret);
                    }
                    if (!string.IsNullOrEmpty(projectId))
                    {
                        SaveToken("google_project_id", projectId);
                    }

                    if (!string.IsNullOrEmpty(clientId))
                    {
                        Console.WriteLine($"[SecureStorage] Successfully imported credentials from: {path} (Project: {projectId ?? "unspecified"})");
                        return (clientId, clientSecret, projectId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecureStorage] Failed to parse credentials from '{path}': {ex.Message}");
            }
        }

        return (null, null, null);
    }
}

