using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Tests;

[TestFixture]
public sealed class SecureKeyStoreTests
{
    private string _directory;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("RimLLMKeyringTest_").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void KeyringKeySurvivesReloadWithoutWritingTheSecretToDisk()
    {
        var store = new MemorySecretStore();
        string path = Path.Combine(_directory, "key.dat");
        byte[] first = RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store);
        byte[] second = RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store);

        CollectionAssert.AreEqual(first, second);
        ClassicAssert.AreEqual(32, first.Length);
        ClassicAssert.AreEqual(1, store.Writes);
        string reference = File.ReadAllText(path);
        ClassicAssert.IsTrue(reference.StartsWith("keyring:", StringComparison.Ordinal));
        ClassicAssert.IsFalse(reference.Contains(Convert.ToBase64String(first), StringComparison.Ordinal));
        ClassicAssert.AreEqual(40, new FileInfo(path).Length);
    }

    [Test]
    public void LostSecretIsNotReplacedWithAnUnrelatedKey()
    {
        var store = new MemorySecretStore();
        string path = Path.Combine(_directory, "key.dat");
        RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store);
        string reference = File.ReadAllText(path);
        store.Items.Clear();

        Assert.Throws<CryptographicException>(() => RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store));
        ClassicAssert.AreEqual(1, store.Writes);
        ClassicAssert.AreEqual(reference, File.ReadAllText(path));
    }

    [TestCase("invalid")]
    [TestCase("keyring:invalid")]
    public void InvalidReferenceIsRejectedWithoutCreatingAnotherSecret(string reference)
    {
        var store = new MemorySecretStore();
        string path = Path.Combine(_directory, "key.dat");
        File.WriteAllText(path, reference);

        Assert.Throws<CryptographicException>(() => RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store));
        ClassicAssert.AreEqual(0, store.Writes);
        ClassicAssert.AreEqual(reference, File.ReadAllText(path));
    }

    [Test]
    public void FailedKeyringWriteDoesNotPublishAReferenceOrPlaintextFallback()
    {
        var store = new MemorySecretStore { FailWrites = true };
        string path = Path.Combine(_directory, "key.dat");

        Assert.Throws<CryptographicException>(() => RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store));
        ClassicAssert.IsFalse(File.Exists(path));
        ClassicAssert.IsEmpty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Test]
    public async Task ConcurrentCreatorsPublishOneSecretAndUseTheSameKey()
    {
        var store = new MemorySecretStore();
        string path = Path.Combine(_directory, "key.dat");
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => RimLLMSecureKeyStore.LoadOrCreateKeyring(path, store))));

        ClassicAssert.AreEqual(1, store.Writes);
        foreach (byte[] result in results) CollectionAssert.AreEqual(results[0], result);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, byte[]> Items { get; } = new();
        private int _writes;
        public int Writes => _writes;
        public bool FailWrites { get; init; }

        public byte[] Read(string account) => Items.TryGetValue(account, out byte[] value) ? value.ToArray() : null;
        public void Write(string account, byte[] key)
        {
            if (FailWrites) throw new CryptographicException("Simulated unavailable keyring.");
            Items[account] = key.ToArray();
            Interlocked.Increment(ref _writes);
        }
        public void Delete(string account) => Items.TryRemove(account, out _);
    }
}
