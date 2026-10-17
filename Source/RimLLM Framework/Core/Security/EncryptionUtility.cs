using System;
using System.IO;
using System.Security.Cryptography;
using Verse;

namespace RimLLM_Framework.Core;

/// <summary>
/// 提供統一的 AES-256 對稱加密與解密工具，用於保護設定檔與遙測中的敏感資料。
/// AES 金鑰以每使用者的 OS 保護秘密保存，不再支援舊版格式。
/// </summary>
public static class EncryptionUtility
{
    private static byte[] _secureKey;
    private static readonly object CryptLock = new();
    private const string SecureVersionPrefix = "v3:";
    private const string SecureKeyFileName = "RimLLM_EncryptionKey.dat";

    // 讓測試使用隔離的 key 檔案；正式環境固定走每使用者的 OS 保護儲存。
    internal static Func<string> SecureKeyPathResolver { get; set; } = GetDefaultSecureKeyPath;

    internal static void ResetSecureKeyForTests()
    {
        lock (CryptLock)
        {
            _secureKey = null;
            SecureKeyPathResolver = GetDefaultSecureKeyPath;
        }
    }

    private static void GetSecureKeySnapshot(out byte[] key, out byte[] macKey)
    {
        lock (CryptLock)
        {
            if (_secureKey == null)
            {
                _secureKey = LoadOrCreateSecureKey();
            }

            key = (byte[])_secureKey.Clone();
        }

        macKey = DeriveSecureMacKey(key);
    }

    private static byte[] LoadOrCreateSecureKey()
        => RimLLMSecureKeyStore.LoadOrCreate(SecureKeyPathResolver());

    private static string GetDefaultSecureKeyPath()
    {
        try
        {
            string configFolder = GenFilePaths.ConfigFolderPath;
            if (!string.IsNullOrEmpty(configFolder))
            {
                return Path.Combine(configFolder, SecureKeyFileName);
            }
        }
        catch
        {
            // 測試或 headless 環境可能尚未初始化 Verse，改用同一使用者的應用程式資料夾。
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            throw new CryptographicException("Per-user application data path is unavailable.");
        }

        return Path.Combine(localAppData, "RimLLM Framework", SecureKeyFileName);
    }

    private static byte[] DeriveSecureMacKey(byte[] key)
        => HMACSHA256.HashData(key, "RimLLM Framework secure storage v3"u8);

    /// <summary>
    /// 加密字串，回傳 Base64 加密密文。
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            GetSecureKeySnapshot(out byte[] key, out byte[] macKey);

            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using MemoryStream ms = new MemoryStream();
            using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                using StreamWriter sw = new StreamWriter(cs);
                sw.Write(plainText);
            }
            byte[] cipherBytes = ms.ToArray();
            byte[] payload = Combine(aes.IV, cipherBytes);
            byte[] mac = ComputeMac(payload, macKey);
            return SecureVersionPrefix + Convert.ToBase64String(Combine(payload, mac));
        }
        catch (Exception ex)
        {
            RimLLMLog.Error($"[RimLLM] Encryption failed: {ex.Message}");
            throw new RimLLMException(LLMError.Unknown, $"Encryption failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解密 Base64 密文，回傳原始字串。
    /// </summary>
    /// <remarks>
    /// 解密失敗或遇到不支援的舊版本/未知前綴時回傳 <c>null</c>（而非空字串），
    /// 讓呼叫端能區分「金鑰本來就是空的」與「金鑰解不開或格式不支援」。
    /// 若此時回傳空字串，呼叫端下次存檔就會以 Encrypt("") 覆寫，造成金鑰靜默永久遺失。
    /// </remarks>
    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        if (!cipherText.StartsWith(SecureVersionPrefix, StringComparison.Ordinal))
            return null;

        try
        {
            GetSecureKeySnapshot(out byte[] key, out byte[] macKey);
            return DecryptPayload(cipherText.Substring(SecureVersionPrefix.Length), key, macKey);
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Decryption failed (malformed ciphertext or unavailable key): {ex.Message}");
            return null;
        }
    }

    private static string DecryptPayload(string encodedPayload, byte[] key, byte[] macKey)
    {
        byte[] allBytes = Convert.FromBase64String(encodedPayload);
        const int ivLength = 16;
        const int macLength = 32;

        if (allBytes.Length <= ivLength + macLength)
        {
            throw new CryptographicException("Encrypted payload is too short.");
        }

        int cipherLength = allBytes.Length - ivLength - macLength;
        byte[] iv = new byte[ivLength];
        byte[] cipherBytes = new byte[cipherLength];
        byte[] expectedMac = new byte[macLength];

        Buffer.BlockCopy(allBytes, 0, iv, 0, ivLength);
        Buffer.BlockCopy(allBytes, ivLength, cipherBytes, 0, cipherLength);
        Buffer.BlockCopy(allBytes, ivLength + cipherLength, expectedMac, 0, macLength);

        byte[] payload = Combine(iv, cipherBytes);
        byte[] actualMac = ComputeMac(payload, macKey);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, actualMac))
        {
            throw new CryptographicException("Encrypted payload authentication failed.");
        }

        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream ms = new MemoryStream(cipherBytes);
        using CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using StreamReader sr = new StreamReader(cs);
        return sr.ReadToEnd();
    }

    private static byte[] ComputeMac(byte[] payload, byte[] macKey)
        => HMACSHA256.HashData(macKey, payload);

    private static byte[] Combine(byte[] first, byte[] second)
    {
        byte[] combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

}
