using System;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class RimLLMEmbeddingClientTests
{
    /// <summary>
    /// MEAI 的慣例是 GetService 至少要能回報自身與 metadata。先前這裡一律回傳 null，
    /// 任何依 EmbeddingGeneratorMetadata 判斷來源模型的中介層都會拿到空手。
    /// </summary>
    [Test]
    public void TestGetService_ReportsMetadataAndSelf()
    {
        var settings = new MockSettings
        {
            EmbeddingProvider = "Google",
            EmbeddingModel = "text-embedding-004"
        };
        var manager = new RimLLMManager(settings);
        var generator = manager.CreateEmbeddingGenerator("test.embed.metadata.mod");

        var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata), null) as EmbeddingGeneratorMetadata;
        ClassicAssert.IsNotNull(metadata);
        ClassicAssert.AreEqual("Google", metadata.ProviderName);
        ClassicAssert.AreEqual("text-embedding-004", metadata.DefaultModelId);

        ClassicAssert.AreSame(generator, generator.GetService(generator.GetType(), null));
        // 具名查詢不屬於這一層，維持 null。
        ClassicAssert.IsNull(generator.GetService(typeof(EmbeddingGeneratorMetadata), "some-key"));
    }

    /// <summary>
    /// 沒有任何一筆向量帶回 token 數時，Usage 必須維持 null。
    /// 填一個全零的 UsageDetails 會讓呼叫端把「供應商沒回報」誤讀成「這批不耗配額」。
    /// </summary>
    [Test]
    public void TestGenerateAsync_LeavesUsageNullWhenProviderReportsNothing()
    {
        var settings = new MockSettings { EmbeddingProvider = "Disabled" };
        var manager = new RimLLMManager(settings);
        var generator = manager.CreateEmbeddingGenerator("test.embed.usage.mod");

        // 空輸入不會碰到供應商，因此即使 Provider 停用也能走完整條組裝路徑。
        GeneratedEmbeddings<Embedding<float>> generated =
            generator.GenerateAsync(new string[0]).GetAwaiter().GetResult();

        ClassicAssert.AreEqual(0, generated.Count);
        ClassicAssert.IsNull(generated.Usage);
    }

    [Test]
    public void TestGenerateAsync_ReturnsEmbedding()
    {
        // 停用狀態會拋例外，僅驗證防濫用與例外對照
        var settings = new MockSettings { EmbeddingProvider = "Disabled" };
        var manager = new RimLLMManager(settings);
        var generator = manager.CreateEmbeddingGenerator("test.embed.mod");
        Assert.ThrowsAsync<RimLLMException>(async () =>
        {
            await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
        });
    }

    [Test]
    public void TestEmbeddingClient_AntiAbuseThrottle()
    {
        var settings = new MockSettings
        {
            EmbeddingProvider = "Disabled",
            EnableAntiAbuse = true,
            MaxRequestsPerWindow = 1,
            ThrottlingWindowSeconds = 60
        };
        var manager = new RimLLMManager(settings);
        RimLLMProvider.Initialize(manager);
        var generator = RimLLMProvider.CreateEmbeddingGenerator("test.embed.antiabuse.mod");

        // 第一次呼叫：因為未設定 Provider 拋出例外（說明已通過防濫用檢查）
        var ex1 = Assert.ThrowsAsync<RimLLMException>(async () =>
        {
            await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
        });
        ClassicAssert.IsTrue(ex1.Message.Contains("No embedding provider is configured"));

        // 第二次呼叫：超出 MaxRequestsPerWindow (1)，預期直接拋出 RateLimit 防濫用例外
        var ex2 = Assert.ThrowsAsync<RimLLMException>(async () =>
        {
            await generator.GenerateAsync(new[] { "hello 2" }).ConfigureAwait(false);
        });
        ClassicAssert.AreEqual(LLMError.RateLimit, ex2.Error);
    }

    /// <summary>
    /// 一批文字必須合成一次 API 呼叫（先前逐筆序列呼叫，N 筆 = N 次來回），
    /// 整批的用量平均分攤到每一筆並計入用量帳本。
    /// </summary>
    [Test]
    public void TestGenerateAsync_BatchesInputsIntoOneRequest_AndRecordsUsage()
    {
        var settings = new MockSettings
        {
            EmbeddingProvider = "OpenAI",
            EmbeddingModel = "text-embedding-3-small",
            EmbeddingApiKey = "sk-embed-test"
        };
        var manager = new RimLLMManager(settings);
        RimLLMProvider.Initialize(manager);

        var handler = new CapturingHttpMessageHandler
        {
            ResponseBody =
                "{\"object\":\"list\",\"model\":\"text-embedding-3-small\"," +
                "\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}," +
                "{\"object\":\"embedding\",\"index\":1,\"embedding\":[0.3,0.4]}," +
                "{\"object\":\"embedding\",\"index\":2,\"embedding\":[0.5,0.6]}]," +
                "\"usage\":{\"prompt_tokens\":10,\"total_tokens\":10}}"
        };
        RimLLMEmbeddingService.TransportOverride =
            new System.ClientModel.Primitives.HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler));
        try
        {
            var generator = manager.CreateEmbeddingGenerator("test.embed.batch.mod");
            GeneratedEmbeddings<Embedding<float>> generated =
                generator.GenerateAsync(new[] { "a", "b", "c" }).GetAwaiter().GetResult();

            ClassicAssert.AreEqual(1, handler.RequestBodies.Count, "三筆文字應合成一次請求");
            var input = System.Text.Json.Nodes.JsonNode.Parse(handler.RequestBodies[0]).AsObject()["input"].AsArray();
            ClassicAssert.AreEqual(3, input.Count);

            ClassicAssert.AreEqual(3, generated.Count);
            ClassicAssert.AreEqual(0.3f, generated[1].Vector.ToArray()[0], 0.0001f, "向量順序須與輸入一致");
            ClassicAssert.AreEqual(10, generated.Usage?.InputTokenCount, "整批用量加總後等於供應商回報值");
            ClassicAssert.AreEqual(10, settings.TotalPromptTokens, "embedding 的 token 必須計入用量帳本");
        }
        finally
        {
            RimLLMEmbeddingService.TransportOverride = null;
        }
    }
}
