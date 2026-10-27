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
    private readonly string _storageDir;
    private readonly string _storageFile;

    public SecureTokenStorage(string? customDir = null)
    {
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

    public (string? ClientId, string? ClientSecret) AutoImportCredentialsJson(string? customPath = null)
    {
        var candidatePaths = new List<string>();
        if (!string.IsNullOrEmpty(customPath))
        {
            candidatePaths.Add(customPath);
        }
        else
        {
            // 1. Local AppData storage dir: %LOCALAPPDATA%\ClipSync\credentials.json
            candidatePaths.Add(Path.Combine(_storageDir, "credentials.json"));

            // 2. Roaming AppData: %APPDATA%\ClipSync\credentials.json
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                candidatePaths.Add(Path.Combine(appData, "ClipSync", "credentials.json"));
            }
            catch { }

            // 3. User Downloads folder (common browser download target for Google Cloud credentials)
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloadsDir = Path.Combine(userProfile, "Downloads");
                candidatePaths.Add(Path.Combine(downloadsDir, "credentials.json"));

                if (Directory.Exists(downloadsDir))
                {
                    // Match Google default filename pattern: client_secret_*.json
                    foreach (var f in Directory.GetFiles(downloadsDir, "client_secret_*.json"))
                    {
                        candidatePaths.Add(f);
                    }
                }
            }
            catch { }

            // 4. Current working directory / repository root
            try
            {
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "credentials.json"));
                candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "apps", "windows", "credentials.json"));
            }
            catch { }

            // 5. Application base directory (where ClipSync.exe is installed)
            try
            {
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

                    if (!string.IsNullOrEmpty(clientId))
                    {
                        SaveToken("google_client_id", clientId);
                    }
                    if (!string.IsNullOrEmpty(clientSecret))
                    {
                        SaveToken("google_client_secret", clientSecret);
                    }

                    if (!string.IsNullOrEmpty(clientId))
                    {
                        Console.WriteLine($"[SecureStorage] Successfully imported credentials from: {path}");
                        return (clientId, clientSecret);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecureStorage] Failed to parse credentials from '{path}': {ex.Message}");
            }
        }

        return (null, null);
    }
}
