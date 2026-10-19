using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class DispatcherAndCircuitBreakerTests
{
    /// <summary>
    /// 走組好的中介層堆疊送出一次請求。GenerateResultAsync 已隨 facade 一併移除，
    /// 現在唯一的入口就是 CreateChatClient 回傳的 IChatClient。
    /// </summary>
    private static string GenerateText(RimLLMManager manager, string modId, IList<ChatMessage> messages)
    {
        IChatClient client = manager.CreateChatClient(modId);
        return client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;
    }

    [Test]
    public void TestFallbackMechanism()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockFail:model-x", "MockSuccess:model-y"],
            MaxRetries = 0,
            RetryDelay = 0f,
            RoutingStrategy = 0
        };
        mockSettings.EnabledProviders["MockFail"] = true;
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockFail"] = "mock-key-1";
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-2";

        var manager = new RimLLMManager(mockSettings);

        int failCalls = 0;
        int successCalls = 0;

        var mockFail = new MockTestProvider
        {
            ProviderId = "MockFail",
            GenerateHandler = (msgs, opts, model) =>
            {
                failCalls++;
                throw new Exception("Simulated connection failure");
            }
        };

        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                successCalls++;
                return System.Threading.Tasks.Task.FromResult("success-data");
            }
        };

        manager.RegisterProvider(mockFail);
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.fallback.unit.id";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };
        string result = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;

        ClassicAssert.AreEqual("success-data", result);
        ClassicAssert.AreEqual(1, failCalls);
        ClassicAssert.AreEqual(1, successCalls);
    }

    [Test]
    public void TestSimpleRequestBuilderApi()
    {
        var options = new RimLLMChatOptions
        {
            Temperature = 0.2f,
            MaxOutputTokens = 64,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            Priority = 3,
            MinFallbackLevel = "High",
            CachedContext = "stable context",
            EnableContextCaching = true
        };

        ClassicAssert.AreEqual("stable context", options.CachedContext);
        ClassicAssert.AreEqual(64, options.MaxOutputTokens);
        ClassicAssert.AreEqual(0.2f, options.Temperature);
        ClassicAssert.AreEqual(ReasoningEffort.Low, options.Reasoning.Effort);
        ClassicAssert.AreEqual(3, options.Priority);
        ClassicAssert.AreEqual("High", options.MinFallbackLevel);
        ClassicAssert.IsTrue(options.EnableContextCaching);
    }

    [Test]
    public void TestSimpleGenerateAsyncOverload()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockSuccess:model-z"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);

        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                return System.Threading.Tasks.Task.FromResult("simple-response");
            }
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.simple.generate";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "be concise"),
            new(ChatRole.User, "hello")
        };
        var options = new RimLLMChatOptions
        {
            CachedContext = "stable context",
            MaxOutputTokens = 55,
            Temperature = 0.3f,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
        };

        string result = client.GetResponseAsync(messages, options).GetAwaiter().GetResult().Text;

        ClassicAssert.AreEqual("simple-response", result);
    }

    [Test]
    public void TestGlobalDefaultReasoningEffortAppliedToAutoRequests()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockSuccess:model-z"],
            MaxRetries = 0,
            RetryDelay = 0f,
            DefaultReasoningEffort = ReasoningEffort.High
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);

        ChatOptions capturedOptions = null;
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                capturedOptions = opts;
                return System.Threading.Tasks.Task.FromResult("ok");
            }
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.global.reasoning.default";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        string result = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;

        ClassicAssert.AreEqual("ok", result);
        ClassicAssert.IsNotNull(capturedOptions);
        ClassicAssert.AreEqual(ReasoningEffort.High, capturedOptions.Reasoning?.Effort);
    }

    [Test]
    public void TestSimpleGenerateObjectAsyncOverload()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockSuccess:model-z"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);

        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("{\"Value\":7,\"Message\":\"ok\"}")
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.simple.object";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "json only"),
            new(ChatRole.User, "make object")
        };
        var options = new RimLLMChatOptions { CachedContext = "stable schema notes" };
        var result = client.GetResponseObjectAsync<TestDataStructure>(messages, options).GetAwaiter().GetResult();

        ClassicAssert.IsNotNull(result);
        ClassicAssert.AreEqual(7, result.Value);
        ClassicAssert.AreEqual("ok", result.Message);
    }

    [Test]
    public void TestSimpleGenerateStreamingAsyncOverload()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockStream:model-z"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockStream"] = true;
        mockSettings.ApiKeys["MockStream"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);

        var mockStream = new MockStreamProvider
        {
            ProviderId = "MockStream",
            StreamHandler = (msgs, opts, model, onChunk) =>
            {
                onChunk("a");
                onChunk("b");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        };
        manager.RegisterProvider(mockStream);

        const string modId = "test.simple.streaming";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var chunks = new List<string>();
        var messages = new List<ChatMessage> { new(ChatRole.User, "stream please") };
        var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
        string result = "";
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                ChatResponseUpdate update = enumerator.Current;
                if (!string.IsNullOrEmpty(update.Text))
                {
                    result += update.Text;
                    chunks.Add(update.Text);
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ClassicAssert.AreEqual("ab", result);
        ClassicAssert.AreEqual(2, chunks.Count);
        ClassicAssert.AreEqual("a", chunks[0]);
        ClassicAssert.AreEqual("b", chunks[1]);
    }

    [Test]
    public void TestGenerateObjectStructureAndCache()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockSuccess:model-z"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);

        string requestedPromptReceived = null;
        string requestedSystemPromptReceived = null;

        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                requestedPromptReceived = System.Linq.Enumerable.FirstOrDefault(msgs, m => m.Role == ChatRole.User)?.Text;
                requestedSystemPromptReceived = string.Join("\n", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(msgs, m => m.Role == ChatRole.System), m => m.Text)));
                // 回傳合法的 JSON 字串，並刻意帶有 markdown 標記與尾隨逗號以測試 JSON 修復器
                return System.Threading.Tasks.Task.FromResult("```json\n{\n  \"Value\": 42,\n  \"Message\": \"Hello Cache\",\n}\n```");
            }
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.object.unit.id";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Base System Prompt"),
            new(ChatRole.User, "Give me 42")
        };

        var resultObject = client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult();

        ClassicAssert.IsNotNull(resultObject);
        ClassicAssert.AreEqual(42, resultObject.Value);
        ClassicAssert.AreEqual("Hello Cache", resultObject.Message);
        ClassicAssert.IsTrue(requestedSystemPromptReceived.Contains("Value"));
        ClassicAssert.IsTrue(requestedSystemPromptReceived.Contains("Base System Prompt"));
    }

    [Test]
    public void TestPureProviderFallbackResolution()
    {
        var mockSettings = new MockSettings();
        mockSettings.SetModelList("OpenRouter", ["model-1", "model-2"]);

        // 直接測管線本體：Manager 的純轉發已移除，解析邏輯只住在這裡。
        var pipeline = new RimLLMFallbackPipeline(
            mockSettings,
            new RimLLMHealthLedger(),
            new RimLLMUsageTracker(mockSettings),
            providerId => null,
            providerId => true);

        // 1. 測試傳統 "Provider:Model" 格式
        bool res1 = pipeline.ResolveFallbackEntry("OpenAI:gpt-4o", out string providerId1, out string modelName1);
        ClassicAssert.IsTrue(res1);
        ClassicAssert.AreEqual("OpenAI", providerId1);
        ClassicAssert.AreEqual("gpt-4o", modelName1);

        // 2. 測試 OpenRouter 純供應商格式 (會自動解析為快取的第一個模型，此處為 model-1)
        bool res2 = pipeline.ResolveFallbackEntry("OpenRouter", out string providerId2, out string modelName2);
        ClassicAssert.IsTrue(res2);
        ClassicAssert.AreEqual("OpenRouter", providerId2);
        ClassicAssert.AreEqual("model-1", modelName2);

        // 3. 測試其他純供應商格式 (會自動回退至 defaultModel)
        bool res3 = pipeline.ResolveFallbackEntry("OpenAI", out string providerId3, out string modelName3);
        ClassicAssert.IsTrue(res3);
        ClassicAssert.AreEqual("OpenAI", providerId3);
        ClassicAssert.AreEqual("default", modelName3);
    }

    [Test]
    public void TestPriorityQueueAndCancellation()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockProv:model-a"],
            MaxConcurrentRequests = 1
        };
        mockSettings.EnabledProviders["MockProv"] = true;
        mockSettings.ApiKeys["MockProv"] = "mock-key";

        var manager = new RimLLMManager(mockSettings);

        var tcs1 = new System.Threading.Tasks.TaskCompletionSource<string>();
        var tcs2 = new System.Threading.Tasks.TaskCompletionSource<string>();

        int callCount = 0;
        var mockProv = new MockTestProvider
        {
            ProviderId = "MockProv",
            GenerateHandler = (msgs, opts, model) =>
            {
                callCount++;
                if (callCount == 1) return tcs1.Task;
                return tcs2.Task;
            }
        };
        manager.RegisterProvider(mockProv);

        RimLLMProvider.Initialize(manager);
        IChatClient client1 = RimLLMProvider.CreateChatClient("mod1");
        IChatClient client2 = RimLLMProvider.CreateChatClient("mod2");

        var messages = new List<ChatMessage> { new(ChatRole.User, "p1") };
        var task1 = client1.GetResponseAsync(messages);

        // 2. 執行第 2 個，但先設定 CancellationToken
        var cts = new System.Threading.CancellationTokenSource();
        var task2 = client2.GetResponseAsync(messages, cancellationToken: cts.Token);

        // 驗證只有 1 個請求實際被調用
        ClassicAssert.AreEqual(1, callCount);

        // 在 req1 還在執行時，取消 req2
        cts.Cancel();

        // 驗證 task2 被標記為已取消
        Assert.Throws<AggregateException>(() => task2.Wait());
        ClassicAssert.IsTrue(task2.IsCanceled);

        // 釋放第 1 個
        tcs1.SetResult("r1");
        ClassicAssert.AreEqual("r1", task1.GetAwaiter().GetResult().Text);

        // 驗證第 2 個請求因為在佇列中被取消，根本沒有被 provider 呼叫過
        ClassicAssert.AreEqual(1, callCount);
    }

    [Test]
    public void TestMinFallbackLevelFilter()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockProv:model-mini", "MockProv:model-pro"],
            RoutingStrategy = 0
        };
        mockSettings.ModelLevelOverrides["MockProv:model-mini"] = 2; // Medium
        mockSettings.ModelLevelOverrides["MockProv:model-pro"] = 3;  // High
        mockSettings.EnabledProviders["MockProv"] = true;
        mockSettings.ApiKeys["MockProv"] = "mock-key";

        var manager = new RimLLMManager(mockSettings);

        var calledModels = new List<string>();
        var mockProv = new MockTestProvider
        {
            ProviderId = "MockProv",
            GenerateHandler = (msgs, opts, model) =>
            {
                calledModels.Add(model);
                return System.Threading.Tasks.Task.FromResult("success");
            }
        };
        manager.RegisterProvider(mockProv);

        const string modId = "mod.minfallback";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "p") };
        var options = new RimLLMChatOptions { MinFallbackLevel = "High" };
        string res = client.GetResponseAsync(messages, options).GetAwaiter().GetResult().Text;

        ClassicAssert.AreEqual("success", res);
        ClassicAssert.AreEqual(1, calledModels.Count);
        ClassicAssert.AreEqual("model-pro", calledModels[0]);
    }

    [Test]
    public void TestCircuitBreaker()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockFail:model-a", "MockSuccess:model-b"],
            MaxRetries = 0,
            RetryDelay = 0f,
            RoutingStrategy = 0
        };
        mockSettings.EnabledProviders["MockFail"] = true;
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockFail"] = "key1";
        mockSettings.ApiKeys["MockSuccess"] = "key2";

        var manager = new RimLLMManager(mockSettings);

        int failCount = 0;
        var mockFail = new MockTestProvider
        {
            ProviderId = "MockFail",
            GenerateHandler = (msgs, opts, model) =>
            {
                failCount++;
                throw new Exception("Temporary Error");
            }
        };
        int successCount = 0;
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                successCount++;
                return System.Threading.Tasks.Task.FromResult("ok");
            }
        };
        manager.RegisterProvider(mockFail);
        manager.RegisterProvider(mockSuccess);

        const string modId = "mod.circuitbreaker";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "p") };
        var options = new ChatOptions { MaxOutputTokens = 5 };

        // 連續呼叫 3 次失敗以進入冷卻
        for (int i = 0; i < 3; i++)
        {
            try { client.GetResponseAsync(messages, options).GetAwaiter().GetResult(); } catch { }
            if (i < 2)
            {
                manager.ClearCooldowns();
            }
        }
        ClassicAssert.AreEqual(3, failCount);
        ClassicAssert.AreEqual(3, successCount);

        // 第 4 次呼叫，因進入冷卻，MockFail 應被跳過，只呼叫 MockSuccess
        string res = client.GetResponseAsync(messages, options).GetAwaiter().GetResult().Text;
        ClassicAssert.AreEqual("ok", res);
        ClassicAssert.AreEqual(3, failCount); // 還是 3，被跳過了
        ClassicAssert.AreEqual(4, successCount);
    }

    [Test]
    public void TestNonRetryableInvalidKeyDoesNotRetryOrTripCircuit()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockFail:model-a", "MockSuccess:model-b"],
            MaxRetries = 5,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockFail"] = true;
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockFail"] = "bad-key";
        mockSettings.ApiKeys["MockSuccess"] = "good-key";

        var manager = new RimLLMManager(mockSettings);

        int failCount = 0;
        var mockFail = new MockTestProvider
        {
            ProviderId = "MockFail",
            GenerateHandler = (msgs, opts, model) =>
            {
                failCount++;
                throw new RimLLMException(LLMError.InvalidKey, "Invalid key");
            }
        };

        int successCount = 0;
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                successCount++;
                return System.Threading.Tasks.Task.FromResult("ok");
            }
        };

        manager.RegisterProvider(mockFail);
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.invalidkey.retry";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "p") };

        for (int i = 0; i < 4; i++)
        {
            ClassicAssert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
        }

        ClassicAssert.AreEqual(4, failCount, "InvalidKey should be tried once per request, not retried or cooled down.");
        ClassicAssert.AreEqual(4, successCount);
    }

    [Test]
    public void TestStaticRepairOnStructuredOutput()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockProv:m"],
            RoutingStrategy = 0
        };
        mockSettings.EnabledProviders["MockProv"] = true;
        mockSettings.ApiKeys["MockProv"] = "key";

        var manager = new RimLLMManager(mockSettings);

        int callCount = 0;
        var mockProv = new MockTestProvider
        {
            ProviderId = "MockProv",
            GenerateHandler = (msgs, opts, model) =>
            {
                callCount++;
                // 模擬帶有 markdown 區塊與缺少結尾括號的 JSON
                return System.Threading.Tasks.Task.FromResult("```json\n{\"Value\": 99, \"Message\": \"repaired\"");
            }
        };
        manager.RegisterProvider(mockProv);

        const string modId = "mod.staticrepair";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "p") };
        var res = client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult();

        ClassicAssert.IsNotNull(res);
        ClassicAssert.AreEqual(99, res.Value);
        ClassicAssert.AreEqual("repaired", res.Message);
        ClassicAssert.AreEqual(1, callCount); // 本地靜態修復完成，僅呼叫 1 次
    }

    [Test]
    public void TestCachedContextRequestApi()
    {
        var options = new RimLLMChatOptions
        {
            CachedContext = "stable colony state"
        };

        ClassicAssert.IsTrue(options.EnableContextCaching);
        ClassicAssert.AreEqual("stable colony state", options.CachedContext);
    }

    [Test]
    public void TestComplexTypeSchemaWarmupAndRecursion()
    {
        // 預熱無空建構子、帶有循環引用的型別，驗證不會 StackOverflow 且產生合理 JSON
        RimLLMJsonHelper.GetSampleJson<ComplexTestDataStructure>();

        string json = RimLLMJsonHelper.GetSampleJson(typeof(ComplexTestDataStructure));

        ClassicAssert.IsNotEmpty(json);
        ClassicAssert.AreNotEqual("{}", json);
        ClassicAssert.IsTrue(json.Contains("\"Name\":\"string\""), "應該遞迴產生 string 欄位的 dummy 資料");
        ClassicAssert.IsTrue(json.Contains("\"Age\":0"), "應該遞迴展開 int 欄位的 dummy 資料");
        ClassicAssert.IsTrue(json.Contains("\"IsActive\":false"), "應該遞迴展開 bool 欄位的 dummy 資料");
        ClassicAssert.IsTrue(json.Contains("\"Skills\":[\"string\"]"), "應該產生 List 的範例陣列元素");
        ClassicAssert.IsTrue(json.Contains("\"Mapping\":{\"string\":0}"), "應該產生 Dictionary 的範例鍵值對");
        ClassicAssert.IsTrue(json.Contains("\"Nested\":{"), "應該遞迴展開 Nested 屬性");
        ClassicAssert.IsTrue(json.Contains("\"SelfRef\":null"), "循環引用欄位在偵測到之後應截斷為 null，避免 StackOverflow");
    }

    [Test]
    public void TestModelLevelOverrideTakesPriority()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockProv:model-mini", "MockProv:model-pro"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockProv"] = true;
        mockSettings.ApiKeys["MockProv"] = "mock-key";
        // 將原本關鍵字判定為 Medium 的 model-mini 覆寫為 High
        mockSettings.ModelLevelOverrides["model-mini"] = 3;

        var manager = new RimLLMManager(mockSettings);
        var calledModels = new List<string>();
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "MockProv",
            GenerateHandler = (msgs, opts, model) =>
            {
                calledModels.Add(model);
                return System.Threading.Tasks.Task.FromResult("success");
            }
        });

        const string modId = "mod.level.override";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var userMsgs = new List<ChatMessage> { new(ChatRole.User, "p") };
        var options = new RimLLMChatOptions { MinFallbackLevel = "High" };
        string res = client.GetResponseAsync(userMsgs, options).GetAwaiter().GetResult().Text;

        // 覆寫生效：model-mini 被視為 High，不再被 MinFallbackLevel 過濾
        ClassicAssert.AreEqual("success", res);
        ClassicAssert.AreEqual(1, calledModels.Count);
        ClassicAssert.AreEqual("model-mini", calledModels[0]);
    }


    [Test]
    public void TestJsonSchemaGenerator()
    {
        var schema = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(TestDataStructure))).AsObject();
        ClassicAssert.AreEqual("object", (string)schema["type"]);
        ClassicAssert.IsNotNull(schema["properties"]);
        ClassicAssert.AreEqual("integer", (string)schema["properties"]?.AsObject()?["Value"]?.AsObject()?["type"]);
        ClassicAssert.AreEqual("string", (string)schema["properties"]?.AsObject()?["Message"]?.AsObject()?["type"]);
        ClassicAssert.IsFalse((bool)schema["additionalProperties"]);
    }

    [Test]
    public void TestJsonSchemaRecursiveTypeDoesNotStackOverflow()
    {
        // NestedData.SelfRef 指回 ComplexTestDataStructure，形成循環。
        var schema = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(ComplexTestDataStructure))).AsObject();

        ClassicAssert.IsNotNull(schema, "循環型別仍應產生可用的 schema，不得遞迴爆棧");

        var nested = schema["properties"]?.AsObject()?["Nested"];
        ClassicAssert.IsNotNull(nested, "非循環的巢狀成員應正常展開");
        ClassicAssert.AreEqual("number", (string)nested["properties"]?.AsObject()?["Weight"]?.AsObject()?["type"]);

        // 循環的截斷點與收斂性由 SchemaBuilderTests 詳測，此處只確認整體有限且合法。
        ClassicAssert.Less(
            schema.ToString().Length,
            200000,
            "循環型別的 schema 應收斂到有限大小");
    }

    [Test]
    public void TestJsonSchemaDictionaryBecomesOpenMap()
    {
        var schema = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(ComplexTestDataStructure))).AsObject();
        var mapping = schema["properties"]?.AsObject()?["Mapping"];

        ClassicAssert.IsNotNull(mapping, "Dictionary 成員應出現在 schema 中");
        ClassicAssert.AreEqual("object", (string)mapping["type"]);
        ClassicAssert.AreEqual("integer", (string)mapping["additionalProperties"]?.AsObject()?["type"],
            "Dictionary 應產生開放式 map schema 而非空物件");
        ClassicAssert.IsNull(mapping["properties"], "開放式 map 不應帶有固定的 properties 清單");

        ClassicAssert.IsTrue(RimLLMSchemaBuilder.ContainsOpenEndedMap(typeof(ComplexTestDataStructure)),
            "含 Dictionary 的型別必須被偵測為開放式 map，以便關閉 strict 模式");
        ClassicAssert.IsFalse(RimLLMSchemaBuilder.ContainsOpenEndedMap(typeof(TestDataStructure)),
            "不含 Dictionary 的型別不應被誤判為開放式 map");
    }

    [Test]
    public void TestJsonSchemaNullableIsRequiredButTypedAsUnion()
    {
        var schema = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(NullableTestDataStructure))).AsObject();

        var optionalType = schema["properties"].AsObject()["OptionalCount"].AsObject()["type"].AsArray();
        CollectionAssert.AreEquivalent(
            new[] { "integer", "null" },
            optionalType.Deserialize<string[]>(),
            "Nullable<int> 的選填性應以聯集型別表達");

        var requiredNames = new List<string>();
        foreach (var item in schema["required"].AsArray()) requiredNames.Add((string)item);

        CollectionAssert.Contains(requiredNames, "Name");
        CollectionAssert.Contains(requiredNames, "OptionalCount", "OpenAI strict 要求 required 涵蓋所有 property");
    }

    [Test]
    public void TestJsonSchemaGeneratorCacheReturnsIndependentInstances()
    {
        var first = RimLLMSchemaBuilder.Build(typeof(TestDataStructure));
        var second = RimLLMSchemaBuilder.Build(typeof(TestDataStructure));

        ClassicAssert.AreEqual(first.Json, second.Json, "schema 快取應提供一致的不可變結果");
    }

    [Test]
    public void TestRepairJsonClosesInterleavedBracketsInOrder()
    {
        // 陣列在物件內：必須先補 ] 再補 }
        string repairedArrayInObject = RimLLMJsonHelper.RepairJson("{\"items\":[1,2");
        ClassicAssert.AreEqual("{\"items\":[1,2]}", repairedArrayInObject, "巢狀括號必須依 LIFO 順序閉合");
        Assert.DoesNotThrow(() => JsonNode.Parse(repairedArrayInObject));

        // 物件在陣列內：必須先補 } 再補 ]
        string repairedObjectInArray = RimLLMJsonHelper.RepairJson("[{\"a\":1");
        ClassicAssert.AreEqual("[{\"a\":1}]", repairedObjectInArray, "巢狀括號必須依 LIFO 順序閉合");
        Assert.DoesNotThrow(() => JsonNode.Parse(repairedObjectInArray));

        // 多層交錯
        string repairedMixed = RimLLMJsonHelper.RepairJson("{\"a\":[{\"b\":[1");
        Assert.DoesNotThrow(() => JsonNode.Parse(repairedMixed), "多層交錯巢狀修復後必須可解析");
    }

    [Test]
    public void TestRepairJsonClosesDanglingString()
    {
        string repaired = RimLLMJsonHelper.RepairJson("{\"message\":\"unterminated");
        Assert.DoesNotThrow(() => JsonNode.Parse(repaired), "未閉合的字串必須先補上引號，補的括號才不會落在字串內部");
        ClassicAssert.AreEqual("unterminated", (string)JsonNode.Parse(repaired).AsObject()["message"]);
    }

    [Test]
    public void TestRepairJsonTrimsDanglingTokens()
    {
        // 截斷在鍵之後
        string afterColon = RimLLMJsonHelper.RepairJson("{\"a\":1,\"b\":");
        Assert.DoesNotThrow(() => JsonNode.Parse(afterColon), "截斷於冒號後應補 null 使其可解析");

        // 截斷在逗號之後
        string afterComma = RimLLMJsonHelper.RepairJson("{\"a\":1,");
        Assert.DoesNotThrow(() => JsonNode.Parse(afterComma), "截斷於逗號後應移除懸空逗號");
    }

    [Test]
    public void TestRepairJsonBailsOutOnMismatchedBrackets()
    {
        // 閉合符號與開啟順序不符，代表結構已損毀，不應嘗試補齊而讓結果更糟。
        const string broken = "{\"a\":]";
        string repaired = RimLLMJsonHelper.RepairJson(broken);
        ClassicAssert.AreEqual(broken, repaired, "括號順序不符時應放棄補齊，交由後續 fallback 處理");
    }

    [Test]
    public void TestSmartRoutingMinLatency()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockSlow:model-s", "MockFast:model-f"],
            RoutingStrategy = 1, // MinLatency
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockSlow"] = true;
        mockSettings.EnabledProviders["MockFast"] = true;
        mockSettings.ApiKeys["MockSlow"] = "key-s";
        mockSettings.ApiKeys["MockFast"] = "key-f";

        var manager = new RimLLMManager(mockSettings);

        var mockSlow = new MockTestProvider
        {
            ProviderId = "MockSlow",
            GenerateHandler = async (msgs, opts, model) =>
            {
                await System.Threading.Tasks.Task.Delay(150);
                return "slow-ok";
            }
        };
        var mockFast = new MockTestProvider
        {
            ProviderId = "MockFast",
            GenerateHandler = async (msgs, opts, model) =>
            {
                await System.Threading.Tasks.Task.Delay(1);
                return "fast-ok";
            }
        };

        manager.RegisterProvider(mockSlow);
        manager.RegisterProvider(mockFast);

        const string modId = "test.routing.latency";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // 第一次呼叫：兩個都沒有延遲歷史，依據 FallbackChain 順序（先 MockSlow）
        string res1 = GenerateText(manager, modId, messages);
        ClassicAssert.AreEqual("slow-ok", res1);

        // 第二次呼叫：因為 MockSlow 已有延遲（100ms），MockFast 尚未有歷史（視為 0 延遲），優先呼叫 MockFast
        string res2 = GenerateText(manager, modId, messages);
        ClassicAssert.AreEqual("fast-ok", res2);

        // 第三次呼叫：此時 MockSlow 平均 100ms，MockFast 平均 5ms，智慧路由應該優先選擇 MockFast
        string res3 = GenerateText(manager, modId, messages);
        ClassicAssert.AreEqual("fast-ok", res3);
    }

    [Test]
    public void TestSmartRoutingPriorityFailover()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockFail:model-x", "MockSuccess:model-y"],
            RoutingStrategy = 0, // PriorityFailover
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockFail"] = true;
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockFail"] = "key-x";
        mockSettings.ApiKeys["MockSuccess"] = "key-y";

        var manager = new RimLLMManager(mockSettings);

        int failCalls = 0;
        int successCalls = 0;

        var mockFail = new MockTestProvider
        {
            ProviderId = "MockFail",
            GenerateHandler = (msgs, opts, model) =>
            {
                failCalls++;
                throw new Exception("Simulated fail");
            }
        };
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) =>
            {
                successCalls++;
                return System.Threading.Tasks.Task.FromResult("success-ok");
            }
        };

        manager.RegisterProvider(mockFail);
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.routing.failover";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // 第一次呼叫：MockFail 失敗，然後 Fallback 到 MockSuccess 成功
        string res1 = GenerateText(manager, modId, messages);
        ClassicAssert.AreEqual("success-ok", res1);
        ClassicAssert.AreEqual(1, failCalls);
        ClassicAssert.AreEqual(1, successCalls);

        // 第二次呼叫：MockFail 此時正處於 60 秒的故障冷卻期，智慧路由應直接跳過它，不進行呼叫，直接執行 MockSuccess
        string res2 = GenerateText(manager, modId, messages);
        ClassicAssert.AreEqual("success-ok", res2);
        ClassicAssert.AreEqual(1, failCalls); // 呼叫次數仍為 1，說明已被跳過！
        ClassicAssert.AreEqual(2, successCalls);
    }

    [Test]
    public void TestJsonRepairSettings()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockJSON:model-j"],
            EnableJsonRepair = false, // 禁用 JSON 修復
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockJSON"] = true;
        mockSettings.ApiKeys["MockJSON"] = "key-j";

        var manager = new RimLLMManager(mockSettings);
        var mockJSON = new MockTestProvider
        {
            ProviderId = "MockJSON",
            GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("```json\n{ \"Value\": 42, \"Message\": \"ok\", }\n```") // 帶有 markdown 與尾隨逗號的不合法 JSON
        };
        manager.RegisterProvider(mockJSON);

        const string modId = "test.json.repair.settings";

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // 1. 當 EnableJsonRepair 為 false 時，預期拋出例外
        Assert.Throws<RimLLMException>(() =>
        {
            string raw = GenerateText(manager, modId, messages);
            RimLLMJsonHelper.DeserializeStructured<TestDataStructure>(raw, mockSettings);
        });

        // 2. 當 EnableJsonRepair 為 true 時，預期成功修復並解析
        mockSettings.EnableJsonRepair = true;
        string rawRepaired = GenerateText(manager, modId, messages);
        var res = RimLLMJsonHelper.DeserializeStructured<TestDataStructure>(rawRepaired, mockSettings);
        ClassicAssert.IsNotNull(res);
        ClassicAssert.AreEqual(42, res.Value);
        ClassicAssert.AreEqual("ok", okStr(res.Message));
    }

    [Test]
    public void TestParseRetryAfterHandlesSecondsAndHttpDate()
    {
        // 秒數格式
        ClassicAssert.AreEqual(TimeSpan.FromSeconds(30), LLMErrorMapper.ParseRetryAfter("30"));

        // HTTP 日期格式（RFC 7231 允許，先前只吃秒數的路徑會整個漏掉）
        string future = DateTimeOffset.UtcNow.AddSeconds(120).ToString("r");
        TimeSpan? fromDate = LLMErrorMapper.ParseRetryAfter(future);
        ClassicAssert.IsNotNull(fromDate, "HTTP 日期格式的 Retry-After 必須能被解析");
        ClassicAssert.Greater(fromDate.Value.TotalSeconds, 60);
        ClassicAssert.LessOrEqual(fromDate.Value.TotalSeconds, 121);

        // 已過期的日期與非正值不應產生建議延遲
        ClassicAssert.IsNull(LLMErrorMapper.ParseRetryAfter(DateTimeOffset.UtcNow.AddSeconds(-60).ToString("r")));
        ClassicAssert.IsNull(LLMErrorMapper.ParseRetryAfter("0"));
        ClassicAssert.IsNull(LLMErrorMapper.ParseRetryAfter("not-a-date"));
        ClassicAssert.IsNull(LLMErrorMapper.ParseRetryAfter((string)null));
    }



    [Test]
    public void TestEmbeddingEndpointNormalizesToServiceRoot()
    {
        // OpenAI SDK 需要的是服務根位址，使用者可能貼上完整的 embeddings 路徑。
        ClassicAssert.AreEqual("http://localhost:11434/v1",
            RimLLMEmbeddingService.NormalizeEmbeddingEndpoint("http://localhost:11434/v1/embeddings"));
        ClassicAssert.AreEqual("http://localhost:1234/v1",
            RimLLMEmbeddingService.NormalizeEmbeddingEndpoint(" http://localhost:1234/v1/ "));
        ClassicAssert.AreEqual("http://localhost:11434",
            RimLLMEmbeddingService.NormalizeEmbeddingEndpoint("http://localhost:11434/api/embed"));
        ClassicAssert.IsNull(RimLLMEmbeddingService.NormalizeEmbeddingEndpoint(""),
            "空字串應回傳 null，讓呼叫端改用預設端點");
        ClassicAssert.IsNull(RimLLMEmbeddingService.NormalizeEmbeddingEndpoint(null));
    }

    [Test]
    public void TestEmbeddingServiceRejectsOfflineProvider()
    {
        var mockSettings = new MockSettings { EmbeddingProvider = "Disabled" };
        var service = new RimLLMEmbeddingService(mockSettings);

        var ex = Assert.Throws<RimLLMException>(() =>
            service.ComputeEmbeddingAsync("hello").GetAwaiter().GetResult());
        ClassicAssert.IsTrue(ex.Message.Contains("No embedding provider is configured"), "停用狀態下不產生向量，應明確拋出錯誤");
    }

    [Test]
    public void TestModelNotFoundDoesNotConsumeRetries()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockNotFound:model-a"],
            MaxRetries = 3,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockNotFound"] = true;
        mockSettings.ApiKeys["MockNotFound"] = "key";

        var manager = new RimLLMManager(mockSettings);
        int calls = 0;
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "MockNotFound",
            GenerateHandler = (msgs, opts, model) =>
            {
                calls++;
                throw new RimLLMException(LLMError.ModelNotFound, "Model or endpoint not found");
            }
        });

        const string modId = "test.modelnotfound";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        Assert.Throws<RimLLMException>(() =>
            client.GetResponseAsync(messages).GetAwaiter().GetResult());
        ClassicAssert.AreEqual(1, calls, "404 屬於不可重試錯誤，不應消耗重試次數");
    }

    [Test]
    public void TestUnknownChatFinishReasonIsRetryable()
    {
        // OpenRouter 回 finish_reason: "error" 時，OpenAI SDK 以此形式擲出例外；屬暫時性錯誤，應同模型重試。
        var finishReasonEx = new ArgumentOutOfRangeException("value", "error", "Unknown ChatFinishReason value.");
        ClassicAssert.IsTrue(RimLLMFallbackPipeline.IsRetryableException(finishReasonEx));

        // 其他 ArgumentOutOfRangeException 仍維持非可重試。
        var otherEx = new ArgumentOutOfRangeException("count", 5, "Count must be non-negative.");
        ClassicAssert.IsFalse(RimLLMFallbackPipeline.IsRetryableException(otherEx));
    }

    [Test]
    public void TestQuotaExceededIsNotRetried_FallsToNextCandidateImmediately()
    {
        // 402：帳戶餘額不會在退避的幾十秒內變出來，重試只是白等。
        ClassicAssert.IsFalse(RimLLMFallbackPipeline.IsRetryableException(
            new RimLLMException(LLMError.QuotaExceeded, "insufficient_quota") { HttpStatusCode = 402 }));
        // 429 帶 "quota" 字樣的每分鐘限流（Gemini 免費層）同樣映成 QuotaExceeded，但等一下就能過，必須重試。
        ClassicAssert.IsTrue(RimLLMFallbackPipeline.IsRetryableException(
            LLMErrorMapper.CreateException(429, "You exceeded your current quota (GenerateRequestsPerMinute)")));

        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockBroke:model-a", "MockRich:model-b"],
            MaxRetries = 5,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockBroke"] = true;
        mockSettings.EnabledProviders["MockRich"] = true;
        mockSettings.ApiKeys["MockBroke"] = "k";
        mockSettings.ApiKeys["MockRich"] = "k";
        var manager = new RimLLMManager(mockSettings);

        int brokeCalls = 0;
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "MockBroke",
            GenerateHandler = (msgs, opts, model) =>
            {
                brokeCalls++;
                throw new RimLLMException(LLMError.QuotaExceeded, "insufficient_quota") { HttpStatusCode = 402 };
            }
        });
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "MockRich",
            GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
        });

        IChatClient client = manager.CreateChatClient("test.quota.norety");
        var messages = new List<ChatMessage> { new(ChatRole.User, "p") };
        ClassicAssert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
        ClassicAssert.AreEqual(1, brokeCalls, "配額耗盡不應消耗重試次數，直接換下一個候選");
    }

    [Test]
    public void TestEmptyStreamErrorIsRetryable()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockEmptyStream:model-a", "MockGoodStream:model-b"],
            MaxRetries = 0,
            RetryDelay = 0f
        };
        mockSettings.EnabledProviders["MockEmptyStream"] = true;
        mockSettings.EnabledProviders["MockGoodStream"] = true;
        mockSettings.ApiKeys["MockEmptyStream"] = "key";
        mockSettings.ApiKeys["MockGoodStream"] = "key";

        var manager = new RimLLMManager(mockSettings);

        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "MockEmptyStream",
            StreamHandler = (msgs, opts, model, onChunk) =>
                throw new RimLLMException(LLMError.NetworkError, "串流未回傳任何內容。")
        });
        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "MockGoodStream",
            StreamHandler = (msgs, opts, model, onChunk) =>
            {
                onChunk("recovered");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        });

        const string modId = "test.emptystream.fallback";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var received = new List<string>();
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
        string result = "";
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                if (!string.IsNullOrEmpty(enumerator.Current.Text))
                {
                    result += enumerator.Current.Text;
                    received.Add(enumerator.Current.Text);
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ClassicAssert.AreEqual("recovered", result, "零輸出串流應視為可重試失敗並由下一個供應商接手");
        CollectionAssert.Contains(received, "recovered");
    }

    [Test]
    public void TestNativeSchemaRejectionRequiresExplicitMarker()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockInvalid:model-a"],
            MaxRetries = 0,
            RetryDelay = 0f,
            EnableNativeSchema = true,
            EnableJsonRepair = false
        };
        mockSettings.EnabledProviders["MockInvalid"] = true;
        mockSettings.ApiKeys["MockInvalid"] = "key";

        var manager = new RimLLMManager(mockSettings);
        int calls = 0;
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "MockInvalid",
            GenerateHandler = (msgs, opts, model) =>
            {
                calls++;
                throw new RimLLMException(LLMError.InvalidResponse, "provider returned garbage");
            }
        });

        const string modId = "test.schema.marker";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        Assert.Throws<RimLLMException>(() =>
            client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult());

        ClassicAssert.AreEqual(1, calls, "僅標記為 schema 拒絕的錯誤才可觸發降級重打");
    }



    [Test]
    public void TestChatInputWhitespaceOnlyIsRejected()
    {
        ClassicAssert.IsFalse(ChatTestDrawer.ShouldSendChatInput(null));
        ClassicAssert.IsFalse(ChatTestDrawer.ShouldSendChatInput(""));
        ClassicAssert.IsFalse(ChatTestDrawer.ShouldSendChatInput("   \t \n "),
            "僅含空白的聊天輸入不應送出請求");
        ClassicAssert.IsTrue(ChatTestDrawer.ShouldSendChatInput(" hi "));
    }

    [Test]
    public void TestDispatcherQueueIsBounded()
    {
        RimLLMDispatcher.ResetQueueForTests();
        try
        {
            for (int i = 0; i < 5000; i++)
            {
                RimLLMDispatcher.TryEnqueueBounded(() => { });
            }

            ClassicAssert.LessOrEqual(RimLLMDispatcher.QueuedCount, 4096,
                "派遣器佇列必須有上限，避免無限成長");
        }
        finally
        {
            RimLLMDispatcher.ResetQueueForTests();
        }
    }

    [Test]
    public void TestDispatcherDrainRespectsPerFrameBudget()
    {
        RimLLMDispatcher.ResetQueueForTests();
        try
        {
            int executed = 0;
            for (int i = 0; i < 500; i++)
            {
                RimLLMDispatcher.TryEnqueueBounded(() => executed++);
            }

            int processed = RimLLMDispatcher.DrainWithBudget(128, long.MaxValue);

            ClassicAssert.AreEqual(128, processed, "單次清空不得超過每幀項目上限");
            ClassicAssert.AreEqual(128, executed);
            ClassicAssert.Greater(RimLLMDispatcher.QueuedCount, 0, "剩餘項目應留待下一幀處理");
        }
        finally
        {
            RimLLMDispatcher.ResetQueueForTests();
        }
    }

    private string okStr(string s) => s;
}
