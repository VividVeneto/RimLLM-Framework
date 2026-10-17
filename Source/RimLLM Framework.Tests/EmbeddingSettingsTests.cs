using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class EmbeddingSettingsTests
{
    [Test]
    public void TestPerProviderEmbeddingDefaults()
    {
        var settings = new RimLLMFrameworkSettings();

        ClassicAssert.AreEqual("gemini-embedding-2", settings.GetEmbeddingModel("Google"));
        ClassicAssert.AreEqual("text-embedding-3-small", settings.GetEmbeddingModel("OpenAI"));
        ClassicAssert.AreEqual("nomic-embed-text", settings.GetEmbeddingModel("LocalAPI_Ollama"));
        ClassicAssert.AreEqual("text-embedding-3-small", settings.GetEmbeddingModel("LocalAPI_OpenAI"));

        ClassicAssert.AreEqual("https://generativelanguage.googleapis.com/v1beta/openai", settings.GetEmbeddingEndpoint("Google"));
        ClassicAssert.AreEqual("https://api.openai.com/v1", settings.GetEmbeddingEndpoint("OpenAI"));
        ClassicAssert.AreEqual("http://localhost:11434/v1", settings.GetEmbeddingEndpoint("LocalAPI_Ollama"));
        ClassicAssert.AreEqual("http://localhost:1234/v1", settings.GetEmbeddingEndpoint("LocalAPI_OpenAI"));
    }

    [Test]
    public void TestPerProviderEmbeddingSettingsIndependence()
    {
        var settings = new RimLLMFrameworkSettings();

        settings.SetEmbeddingModel("Google", "my-gemini-embed");
        settings.SetEmbeddingModel("LocalAPI_Ollama", "my-ollama-embed");
        settings.SetEmbeddingEndpoint("LocalAPI_Ollama", "http://192.168.1.100:11434/v1");
        settings.SetEmbeddingApiKey("OpenAI", "sk-custom-openai-embed-key");

        ClassicAssert.AreEqual("my-gemini-embed", settings.GetEmbeddingModel("Google"));
        ClassicAssert.AreEqual("my-ollama-embed", settings.GetEmbeddingModel("LocalAPI_Ollama"));
        ClassicAssert.AreEqual("text-embedding-3-small", settings.GetEmbeddingModel("OpenAI"));

        ClassicAssert.AreEqual("http://192.168.1.100:11434/v1", settings.GetEmbeddingEndpoint("LocalAPI_Ollama"));
        ClassicAssert.AreEqual("https://generativelanguage.googleapis.com/v1beta/openai", settings.GetEmbeddingEndpoint("Google"));

        ClassicAssert.AreEqual("sk-custom-openai-embed-key", settings.GetEmbeddingApiKey("OpenAI"));
        ClassicAssert.AreEqual("", settings.GetEmbeddingApiKey("Google"));
    }

    [Test]
    public void TestEmbeddingPropertiesDelegateToActiveProvider()
    {
        var settings = new RimLLMFrameworkSettings();

        settings.SetEmbeddingModel("Google", "gemini-embed-custom");
        settings.SetEmbeddingModel("OpenAI", "openai-embed-custom");

        settings.EmbeddingProvider = "Google";
        ClassicAssert.AreEqual("gemini-embed-custom", settings.EmbeddingModel);

        settings.EmbeddingProvider = "OpenAI";
        ClassicAssert.AreEqual("openai-embed-custom", settings.EmbeddingModel);

        // 修改屬性應同步寫入目前作用中供應商的配置
        settings.EmbeddingModel = "openai-embed-v2";
        ClassicAssert.AreEqual("openai-embed-v2", settings.GetEmbeddingModel("OpenAI"));
        ClassicAssert.AreEqual("gemini-embed-custom", settings.GetEmbeddingModel("Google"));
    }

    [Test]
    public void TestEmbeddingEndpointsResolution()
    {
        ClassicAssert.AreEqual("https://generativelanguage.googleapis.com/v1beta/openai",
            RimLLMEmbeddingService.ResolveDefaultEndpoint("Google"));
        ClassicAssert.AreEqual("https://api.openai.com/v1",
            RimLLMEmbeddingService.ResolveDefaultEndpoint("OpenAI"));
        ClassicAssert.AreEqual("http://localhost:11434/v1",
            RimLLMEmbeddingService.ResolveDefaultEndpoint("LocalAPI_Ollama"));
        ClassicAssert.AreEqual("http://localhost:1234/v1",
            RimLLMEmbeddingService.ResolveDefaultEndpoint("LocalAPI_OpenAI"));

        Assert.Throws<RimLLMException>(() =>
        {
            RimLLMEmbeddingService.ResolveDefaultEndpoint("UnknownProvider");
        });
    }

    [Test]
    public void TestGetMainProviderIdForEmbedding()
    {
        ClassicAssert.AreEqual(ProviderIds.Gemini,
            RimLLMEmbeddingService.GetMainProviderIdForEmbedding("Google"));
        ClassicAssert.AreEqual(ProviderIds.OpenAI,
            RimLLMEmbeddingService.GetMainProviderIdForEmbedding("OpenAI"));
        ClassicAssert.AreEqual(ProviderIds.OpenAICompatible,
            RimLLMEmbeddingService.GetMainProviderIdForEmbedding("LocalAPI_OpenAI"));
        ClassicAssert.AreEqual(ProviderIds.OpenAICompatible,
            RimLLMEmbeddingService.GetMainProviderIdForEmbedding("LocalAPI_Ollama"));
    }

    [Test]
    public void TestEmbeddingProviderDisplayName()
    {
        ClassicAssert.AreEqual("Google Gemini", EmbeddingSettingsDrawer.GetProviderDisplayName("Google"));
        ClassicAssert.AreEqual("OpenAI", EmbeddingSettingsDrawer.GetProviderDisplayName("OpenAI"));
        ClassicAssert.AreEqual("Ollama (本地)", EmbeddingSettingsDrawer.GetProviderDisplayName("LocalAPI_Ollama"));
        ClassicAssert.AreEqual("OpenAI 相容 (本地/自訂)", EmbeddingSettingsDrawer.GetProviderDisplayName("LocalAPI_OpenAI"));
        ClassicAssert.AreEqual("CustomProvider", EmbeddingSettingsDrawer.GetProviderDisplayName("CustomProvider"));
    }

    [Test]
    public void TestDefaultPresetModelsContainsExpectedPresets()
    {
        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels.ContainsKey("Google"));
        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels["Google"].Contains("gemini-embedding-2"));
        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels["Google"].Contains("gemini-embedding-001"));

        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels.ContainsKey("OpenAI"));
        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels["OpenAI"].Contains("text-embedding-3-small"));

        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels.ContainsKey("LocalAPI_Ollama"));
        ClassicAssert.IsTrue(EmbeddingSettingsDrawer.DefaultPresetModels["LocalAPI_Ollama"].Contains("nomic-embed-text"));
    }
}
