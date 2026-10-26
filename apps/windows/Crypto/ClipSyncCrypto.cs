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

    public static string ComputeSha256(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
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
