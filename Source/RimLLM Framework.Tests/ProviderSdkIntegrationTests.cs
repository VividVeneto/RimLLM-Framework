using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using OpenAI.Chat;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 驗證官方 SDK、MEAI adapter 與 provider capability 分流。
/// 測試不使用真實 API key，也不發出網路請求。
/// </summary>
[TestFixture]
public class ProviderSdkIntegrationTests
{
    [Test]
    public void OfficialProviderAssembliesCanBeLoaded()
    {
        ClassicAssert.IsNotNull(typeof(IChatClient).Assembly, "Microsoft.Extensions.AI assembly 應可載入。");
        ClassicAssert.IsNotNull(typeof(ChatClient).Assembly, "OpenAI assembly 應可載入。");
    }

    [Test]
    public void FrameworkTargetsNet10WithoutLegacyPolyfills()
    {
        string framework = FindWorkspaceAssembly("RimLLM Framework.dll");
        ClassicAssert.IsNotNull(framework);
        using var stream = File.OpenRead(framework);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var references = new System.Collections.Generic.List<string>();
        foreach (var handle in reader.AssemblyReferences)
            references.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
        CollectionAssert.Contains(references, "System.Runtime");
        CollectionAssert.DoesNotContain(references, "mscorlib");
        CollectionAssert.DoesNotContain(references, "Microsoft.Bcl.AsyncInterfaces");
        foreach (string file in new[] { "Microsoft.Bcl.AsyncInterfaces.dll", "System.Threading.Tasks.Extensions.dll", "System.ValueTuple.dll", "System.Text.Json.dll" })
            ClassicAssert.IsNull(FindWorkspaceAssembly(file), "執行環境內建組件不得再隨 Mod 出貨：" + file);
    }

    [Test]
    public void MicrosoftExtensionsAiAssemblyCanBeReflectedWithoutTypeLoadFailures()
    {
        string assemblyPath = FindWorkspaceAssembly("Microsoft.Extensions.AI.dll");
        ClassicAssert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 Microsoft.Extensions.AI.dll。");

        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        try
        {
            ClassicAssert.IsNotEmpty(assembly.GetTypes());
        }
        catch (ReflectionTypeLoadException exception)
        {
            Assert.Fail(
                "Microsoft.Extensions.AI.dll 反射載入失敗：" +
                string.Join(
                    "\n",
                    Array.ConvertAll(
                        exception.LoaderExceptions,
                        loaderException => loaderException?.Message ?? "未知載入例外")));
        }
    }



    private static string FindWorkspaceAssembly(string fileName)
    {
        string directory = TestContext.CurrentContext.TestDirectory;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
        {
            string candidate = Path.Combine(directory, "Assemblies", fileName);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }

            DirectoryInfo parent = Directory.GetParent(directory);
            directory = parent?.FullName;
        }

        return null;
    }

    [Test]
    public void OfficialProvidersCreateAdaptersWithoutSendingRequests()
    {
        using (IChatClient openAiClient = OpenAIProvider.CreateOpenAiChatClient(
            "unit-test-key",
            "unit-test-model",
            "https://example.invalid/v1/chat/completions"))
        {
            ClassicAssert.IsNotNull(openAiClient);
        }

        // Gemini 經官方 OpenAI 相容端點存取，建立 client 不發出網路請求。
        var geminiSettings = new MockSettings();
        geminiSettings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";
        using IChatClient geminiClient = new GeminiProvider(geminiSettings).CreateChatClient("gemini-2.5-flash");
        ClassicAssert.IsNotNull(geminiClient);
    }

    [Test]
    public void ProvidersRejectMissingCredentialsAndModels()
    {
        Assert.Throws<ArgumentException>(() => OpenAIProvider.CreateOpenAiChatClient("", "model"));
        Assert.Throws<ArgumentException>(() => OpenAIProvider.CreateOpenAiChatClient("key", ""));

        // Gemini 沿用 OpenAI 路徑：無金鑰時同樣拒絕建立。
        Assert.Throws<ArgumentException>(() => new GeminiProvider(new MockSettings()).CreateChatClient("gemini-2.5-flash"));
    }

    [Test]
    public void GeminiDeclaresOpenAiFamilyCapabilities()
    {
        var settings = new MockSettings();
        settings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";
        var provider = new GeminiProvider(settings);

        ClassicAssert.AreEqual(ProviderIds.Gemini, provider.ProviderId);
        ClassicAssert.IsTrue(provider.Capabilities.SupportsNativeStructuredOutput);
        ClassicAssert.IsTrue(provider.Capabilities.SupportsStreaming);
        ClassicAssert.IsTrue(provider.Capabilities.SupportsUsageMetadata);
        ClassicAssert.IsTrue(provider.Capabilities.SupportsFunctionCalling);
    }

    [Test]
    public void OpenAiEndpointNormalizationHandlesWhitespaceAndTrailingSlash()
    {
        ClassicAssert.AreEqual(
            "https://example.invalid/v1",
            OpenAIProvider.NormalizeEndpoint(" https://example.invalid/v1/chat/completions/ "));
        ClassicAssert.IsNull(OpenAIProvider.NormalizeEndpoint(null));
    }

    /// <summary>檢查實際出貨資產的 TFM，避免意外重新引入舊 Framework/Mono 組建。</summary>
    [TestCase("RimLLM Framework.dll")]
    [TestCase("Microsoft.Extensions.AI.Abstractions.dll")]
    [TestCase("Microsoft.Extensions.AI.dll")]
    [TestCase("Microsoft.Extensions.AI.OpenAI.dll")]
    public void ShippedAssembliesTargetNet10(string fileName)
    {
        string path = FindWorkspaceAssembly(fileName);
        ClassicAssert.IsNotNull(path, "缺少正式組件：" + fileName);
        // 直接讀取正式檔案的 metadata，避免 LoadFrom 被已載入的同名 Debug 組件取代。
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        string framework = null;
        foreach (var handle in reader.CustomAttributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Runtime.Versioning" ||
                reader.GetString(type.Name) != "TargetFrameworkAttribute") continue;
            var value = reader.GetBlobReader(attribute.Value);
            ClassicAssert.AreEqual(1, value.ReadUInt16());
            framework = value.ReadSerializedString();
            break;
        }
        ClassicAssert.AreEqual(".NETCoreApp,Version=v10.0", framework);
    }

    [Test]
    public void BuiltInSdkProvidersExposeNativeCapabilities()
    {
        var settings = new MockSettings();
        var manager = new RimLLMManager(settings);

        LLMProviderCapabilities openAi = manager.GetProviderCapabilities(ProviderIds.OpenAI);
        ClassicAssert.IsTrue(openAi.SupportsNativeStructuredOutput);
        ClassicAssert.IsTrue(openAi.SupportsStreaming);
        ClassicAssert.IsTrue(openAi.SupportsUsageMetadata);

        LLMProviderCapabilities gemini = manager.GetProviderCapabilities(ProviderIds.Gemini);
        ClassicAssert.IsTrue(gemini.SupportsNativeStructuredOutput);
        ClassicAssert.IsTrue(gemini.SupportsStreaming);
        ClassicAssert.IsTrue(gemini.SupportsUsageMetadata);

        LLMProviderCapabilities unknown = manager.GetProviderCapabilities("missing-provider");
        ClassicAssert.IsFalse(unknown.SupportsNativeStructuredOutput);
        ClassicAssert.IsFalse(unknown.SupportsStreaming);

        // 所有內建 provider 一律走 OpenAI SDK（含 OpenAI 相容端點）+ MEAI，
        // 不再保留 raw HTTP 對話路徑，也不再使用 Google.GenAI。
        settings.ApiKeys["OpenAI"] = "mock-key";
        settings.ApiKeys["Gemini"] = "mock-key";
        var sdkOpenAi = new TestOpenAIProvider(settings);
        ClassicAssert.IsNotNull(sdkOpenAi.CreateChatClient("gpt-4o"));
        ClassicAssert.IsTrue(sdkOpenAi.Capabilities.SupportsNativeStructuredOutput);

        var sdkGemini = new TestGeminiProvider(settings);
        ClassicAssert.IsNotNull(sdkGemini.CreateChatClient("gemini-1.5-pro"));
        ClassicAssert.IsTrue(sdkGemini.Capabilities.SupportsNativeStructuredOutput);

        // 測試 OpenAICompatibleProvider (RequiresApiKey = false) 且無 API Key 時自動填入 PlaceholderApiKey
        var localSettings = new MockSettings();
        var localProvider = new OpenAICompatibleProvider(localSettings);
        using var client = localProvider.CreateChatClient("local-model");
        ClassicAssert.IsNotNull(client);
    }

    [Test]
    public void CreateChatClient_ReturnsBoundFacade()
    {
        var settings = new MockSettings();
        var manager = new RimLLMManager(settings);
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient("sdk.integration.test");
        ClassicAssert.IsNotNull(client);
        // 回傳的是包了中介層的堆疊，不再是裸的 facade；
        // 「這條堆疊屬於框架」改由 GetService 表達（DelegatingChatClient 會往內層轉發）。
        ClassicAssert.IsInstanceOf<RimLLMFailoverChatClient>(client.GetService(typeof(RimLLMFailoverChatClient)));
        ChatClientMetadata metadata = client.GetService<ChatClientMetadata>();
        ClassicAssert.IsNotNull(metadata);
        ClassicAssert.AreEqual("RimLLM", metadata.ProviderName);
    }

    [Test]
    public void CreateChatClient_NeedsNoRegistration()
    {
        var settings = new MockSettings();
        var manager = new RimLLMManager(settings);
        RimLLMProvider.Initialize(manager);

        // 已移除呼叫者組件驗證：任何 modId 都能直接取得 client，不需事先註冊。
        ClassicAssert.IsNotNull(RimLLMProvider.CreateChatClient("never.registered"));

        // modId 仍為必填，因為防濫用節流與遙測歸屬都以它為鍵。
        Assert.Throws<ArgumentException>(() => RimLLMProvider.CreateChatClient(string.Empty));
        Assert.Throws<ArgumentException>(() => RimLLMProvider.CreateEmbeddingGenerator(null));
    }

}
