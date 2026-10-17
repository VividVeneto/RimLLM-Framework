using System.Security.Cryptography;
using RimLLM_Framework.Core;

if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
string mode = args.FirstOrDefault() ?? "roundtrip";
string folder = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "rimllm-native-" + Guid.NewGuid().ToString("N"));
folder = Path.GetFullPath(folder);
if (!folder.StartsWith(Path.Combine(Path.GetTempPath(), "rimllm-native-"), StringComparison.Ordinal))
    throw new InvalidOperationException("Test paths must stay under the isolated temporary prefix.");
Directory.CreateDirectory(folder);
string path = Path.Combine(folder, "key.dat");
ISecretStore store = OperatingSystem.IsLinux() ? new LinuxSecretStore()
    : OperatingSystem.IsMacOS() ? new MacOSSecretStore() : throw new PlatformNotSupportedException();

if (mode == "unavailable")
{
    try
    {
        RimLLMSecureKeyStore.LoadOrCreate(path);
        throw new Exception("Unavailable service was accepted.");
    }
    catch (CryptographicException)
    {
        if (File.Exists(path)) throw new Exception("Unavailable service published a key file.");
        Console.WriteLine("PASS: unavailable Secret Service fails without plaintext fallback.");
        return;
    }
}
if (mode == "create")
{
    byte[] key = RimLLMSecureKeyStore.LoadOrCreate(path);
    Console.WriteLine(Convert.ToHexString(SHA256.HashData(key)));
    return;
}
if (mode == "delete")
{
    string id = File.ReadAllText(path)["keyring:".Length..];
    store.Delete(id);
    File.Delete(path);
    File.Delete(path + ".lock");
    Directory.Delete(folder);
    return;
}

void UseIsolatedKey()
{
    EncryptionUtility.ResetSecureKeyForTests();
    EncryptionUtility.SecureKeyPathResolver = () => path;
}

UseIsolatedKey();
string cipher = EncryptionUtility.Encrypt("native-smoke-key");
if (!cipher.StartsWith("v3:") || EncryptionUtility.Decrypt(cipher) != "native-smoke-key")
    throw new Exception("Native keyring roundtrip failed.");
string second = EncryptionUtility.Encrypt("native-smoke-key");
if (cipher == second) throw new Exception("Encryption reused its IV.");
UseIsolatedKey();
if (EncryptionUtility.Decrypt(cipher) != "native-smoke-key") throw new Exception("Reload failed.");
string reference = File.ReadAllText(path);
string account = reference["keyring:".Length..];
byte[] master = store.Read(account);
if (master.Length != 32 || reference.Contains(Convert.ToBase64String(master))) throw new Exception("Secret leaked into reference file.");
foreach (string old in new[] { "v1:AAAA", "v2:AAAA", "AAAA", "v4:AAAA" })
    if (EncryptionUtility.Decrypt(old) != null) throw new Exception("Unsupported ciphertext was accepted.");
store.Delete(account);
UseIsolatedKey();
if (EncryptionUtility.Decrypt(cipher) != null || File.ReadAllText(path) != reference)
    throw new Exception("A missing key was silently regenerated.");
File.Delete(path);
    File.Delete(path + ".lock");
    Directory.Delete(folder);
Console.WriteLine("PASS: native Secret Service v3 roundtrip, reload, random IV, metadata-only file, rejected legacy formats, and lost-key preservation.");
