using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 針對健康帳本粒度、重試退避、成本路由與新增錯誤碼的行為測試。
/// </summary>
[TestFixture]
public class OptimizationTests
{
    private static RimLLMFallbackPipeline BuildPipeline(
        MockSettings settings,
        RimLLMHealthLedger ledger,
        RimLLMUsageTracker tracker,
        Dictionary<string, ILLMProvider> providers)
    {
        return new RimLLMFallbackPipeline(
            settings,
            ledger,
            tracker,
            id => providers.TryGetValue(id, out var provider) ? provider : null,
            id => settings.EnabledProviders.TryGetValue(id, out bool enabled) && enabled);
    }

    private static RimLLMFailoverChatClient BuildRouter(
        MockSettings settings,
        RimLLMHealthLedger ledger,
        RimLLMUsageTracker tracker,
        Dictionary<string, ILLMProvider> providers)
    {
        return new RimLLMFailoverChatClient(
            settings, ledger, tracker,
            BuildPipeline(settings, ledger, tracker, providers),
            "test.optimization");
    }

    private static void RegisterProvider(
        MockSettings settings,
        Dictionary<string, ILLMProvider> providers,
        MockTestProvider provider)
    {
        providers[provider.ProviderId] = provider;
        settings.EnabledProviders[provider.ProviderId] = true;
        settings.ApiKeys[provider.ProviderId] = "key";
    }

    // ---------- 健康帳本粒度（供應商 + 模型） ----------

    [Test]
    public async Task CooldownIsolatesSiblingModelsOfTheSameProvider()
    {
        var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

        RegisterProvider(settings, providers, new MockTestProvider
        {
            ProviderId = "MultiModel",
            GenerateHandler = (msgs, opts, model) => model == "bad"
                ? throw new RimLLMException(LLMError.ProviderOffline, "boom")
                : Task.FromResult("ok")
        });
        settings.FallbackChain = ["MultiModel:bad", "MultiModel:good"];

        var router = BuildRouter(settings, ledger, tracker, providers);
        ChatResponse response = await router.GetResponseAsync(NewMessages());

        ClassicAssert.AreEqual("ok", response.Text);

        // 壞掉的模型進入冷卻，但同一個供應商底下健康的模型不受牽連。
        ClassicAssert.IsTrue(ledger.IsInCooldown("MultiModel:bad"));
        ClassicAssert.IsFalse(ledger.IsInCooldown("MultiModel:good"));
    }

    [Test]
    public void RetriesOfOneRequestRecordAtMostOneFailure()
    {
        var settings = new MockSettings { MaxRetries = 3, RetryDelay = 0f, RoutingStrategy = 0 };
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

        RegisterProvider(settings, providers, new MockTestProvider
        {
            ProviderId = "Flaky",
            GenerateHandler = (msgs, opts, model) => throw new RimLLMException(LLMError.NetworkError, "offline")
        });
        settings.FallbackChain = ["Flaky:m1"];

        var router = BuildRouter(settings, ledger, tracker, providers);

        Assert.ThrowsAsync<RimLLMException>(async () => await router.GetResponseAsync(NewMessages()));

        // 4 次嘗試（1 次 + 3 次重試）只能記成 1 次失敗，否則單一次網路抖動
        // 就會直接把目標推過熔斷門檻。
        ledger.IsInCooldown("Flaky:m1", out _, out int continuousFailures);
        ClassicAssert.AreEqual(1, continuousFailures);
    }

    // ---------- 成本優先路由 ----------

    [Test]
    public void LowestCostRoutingTriesTheCheaperModelFirst()
    {
        var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 3 };
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

        RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
        // 鏈的順序刻意把貴的放前面，證明是策略而非順序決定的。
        settings.FallbackChain = ["openai:gpt-4o", "openai:gpt-4o-mini"];

        var policy = BuildPipeline(settings, ledger, tracker, providers);

        var candidates = policy.ResolveCandidates(null, null);

        ClassicAssert.AreEqual("gpt-4o-mini", candidates[0].ModelName);
    }

    [Test]
    public void PriorityFailoverKeepsTheConfiguredChainOrder()
    {
        var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

        RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
        settings.FallbackChain = ["openai:gpt-4o", "openai:gpt-4o-mini"];

        var policy = BuildPipeline(settings, ledger, tracker, providers);

        var candidates = policy.ResolveCandidates(null, null);

        ClassicAssert.AreEqual("gpt-4o", candidates[0].ModelName);
    }

    // ---------- 新增的錯誤碼辨識 ----------

    [Test]
    public void ContextWindowAndContentPolicyRejectionsGetTheirOwnErrorCodes()
    {
        ClassicAssert.AreEqual(
            LLMError.ContextWindowExceeded,
            LLMErrorMapper.CreateException(400, "This model's maximum context length is 8192 tokens").Error);

        // 413 依定義就是酬載過大，不必比對訊息。
        ClassicAssert.AreEqual(
            LLMError.ContextWindowExceeded,
            LLMErrorMapper.CreateException(413, "Payload Too Large").Error);

        ClassicAssert.AreEqual(
            LLMError.ContentFilter,
            LLMErrorMapper.CreateException(400, "content_policy_violation: the prompt was rejected").Error);

        ClassicAssert.AreEqual(
            LLMError.ContentFilter,
            LLMErrorMapper.CreateException(400, "blocked by the safety filter").Error);
    }

    [Test]
    public void ParameterRejectionsStillMapToInvalidResponseSoTheRetryPathIsUnchanged()
    {
        RimLLMException schemaRejected =
            LLMErrorMapper.CreateException(400, "Invalid schema for response_format 'json_schema'");
        ClassicAssert.AreEqual(LLMError.InvalidResponse, schemaRejected.Error);
        ClassicAssert.IsTrue(schemaRejected.IsSchemaRejection);

        RimLLMException reasoningRejected =
            LLMErrorMapper.CreateException(400, "Unrecognized request argument: reasoning_effort");
        ClassicAssert.AreEqual(LLMError.InvalidResponse, reasoningRejected.Error);
        ClassicAssert.IsTrue(reasoningRejected.IsReasoningRejection);

        RimLLMException temperatureRejected =
            LLMErrorMapper.CreateException(400, "Unsupported value: 'temperature' does not support 0.7");
        ClassicAssert.AreEqual(LLMError.InvalidResponse, temperatureRejected.Error);
        ClassicAssert.IsTrue(temperatureRejected.IsTemperatureRejection);

        // 一般的請求組裝錯誤仍是 InvalidResponse
        ClassicAssert.AreEqual(
            LLMError.InvalidResponse,
            LLMErrorMapper.CreateException(400, "missing required field 'messages'").Error);
    }

    // ---------- 共用輔助 ----------

    private static List<ChatMessage> NewMessages(string prompt = "hello")
    {
        return
        [
            new(ChatRole.System, "sys"),
            new(ChatRole.User, prompt)
        ];
    }


    [Test]
    public void StreamingHoldsItsQueueSlotForTheWholeEnumeration()
    {
        // 這是佇列中介層最容易踩的坑：GetStreamingResponseAsync 只是「建立」一個
        // 延遲的 IAsyncEnumerable，若名額在它回傳時就釋放，串流請求等於完全不受
        // 併發上限限制，而且不會有任何錯誤徵兆。
        var settings = new MockSettings { MaxConcurrentRequests = 1 };
        var queue = new RimLLMRequestQueue(settings);
        var client = new RimLLMRequestQueueChatClient(
            new MockCustomChatClient
            {
                StreamHandler = (msgs, opts, onChunk) =>
                {
                    onChunk("chunk");
                    return Task.CompletedTask;
                }
            },
            queue);

        var enumerator = client.GetStreamingResponseAsync(NewMessages()).GetAsyncEnumerator();

        // 開始列舉即佔用唯一的名額。
        ClassicAssert.IsTrue(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult());

        Task<ChatResponse> blocked = client.GetResponseAsync(NewMessages());
        ClassicAssert.IsFalse(blocked.Wait(200), "串流仍在列舉中，第二個請求不該取得名額");

        // 名額直到列舉器被釋放才歸還——列舉結束本身不代表呼叫端已經用完。
        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) { }
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();

        ClassicAssert.IsTrue(blocked.Wait(5000), "名額歸還後第二個請求應該完成");
    }

    [Test]
    public void RetryBackoffDoesNotHoldTheConcurrencySlot()
    {
        // 名額若疊在路由外層，請求 A 在指數退避期間會一直占著唯一的名額，
        // 與 A 無關的請求 B 只能排隊等 A 重試完。名額改為逐候選嘗試持有後，
        // B 必須能在 A 的退避空窗內完成。
        var settings = new MockSettings
        {
            FallbackChain = ["Flaky:m"],
            MaxConcurrentRequests = 1,
            MaxRetries = 1,
            RetryDelay = 0.5f
        };
        settings.EnabledProviders["Flaky"] = true;
        settings.ApiKeys["Flaky"] = "k";
        var manager = new RimLLMManager(settings);

        int flakyCalls = 0;
        var bDone = new System.Threading.ManualResetEventSlim(false);
        bool bFinishedBeforeRetry = false;
        manager.RegisterProvider(new MockTestProvider
        {
            ProviderId = "Flaky",
            GenerateHandler = (msgs, opts, model) =>
            {
                string prompt = System.Linq.Enumerable.Last(msgs).Text;
                if (prompt == "A")
                {
                    int call = System.Threading.Interlocked.Increment(ref flakyCalls);
                    if (call == 1) throw new RimLLMException(LLMError.NetworkError, "flake");
                    bFinishedBeforeRetry = bDone.IsSet;
                    return Task.FromResult("A-ok");
                }
                bDone.Set();
                return Task.FromResult("B-ok");
            }
        });

        IChatClient client = manager.CreateChatClient("test.queue.backoff");
        Task<ChatResponse> a = client.GetResponseAsync(NewMessages("A"));
        // 等 A 的第一次嘗試失敗、進入退避，再送 B。
        System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref flakyCalls) >= 1, 5000);
        Task<ChatResponse> b = client.GetResponseAsync(NewMessages("B"));

        ClassicAssert.IsTrue(b.Wait(5000), "B 不該被 A 的退避卡住");
        ClassicAssert.AreEqual("B-ok", b.Result.Text);
        ClassicAssert.IsTrue(a.Wait(10000));
        ClassicAssert.AreEqual("A-ok", a.Result.Text);
        ClassicAssert.IsTrue(bFinishedBeforeRetry, "B 必須在 A 重試之前就拿到名額並完成");
    }

    [Test]
    public void AntiAbuseWindowIsSharedAcrossClientsOfTheSameMod()
    {
        // 節流狀態必須由所有 client 共用：若每個 client 各持一份，同一個 Mod
        // 只要多呼叫幾次 CreateChatClient 就能繞過節流，ClearCooldowns 也只會清掉其中一份。
        var settings = new MockSettings
        {
            EnableAntiAbuse = true,
            MaxRequestsPerWindow = 2,
            ThrottlingWindowSeconds = 60,
            CoolDownDurationSeconds = 60
        };
        var store = new RimLLMThrottleStore(settings);

        IChatClient first = new RimLLMGuardChatClient(
            new MockCustomChatClient(), settings, store, "same.mod", new RimLLMUsageTracker(settings));
        IChatClient second = new RimLLMGuardChatClient(
            new MockCustomChatClient(), settings, store, "same.mod", new RimLLMUsageTracker(settings));

        first.GetResponseAsync(NewMessages()).GetAwaiter().GetResult();
        second.GetResponseAsync(NewMessages()).GetAwaiter().GetResult();

        // 第 3 次跨越視窗上限——不論它是由哪一個 client 發出的。
        Assert.ThrowsAsync<RimLLMException>(async () => await second.GetResponseAsync(NewMessages()));
    }

    // ---------- OpenAI Patch 傳播器停用與工廠輔助測試 ----------

    [Test]
    public void DisablePatchPropagatorsHandlesNullGracefully()
    {
        OpenAI.Chat.ChatCompletionOptions nullOptions = null;
        var result = nullOptions.DisablePatchPropagators();
        ClassicAssert.IsNull(result);
    }

    [Test]
    public void DisablePatchPropagatorsClearsPropagatorsAndAllowsPatching()
    {
        var options = new OpenAI.Chat.ChatCompletionOptions();
        var sanitized = options.DisablePatchPropagators();
        ClassicAssert.AreSame(options, sanitized);

        // 驗證清空後直接操作 Patch 不會引發 NullReferenceException
        sanitized.Patch.Set(System.Text.Encoding.UTF8.GetBytes("$.response_format"), "json_object");
        sanitized.Patch.Set(System.Text.Encoding.UTF8.GetBytes("$.max_tokens"), 100);
    }

    // ---------- TPS 優化：非拋版判定、日誌快徑、派遣器、分級快取 ----------

    [Test]
    public void TryResolveCandidatesReturnsFalseInsteadOfThrowingOnEmptyChain()
    {
        // 相容層每秒輪詢一次：空鏈時不得以例外控制流程，否則每秒配置例外與堆疊。
        var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
        settings.FallbackChain = [];
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        var policy = BuildPipeline(settings, ledger, tracker, providers);

        bool ok = policy.TryResolveCandidates(null, null, false, out var candidates, out string reason);

        ClassicAssert.IsFalse(ok);
        ClassicAssert.IsNull(candidates);
        ClassicAssert.IsNotEmpty(reason);
        // 拋版語意維持不變，仍擲出 ProviderOffline。
        Assert.ThrowsAsync<RimLLMException>(async () => { policy.ResolveCandidates(null, null); await Task.CompletedTask; });
    }

    [Test]
    public void TryResolveCandidatesSucceedsWhenEligibleCandidatesExist()
    {
        var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
        settings.FallbackChain = ["openai:gpt-4o-mini"];
        var policy = BuildPipeline(settings, ledger, tracker, providers);

        bool ok = policy.TryResolveCandidates(null, null, false, out var candidates, out string reason);

        ClassicAssert.IsTrue(ok);
        ClassicAssert.IsNull(reason);
        ClassicAssert.AreEqual(1, candidates.Count);
    }

    [Test]
    public void ManagerTryGetEffectiveCapabilitiesDoesNotThrowWhenOffline()
    {
        var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
        settings.FallbackChain = [];
        var manager = new RimLLMManager(settings);

        bool ok = manager.TryGetEffectiveCapabilities(null, out var caps, out string reason);

        ClassicAssert.IsFalse(ok);
        ClassicAssert.IsNull(caps);
        ClassicAssert.IsNotEmpty(reason);
    }

    [Test]
    public void SanitizeFastPathLeavesPlainMessagesUntouched()
    {
        // 不含金鑰指標的一般遊戲日誌必須原樣通過（僅保留換行跳脫與截斷語意）。
        const string plain = "Attempting to call provider: openai (Model: gpt-4o-mini), retrying attempt 2...";
        ClassicAssert.AreEqual(plain, RimLLMLog.SanitizeForLog(plain, 500));

        string escaped = RimLLMLog.SanitizeForLog("line1\r\nline2", 500);
        ClassicAssert.AreEqual("line1\\r\\nline2", escaped);

        // 含指標時遮罩語意不變。
        string redacted = RimLLMLog.SanitizeForLog("request failed, api_key=SECRETVALUE123", 500);
        ClassicAssert.IsFalse(redacted.Contains("SECRETVALUE123"));
        ClassicAssert.IsTrue(redacted.Contains("[redacted]"));
    }

    [Test]
    public void DispatcherEmptyDrainReturnsZeroWithoutWork()
    {
        RimLLMDispatcher.ResetQueueForTests();
        ClassicAssert.AreEqual(0, RimLLMDispatcher.DrainWithBudget(128, 2));
        ClassicAssert.AreEqual(0, RimLLMDispatcher.DrainWithBudget(0, 2));
        RimLLMDispatcher.ResetQueueForTests();
    }

    [Test]
    public void ModelLevelCacheIsConsistentAcrossCases()
    {
        var settings = new MockSettings();
        var tracker = new RimLLMUsageTracker(settings);

        // 快取鍵大小寫不敏感：三次呼叫結果一致，且與覆寫查詢互不干擾。
        ClassicAssert.AreEqual(tracker.GetModelLevel("openai", "gpt-4o"), tracker.GetModelLevel("OpenAI", "GPT-4o"));
        ClassicAssert.AreEqual(RimLLMUsageTracker.ModelLevelLow, tracker.GetModelLevel("local", "anything"));
        ClassicAssert.AreEqual(RimLLMUsageTracker.ModelLevelMedium, tracker.GetModelLevel("openai", "some-unknown-model-xyz"));
    }

    [Test]
    public void ProviderCapabilitiesAreReusedAcrossReads()
    {
        var provider = new OpenAIProvider(new MockSettings());

        ClassicAssert.AreSame(provider.Capabilities, provider.Capabilities, "能力描述應只建立一次並重用。");
        ClassicAssert.IsTrue(provider.Capabilities.SupportsFunctionCalling);
    }

    [Test]
    public void PublicCapabilitiesQueryReturnsCopy()
    {
        var manager = new RimLLMManager(new MockSettings());

        LLMProviderCapabilities first = manager.GetProviderCapabilities(ProviderIds.OpenAI);
        first.SupportsFunctionCalling = false;

        ClassicAssert.IsTrue(
            manager.GetProviderCapabilities(ProviderIds.OpenAI).SupportsFunctionCalling,
            "公開 API 交出的必須是複本，改動不得影響供應商內部重用的實例。");
    }

    private sealed class SchemaElementPayload
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }

    [Test]
    public void SchemaResultElementMatchesJsonAndIsStable()
    {
        RimLLMSchemaResult schema = RimLLMSchemaBuilder.Build(typeof(SchemaElementPayload));

        System.Text.Json.JsonElement a = schema.Element;
        System.Text.Json.JsonElement b = schema.Element;

        ClassicAssert.AreEqual(schema.Json, a.GetRawText(), "快取的 Element 必須與 Json 字串同源。");
        ClassicAssert.AreEqual(a.GetRawText(), b.GetRawText());
        ClassicAssert.AreEqual(System.Text.Json.JsonValueKind.Object, a.ValueKind);
    }

    [Test]
    public void GetOrCreateSanitizedOptionsCreatesOrSanitizesOptions()
    {
        // Case 1: baseFactory 為 null
        var opts1 = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(null, null);
        ClassicAssert.IsNotNull(opts1);

        // Case 2: baseFactory 回傳現有 options
        var existing = new OpenAI.Chat.ChatCompletionOptions();
        var opts2 = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(client => existing, null);
        ClassicAssert.AreSame(existing, opts2);

        // Case 3: baseFactory 回傳非 ChatCompletionOptions 物件
        var opts3 = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(client => "not-options", null);
        ClassicAssert.IsNotNull(opts3);
    }
}
