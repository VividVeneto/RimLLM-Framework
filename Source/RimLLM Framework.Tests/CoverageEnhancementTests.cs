using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class CoverageEnhancementTests
{
    private string _encryptionKeyDirectory;

    [SetUp]
    public void SetUpEncryptionKeyStore()
    {
        _encryptionKeyDirectory = Path.Combine(
            Path.GetTempPath(),
            "RimLLMCoverageEncryptionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_encryptionKeyDirectory);
        EncryptionUtility.ResetSecureKeyForTests();
        EncryptionUtility.SecureKeyPathResolver = () =>
            Path.Combine(_encryptionKeyDirectory, "RimLLM_EncryptionKey.dat");
    }

    [TearDown]
    public void TearDownEncryptionKeyStore()
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
    public void TestHealthLedger()
    {
        var ledger = new RimLLMHealthLedger();

        // Null / empty 安全性
        ledger.RecordSuccess(null);
        ledger.RecordFailure(null);
        ClassicAssert.IsFalse(ledger.IsInCooldown(null));
        ClassicAssert.AreEqual(0f, ledger.GetAverageLatency(null));

        // 1~2 次失敗（可重試）給予短暫冷卻以利備援切換，並累積失敗計數
        ledger.RecordFailure("p1", isRetryable: true);
        ClassicAssert.IsTrue(ledger.IsInCooldown("p1", out DateTime cdTime, out int failures));
        ClassicAssert.AreEqual(1, failures);
        ClassicAssert.IsTrue(cdTime > DateTime.UtcNow);

        ledger.RecordFailure("p1", isRetryable: true);
        ClassicAssert.IsTrue(ledger.IsInCooldown("p1", out cdTime, out failures));
        ClassicAssert.AreEqual(2, failures);

        // 非可重試失敗不進入冷卻或連續失敗計數
        var ledger2 = new RimLLMHealthLedger();
        ledger2.RecordFailure("p_nonretry", isRetryable: false);
        ClassicAssert.IsFalse(ledger2.IsInCooldown("p_nonretry"));

        // 第 3 次失敗觸發指數熔斷冷卻
        ledger.RecordFailure("p1", isRetryable: true);
        ClassicAssert.IsTrue(ledger.IsInCooldown("p1", out cdTime, out failures));
        ClassicAssert.AreEqual(3, failures);
        ClassicAssert.IsTrue(cdTime > DateTime.UtcNow);

        // 成功呼叫清除連續失敗與冷卻，並記錄延遲
        ledger.RecordSuccess("p1", 120);
        ledger.RecordSuccess("p1", 180);
        ClassicAssert.IsFalse(ledger.IsInCooldown("p1"));
        ClassicAssert.AreEqual(150f, ledger.GetAverageLatency("p1"));

        // 驗證超過 5 筆延遲的滾動平均
        for (int i = 0; i < 5; i++)
        {
            ledger.RecordSuccess("p_lat", 100);
        }
        ledger.RecordSuccess("p_lat", 200); // 應踢除第一筆 100
        ClassicAssert.AreEqual(120f, ledger.GetAverageLatency("p_lat"));

        // Clear 測試
        ledger.Clear();
        ClassicAssert.IsFalse(ledger.IsInCooldown("p1"));
        ClassicAssert.IsFalse(ledger.IsInCooldown("p2"));
        ClassicAssert.AreEqual(0f, ledger.GetAverageLatency("p1"));
    }

    [Test]
    public void TestSchemaBuilderCacheKeysAndEdgeCases()
    {
        // Null type
        Assert.Throws<ArgumentNullException>(() => RimLLMSchemaBuilder.Build(null));
        ClassicAssert.IsFalse(RimLLMSchemaBuilder.ContainsOpenEndedMap(null));

        var normalResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure));
        ClassicAssert.IsNotNull(normalResult);

        // Cache hit check
        var cachedResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure));
        ClassicAssert.AreSame(normalResult, cachedResult);
    }

    private class SimpleTestDataStructure
    {
        public string Name { get; set; }
        public int Value { get; set; }
    }



    [Test]
    public void TestEmbeddingServiceParameterValidation()
    {
        var mockSettings = new MockSettings
        {
            EmbeddingProvider = RimLLMEmbeddingService.DisabledProviderId
        };
        var service = new RimLLMEmbeddingService(mockSettings);

        // Empty text validation
        Assert.ThrowsAsync<ArgumentException>(async () => await service.ComputeEmbeddingAsync(""));
        Assert.ThrowsAsync<ArgumentException>(async () => await service.ComputeEmbeddingAsync(null));

        // Disabled provider validation
        Assert.ThrowsAsync<RimLLMException>(async () => await service.ComputeEmbeddingAsync("test text"));
        Assert.ThrowsAsync<RimLLMException>(async () => await service.FetchAvailableModelsAsync());

        // Unknown provider validation
        mockSettings.EmbeddingProvider = "InvalidProviderName";
        Assert.ThrowsAsync<RimLLMException>(async () => await service.ComputeEmbeddingAsync("test text"));
        Assert.ThrowsAsync<RimLLMException>(async () => await service.FetchAvailableModelsAsync());

        // Batch embedding validation
        Assert.ThrowsAsync<ArgumentNullException>(async () => await service.ComputeEmbeddingsAsync(null));
        var emptyBatch = service.ComputeEmbeddingsAsync(new List<string>()).GetAwaiter().GetResult();
        ClassicAssert.AreEqual(0, emptyBatch.Count);
    }

    [Test]
    public void TestEncryptionUtilityEdgeCasesAndTampering()
    {
        ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Encrypt(null));
        ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Encrypt(""));
        ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Decrypt(null));
        ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Decrypt(""));
        const string plain = "secret-api-key-value-12345";
        string encrypted = EncryptionUtility.Encrypt(plain);
        ClassicAssert.IsTrue(encrypted.StartsWith("v3:"));
        ClassicAssert.AreEqual(plain, EncryptionUtility.Decrypt(encrypted));
        byte[] payload = Convert.FromBase64String(encrypted[3..]);
        payload[20] ^= 1;
        ClassicAssert.IsNull(EncryptionUtility.Decrypt("v3:" + Convert.ToBase64String(payload)));
        ClassicAssert.IsNull(EncryptionUtility.Decrypt("v3:corrupted-base64!"));
    }

    [Test]
    public void TestRequestQueueRejectsAlreadyCanceledToken()
    {
        var settings = new MockSettings { MaxConcurrentRequests = 2 };
        var queue = new RimLLMRequestQueue(settings);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await queue.AcquireSlotAsync(5, cts.Token);
        });
    }

    [Test]
    public void TestRequestQueueKeepsFifoWithinSamePriority()
    {
        // 同優先級、同一個時鐘刻度內入列的請求先前以 DateTime 判先後，鍵值相等時
        // BinarySearch 會把後到的插到前面，變成後進先出。改用入列序號後必須嚴格 FIFO。
        var settings = new MockSettings { MaxConcurrentRequests = 1 };
        var queue = new RimLLMRequestQueue(settings);

        IDisposable holder = queue.AcquireSlotAsync(0, CancellationToken.None).GetAwaiter().GetResult();
        var waiters = new List<Task<IDisposable>>();
        for (int i = 0; i < 5; i++)
        {
            waiters.Add(queue.AcquireSlotAsync(0, CancellationToken.None));
        }
        // 高優先級最後入列，仍要排在所有同級項目之前。
        Task<IDisposable> urgent = queue.AcquireSlotAsync(10, CancellationToken.None);

        holder.Dispose();
        ClassicAssert.IsTrue(urgent.Wait(2000), "高優先級應最先取得名額");
        ClassicAssert.IsFalse(waiters.Exists(w => w.IsCompleted), "名額只有一個，同級項目此時都還在等");
        urgent.Result.Dispose();
        for (int i = 0; i < 5; i++)
        {
            int granted = Task.WaitAny(waiters.ToArray(), 2000);
            ClassicAssert.AreEqual(i, granted, "同優先級必須依入列順序取得名額");
            waiters[granted].Result.Dispose();
            waiters[granted] = new TaskCompletionSource<IDisposable>().Task; // 已處理者換成永不完成的佔位
        }
    }

    [Test]
    [NonParallelizable]
    public void TestDispatcherQueueBudgetAndErrorHandling()
    {
        RimLLMDispatcher.ResetQueueForTests();

        // TryEnqueueBounded with null
        ClassicAssert.IsFalse(RimLLMDispatcher.TryEnqueueBounded(null));

        // DrainWithBudget error recovery
        int executionCount = 0;
        RimLLMDispatcher.TryEnqueueBounded(() =>
        {
            executionCount++;
            throw new Exception("Simulated main thread dispatch exception");
        });

        RimLLMDispatcher.TryEnqueueBounded(() =>
        {
            executionCount++;
        });

        int processed = RimLLMDispatcher.DrainWithBudget(10, 100);
        ClassicAssert.AreEqual(2, processed);
        ClassicAssert.AreEqual(2, executionCount);

        RimLLMDispatcher.ResetQueueForTests();
        ClassicAssert.AreEqual(0, RimLLMDispatcher.QueuedCount);
    }

    [Test]
    public void TestChatClientAndEmbeddingClientFacades()
    {
        var manager = new RimLLMManager(new MockSettings());
        IChatClient chatClient = manager.CreateChatClient("test.mod");
        var metadata = (ChatClientMetadata)chatClient.GetService(typeof(ChatClientMetadata));
        ClassicAssert.IsNotNull(metadata);
        ClassicAssert.AreEqual("RimLLM", metadata.ProviderName);
        ClassicAssert.IsNull(chatClient.GetService(typeof(string)));
        chatClient.Dispose();

        var embeddingClient = new RimLLMEmbeddingClient(manager, "test.mod");
        ClassicAssert.IsNull(embeddingClient.GetService(typeof(string)));
        Assert.ThrowsAsync<ArgumentNullException>(async () => await embeddingClient.GenerateAsync(null));
        embeddingClient.Dispose();
    }

    [Test]
    public void TestChatClientExecutorNullMessagesHandling()
    {
        var cachedContextOptions = new RimLLMChatOptions { CachedContext = "System instruction" };
        var messagesWithSys = RimLLMChatClientExecutor.BuildMessages(null, cachedContextOptions);

        ClassicAssert.IsNotNull(messagesWithSys);
        ClassicAssert.AreEqual(1, messagesWithSys.Count);
        ClassicAssert.AreEqual(ChatRole.System, messagesWithSys[0].Role);
        ClassicAssert.AreEqual("System instruction", messagesWithSys[0].Text);

        var messagesEmpty = RimLLMChatClientExecutor.BuildMessages(null, null);

        ClassicAssert.IsNotNull(messagesEmpty);
        ClassicAssert.AreEqual(1, messagesEmpty.Count);
        ClassicAssert.AreEqual(ChatRole.User, messagesEmpty[0].Role);
    }

    [Test]
    public async Task TestLLMProviderExtensions()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () => await LLMProviderExtensions.GenerateAsync(null, new List<ChatMessage>(), null, "m"));
        Assert.ThrowsAsync<ArgumentNullException>(async () => await LLMProviderExtensions.StreamAsync(null, new List<ChatMessage>(), null, "m", _ => { }));

        var mock = new MockTestProvider
        {
            ProviderId = "ExtMock",
            GenerateHandler = (msgs, opts, m) => Task.FromResult("generated result")
        };

        string res = await mock.GenerateAsync(new List<ChatMessage> { new(ChatRole.User, "hi") }, null, "m1");
        ClassicAssert.AreEqual("generated result", res);

        var chunks = new List<string>();
        await mock.StreamAsync(new List<ChatMessage> { new(ChatRole.User, "hi") }, null, "m1", c => chunks.Add(c));
        ClassicAssert.IsTrue(chunks.Count > 0);
    }

    [Test]
    public async Task TestGeminiChatClientCoverage()
    {
        var settings = new MockSettings();
        settings.ApiKeys["Gemini"] = "test-key";
        var provider = new TestGeminiProvider(settings);
        using IChatClient client = provider.CreateChatClient("gemini-1.5-flash");
        ClassicAssert.IsNotNull(client.GetService(typeof(ChatClientMetadata)));
        ClassicAssert.IsNull(client.GetService(typeof(string)));

        var resp = await client.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hello") });
        ClassicAssert.IsNotNull(resp);

        // 串流需以 SSE 格式回應，這是 OpenAI wire 協定的既有測試寫法。
        provider.WireHandler.ResponseContentType = "text/event-stream";
        provider.WireHandler.ResponseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";
        var streamed = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hello") }))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                streamed.Add(update.Text);
            }
        }
        ClassicAssert.IsTrue(streamed.Count > 0);

        // 測試重複釋放串流列舉器時安全忽略 ObjectDisposedException
        var streamEnumerator = client.GetStreamingResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hello") }).GetAsyncEnumerator();
        await streamEnumerator.MoveNextAsync();
        await streamEnumerator.DisposeAsync();
        await streamEnumerator.DisposeAsync();
    }

    [Test]
    public async Task TestFallbackPipelineFullCoverage()
    {
        var settings = new MockSettings();
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);

        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        var pipeline = new RimLLMFallbackPipeline(
            settings,
            ledger,
            tracker,
            id => providers.TryGetValue(id, out var p) ? p : null,
            id => settings.EnabledProviders.TryGetValue(id, out bool enabled) && enabled);

        // 1. 空鏈例外
        settings.FallbackChain = [];
        Assert.Throws<RimLLMException>(() => pipeline.ResolveCandidates(null, null));

        // 2. 無符合資格 Provider 例外
        settings.FallbackChain = ["NonExistent:m1"];
        Assert.Throws<RimLLMException>(() => pipeline.ResolveCandidates(null, null));

        // 3. MinLatency 路由策略 (Strategy = 1)
        var p1 = new MockTestProvider { ProviderId = "P1" };
        var p2 = new MockTestProvider { ProviderId = "P2" };
        providers["P1"] = p1;
        providers["P2"] = p2;
        settings.EnabledProviders["P1"] = true;
        settings.EnabledProviders["P2"] = true;
        settings.ApiKeys["P1"] = "k1";
        settings.ApiKeys["P2"] = "k2";
        settings.FallbackChain = ["P1:m1", "P2:m2"];
        settings.RoutingStrategy = 1; // MinLatency

        // 設定 P1 延遲 300ms, P2 延遲 50ms。
        // 健康帳本以「供應商:模型」為鍵，因此這裡要對應 fallback chain 的完整條目。
        ledger.RecordSuccess("P1:m1", 300);
        ledger.RecordSuccess("P2:m2", 50);

        ClassicAssert.AreEqual("P2", pipeline.ResolveCandidates(null, null)[0].ProviderId); // P2 延遲較低應排在前面

        // 4. PreferredModelId 優先插入
        settings.RoutingStrategy = 0; // Priority
        var preferred = pipeline.ResolveCandidates("P1:preferred-m", null)[0];
        ClassicAssert.AreEqual("P1:preferred-m", $"{preferred.ProviderId}:{preferred.ModelName}");

        // 5. MinFallbackLevel 分級過濾與 API 價格分級
        ClassicAssert.AreEqual(3, tracker.GetModelLevel("openai", "gpt-4o")); // Completion $10.00 >= $3.00 -> High (3)
        ClassicAssert.AreEqual(2, tracker.GetModelLevel("openai", "gpt-4o-mini")); // Completion $0.60 >= $0.50 -> Medium (2)
        ClassicAssert.AreEqual(1, tracker.GetModelLevel("gemini", "gemini-2.0-flash-lite")); // Completion $0.30 < $0.50 -> Low (1)
        ClassicAssert.AreEqual(1, tracker.GetModelLevel("deepseek", "deepseek-chat")); // Completion $0.28 < $0.50 -> Low (1)
        ClassicAssert.AreEqual(1, tracker.GetModelLevel("openai-compatible", "local-llama")); // 本地免費 -> Low (1)

        settings.ModelLevelOverrides["P2:pro-model"] = 3;
        settings.FallbackChain = ["P1:mini-model", "P2:pro-model"]; // P1 預設為 tier 2, P2 覆寫為 tier 3
        var tiered = pipeline.ResolveCandidates(null, "high"); // 等級 3
        ClassicAssert.AreEqual(1, tiered.Count); // P1 (tier 2) 被過濾掉
        ClassicAssert.AreEqual("P2", tiered[0].ProviderId);

        // 6. RoundRobin 路由策略 (Strategy = 2)
        settings.RoutingStrategy = 2;
        ClassicAssert.AreEqual(2, pipeline.ResolveCandidates(null, "low").Count);

        // 7. 非可重試例外直接跳往備援
        settings.RoutingStrategy = 0;
        settings.FallbackChain = ["P1:m1", "P2:m2"];
        settings.MaxRetries = 2;
        int p1Attempts = 0;
        int p2Attempts = 0;
        p1.GenerateHandler = (msgs, opts, m) =>
        {
            p1Attempts++;
            throw new ArgumentException("Invalid arguments - non-retryable");
        };
        p2.GenerateHandler = (msgs, opts, m) =>
        {
            p2Attempts++;
            return Task.FromResult("fallback-after-non-retry");
        };
        var router = new RimLLMFailoverChatClient(settings, ledger, tracker, pipeline, "test.pipe");
        await router.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hi") });
        ClassicAssert.AreEqual(1, p1Attempts); // 非可重試不重試，直接中斷給下一個
        ClassicAssert.AreEqual(1, p2Attempts);

        // 8. ClearCooldowns 會清空健康帳本
        ledger.RecordSuccess("P1:m1", 99);
        ledger.RecordFailure("P1:m1", isRetryable: true);
        ClassicAssert.IsTrue(ledger.IsInCooldown("P1:m1"));

        pipeline.ClearCooldowns();
        ClassicAssert.IsFalse(ledger.IsInCooldown("P1:m1"));
        ClassicAssert.AreEqual(0f, ledger.GetAverageLatency("P1:m1"));
    }

    [Test]
    public async Task TestChatExecutionPipelineCoverage()
    {
        var settings = new MockSettings();
        var manager = new RimLLMManager(settings);

        // 2. WarnOnly when daily budget is exceeded (BudgetPolicy = 1)
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
        settings.DailyTokenBudgetLimit = 100000;
        settings.DailyAccumulatedTokens = 200000;
        settings.BudgetPolicy = 1; // WarnOnly

        // 預算檢查上移為中介層；WarnOnly 只警告，請求必須原樣送到下游。
        // 此處只測預算行為，關掉節流避免視窗計數干擾。
        settings.EnableAntiAbuse = false;
        var warnOnlyClient = new RimLLMGuardChatClient(
            new MockCustomChatClient(), settings, new RimLLMThrottleStore(settings),
            "warn-only-mod", new RimLLMUsageTracker(settings));
        var mockMessages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        ChatResponse passThrough = await warnOnlyClient.GetResponseAsync(mockMessages);
        ClassicAssert.AreEqual("mock-text", passThrough?.Text, "WarnOnly 必須放行到下游，不得取代回應");

        string streamedText = "";
        var streamEnumerator = warnOnlyClient.GetStreamingResponseAsync(mockMessages).GetAsyncEnumerator();
        try
        {
            while (streamEnumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                streamedText += streamEnumerator.Current.Text;
            }
        }
        finally
        {
            streamEnumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        ClassicAssert.AreEqual("mock-stream-text", streamedText, "串流路徑同樣不該被預算攔截");

        // 3. Structured request under WarnOnly still reaches the inner client
        settings.DailyAccumulatedTokens = 200000;
        var structuredOptions = new RimLLMChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RimLLMChatOptions.ResponseTypeKey] = typeof(NullableTestDataStructure)
            }
        };
        ChatResponse objRes = await warnOnlyClient.GetResponseAsync(mockMessages, structuredOptions);
        ClassicAssert.AreEqual("mock-text", objRes?.Text);

        // 4. JSON Repair disabled throws RimLLMException
        settings.EnableJsonRepair = false;
        Assert.Throws<RimLLMException>(() => RimLLMJsonHelper.DeserializeStructured<NullableTestDataStructure>("invalid json", settings));

        // 5. Static JSON repair fallback
        settings.EnableJsonRepair = true;
        string markdownJson = "```json\n{\"Name\":\"repaired-str\",\"OptionalCount\":42}\n```";
        var parsed = RimLLMJsonHelper.DeserializeStructured<NullableTestDataStructure>(markdownJson, settings);
        ClassicAssert.AreEqual("repaired-str", parsed.Name);
        ClassicAssert.AreEqual(42, parsed.OptionalCount);

        // 6. Anti-abuse rate limiting trigger & cooldown
        // 節流狀態已從 pipeline 抽到共用的 RimLLMThrottleStore（由防濫用中介層使用）。
        settings.EnableAntiAbuse = true;
        settings.MaxRequestsPerWindow = 3;
        settings.ThrottlingWindowSeconds = 60;
        settings.CoolDownDurationSeconds = 60;
        var throttleStore = new RimLLMThrottleStore(settings);
        throttleStore.CheckAntiAbuse("test-abuse-mod");
        throttleStore.CheckAntiAbuse("test-abuse-mod");
        throttleStore.CheckAntiAbuse("test-abuse-mod");
        // 第 4 次觸發限流
        Assert.Throws<RimLLMException>(() => throttleStore.CheckAntiAbuse("test-abuse-mod"));
        // 冷卻中再次呼叫也拋出限流
        Assert.Throws<RimLLMException>(() => throttleStore.CheckAntiAbuse("test-abuse-mod"));
        throttleStore.ClearCooldowns();

        // 即使呼叫端輪換 modId，也必須受到共享安全上限保護。
        settings.MaxRequestsPerWindow = 1;
        var rotatingIdStore = new RimLLMThrottleStore(settings);
        for (int i = 0; i < 10; i++)
        {
            rotatingIdStore.CheckAntiAbuse("rotating-mod-" + i);
        }
        Assert.Throws<RimLLMException>(
            () => rotatingIdStore.CheckAntiAbuse("rotating-mod-overflow"));
        rotatingIdStore.ClearCooldowns();

        // 工具續輪不增加 per-Mod 視窗，但每個實際呼叫仍須消耗共享安全上限。
        var toolLoopStore = new RimLLMThrottleStore(settings);
        for (int i = 0; i < 10; i++)
        {
            toolLoopStore.CheckAntiAbuse("tool-loop-mod", countTowardWindow: false);
        }
        Assert.Throws<RimLLMException>(
            () => toolLoopStore.CheckAntiAbuse("tool-loop-mod", countTowardWindow: false));
        toolLoopStore.ClearCooldowns();

        // 7. HardBlock 預算政策 (BudgetPolicy = 0)：改由守衛中介層負責攔阻。
        // 此處只測預算行為，關掉節流避免視窗計數干擾。
        settings.EnableAntiAbuse = false;
        settings.BudgetPolicy = 0;
        settings.DailyTokenBudgetLimit = 100000;
        settings.DailyAccumulatedTokens = 200000;
        var hardBlockClient = new RimLLMGuardChatClient(
            new MockCustomChatClient(), settings, new RimLLMThrottleStore(settings),
            "hard-block-mod", new RimLLMUsageTracker(settings));
        Assert.ThrowsAsync<RimLLMException>(
            async () => await hardBlockClient.GetResponseAsync(mockMessages));

        // 8. 驗證空物件與結構化必填驗證
        Assert.Throws<InvalidOperationException>(() => RimLLMJsonHelper.DeserializeAndValidate<NullableTestDataStructure>("null"));
    }

    [Test]
    public async Task TestNativeSchemaRejectionAndDoubleRepair()
    {
        var settings = new MockSettings();
        var manager = new RimLLMManager(settings);

        // 1. 模擬原生 Schema 被 400 拒絕時自動降級重試
        int callCount = 0;
        var rejectingProvider = new MockTestProvider
        {
            ProviderId = "SchemaRejectMock",
            GenerateHandler = (msgs, opts, m) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "400 Unrecognized parameter: response_format / schema is not supported");
                }
                return Task.FromResult("{\"Name\":\"fallback-success\"}");
            }
        };
        rejectingProvider.Capabilities.SupportsNativeStructuredOutput = true;
        settings.EnableNativeSchema = true;
        settings.EnabledProviders["SchemaRejectMock"] = true;
        settings.ApiKeys["SchemaRejectMock"] = "test-key";
        settings.FallbackChain = ["SchemaRejectMock:m1"];
        manager.RegisterProvider(rejectingProvider);

        IChatClient schemaClient = manager.CreateChatClient("test.mod");
        var structuredOptions = new RimLLMChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RimLLMChatOptions.ResponseTypeKey] = typeof(NullableTestDataStructure)
            }
        };
        ChatResponse res = await schemaClient.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, structuredOptions);
        ClassicAssert.IsNotNull(res);
        ClassicAssert.IsTrue(res.Text.Contains("fallback-success"));

        // 2. 結構化修復失敗時拋出 RimLLMException
        Assert.Throws<RimLLMException>(() => RimLLMJsonHelper.DeserializeStructured<NullableTestDataStructure>("totally broken no json anywhere", settings));

        // 4. 結構化欄位驗證 (Field 必填為 null 拋出 InvalidOperationException)
        Assert.Throws<InvalidOperationException>(() => RimLLMJsonHelper.DeserializeAndValidate<TestStructureWithRequiredField>("{\"RequiredField\":null}"));
        Assert.Throws<InvalidOperationException>(() => RimLLMJsonHelper.DeserializeAndValidate<List<TestStructureWithRequiredField>>("[{\"RequiredField\":null}]"));

    }

    /// <summary>
    /// 服務端持續拒絕原生 schema 時，降級重打只能發生一次且必須改走提示式路徑。
    /// 先前的 catch 遞迴呼叫 GenerateAsync 本身，第二次仍送原生 schema，只要服務端一直拒絕就會無限重打；
    /// 上面的 TestNativeSchemaRejectionAndDoubleRepair 因 mock 第二次無條件成功而看不出來。
    /// </summary>
    [Test]
    public async Task TestPersistentNativeSchemaRejection_FallsBackToPromptPathExactlyOnce()
    {
        var settings = new MockSettings { EnableNativeSchema = true };
        var manager = new RimLLMManager(settings);

        int nativeCalls = 0;
        int promptCalls = 0;
        var alwaysRejecting = new MockTestProvider
        {
            ProviderId = "SchemaAlwaysReject",
            GenerateHandler = (msgs, opts, m) =>
            {
                // executor 只有在走原生 schema 時才會設定 ResponseFormat。
                if (opts?.ResponseFormat != null)
                {
                    nativeCalls++;
                    throw new RimLLMException(LLMError.InvalidResponse, "400 response_format json_schema is not supported")
                    {
                        IsSchemaRejection = true
                    };
                }
                promptCalls++;
                return Task.FromResult("{\"Name\":\"prompt-path\"}");
            }
        };
        alwaysRejecting.Capabilities.SupportsNativeStructuredOutput = true;
        settings.EnabledProviders["SchemaAlwaysReject"] = true;
        settings.ApiKeys["SchemaAlwaysReject"] = "test-key";
        settings.FallbackChain = ["SchemaAlwaysReject:m1"];
        manager.RegisterProvider(alwaysRejecting);

        IChatClient client = manager.CreateChatClient("test.mod");
        var options = new RimLLMChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RimLLMChatOptions.ResponseTypeKey] = typeof(NullableTestDataStructure)
            }
        };
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            ChatResponse res = await client.GetResponseAsync(
                new List<ChatMessage> { new(ChatRole.User, "hi") }, options, cts.Token);
            StringAssert.Contains("prompt-path", res.Text);
        }

        ClassicAssert.AreEqual(1, nativeCalls, "原生 schema 只能嘗試一次");
        ClassicAssert.AreEqual(1, promptCalls, "被拒後只能以提示式路徑重打一次");
    }

    /// <summary>
    /// 前綴比對必須停在版本邊界：先前 "gpt-4"（$30/$60）會吃到 "gpt-4.1-mini"（$0.40/$1.60），
    /// 成本高估 75 倍；日期後綴等以 "-" 接續的變體仍要能命中。
    /// </summary>
    [Test]
    public void TestModelRatePrefixMatchStopsAtVersionBoundary()
    {
        var tracker = new RimLLMUsageTracker(new MockSettings());

        float mini = tracker.EstimateCost("openai", "gpt-4.1-mini", 1000000, 1000000, 0);
        ClassicAssert.AreEqual(2.00f, mini, 0.01f, "gpt-4.1-mini 必須以自己的費率計算，不能落到 gpt-4");

        float datedMini = tracker.EstimateCost("openai", "gpt-4.1-mini-2025-04-14", 1000000, 1000000, 0);
        ClassicAssert.AreEqual(mini, datedMini, 0.001f, "日期後綴以 - 接續，仍屬同一費率");

        float gpt4 = tracker.EstimateCost("openai", "gpt-4-0613", 1000000, 1000000, 0);
        ClassicAssert.AreEqual(90.00f, gpt4, 0.01f, "gpt-4 自己的版本後綴照常命中");

        ClassicAssert.AreEqual(0f, tracker.EstimateCost("openai", "gpt-40", 1000000, 1000000, 0),
            "前綴後接數字不是版本邊界，不得命中 gpt-4");
    }

    [Test]
    public async Task TestBudgetAndTelemetryAccountingDeepening()
    {
        var settings = new MockSettings();
        var tracker = new RimLLMUsageTracker(settings);

        // 1. 定價精準匹配與前綴模糊匹配
        float exactCost = tracker.EstimateCost("openai", "gpt-4o", 1000000, 1000000, 0);
        ClassicAssert.AreEqual(12.50f, exactCost, 0.01f);

        float prefixCost = tracker.EstimateCost("openai", "gpt-4o-2024-11-20", 1000000, 1000000, 0);
        ClassicAssert.AreEqual(12.50f, prefixCost, 0.01f);

        float geminiPrefixCost = tracker.EstimateCost("gemini", "gemini-2.0-flash-001", 1000000, 1000000, 0);
        ClassicAssert.AreEqual(0.50f, geminiPrefixCost, 0.01f);

        // 2. 快取折扣計算
        float fullPromptCost = tracker.EstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 0);
        float cachedPromptCost = tracker.EstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 1000000);
        ClassicAssert.IsTrue(cachedPromptCost < fullPromptCost);
        ClassicAssert.AreEqual(fullPromptCost * 0.25f, cachedPromptCost, 0.001f);

        // 3. 未知模型回傳 0f
        float unknownCost = tracker.EstimateCost("unknown-prov", "unknown-model", 1000, 1000, 0);
        ClassicAssert.AreEqual(0f, unknownCost);

        // 4. 預算政策審查（token 制）
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
        settings.DailyTokenBudgetLimit = 100000;
        settings.DailyAccumulatedTokens = 200000;

        // Policy 0 = HardBlock
        settings.BudgetPolicy = 0;
        bool ok = tracker.CheckBudgetLimit();
        ClassicAssert.IsFalse(ok);

        // Policy 1 = WarnOnly：超限只警告，請求照樣放行
        settings.BudgetPolicy = 1;
        ok = tracker.CheckBudgetLimit();
        ClassicAssert.IsTrue(ok);

        // 5. WarnOnly 的警告去重：同一天只吵一次，換日後才重新允許
        var warnTracker = new RimLLMUsageTracker(settings);
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
        ClassicAssert.IsTrue(warnTracker.ShouldWarnBudgetExceeded(), "當日第一次超限必須警告");
        ClassicAssert.IsFalse(warnTracker.ShouldWarnBudgetExceeded(), "同一天不得重複警告");

        // 當日手動重置只會把基準日重寫成同一個字串，因此不會重新允許警告
        ClassicAssert.IsFalse(warnTracker.ShouldWarnBudgetExceeded(), "當日手動重置後仍不得重複警告");

        // 換日後重新允許
        settings.DailyBudgetResetDate = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        ClassicAssert.IsTrue(warnTracker.ShouldWarnBudgetExceeded(), "換日後須重新允許警告");

        // 6. 跨天重置
        settings.DailyBudgetResetDate = "2000-01-01";
        tracker.CheckDailyReset();
        ClassicAssert.AreEqual(0f, settings.DailyAccumulatedCost);
        ClassicAssert.AreEqual(0, settings.DailyAccumulatedTokens);
        ClassicAssert.AreEqual(DateTime.Today.ToString("yyyy-MM-dd"), settings.DailyBudgetResetDate);
    }

    [Test]
    public void TestGuardChatClientIsToolLoopContinuationDetailed()
    {
        // Null / empty cases
        ClassicAssert.IsFalse(RimLLMGuardChatClient.IsToolLoopContinuation(null));
        ClassicAssert.IsFalse(RimLLMGuardChatClient.IsToolLoopContinuation(new List<ChatMessage>()));

        // Message with null contents
        var msgNullContents = new ChatMessage(ChatRole.User, (IList<AIContent>)null);
        ClassicAssert.IsFalse(RimLLMGuardChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgNullContents }));

        // Message with empty contents
        var msgEmptyContents = new ChatMessage(ChatRole.User, []);
        ClassicAssert.IsFalse(RimLLMGuardChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgEmptyContents }));

        // Message with non-tool contents (TextContent)
        var msgTextOnly = new ChatMessage(ChatRole.User, [new TextContent("Hello")]);
        ClassicAssert.IsFalse(RimLLMGuardChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgTextOnly }));

        // Message with FunctionResultContent
        var msgToolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call1", "result")]);
        ClassicAssert.IsTrue(RimLLMGuardChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgTextOnly, msgToolResult }));

        // Last message is not tool result even if previous was
        ClassicAssert.IsFalse(RimLLMGuardChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgToolResult, msgTextOnly }));
    }

    [Test]
    public void TestChatEntryMetaDeserializeEdgeCases()
    {
        // Null or empty
        ClassicAssert.IsNull(ChatTestDrawer.ChatEntryMeta.Deserialize(null));
        ClassicAssert.IsNull(ChatTestDrawer.ChatEntryMeta.Deserialize(""));

        // Malformed entry with no equals
        var metaNoEquals = ChatTestDrawer.ChatEntryMeta.Deserialize("plain_text|no_equals");
        ClassicAssert.IsNotNull(metaNoEquals);
        ClassicAssert.AreEqual("", metaNoEquals.ModelId);

        // Empty key or value
        var metaEmptyKeyVal = ChatTestDrawer.ChatEntryMeta.Deserialize("=val|key=");
        ClassicAssert.IsNotNull(metaEmptyKeyVal);

        // Fully populated with unknown keys and invalid numbers
        string raw = "unknown=ignored|model=test-model|ms=invalid|tok=50|p=20|c=30|est=1";
        var meta = ChatTestDrawer.ChatEntryMeta.Deserialize(raw);
        ClassicAssert.IsNotNull(meta);
        ClassicAssert.AreEqual("test-model", meta.ModelId);
        ClassicAssert.AreEqual(0, meta.ElapsedMs); // "invalid" defaults to 0
        ClassicAssert.AreEqual(50, meta.TotalTokens);
        ClassicAssert.AreEqual(20, meta.PromptTokens);
        ClassicAssert.AreEqual(30, meta.CompletionTokens);
        ClassicAssert.IsTrue(meta.IsEstimatedTokens);

        // Round-trip serialize
        string serialized = meta.Serialize();
        ClassicAssert.IsTrue(serialized.Contains("model=test-model"));
        ClassicAssert.IsTrue(serialized.Contains("est=1"));
    }

    [Test]
    public void TestFallbackPipelinePrependPreferredModelEdgeCases()
    {
        var settings = new MockSettings();
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        var pipeline = new RimLLMFallbackPipeline(
            settings,
            ledger,
            tracker,
            id => providers.TryGetValue(id, out var p) ? p : null,
            id => settings.EnabledProviders.TryGetValue(id, out bool enabled) && enabled);

        var p1 = new MockTestProvider { ProviderId = "P1" };
        providers["P1"] = p1;
        settings.EnabledProviders["P1"] = true;
        settings.ApiKeys["P1"] = "k1";
        settings.FallbackChain = ["P1:default-model"];

        // Null or empty preferredModelId
        var res = pipeline.ResolveCandidates(null, null);
        ClassicAssert.AreEqual("default-model", res[0].ModelName);

        res = pipeline.ResolveCandidates("", null);
        ClassicAssert.AreEqual("default-model", res[0].ModelName);

        // Preferred model without colon
        res = pipeline.ResolveCandidates("invalidFormat", null);
        ClassicAssert.AreEqual("default-model", res[0].ModelName);

        // Preferred model already in chain at index 0
        res = pipeline.ResolveCandidates("P1:default-model", null);
        ClassicAssert.AreEqual(1, res.Count);
        ClassicAssert.AreEqual("default-model", res[0].ModelName);

        // Preferred model with disabled provider
        settings.EnabledProviders["P2"] = false;
        res = pipeline.ResolveCandidates("P2:some-model", null);
        ClassicAssert.AreEqual("default-model", res[0].ModelName);
    }

    private static RimLLMFallbackPipeline CreateThreeModelPipeline(MockSettings settings)
    {
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in new[] { "P1", "P2", "P3" })
        {
            providers[id] = new MockTestProvider { ProviderId = id };
            settings.EnabledProviders[id] = true;
            settings.ApiKeys[id] = "k-" + id;
        }
        settings.FallbackChain = ["P1:m1", "P2:m2", "P3:m3"];

        return new RimLLMFallbackPipeline(
            settings,
            new RimLLMHealthLedger(),
            new RimLLMUsageTracker(settings),
            id => providers.TryGetValue(id, out var p) ? p : null,
            id => settings.EnabledProviders.TryGetValue(id, out bool enabled) && enabled);
    }

    [Test]
    public void TestPreferredModelAlreadyInChainMovesToFront()
    {
        var settings = new MockSettings();
        var pipeline = CreateThreeModelPipeline(settings);

        var res = pipeline.ResolveCandidates("P3:M3", null);

        ClassicAssert.AreEqual(3, res.Count);
        ClassicAssert.AreEqual("P3", res[0].ProviderId);
        // 移動的是鏈上既有條目，模型名稱沿用使用者設定的大小寫
        ClassicAssert.AreEqual("m3", res[0].ModelName);
        ClassicAssert.AreEqual("P1", res[1].ProviderId);
        ClassicAssert.AreEqual("P2", res[2].ProviderId);
    }

    [Test]
    public void TestPreferredModelStaysFirstUnderEveryRoutingStrategy()
    {
        var settings = new MockSettings();
        var pipeline = CreateThreeModelPipeline(settings);
        // P2 最貴、P1 最便宜：LowestCost 排序若不固定選定模型，P2 會被排到最後
        settings.ModelLevelOverrides["m1"] = 1;
        settings.ModelLevelOverrides["m2"] = 3;
        settings.ModelLevelOverrides["m3"] = 2;

        foreach (int strategy in new[] { 0, 1, 2, 3 })
        {
            settings.RoutingStrategy = strategy;
            for (int i = 0; i < 20; i++)
            {
                var res = pipeline.ResolveCandidates("P2:m2", null);
                ClassicAssert.AreEqual(3, res.Count, $"strategy {strategy}");
                ClassicAssert.AreEqual("P2", res[0].ProviderId, $"strategy {strategy}");
            }
        }
    }

    [Test]
    public void TestFallbackAliasResolvesWholeChainWithoutPinning()
    {
        var settings = new MockSettings();
        var pipeline = CreateThreeModelPipeline(settings);
        settings.RoutingStrategy = 0; // PriorityFailover：保留鏈序，別名不得釘選打亂

        foreach (string alias in new[] { "fallback", "Fallback", "FALLBACK" })
        {
            var res = pipeline.ResolveCandidates(alias, null);
            ClassicAssert.AreEqual(3, res.Count, $"alias {alias}");
            ClassicAssert.AreEqual("P1", res[0].ProviderId, $"alias {alias}");
            ClassicAssert.AreEqual("P2", res[1].ProviderId, $"alias {alias}");
            ClassicAssert.AreEqual("P3", res[2].ProviderId, $"alias {alias}");
        }
    }

    [Test]
    public void TestGetFallbackChainReturnsDefensiveCopy()
    {
        var settings = new MockSettings { FallbackChain = ["P1:m1", "P2:m2"] };
        RimLLMProvider.Initialize(new RimLLMManager(settings));

        var chain = RimLLMProvider.GetFallbackChain();
        CollectionAssert.AreEqual(new[] { "P1:m1", "P2:m2" }, chain);

        chain.Clear();
        CollectionAssert.AreEqual(new[] { "P1:m1", "P2:m2" }, RimLLMProvider.GetFallbackChain());
    }

#pragma warning disable CS0649
    private class TestStructureWithRequiredField
    {
        public string RequiredField;
    }
#pragma warning restore CS0649
}
