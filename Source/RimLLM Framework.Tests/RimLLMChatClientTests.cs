using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class RimLLMChatClientTests
{
    private RimLLMManager CreateManager()
    {
        var settings = new MockSettings { FallbackChain = ["TestMock:mock-model"] };
        settings.EnabledProviders["TestMock"] = true;
        settings.ApiKeys["TestMock"] = "mock-key";
        var manager = new RimLLMManager(settings);
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "TestMock",
            Capabilities = new LLMProviderCapabilities { SupportsStreaming = true, SupportsNativeStructuredOutput = true, SupportsUsageMetadata = true },
            GenerateHandler = (messages, options, model) =>
                System.Threading.Tasks.Task.FromResult(
                    (options != null && (options.ResponseFormat != null || (options.AdditionalProperties != null && options.AdditionalProperties.ContainsKey("rimllm_response_schema"))))
                        ? "{\"Value\":42,\"Message\":\"mock\"}"
                        : "mock-reply for " + model)
        });
        return manager;
    }

    private IChatClient CreateClient(RimLLMManager manager, string modId)
    {
        return manager.CreateChatClient(modId);
    }

    [Test]
    public void TestGetResponseAsync_ReturnsTextAndModel()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.facade.mod");
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions()).GetAwaiter().GetResult();
        ClassicAssert.IsNotNull(response);
        ClassicAssert.IsNotEmpty(response.Text);
        ClassicAssert.AreEqual("TestMock", response.ModelId.Split([':'], StringSplitOptions.None)[0]);
    }

    [Test]
    public void TestGetResponseAsync_ModelIdSpecifiedPreferred()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.modelid.mod");
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new ChatOptions { ModelId = "TestMock:preferred-model" }).GetAwaiter().GetResult();
        ClassicAssert.AreEqual("TestMock", response.ModelId.Split([':'], StringSplitOptions.None)[0]);
        ClassicAssert.AreEqual("preferred-model", response.ModelId.Split([':'], StringSplitOptions.None)[1]);
        ClassicAssert.AreEqual("mock-reply for preferred-model", response.Text);
    }

    /// <summary>
    /// 供應商沒有回報用量時，Usage 就是 null——框架不再捏造一份全零的 UsageDetails。
    /// 「零個 token」與「供應商沒說」是兩回事，前者會讓費用面板誤以為這次呼叫免費。
    /// </summary>
    [Test]
    public void TestGetResponseAsync_UsageIsNullWhenProviderReportsNone()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.usage.mod");
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions()).GetAwaiter().GetResult();
        ClassicAssert.IsNull(response.Usage);
    }

    [Test]
    public void TestGetResponseAsync_RimLLMChatOptionsPriorityPassed()
    {
        // 透過 manager 佇列行為間接驗證：Priority 越高越先執行（此處僅驗證不會例外、回傳正常）
        var manager = CreateManager();
        var client = CreateClient(manager, "test.priority.mod");
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions { Priority = 5 }).GetAwaiter().GetResult();
        ClassicAssert.IsNotEmpty(response.Text);
    }

    [Test]
    public void TestGetResponseAsync_StandardChatOptionsDefaults()
    {
        // 純標準 ChatOptions：框架預設值路徑（無 RimLLMChatOptions 延伸欄位）
        var manager = CreateManager();
        var client = CreateClient(manager, "test.defaults.mod");
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new ChatOptions()).GetAwaiter().GetResult();
        ClassicAssert.IsNotNull(response);
        ClassicAssert.IsNotEmpty(response.Text);
    }

    /// <summary>
    /// GetService 必須能穿透整條中介層堆疊。DelegatingChatClient 會往內層轉發，
    /// 因此無論外面包了幾層，呼叫端都拿得到框架的 ChatClientMetadata。
    /// </summary>
    [Test]
    public void TestGetService_ResolvesMetadataThroughTheWholeStack()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.getservice.mod");

        var metadata = client.GetService(typeof(ChatClientMetadata), null) as ChatClientMetadata;
        ClassicAssert.IsNotNull(metadata);
        ClassicAssert.AreEqual("RimLLM", metadata.ProviderName);
    }

    /// <summary>
    /// 框架欄位一律由 RimLLMChatOptions 的靜態讀取器從 AdditionalProperties 取出，
    /// 不再經過中介請求物件——呼叫端用 RimLLMChatOptions 或純 ChatOptions 塞鍵等價。
    /// </summary>
    [Test]
    public void TestFrameworkFieldsReadDirectlyFromChatOptions()
    {
        var sugar = new RimLLMChatOptions { DisableReasoning = true, Priority = 7 };
        ClassicAssert.IsTrue(RimLLMChatOptions.GetDisableReasoning(sugar));
        ClassicAssert.AreEqual(7, RimLLMChatOptions.GetPriority(sugar));

        var plain = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RimLLMChatOptions.DisableReasoningKey] = true,
                [RimLLMChatOptions.PriorityKey] = 7
            }
        };
        ClassicAssert.IsTrue(RimLLMChatOptions.GetDisableReasoning(plain));
        ClassicAssert.AreEqual(7, RimLLMChatOptions.GetPriority(plain));
    }

    [Test]
    public void TestRimLLMChatOptionsClonePreservesFrameworkFields()
    {
        var original = new RimLLMChatOptions
        {
            Temperature = 0.5f,
            Priority = 9,
            MinFallbackLevel = "Medium",
            CachedContext = "world rules",
            DisableReasoning = true
        };

        ChatOptions cloned = original.Clone();

        // 框架欄位存放於 AdditionalProperties，因此 MEAI 的 base.Clone() 就足以保留它們，
        // 不再需要覆寫 Clone、也不再要求 clone 結果維持衍生型別：前方的 middleware
        // （ConfigureOptions、FunctionInvocation 等）即使 clone 成純 ChatOptions 也不會切掉設定。
        ClassicAssert.AreEqual(0.5f, cloned.Temperature);
        ClassicAssert.AreEqual(9, RimLLMChatOptions.GetPriority(cloned));
        ClassicAssert.AreEqual("Medium", RimLLMChatOptions.GetMinFallbackLevel(cloned));
        ClassicAssert.AreEqual("world rules", RimLLMChatOptions.GetCachedContext(cloned));
        ClassicAssert.IsTrue(RimLLMChatOptions.GetDisableReasoning(cloned));
        ClassicAssert.IsTrue(RimLLMChatOptions.GetEnableContextCaching(cloned), "CachedContext 不為空時應沿用計算預設值");

        // 明確設定 false 時不可被 CachedContext 的計算預設值蓋掉。
        original.EnableContextCaching = false;
        ClassicAssert.IsFalse(RimLLMChatOptions.GetEnableContextCaching(original.Clone()));
    }

    [Test]
    public void TestChatOptionsCloneGivesIndependentAdditionalProperties()
    {
        // 這是整條中介層堆疊的共同前提：每一層都會 clone options 再改寫，
        // 若 base.Clone() 只是共用同一個字典，中介層的寫入就會回頭汙染呼叫端的物件。
        var original = new RimLLMChatOptions { Priority = 3 };
        ChatOptions cloned = original.Clone();

        cloned.AdditionalProperties["injected_by_middleware"] = true;

        ClassicAssert.IsFalse(
            original.AdditionalProperties.ContainsKey("injected_by_middleware"),
            "clone 之後的寫入不可回頭影響呼叫端的 options");
        ClassicAssert.AreEqual(3, RimLLMChatOptions.GetPriority(cloned), "clone 必須保留框架欄位");
    }

    [Test]
    public void TestGetStreamingResponseAsync_YieldsChunks()
    {
        var mockSettings = new MockSettings { FallbackChain = ["TestMockStream:model-s"] };
        mockSettings.EnabledProviders["TestMockStream"] = true;
        mockSettings.ApiKeys["TestMockStream"] = "key";
        var manager = new RimLLMManager(mockSettings);
        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "TestMockStream",
            StreamHandler = (messages, options, model, onChunk) =>
            {
                onChunk("mock-");
                onChunk("stream");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        });

        var client = CreateClient(manager, "test.stream.mod");
        var chunks = new List<string>();
        var enumerator = client.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions()).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var update = enumerator.Current;
                if (!string.IsNullOrEmpty(update.Text))
                {
                    chunks.Add(update.Text);
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        ClassicAssert.AreEqual("mock-stream", string.Concat(chunks));
    }

    [Test]
    public void TestGetStreamingResponseAsync_PreservesProviderAndModelMetadata()
    {
        // 回歸測試：串流路徑先前只回傳 Text 與 Contents，把成功那次嘗試的
        // ProviderId / ModelName / token 計數整組丟掉，使呼叫端只拿到空的 ModelId。
        var mockSettings = new MockSettings { FallbackChain = ["TestMockStream:model-s"] };
        mockSettings.EnabledProviders["TestMockStream"] = true;
        mockSettings.ApiKeys["TestMockStream"] = "key";
        var manager = new RimLLMManager(mockSettings);
        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "TestMockStream",
            StreamHandler = (messages, options, model, onChunk) =>
            {
                onChunk("chunk");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        });

        var client = CreateClient(manager, "test.stream.meta.mod");
        string modelId = null;
        var enumerator = client.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions()).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                modelId = modelId ?? enumerator.Current.ModelId;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ClassicAssert.AreEqual("TestMockStream:model-s", modelId);
    }

    [Test]
    public void TestGetStreamingResponseAsync_ProducerFailurePropagates()
    {
        // 所有供應商都失敗時，例外必須傳到列舉端，不能被靜默吞成「串流正常結束」。
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockDead:model-a"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockDead"] = true;
        mockSettings.ApiKeys["MockDead"] = "key";

        var manager = new RimLLMManager(mockSettings);
        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "MockDead",
            StreamHandler = (messages, options, model, onChunk) =>
                throw new RimLLMException(LLMError.ProviderOffline, "provider is down")
        });

        var client = CreateClient(manager, "test.stream.failure.mod");
        var enumerator = client.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions()).GetAsyncEnumerator();

        Assert.Throws<RimLLMException>(() =>
        {
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }, "產生端失敗時列舉必須擲出例外");
    }

    [Test]
    public void TestMetadata_ProviderNameIsRimLLM()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.metadata.mod");
        var metadata = (ChatClientMetadata)client.GetService(typeof(ChatClientMetadata));
        ClassicAssert.AreEqual("RimLLM", metadata.ProviderName);
    }

    [Test]
    public void TestGetResponseObjectAsync_DeserializesViaFacade()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.obj.mod");
        var result = client.GetResponseObjectAsync<TestDataStructure>(
            new List<ChatMessage> { new(ChatRole.User, "give data") },
            new RimLLMChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
                    "custom_type",
                    "RimLLM structured response")
            }).GetAwaiter().GetResult();
        ClassicAssert.IsNotNull(result);
        ClassicAssert.AreEqual(42, result.Value);
        ClassicAssert.AreEqual("mock", result.Message);
    }

    [Test]
    public void TestGetResponseObjectAsync_NonFacadeUsesSimplifiedPath()
    {
        var plainClient = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"Value\":5,\"Message\":\"ok\"}"))
        };
        var result = plainClient.GetResponseObjectAsync<TestDataStructure>(
            new List<ChatMessage> { new(ChatRole.User, "hi") }).GetAwaiter().GetResult();
        ClassicAssert.AreEqual(5, result.Value);
        ClassicAssert.AreEqual("ok", result.Message);
    }

    [Test]
    public void TestGetResponseObjectAsync_FacadeFullPath_StaticRepair()
    {
        var settings = new MockSettings { FallbackChain = ["StaticRepairMock:model-sr"] };
        settings.EnabledProviders["StaticRepairMock"] = true;
        settings.ApiKeys["StaticRepairMock"] = "key";

        var manager = new RimLLMManager(settings);
        int callCount = 0;
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "StaticRepairMock",
            GenerateHandler = (messages, options, model) =>
            {
                callCount++;
                // 模擬含 markdown 與尾隨逗號的 JSON
                return System.Threading.Tasks.Task.FromResult("```json\n{\"Value\":99,\"Message\":\"repaired\",}\n```");
            }
        });

        var client = CreateClient(manager, "test.staticrepair.mod");
        var result = client.GetResponseObjectAsync<TestDataStructure>(
            new List<ChatMessage> { new(ChatRole.User, "get repaired data") }).GetAwaiter().GetResult();

        ClassicAssert.IsNotNull(result);
        ClassicAssert.AreEqual(99, result.Value);
        ClassicAssert.AreEqual("repaired", result.Message);
        ClassicAssert.AreEqual(1, callCount);
    }

    [Test]
    public void TestGetResponseObjectAsync_SchemaCached()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.schemacache.mod");

        var res1 = client.GetResponseObjectAsync<TestDataStructure>(
            new List<ChatMessage> { new(ChatRole.User, "req 1") }).GetAwaiter().GetResult();
        var res2 = client.GetResponseObjectAsync<TestDataStructure>(
            new List<ChatMessage> { new(ChatRole.User, "req 2") }).GetAwaiter().GetResult();

        ClassicAssert.IsNotNull(res1);
        ClassicAssert.IsNotNull(res2);
        ClassicAssert.AreEqual(42, res1.Value);
        ClassicAssert.AreEqual(42, res2.Value);
    }

    [Test]
    public void TestGetResponseAsync_PureChatOptionsDefaults()
    {
        var manager = CreateManager();
        var client = CreateClient(manager, "test.pureoptions.mod");
        var options = new ChatOptions(); // Pure MEAI ChatOptions without RimLLMChatOptions
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hello") }, options).GetAwaiter().GetResult();

        ClassicAssert.IsNotNull(response);
        ClassicAssert.IsNotEmpty(response.Text);
    }

    [Test]
    public void TestGetResponseAsync_DoesNotEchoRequestAdditionalProperties()
    {
        // 回應先前會把「請求的」AdditionalProperties 原樣回填。框架欄位改存在同一個
        // 字典之後，那等於把 rimllm_priority、rimllm_response_type 等內部設定洩漏給
        // 呼叫端；ChatResponse.AdditionalProperties 的語意也本該是供應商的回應中繼資料，
        // 而不是請求的回音。
        var manager = CreateManager();
        var client = CreateClient(manager, "test.addprops.mod");
        var options = new RimLLMChatOptions
        {
            Priority = 4,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["custom_key"] = "custom_val" }
        };
        var response = client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, options).GetAwaiter().GetResult();

        ClassicAssert.IsNotEmpty(response.Text);
        if (response.AdditionalProperties != null)
        {
            ClassicAssert.IsFalse(response.AdditionalProperties.ContainsKey("custom_key"));
            ClassicAssert.IsFalse(response.AdditionalProperties.ContainsKey(RimLLMChatOptions.PriorityKey));
        }
    }

    [Test]
    public void TestOpenAICompatibleProvider_CreateChatClient_WithoutApiKey_Succeeds()
    {
        var settings = new MockSettings();
        var provider = new Providers.OpenAICompatibleProvider(settings);
        // 本地相容 Provider 在金鑰為空時應自動回退 PlaceholderApiKey 而非拋出 ArgumentException
        using var client = provider.CreateChatClient("llama3");
        ClassicAssert.IsNotNull(client);
    }

    [Test]
    public void TestBuildMessages_AppendsCachedContextToExistingSystemMessage()
    {
        var options = new RimLLMChatOptions { CachedContext = "Cached Lore" };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Caller System Message"),
            new(ChatRole.User, "Hello")
        };

        var built = RimLLMChatClientExecutor.BuildMessages(messages, options);
        ClassicAssert.AreEqual(2, built.Count);
        ClassicAssert.AreEqual(ChatRole.System, built[0].Role);
        StringAssert.Contains("Caller System Message", built[0].Text);
        StringAssert.Contains("Cached Lore", built[0].Text);

        // 呼叫端的系統訊息只能出現一次。先前是「前置一份已含既有內容的字串」，
        // 結果同一段提示詞在送出的訊息裡重複了兩遍。
        ClassicAssert.AreEqual(
            built[0].Text.IndexOf("Caller System Message", StringComparison.Ordinal),
            built[0].Text.LastIndexOf("Caller System Message", StringComparison.Ordinal));
    }

    [Test]
    public void TestBuildOptions_IncludesExecutorManagedFlag()
    {
        var options = RimLLMChatClientExecutor.BuildOptions(null, "gpt-4o", false, null);
        ClassicAssert.IsNotNull(options.AdditionalProperties);
        ClassicAssert.IsTrue(options.AdditionalProperties.ContainsKey(RimLLMChatOptions.ExecutorManagedKey));
        ClassicAssert.IsTrue((bool)options.AdditionalProperties[RimLLMChatOptions.ExecutorManagedKey]);
    }

    [Test]
    public void TestGetResponseAsync_RecordUsage_NotDoubleCounted()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["TestMock:m1"]
        };
        mockSettings.EnabledProviders["TestMock"] = true;
        mockSettings.ApiKeys["TestMock"] = "key";
        var manager = new RimLLMManager(mockSettings);
        RimLLMProvider.Initialize(manager);

        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "TestMock",
            GenerateHandler = (messages, options, model) => System.Threading.Tasks.Task.FromResult("mock output text")
        });

        var client = CreateClient(manager, "test.usage.mod");
        var response = client.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hello world") }).GetAwaiter().GetResult();
        ClassicAssert.IsNotNull(response);

        var stats = manager.UsageTracker.ProviderStatistics["TestMock"];
        ClassicAssert.AreEqual(1, stats.TotalCount, "經由框架管線執行時用量應僅記錄一次，不得重複記帳");
        ClassicAssert.AreEqual(1, stats.SuccessCount);
    }

    [Test]
    public void TestStreamEarlyDispose_CancelsBackgroundStream()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["StreamCancelMock:m1"]
        };
        mockSettings.EnabledProviders["StreamCancelMock"] = true;
        mockSettings.ApiKeys["StreamCancelMock"] = "key";
        var manager = new RimLLMManager(mockSettings);

        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "StreamCancelMock",
            StreamHandler = async (messages, options, model, onChunk) =>
            {
                onChunk("chunk-1");
                for (int i = 0; i < 20; i++)
                {
                    await System.Threading.Tasks.Task.Delay(50);
                }
            }
        });

        var client = CreateClient(manager, "test.cancel.mod");
        var enumerator = client.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }).GetAsyncEnumerator();

        bool moved = enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        ClassicAssert.IsTrue(moved);
        ClassicAssert.AreEqual("chunk-1", enumerator.Current.Text);
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Test]
    public void TestGetStreamingResponseAsync_DoubleDisposeAsync_SwallowsObjectDisposedException()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockDoubleDispose:default"]
        };
        mockSettings.EnabledProviders["MockDoubleDispose"] = true;
        mockSettings.ApiKeys["MockDoubleDispose"] = "key";
        var manager = new RimLLMManager(mockSettings);
        var provider = new MockStreamProvider
        {
            ProviderId = "MockDoubleDispose",
            StreamHandler = (msgs, opts, model, onChunk) =>
            {
                onChunk("test");
                return Task.CompletedTask;
            }
        };
        manager.RegisterProvider(provider);
        var client = CreateClient(manager, "test.double.dispose");
        var enumerator = client.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }).GetAsyncEnumerator();

        bool moved = enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        ClassicAssert.IsTrue(moved);
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // 第二次呼叫 DisposeAsync，CTS 已處於 Disposed 狀態，驗證安全忽略 ObjectDisposedException
        Assert.DoesNotThrow(() => enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult());
    }

    [Test]
    public void TestOpenAiStream_SelfHealsInPlaceOn400()
    {
        RimLLM_Framework.Providers.RimLLMReasoningSupport.Reset();
        try
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenAI"] = "test-key";
            var provider = new TestOpenAIProvider(mockSettings);

            provider.WireHandler.ScriptResponse(
                System.Net.HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"unsupported parameter: reasoning_effort\",\"type\":\"invalid_request_error\"}}");
            provider.WireHandler.ResponseContentType = "text/event-stream";
            provider.WireHandler.ResponseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"healed\"}}]}\n\ndata: [DONE]\n\n";

            using var client = provider.CreateChatClient("gpt-4o");
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
            };

            var chunks = new List<string>();
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new(ChatRole.User, "stream test") },
                options).GetAsyncEnumerator();

            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (!string.IsNullOrEmpty(enumerator.Current.Text))
                    {
                        chunks.Add(enumerator.Current.Text);
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            ClassicAssert.AreEqual("healed", string.Concat(chunks));
            ClassicAssert.IsTrue(RimLLM_Framework.Providers.RimLLMReasoningSupport.IsReasoningUnsupported("OpenAI", "gpt-4o"));
        }
        finally
        {
            RimLLM_Framework.Providers.RimLLMReasoningSupport.Reset();
        }
    }
}
