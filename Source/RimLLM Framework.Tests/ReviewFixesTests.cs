using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 審查發現問題的回歸測試。
/// 對應六個修正：驗證路徑追蹤、快取鍵結構化序列化、快取深層快照、
/// Embedding 實際 provider/model 標記、串流背壓與總量限制、Ollama 探測限流。
/// </summary>
[TestFixture]
public class ReviewFixesTests
{
    // ---------- [P1] 結構化驗證：重複型別的物件不再被跳過 ----------

    private class Item
    {
        public string Name;
        public string RequiredField;
    }

    private class ItemContainer
    {
        public List<Item> Items;
    }

    [Test]
    public void StructuredValidation_SecondSameTypeElement_StillValidates()
    {
        // 兩個同型別元素，第二個的 RequiredField 為 null：
        // 舊實作會因為 visitedTypes 永久記住 Item 而跳過第二個，這裡必須拋出。
        var container = new ItemContainer
        {
            Items =
            [
                new() { Name = "first", RequiredField = "ok" },
                new() { Name = "second", RequiredField = null }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RimLLMJsonHelper.ValidateStructuredObject(container));
        ClassicAssert.IsNotNull(ex);
        ClassicAssert.IsTrue(ex.Message.Contains("RequiredField"), $"實際訊息：{ex.Message}");
    }

    [Test]
    public void StructuredValidation_AllRequiredFieldsPresent_Passes()
    {
        var container = new ItemContainer
        {
            Items =
            [
                new() { Name = "first", RequiredField = "ok" },
                new() { Name = "second", RequiredField = "ok" }
            ]
        };

        RimLLMJsonHelper.ValidateStructuredObject(container); // 不拋出即通過
    }

    [Test]
    public void StructuredValidation_CyclicReference_DoesNotStackOverflow()
    {
        // 循環引用（Node.Next 指回自己）不該 StackOverflow，也不該誤報 null。
        var node = new CyclicNode();
        node.Next = node;

        RimLLMJsonHelper.ValidateStructuredObject(node);
    }

    private class CyclicNode
    {
        public CyclicNode Next;
    }

    // ---------- [P2] 快取鍵：結構化序列化區分內容 ----------

    private static List<ChatMessage> MessagesWithToolResult(object result)
    {
        return
        [
            new(ChatRole.User, "check weather"),
            new(ChatRole.Assistant,
            [
                new FunctionResultContent("call-1", result)
            ])
        ];
    }

    // ---------- [P2] 快取深層快照：呼叫端修改不污染快取 ----------

    // ---------- [P2] Embedding：模型/供應商在請求開始時捕捉 ----------

    [Test]
    public void EmbeddingResult_CarriesProviderAndModel()
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
                "\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}]," +
                "\"usage\":{\"prompt_tokens\":10,\"total_tokens\":10}}"
        };
        RimLLMEmbeddingService.TransportOverride =
            new System.ClientModel.Primitives.HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler));
        try
        {
            var generator = manager.CreateEmbeddingGenerator("test.embed.capture.mod");
            GeneratedEmbeddings<Embedding<float>> generated =
                generator.GenerateAsync(new[] { "hello" }).GetAwaiter().GetResult();

            ClassicAssert.AreEqual(1, generated.Count);
            ClassicAssert.AreEqual("text-embedding-3-small", generated[0].ModelId, "向量應標上實際使用的模型");

            var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata), null) as EmbeddingGeneratorMetadata;
            ClassicAssert.IsNotNull(metadata);
            ClassicAssert.AreEqual("OpenAI", metadata.ProviderName);
        }
        finally
        {
            RimLLMEmbeddingService.TransportOverride = null;
        }
    }

    // ---------- [P2] 串流：總量限制 ----------

    [Test]
    public void StreamAsync_ForwardsHugeChunk()
    {
        var client = new CapturingChatClient
        {
            StreamUpdates =
            {
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextContent(new string('x', 4 * 1024 * 1024 + 1000))
                ])
            }
        };

        var received = new List<ChatResponseUpdate>();
        RimLLMChatClientExecutor.StreamAsync(
            client,
            new List<ChatMessage> { new(ChatRole.User, "hello") },
            null,
            "gpt-test",
            useNativeSchema: false,
            "OpenAI",
            async update => { received.Add(update); await Task.CompletedTask; },
            30f,
            CancellationToken.None).GetAwaiter().GetResult();

        // 不拋出、update 仍全部轉發；估算只累加數值，不保留文字。
        ClassicAssert.AreEqual(1, received.Count);
    }

    [Test]
    public void StreamAsync_WithoutReportedUsage_RecordsEstimateOfAllChunks()
    {
        RimLLMProvider.TryGetManager(out RimLLMManager previousManager);
        var settings = new MockSettings();
        RimLLMProvider.Initialize(new RimLLMManager(settings));
        try
        {
            // 最後一塊超過舊實作的 4MB 字元上限：若有人把封頂加回來，估算會少算而失敗。
            string huge = new('x', 4 * 1024 * 1024 + 4000);
            var client = new CapturingChatClient();
            foreach (string chunk in new[] { "ab", "中文", huge })
            {
                client.StreamUpdates.Add(new ChatResponseUpdate(
                    ChatRole.Assistant, [new TextContent(chunk)]));
            }

            RimLLMChatClientExecutor.StreamAsync(
                client,
                new List<ChatMessage> { new(ChatRole.User, "hello") },
                null,
                "gpt-test",
                useNativeSchema: false,
                "OpenAI",
                null,
                30f,
                CancellationToken.None).GetAwaiter().GetResult();

            // 逐塊累加的估算必須等於對整段文字一次估算的結果，且不得封頂。
            ClassicAssert.AreEqual(
                RimLLMChatClientExecutor.EstimateTokens("ab中文" + huge),
                settings.TotalCompletionTokens);
        }
        finally
        {
            RimLLMProvider.Initialize(previousManager);
        }
    }

    [Test]
    public void StreamAsync_ForwardsAllUpdatesEvenWithManyChunks()
    {
        var client = new CapturingChatClient();
        for (int i = 0; i < 5000; i++)
        {
            client.StreamUpdates.Add(new ChatResponseUpdate(
                ChatRole.Assistant, [new TextContent("chunk-" + i)]));
        }

        var received = new List<ChatResponseUpdate>();
        RimLLMChatClientExecutor.StreamAsync(
            client,
            new List<ChatMessage> { new(ChatRole.User, "hello") },
            null,
            "gpt-test",
            useNativeSchema: false,
            "OpenAI",
            async update => { received.Add(update); await Task.CompletedTask; },
            30f,
            CancellationToken.None).GetAwaiter().GetResult();

        ClassicAssert.AreEqual(5000, received.Count, "所有 chunk 都必須原樣轉發");
    }

    // ---------- [P2] Ollama 探測限流常數 ----------

    [Test]
    public void OllamaProbeLimits_AreSane()
    {
        ClassicAssert.Greater(RimLLMEmbeddingService.MaxOllamaModelsToProbe, 0);
        ClassicAssert.Greater(RimLLMEmbeddingService.MaxOllamaProbeConcurrency, 0);
        ClassicAssert.LessOrEqual(RimLLMEmbeddingService.MaxOllamaProbeConcurrency, RimLLMEmbeddingService.MaxOllamaModelsToProbe);
    }

    // ---------- [P3] 快取鍵追蹤修正：bool 標記與參數排序 ----------

    // ---------- [P3] 深層複製追蹤修正：巢狀容器隔離 ----------

    // ---------- [P3] 結構化驗證追蹤修正：集合 null 元素 ----------

    private class NullableItemContainer
    {
        public List<int?> Values;
    }

    [Test]
    public void StructuredValidation_NullElement_Throws()
    {
        var container = new ItemContainer
        {
            Items = [null]
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RimLLMJsonHelper.ValidateStructuredObject(container));
        ClassicAssert.IsTrue(ex.Message.Contains("null"), $"實際訊息：{ex.Message}");
    }

    [Test]
    public void StructuredValidation_NullableElement_AllowsNull()
    {
        var container = new NullableItemContainer
        {
            Values = [1, null, 3]
        };

        RimLLMJsonHelper.ValidateStructuredObject(container); // 不拋出即通過
    }
}
