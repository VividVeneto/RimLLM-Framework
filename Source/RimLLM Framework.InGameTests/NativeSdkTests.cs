using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json;
using DevTools.Testing;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.InGameTests;

/// <summary>在遊戲真正的組件載入環境驗證 .NET 10 的工具與 Schema 相依。</summary>
[TestFixture(TestType.MainMenu)]
internal sealed class NativeSdkTests
{
    [Test]
    public void SystemStorageKeyReloadsInsideTheGame()
    {
        string folder = Directory.CreateTempSubdirectory("RimLLMGameKeyring_").FullName;
        string path = Path.Combine(folder, "key.dat");
        try
        {
            byte[] first = RimLLMSecureKeyStore.LoadOrCreate(path);
            byte[] second = RimLLMSecureKeyStore.LoadOrCreate(path);
            Expect.IsTrue(first.SequenceEqual(second));
            Expect.AreEqual(32, first.Length);
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                string reference = File.ReadAllText(path);
                ISecretStore store = OperatingSystem.IsMacOS() ? new MacOSSecretStore()
                    : OperatingSystem.IsLinux() ? new LinuxSecretStore() : null;
                if (store != null && reference.StartsWith("keyring:", StringComparison.Ordinal))
                    store.Delete(reference["keyring:".Length..]);
            }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void ToolFactoryAndStructuredSchemaUseGameRuntime()
    {
        var function = AIFunctionFactory.Create((string value) => value, "Echo");
        Expect.IsNotNull(function);
        Expect.AreEqual(JsonValueKind.Object, function.JsonSchema.ValueKind);
        string schema = RimLLMSchemaBuilder.BuildJson(typeof(SchemaProbe));
        using var document = JsonDocument.Parse(schema);
        Expect.IsTrue(document.RootElement.GetProperty("properties").TryGetProperty("Email", out _));
    }

    private sealed class SchemaProbe
    {
        [EmailAddress]
        public string Email { get; set; }
    }
}
