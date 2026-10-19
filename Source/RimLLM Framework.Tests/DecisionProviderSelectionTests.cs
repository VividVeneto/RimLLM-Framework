using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 第三方 Mod 選用決策供應商的新公開 API 測試：
/// <see cref="RimLLMProvider.GetDecisionAvailability"/>、
/// <c>EvaluateWithProviderAsync</c> 與 <c>FetchModelsAsync</c>。
///
/// 網路層以內部接縫（<see cref="RimLLMDecisionService.MessageHandlerFactory"/>）換成假的 message handler，
/// 因此測得到「端點與金鑰來自哪一家供應商」與「用量記到誰帳上」，又不必真的連網。
/// </summary>
[TestFixture]
[NonParallelizable]
public class DecisionProviderSelectionTests
{
    private const string TestModId = "rimllm.tests.decision";
    private const string OtherModId = "rimllm.tests.decision.other";

    /// <summary>假的供應商端點；SDK 只要求它是 HTTPS origin，不會真的送出請求。</summary>
    private const string FakeEndpoint = "https://decision.example.test";

    private DecisionTestDoubles.FakeJevHandler _handler;
    private Func<HttpMessageHandler> _originalMessageHandlerFactory;

    [SetUp]
    public void SetUp()
    {
        _handler = new DecisionTestDoubles.FakeJevHandler();
        _originalMessageHandlerFactory = RimLLMDecisionService.MessageHandlerFactory;
        RimLLMDecisionService.MessageHandlerFactory = () => _handler;
    }

    [TearDown]
    public void TearDown()
    {
        RimLLMDecisionService.MessageHandlerFactory = _originalMessageHandlerFactory;
        _handler = null;
    }

    // ---------- GetDecisionAvailability ----------

    [Test]
    public void GetDecisionAvailability_WhenProviderDisabled_ReportsDisabledWithoutThrowing()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsFalse(availability.Enabled);
        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.Disabled, availability.ProviderId);
        ClassicAssert.IsEmpty(availability.Model);
        ClassicAssert.IsEmpty(availability.Endpoint);
        ClassicAssert.IsFalse(availability.HasKey);
    }

    [Test]
    public void GetDecisionAvailability_WhenProviderEnabledWithKey_ReportsModelAndOrigin()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionModels[RimLLMDecisionProviderCatalog.TypeSafe] = "jev-1.13";
        // 玩家貼整條 /v1/systemone 位址：回報的端點必須已經縮成 origin。
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = "https://api.typesafe.ai/v1/systemone";

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsTrue(availability.Enabled);
        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.TypeSafe, availability.ProviderId);
        ClassicAssert.IsTrue(availability.HasKey);
        ClassicAssert.AreEqual("jev-1.13", availability.Model);
        ClassicAssert.AreEqual("https://api.typesafe.ai", availability.Endpoint);
    }

    [Test]
    public void GetDecisionAvailability_WhenProviderEnabledWithoutKey_ReportsEnabledButNoKey()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsTrue(availability.Enabled);
        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.TypeSafe, availability.ProviderId);
        ClassicAssert.IsFalse(availability.HasKey);
        // 沒設模型時用供應商目錄的預設值，讓呼叫端知道實際會送出的模型。
        ClassicAssert.AreEqual(
            RimLLMDecisionProviderCatalog.GetDefaultModel(RimLLMDecisionProviderCatalog.TypeSafe),
            availability.Model);
    }

    [Test]
    public void GetDecisionAvailability_NeverExposesTheApiKey()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "super-secret-key";

        DecisionAvailability availability = AvailabilityFor(settings);

        string dumped = string.Join("|", availability.ProviderId, availability.Model, availability.Endpoint);
        StringAssert.DoesNotContain("super-secret-key", dumped);
    }

    // ---------- EvaluateWithProviderAsync ----------

    [Test]
    public async Task EvaluateWithProviderAsync_UsesTheChosenProvider_AndChargesThatProvider()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        // 啟用的是 TypeSafe（沒有金鑰），要使用的是未啟用但自己存了金鑰的 Liquid。
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "liquid-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = FakeEndpoint;
        settings.DecisionModels[RimLLMDecisionProviderCatalog.Liquid] = "liquid/d1";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        (JevDecisionPlan plan, JevNoulHandle handle) = DecisionTestDoubles.OneQuestion();
        JevResult result = await client.EvaluateWithProviderAsync("state", plan, RimLLMDecisionProviderCatalog.Liquid);

        ClassicAssert.AreEqual(0.75, result.Get(handle).Probability, 1e-9);
        ClassicAssert.AreEqual(1, _handler.RequestUris.Count);
        ClassicAssert.AreEqual("/v1/systemone", _handler.RequestUris[0].AbsolutePath);
        ClassicAssert.AreEqual("https://decision.example.test", _handler.RequestUris[0].GetLeftPart(UriPartial.Authority));
        // 送出的金鑰必須是 Liquid 自己的，不是啟用中供應商的。
        ClassicAssert.AreEqual("Bearer liquid-key", _handler.AuthorizationHeaders[0]);

        // 用量歸屬到實際使用的供應商，不是啟用中的那一個。
        ClassicAssert.IsTrue(manager.UsageTracker.ProviderStatistics.ContainsKey(RimLLMDecisionProviderCatalog.Liquid));
        ClassicAssert.AreEqual(
            11L,
            manager.UsageTracker.ProviderStatistics[RimLLMDecisionProviderCatalog.Liquid].TotalPromptTokens);
        ClassicAssert.IsFalse(
            manager.UsageTracker.ProviderStatistics.ContainsKey(RimLLMDecisionProviderCatalog.TypeSafe));
        ClassicAssert.AreEqual(11L, settings.TotalPromptTokens);
        ClassicAssert.AreEqual(7L, settings.TotalCompletionTokens);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithoutKey_FailsBeforeAnyNetworkCall()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync(
                "state", DecisionTestDoubles.OneQuestionPlan(), RimLLMDecisionProviderCatalog.Liquid));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris, "缺金鑰時不得送出任何網路請求");
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithUnknownProvider_FailsWithUnknownError()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync(
                "state", DecisionTestDoubles.OneQuestionPlan(), "NotAProvider"));

        ClassicAssert.AreEqual(LLMError.Unknown, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris, "供應商不存在時不得送出任何網路請求");
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithDisabledProvider_FailsWithUnknownError()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync(
                "state", DecisionTestDoubles.OneQuestionPlan(), RimLLMDecisionProviderCatalog.Disabled));

        ClassicAssert.AreEqual(LLMError.Unknown, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_LeavesPlayerSettingsUntouched()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "liquid-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = FakeEndpoint;
        settings.DecisionModels[RimLLMDecisionProviderCatalog.Liquid] = "liquid/d1";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateWithProviderAsync(
            "state", DecisionTestDoubles.OneQuestionPlan(), RimLLMDecisionProviderCatalog.Liquid);

        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.TypeSafe, settings.DecisionProvider);
        ClassicAssert.AreEqual("type-safe-key", settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe]);
        ClassicAssert.AreEqual("liquid-key", settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid]);
        ClassicAssert.AreEqual(FakeEndpoint, settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid]);
        ClassicAssert.AreEqual("liquid/d1", settings.DecisionModels[RimLLMDecisionProviderCatalog.Liquid]);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithEmptyProviderId_BehavesLikeEvaluateAsync()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, OtherModId);

        (JevDecisionPlan plan, JevNoulHandle handle) = DecisionTestDoubles.OneQuestion();
        JevResult result = await client.EvaluateWithProviderAsync("state", plan, "");

        ClassicAssert.AreEqual(0.75, result.Get(handle).Probability, 1e-9);
        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
        ClassicAssert.IsTrue(
            manager.UsageTracker.ProviderStatistics.ContainsKey(RimLLMDecisionProviderCatalog.TypeSafe));
    }

    [Test]
    public async Task EvaluateAsync_StillUsesTheActiveProvider()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "liquid-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = FakeEndpoint;
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateAsync("state", DecisionTestDoubles.OneQuestionPlan());

        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
        ClassicAssert.IsTrue(
            manager.UsageTracker.ProviderStatistics.ContainsKey(RimLLMDecisionProviderCatalog.TypeSafe));
        ClassicAssert.IsFalse(
            manager.UsageTracker.ProviderStatistics.ContainsKey(RimLLMDecisionProviderCatalog.Liquid));
    }

    // ---------- FetchModelsAsync ----------

    [Test]
    public async Task FetchModelsAsync_UsesThatProvidersEndpointAndKey()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "liquid-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        IReadOnlyList<string> models = await client.FetchModelsAsync(RimLLMDecisionProviderCatalog.Liquid);

        ClassicAssert.AreEqual(1, _handler.RequestUris.Count);
        ClassicAssert.AreEqual("/v1/models", _handler.RequestUris[0].AbsolutePath);
        ClassicAssert.AreEqual("https://decision.example.test", _handler.RequestUris[0].GetLeftPart(UriPartial.Authority));
        ClassicAssert.AreEqual("Bearer liquid-key", _handler.AuthorizationHeaders[0]);
        ClassicAssert.AreEqual(new[] { "jev-latest", "jev-1.13" }, models);
    }

    [Test]
    public async Task FetchModelsAsync_DoesNotRecordUsage_AndIgnoresTheDailyBudget()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "liquid-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = FakeEndpoint;
        // 每日 token 預算早已超限：模型清單查詢不該被它擋下。
        settings.DailyTokenBudgetLimit = 1;
        settings.DailyAccumulatedTokens = 1000;
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        IReadOnlyList<string> models = await client.FetchModelsAsync(RimLLMDecisionProviderCatalog.Liquid);

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual(0L, settings.TotalPromptTokens);
        ClassicAssert.AreEqual(0L, settings.TotalCompletionTokens);
        ClassicAssert.IsEmpty(manager.UsageTracker.ProviderStatistics);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_IsBlockedByTheDailyBudget()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = FakeEndpoint;
        settings.DailyTokenBudgetLimit = 1;
        settings.DailyAccumulatedTokens = 1000;
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync(
                "state", DecisionTestDoubles.OneQuestionPlan(), RimLLMDecisionProviderCatalog.TypeSafe));

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris, "預算超限時不得送出任何網路請求");
    }

    // ---------- 既有行為的回歸測試 ----------

    [Test]
    public async Task ServiceFetchModelsAsync_StillUsesTheActiveProvider()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = FakeEndpoint;

        var service = new RimLLMDecisionService(settings);
        IReadOnlyList<JevModel> models = await service.FetchModelsAsync();

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual("jev-latest", models[0].Name);
        ClassicAssert.AreEqual("/v1/models", _handler.RequestUris[0].AbsolutePath);
        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
    }

    [Test]
    public async Task ServiceFetchModelsAsync_WithExplicitEndpoint_StillWorksForTheSettingsPage()
    {
        // 設定頁在尚未啟用供應商時，用明確的端點／模型／金鑰預覽模型清單。
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;

        var service = new RimLLMDecisionService(settings);
        IReadOnlyList<JevModel> models = await service.FetchModelsAsync(FakeEndpoint, "jev-latest", "preview-key");

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual("/v1/models", _handler.RequestUris[0].AbsolutePath);
        ClassicAssert.AreEqual("Bearer preview-key", _handler.AuthorizationHeaders[0]);
    }

    [Test]
    public async Task ServiceFetchModelsAsync_WithoutKey_StillRefusesBeforeAnyNetworkCall()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;

        var service = new RimLLMDecisionService(settings);
        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await service.FetchModelsAsync(FakeEndpoint, "jev-latest", ""));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris);
    }

    [Test]
    public void SettingsPagePreviewArguments_ComeFromTheSelectedProvider()
    {
        // 設定頁的「抓取模型清單」與「連線測試」都靠這組參數；
        // 參數必須來自被選中的供應商，而不是啟用中的那一家的。
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "type-safe-key";
        settings.DecisionModels[RimLLMDecisionProviderCatalog.TypeSafe] = "jev-1.13";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = FakeEndpoint;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "liquid-key";
        settings.DecisionModels[RimLLMDecisionProviderCatalog.Liquid] = "liquid/d1";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = "https://liquid.example.test";

        var service = new RimLLMDecisionService(settings);

        DecisionPreviewArguments preview = service.GetPreviewArguments(RimLLMDecisionProviderCatalog.Liquid);

        ClassicAssert.AreEqual("https://liquid.example.test", preview.Endpoint);
        ClassicAssert.AreEqual("liquid/d1", preview.Model);
        ClassicAssert.AreEqual("liquid-key", preview.ApiKey);
    }

    // ---------- 外部設定實作者的相容行為 ----------

    [Test]
    public async Task ExternalSettingsImplementation_DoesNotBorrowTheEnabledProvidersKey()
    {
        // 依舊版介面編譯、自己實作 IRimLLMSettings 的外部 Mod 不會覆寫新的逐供應商方法。
        // 介面上的預設實作必須回空字串，而不是把啟用中供應商的金鑰借給別家：
        // 否則請求會帶著 A 的金鑰打到 B 的端點，用量也記到 B 帳上。
        var settings = new LegacyStyleSettings
        {
            DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe,
            DecisionApiKey = "type-safe-key",
            DecisionEndpoint = FakeEndpoint
        };

        var service = new RimLLMDecisionService(settings);
        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await service.EvaluateWithProviderDetailedAsync(
                "state", DecisionTestDoubles.OneQuestionPlan(), RimLLMDecisionProviderCatalog.Liquid));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris, "不得拿啟用中供應商的金鑰去呼叫別家供應商");
    }

    /// <summary>
    /// 模擬依舊版介面編譯的外部設定實作者：只維護「啟用中供應商」一份資料，
    /// 沒有覆寫逐供應商的存取方法。
    /// </summary>
    private sealed class LegacyStyleSettings : IRimLLMSettings
    {
        public List<string> FallbackChain { get; set; } = [];
        public float ApiTimeout { get; set; } = 30f;
        public bool DetailedLogging { get; set; }
        public ReasoningEffort? DefaultReasoningEffort { get; set; }
        public int MaxRetries { get; set; }
        public float RetryDelay { get; set; }
        public int MaxConcurrentRequests { get; set; }
        public long TotalPromptTokens { get; set; }
        public long TotalCompletionTokens { get; set; }
        public float TotalEstimatedCost { get; set; }
        public float DailyBudgetLimit { get; set; }
        public int BudgetPolicy { get; set; }
        public bool EnableAntiAbuse { get; set; }
        public int MaxRequestsPerWindow { get; set; }
        public int ThrottlingWindowSeconds { get; set; }
        public int CoolDownDurationSeconds { get; set; }
        public float DailyAccumulatedCost { get; set; }
        public string DailyBudgetResetDate { get; set; } = "";
        public int RoutingStrategy { get; set; }
        public bool EnableNativeSchema { get; set; }
        public bool EnableJsonRepair { get; set; }
        public string EmbeddingProvider { get; set; } = "Disabled";
        public string EmbeddingModel { get; set; } = "";
        public string EmbeddingEndpoint { get; set; } = "";
        public string EmbeddingApiKey { get; set; } = "";
        public string DecisionProvider { get; set; } = "Disabled";
        public string DecisionModel { get; set; } = "";
        public string DecisionEndpoint { get; set; } = "";
        public string DecisionApiKey { get; set; } = "";
        public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = [];

        public string GetApiKey(string providerId) => "";
        public string GetActiveApiKey(string providerId) => "";
        public string GetEndpoint(string providerId, string defaultVal) => defaultVal;
        public bool IsProviderEnabled(string providerId) => true;
        public List<string> GetModelList(string providerId) => [];
        public string GetDefaultModel(string providerId, string defaultVal) => defaultVal;
        public void SetModelList(string providerId, List<string> models) { }
        public int GetModelLevelOverride(string modelName) => 0;
        public void MarkTelemetryDirty() { }
        public void SaveTelemetry() { }
        public void Write() { }
    }
    // ---------- 測試輔助 ----------

    private static DecisionAvailability AvailabilityFor(MockSettings settings)
    {
        return DecisionTestDoubles.WithManager(settings, RimLLMProvider.GetDecisionAvailability);
    }
}
