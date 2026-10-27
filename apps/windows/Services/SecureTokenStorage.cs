using System;
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
        string path = customPath ?? Path.Combine(_storageDir, "credentials.json");
        if (!File.Exists(path))
        {
            return (null, null);
        }

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

            if (clientElement.ValueKind != JsonValueKind.Undefined)
            {
                string? clientId = clientElement.TryGetProperty("client_id", out var idProp) ? idProp.GetString() : null;
                string? clientSecret = clientElement.TryGetProperty("client_secret", out var secretProp) ? secretProp.GetString() : null;

                if (!string.IsNullOrEmpty(clientId))
                {
                    SaveToken("google_client_id", clientId);
                }
                if (!string.IsNullOrEmpty(clientSecret))
                {
                    SaveToken("google_client_secret", clientSecret);
                }

                return (clientId, clientSecret);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SecureStorage] Failed to parse credentials.json: {ex.Message}");
        }

        return (null, null);
    }
}
