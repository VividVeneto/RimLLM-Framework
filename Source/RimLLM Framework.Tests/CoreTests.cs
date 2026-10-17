using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class CoreTests
{
    private string _encryptionKeyDirectory;

    [SetUp]
    public void SetUpEncryptionKeyStore()
    {
        _encryptionKeyDirectory = Path.Combine(
            Path.GetTempPath(),
            "RimLLMEncryptionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_encryptionKeyDirectory);

        EncryptionUtility.ResetSecureKeyForTests();
        EncryptionUtility.SecureKeyPathResolver = () =>
            Path.Combine(_encryptionKeyDirectory, "RimLLM_EncryptionKey.dat");
    }

    [TearDown]
    public void TearDown()
    {
        EncryptionUtility.ResetSecureKeyForTests();
        try
        {
            Directory.Delete(_encryptionKeyDirectory, true);
        }
        catch
        {
        }
    }

    [Test]
    public void TestEncryption()
    {
        string original = "sk-proj-1234567890abcdefghijklmnopqrstuvwxyz";
        string cipher = EncryptionUtility.Encrypt(original);
        ClassicAssert.IsNotEmpty(cipher);
        ClassicAssert.AreNotEqual(original, cipher);
        ClassicAssert.IsTrue(cipher.StartsWith("v3:"));

        string decrypted = EncryptionUtility.Decrypt(cipher);
        ClassicAssert.AreEqual(original, decrypted);

        ClassicAssert.AreEqual("", EncryptionUtility.Encrypt(""));
        ClassicAssert.AreEqual("", EncryptionUtility.Decrypt(""));
    }

    [Test]
    public void SamePlaintextUsesFreshIvAndRemainsDecryptable()
    {
        const string original = "sensitive-api-key";
        string first = EncryptionUtility.Encrypt(original);
        string second = EncryptionUtility.Encrypt(original);
        ClassicAssert.AreNotEqual(first, second);
        ClassicAssert.AreEqual(original, EncryptionUtility.Decrypt(first));
        ClassicAssert.AreEqual(original, EncryptionUtility.Decrypt(second));
    }

    [TestCase("raw-unprefixed-string")]
    [TestCase("c2VjcmV0LWtleQ==")]
    [TestCase("v1:c2VjcmV0LWtleQ==")]
    [TestCase("v2:c2VjcmV0LWtleQ==")]
    [TestCase("v4:c2VjcmV0LWtleQ==")]
    [TestCase("unknown:c2VjcmV0LWtleQ==")]
    public void UnsupportedFormatsAreRejectedWithoutCreatingAStorageKey(string ciphertext)
    {
        ClassicAssert.IsNull(EncryptionUtility.Decrypt(ciphertext));
        ClassicAssert.IsEmpty(Directory.GetFiles(_encryptionKeyDirectory));
    }

    [Test]
    public void TestSecureKeyStoreSurvivesReloadAndRejectsADifferentKey()
    {
        const string original = "persisted-secret";
        string keyPath = Path.Combine(_encryptionKeyDirectory, "RimLLM_EncryptionKey.dat");
        string cipher = EncryptionUtility.Encrypt(original);

        ClassicAssert.IsTrue(File.Exists(keyPath), "新的加密格式必須建立受保護的 per-user key 檔案");
        ClassicAssert.IsNotEmpty(File.ReadAllBytes(keyPath));

        EncryptionUtility.ResetSecureKeyForTests();
        EncryptionUtility.SecureKeyPathResolver = () => keyPath;
        ClassicAssert.AreEqual(original, EncryptionUtility.Decrypt(cipher),
            "重新載入同一個受保護 key 後仍應能解密");

        string differentKeyPath = Path.Combine(_encryptionKeyDirectory, "different-key.dat");
        EncryptionUtility.ResetSecureKeyForTests();
        EncryptionUtility.SecureKeyPathResolver = () => differentKeyPath;
        ClassicAssert.IsNull(EncryptionUtility.Decrypt(cipher),
            "使用不同的儲存金鑰不得解開原本的密文");
    }

    [Test]
    public void TestSanitizeForLogRedactsProviderSpecificKeys()
    {
        var secrets = new Dictionary<string, string>
        {
            { "Google", "AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7" },
            { "Groq", "gsk_abcdefghijklmnopqrstuvwxyz0123456789" },
            { "Grok", "xai-abcdefghijklmnopqrstuvwxyz0123456789" },
            { "Nvidia", "nvapi-abcdefghijklmnopqrstuvwxyz0123456789" },
            { "OpenAI", "sk-abcdefghijklmnopqrstuvwxyz0123456789" },
            { "Anthropic", "sk-ant-abcdefghijklmnopqrstuvwxyz0123" }
        };

        foreach (var kvp in secrets)
        {
            string sanitized = RimLLMLog.SanitizeForLog($"request failed with key {kvp.Value}");
            ClassicAssert.IsFalse(sanitized.Contains(kvp.Value),
                $"日誌遮罩必須涵蓋全部供應商的金鑰格式（{kvp.Key} 未被遮罩）");
        }

        string bearer = RimLLMLog.SanitizeForLog("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345");
        ClassicAssert.IsFalse(bearer.Contains("abcdefghijklmnopqrstuvwxyz012345"), "Bearer token 必須被遮罩");
    }

    [Test]
    public void TestSanitizeForLogTruncatesAndEscapesNewlines()
    {
        string sanitized = RimLLMLog.SanitizeForLog("line1\r\nline2", 500);
        ClassicAssert.IsFalse(sanitized.Contains("\n"), "換行必須被跳脫以防日誌注入");
        ClassicAssert.IsTrue(sanitized.Contains("\\r\\n"));

        string longText = new('x', 600);
        ClassicAssert.IsTrue(RimLLMLog.SanitizeForLog(longText, 100).Length <= 103, "超長內容必須被截斷");
    }

    [Test]
    public void TestDecryptFailureReturnsNullInsteadOfEmpty()
    {
        // 目前格式的損毀內容仍應回傳 null，與空值區分。
        string result = EncryptionUtility.Decrypt("v3:bm90LWEtdmFsaWQtcGF5bG9hZA==");

        ClassicAssert.IsNull(result,
            "解密失敗必須回傳 null 而非空字串，呼叫端才能保留原始密文而不覆寫為空");
        ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Decrypt(""),
            "空輸入仍應回傳空字串，與解密失敗區分");
    }

    [Test]
    public void TestLanguageKeysAreConsistentAcrossLocales()
    {
        string repoRoot = FindRepositoryRoot();
        if (repoRoot == null)
        {
            Assert.Ignore("找不到 repository 根目錄，略過語言檔一致性檢查。");
        }

        var localeKeys = new Dictionary<string, List<string>>();
        foreach (string locale in new[] { "English", "ChineseSimplified", "ChineseTraditional" })
        {
            string path = System.IO.Path.Combine(repoRoot, "Languages", locale, "Keyed", "Keys.xml");
            ClassicAssert.IsTrue(System.IO.File.Exists(path), $"缺少語言檔: {path}");

            var doc = System.Xml.Linq.XDocument.Load(path);
            var keys = new List<string>();
            foreach (var element in doc.Root.Elements())
            {
                keys.Add(element.Name.LocalName);
            }

            var duplicates = new List<string>();
            var seen = new HashSet<string>();
            foreach (string key in keys)
            {
                if (!seen.Add(key)) duplicates.Add(key);
            }
            ClassicAssert.IsEmpty(duplicates, $"{locale} 語言檔含重複鍵: {string.Join(", ", duplicates.ToArray())}");

            keys.Sort(StringComparer.Ordinal);
            localeKeys[locale] = keys;
        }

        CollectionAssert.AreEqual(localeKeys["English"], localeKeys["ChineseTraditional"],
            "三語系語言檔的鍵集合必須完全一致");
        CollectionAssert.AreEqual(localeKeys["English"], localeKeys["ChineseSimplified"],
            "三語系語言檔的鍵集合必須完全一致");
    }

    private static string FindRepositoryRoot()
    {
        string envRoot = System.Environment.GetEnvironmentVariable("RIMLLM_REPO_ROOT");
        if (!string.IsNullOrEmpty(envRoot) && System.IO.Directory.Exists(envRoot))
        {
            return envRoot;
        }

        var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "About", "About.xml")))
            {
                return dir.FullName;
            }
        }
        return null;
    }
}
