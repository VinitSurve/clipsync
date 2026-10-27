using System;
using System.Security.Cryptography;
using System.Text;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Crypto;

public sealed class ClipSyncCrypto
{
    private readonly byte[] _key;

    public ClipSyncCrypto(string passphrase, string salt)
    {
        byte[] passBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] saltBytes = Encoding.UTF8.GetBytes(salt);

        _key = Rfc2898DeriveBytes.Pbkdf2(
            passBytes,
            saltBytes,
            100000,
            HashAlgorithmName.SHA256,
            32
        );
    }

    public EncryptedPayloadEnvelope Encrypt(string plainText)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] nonce = new byte[12]; // 96-bit IV
        RandomNumberGenerator.Fill(nonce);

        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[16]; // 128-bit authentication tag

        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        return new EncryptedPayloadEnvelope(
            Version: 1,
            Iv: ToBase64Url(nonce),
            Ciphertext: ToBase64Url(cipherBytes),
            Tag: ToBase64Url(tag)
        );
    }

    public string Decrypt(EncryptedPayloadEnvelope envelope)
    {
        byte[] nonce = FromBase64Url(envelope.Iv);
        byte[] cipherBytes = FromBase64Url(envelope.Ciphertext);
        byte[] tag = FromBase64Url(envelope.Tag);

        byte[] decryptedBytes = new byte[cipherBytes.Length];
        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Decrypt(nonce, cipherBytes, tag, decryptedBytes);

        return Encoding.UTF8.GetString(decryptedBytes);
    }

    public (byte[] Ciphertext, string Iv, string Tag, string Sha256Hex) EncryptBinary(byte[] plainBytes)
    {
        string sha256Hex = ComputeSha256(plainBytes);

        byte[] nonce = new byte[12]; // 96-bit IV
        RandomNumberGenerator.Fill(nonce);

        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[16]; // 128-bit authentication tag

        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        return (cipherBytes, ToBase64Url(nonce), ToBase64Url(tag), sha256Hex);
    }

    public byte[] DecryptBinary(byte[] cipherBytes, string ivBase64, string tagBase64, string? expectedSha256 = null)
    {
        byte[] nonce = FromBase64Url(ivBase64);
        byte[] tag = FromBase64Url(tagBase64);

        byte[] plainBytes = new byte[cipherBytes.Length];
        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            string actualHash = ComputeSha256(plainBytes);
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("Integrity check failed: decrypted payload hash does not match expected SHA-256");
            }
        }

        return plainBytes;
    }

    public static string ComputeSha256(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        return ComputeSha256(bytes);
    }

    public static string ComputeSha256(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ToBase64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    public static byte[] FromBase64Url(string input)
    {
        string base64 = input.Replace("-", "+").Replace("_", "/");
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
