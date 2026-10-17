using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RimLLM_Framework.Core;

/// <summary>Windows 使用 DPAPI；其他桌面平台只在設定目錄保存系統鑰匙圈的項目識別碼。</summary>
internal static class RimLLMSecureKeyStore
{
    private const string ReferencePrefix = "keyring:";
    private const int KeyLength = 32;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GreenMushroom.RimLLMFramework");

    internal static byte[] LoadOrCreate(string path)
    {
        path = PreparePath(path);
        using var gate = AcquireLock(path);
        if (OperatingSystem.IsWindows())
            return LoadOrCreateWindows(path);

        ISecretStore store = OperatingSystem.IsMacOS() ? new MacOSSecretStore()
            : OperatingSystem.IsLinux() ? new LinuxSecretStore()
            : throw new PlatformNotSupportedException("No system keyring is supported on this platform.");
        return LoadOrCreateKeyringCore(path, store);
    }

    /// <summary>讓平台無關的識別碼、競態與遺失金鑰行為能以隔離的儲存體驗證。</summary>
    internal static byte[] LoadOrCreateKeyring(string path, ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        path = PreparePath(path);
        using var gate = AcquireLock(path);
        return LoadOrCreateKeyringCore(path, store);
    }

    private static byte[] LoadOrCreateKeyringCore(string path, ISecretStore store)
    {
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length != ReferencePrefix.Length + 32)
                throw new CryptographicException("Stored keyring reference is invalid.");
            string reference = File.ReadAllText(path, Encoding.UTF8);
            if (!reference.StartsWith(ReferencePrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(reference[ReferencePrefix.Length..], "N", out _))
                throw new CryptographicException("Stored keyring reference is invalid.");

            // 指向的秘密遺失時不可重建，否則原有密文會永久失去對應金鑰。
            return ValidateKey(store.Read(reference[ReferencePrefix.Length..]));
        }

        string account = Guid.NewGuid().ToString("N");
        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            store.Write(account, key);
            WriteAtomically(path, Encoding.UTF8.GetBytes(ReferencePrefix + account));
        }
        catch
        {
            // 識別碼尚未發佈，沒有任何密文能引用這個新秘密；清除未完成的項目。
            try { store.Delete(account); } catch { }
            throw;
        }
        return key;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] LoadOrCreateWindows(string path)
    {
        if (File.Exists(path))
            return ValidateKey(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));

        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        WriteAtomically(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }

    private static byte[] ValidateKey(byte[] key)
    {
        if (key is not { Length: KeyLength })
            throw new CryptographicException("The system keyring storage key is missing or invalid.");
        return key;
    }

    private static string PreparePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new CryptographicException("Secure key path is unavailable.");
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static FileStream AcquireLock(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path + ".lock", options); }
            catch (IOException ex)
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new CryptographicException("Could not acquire the storage key lock; another process may be using it.", ex);
                Thread.Sleep(25);
            }
        }
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>兩種原生鑰匙圈共用的秘密操作；識別碼不含 API 金鑰或使用者路徑。</summary>
internal interface ISecretStore
{
    byte[] Read(string account);
    void Write(string account, byte[] key);
    void Delete(string account);
}
