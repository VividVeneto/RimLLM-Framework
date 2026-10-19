using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
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
/// 網路層以內部接縫（<see cref="RimLLMDecisionService.HttpClientFactory"/>）換成假的 message handler，
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

    private FakeJevHandler _handler;
    private Func<string, HttpMessageHandler> _originalMessageHandlerFactory;

    [SetUp]
    public void SetUp()
    {
        _handler = new FakeJevHandler();
        _originalMessageHandlerFactory = RimLLMDecisionService.MessageHandlerFactory;
        RimLLMDecisionService.MessageHandlerFactory = _ => _handler;
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
        MockSettings settings = NewSettings();
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
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionModels["TypeSafe"] = "jev-1.13";
        // 玩家貼整條 /v1/systemone 位址：回報的端點必須已經縮成 origin。
        settings.DecisionEndpoints["TypeSafe"] = "https://api.typesafe.ai/v1/systemone";

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsTrue(availability.Enabled);
        ClassicAssert.AreEqual("TypeSafe", availability.ProviderId);
        ClassicAssert.IsTrue(availability.HasKey);
        ClassicAssert.AreEqual("jev-1.13", availability.Model);
        ClassicAssert.AreEqual("https://api.typesafe.ai", availability.Endpoint);
    }

    [Test]
    public void GetDecisionAvailability_WhenProviderEnabledWithoutKey_ReportsEnabledButNoKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsTrue(availability.Enabled);
        ClassicAssert.AreEqual("TypeSafe", availability.ProviderId);
        ClassicAssert.IsFalse(availability.HasKey);
        // 沒設模型時用供應商目錄的預設值，讓呼叫端知道實際會送出的模型。
        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.GetDefaultModel("TypeSafe"), availability.Model);
    }

    [Test]
    public void GetDecisionAvailability_NeverExposesTheApiKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "super-secret-key";

        DecisionAvailability availability = AvailabilityFor(settings);

        string dumped = string.Join("|", availability.ProviderId, availability.Model, availability.Endpoint);
        StringAssert.DoesNotContain("super-secret-key", dumped);
    }

    // ---------- EvaluateWithProviderAsync ----------

    [Test]
    public async Task EvaluateWithProviderAsync_UsesTheChosenProvider_AndChargesThatProvider()
    {
        MockSettings settings = NewSettings();
        // 啟用的是 TypeSafe（沒有金鑰），要使用的是未啟用但自己存了金鑰的 Liquid。
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["Liquid"] = "liquid-key";
        settings.DecisionEndpoints["Liquid"] = FakeEndpoint;
        settings.DecisionModels["Liquid"] = "liquid/d1";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        (JevDecisionPlan plan, JevNoulHandle handle) = OneQuestion();
        JevResult result = await client.EvaluateWithProviderAsync("state", plan, "Liquid");

        ClassicAssert.AreEqual(0.75, result.Get(handle).Probability, 1e-9);
        ClassicAssert.AreEqual(1, _handler.RequestPaths.Count);
        ClassicAssert.AreEqual("/v1/systemone", _handler.RequestPaths[0]);
        ClassicAssert.AreEqual("https://decision.example.test", _handler.RequestOrigins[0]);
        // 送出的金鑰必須是 Liquid 自己的，不是啟用中供應商的。
        ClassicAssert.AreEqual("Bearer liquid-key", _handler.AuthorizationHeaders[0]);

        // 用量歸屬到實際使用的供應商，不是啟用中的那一個。
        ClassicAssert.IsTrue(manager.UsageTracker.ProviderStatistics.ContainsKey("Liquid"));
        ClassicAssert.AreEqual(
            11L,
            manager.UsageTracker.ProviderStatistics["Liquid"].TotalPromptTokens);
        ClassicAssert.IsFalse(manager.UsageTracker.ProviderStatistics.ContainsKey("TypeSafe"));
        ClassicAssert.AreEqual(11L, settings.TotalPromptTokens);
        ClassicAssert.AreEqual(7L, settings.TotalCompletionTokens);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithoutKey_FailsBeforeAnyNetworkCall()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["Liquid"] = "";
        settings.DecisionEndpoints["Liquid"] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "Liquid"));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestPaths, "缺金鑰時不得送出任何網路請求");
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithUnknownProvider_FailsWithUnknownError()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "NotAProvider"));

        ClassicAssert.AreEqual(LLMError.Unknown, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestPaths, "供應商不存在時不得送出任何網路請求");
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithDisabledProvider_FailsWithUnknownError()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "Disabled"));

        ClassicAssert.AreEqual(LLMError.Unknown, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestPaths);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_LeavesPlayerSettingsUntouched()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionApiKeys["Liquid"] = "liquid-key";
        settings.DecisionEndpoints["Liquid"] = FakeEndpoint;
        settings.DecisionModels["Liquid"] = "liquid/d1";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "Liquid");

        ClassicAssert.AreEqual("TypeSafe", settings.DecisionProvider);
        ClassicAssert.AreEqual("type-safe-key", settings.DecisionApiKeys["TypeSafe"]);
        ClassicAssert.AreEqual("liquid-key", settings.DecisionApiKeys["Liquid"]);
        ClassicAssert.AreEqual(FakeEndpoint, settings.DecisionEndpoints["Liquid"]);
        ClassicAssert.AreEqual("liquid/d1", settings.DecisionModels["Liquid"]);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_WithEmptyProviderId_BehavesLikeEvaluateAsync()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionEndpoints["TypeSafe"] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, OtherModId);

        (JevDecisionPlan plan, JevNoulHandle handle) = OneQuestion();
        JevResult result = await client.EvaluateWithProviderAsync("state", plan, "");

        ClassicAssert.AreEqual(0.75, result.Get(handle).Probability, 1e-9);
        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
        ClassicAssert.IsTrue(manager.UsageTracker.ProviderStatistics.ContainsKey("TypeSafe"));
    }

    [Test]
    public async Task EvaluateAsync_StillUsesTheActiveProvider()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionApiKeys["Liquid"] = "liquid-key";
        settings.DecisionEndpoints["TypeSafe"] = FakeEndpoint;
        settings.DecisionEndpoints["Liquid"] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateAsync("state", OneQuestionPlan());

        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
        ClassicAssert.IsTrue(manager.UsageTracker.ProviderStatistics.ContainsKey("TypeSafe"));
        ClassicAssert.IsFalse(manager.UsageTracker.ProviderStatistics.ContainsKey("Liquid"));
    }

    // ---------- FetchModelsAsync ----------

    [Test]
    public async Task FetchModelsAsync_UsesThatProvidersEndpointAndKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["Liquid"] = "liquid-key";
        settings.DecisionEndpoints["Liquid"] = FakeEndpoint;

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        IReadOnlyList<string> models = await client.FetchModelsAsync("Liquid");

        ClassicAssert.AreEqual(1, _handler.RequestPaths.Count);
        ClassicAssert.AreEqual("/v1/models", _handler.RequestPaths[0]);
        ClassicAssert.AreEqual("https://decision.example.test", _handler.RequestOrigins[0]);
        ClassicAssert.AreEqual("Bearer liquid-key", _handler.AuthorizationHeaders[0]);
        ClassicAssert.AreEqual(new[] { "jev-latest", "jev-1.13" }, models);
    }

    [Test]
    public async Task FetchModelsAsync_DoesNotRecordUsage_AndIgnoresTheDailyBudget()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["Liquid"] = "liquid-key";
        settings.DecisionEndpoints["Liquid"] = FakeEndpoint;
        // 每日 token 預算早已超限：模型清單查詢不該被它擋下。
        settings.DailyTokenBudgetLimit = 1;
        settings.DailyAccumulatedTokens = 1000;
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        IReadOnlyList<string> models = await client.FetchModelsAsync("Liquid");

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual(0L, settings.TotalPromptTokens);
        ClassicAssert.AreEqual(0L, settings.TotalCompletionTokens);
        ClassicAssert.IsEmpty(manager.UsageTracker.ProviderStatistics);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_IsBlockedByTheDailyBudget()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionEndpoints["TypeSafe"] = FakeEndpoint;
        settings.DailyTokenBudgetLimit = 1;
        settings.DailyAccumulatedTokens = 1000;
        settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "TypeSafe"));

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestPaths, "預算超限時不得送出任何網路請求");
    }

    // ---------- 既有行為的回歸測試 ----------

    [Test]
    public async Task ServiceFetchModelsAsync_StillUsesTheActiveProvider()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionEndpoints["TypeSafe"] = FakeEndpoint;

        var service = new RimLLMDecisionService(settings);
        IReadOnlyList<JevModel> models = await service.FetchModelsAsync();

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual("jev-latest", models[0].Name);
        ClassicAssert.AreEqual("/v1/models", _handler.RequestPaths[0]);
        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
    }

    [Test]
    public async Task ServiceFetchModelsAsync_WithExplicitEndpoint_StillWorksForTheSettingsPage()
    {
        // 設定頁在尚未啟用供應商時，用明確的端點／模型／金鑰預覽模型清單。
        MockSettings settings = NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;

        var service = new RimLLMDecisionService(settings);
        IReadOnlyList<JevModel> models = await service.FetchModelsAsync(FakeEndpoint, "jev-latest", "preview-key");

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual("/v1/models", _handler.RequestPaths[0]);
        ClassicAssert.AreEqual("Bearer preview-key", _handler.AuthorizationHeaders[0]);
    }

    [Test]
    public async Task ServiceFetchModelsAsync_WithoutKey_StillRefusesBeforeAnyNetworkCall()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;

        var service = new RimLLMDecisionService(settings);
        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await service.FetchModelsAsync(FakeEndpoint, "jev-latest", ""));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestPaths);
    }

    // ---------- 測試輔助 ----------

    private static MockSettings NewSettings()
    {
        var settings = new MockSettings
        {
            // 防濫用節流在這裡只會干擾測試，關掉以免請求數影響後續斷言。
            EnableAntiAbuse = false
        };
        return settings;
    }

    private static DecisionAvailability AvailabilityFor(MockSettings settings)
    {
        RimLLMProvider.TryGetManager(out RimLLMManager previousManager);
        RimLLMProvider.Initialize(new RimLLMManager(settings));
        try
        {
            return RimLLMProvider.GetDecisionAvailability();
        }
        finally
        {
            RimLLMProvider.Initialize(previousManager);
        }
    }

    private static JevDecisionPlan OneQuestionPlan()
    {
        return OneQuestion().Plan;
    }

    /// <summary>建立只有一題 Noul 的計畫，並保留 handle 以便讀回機率。</summary>
    private static (JevDecisionPlan Plan, JevNoulHandle Handle) OneQuestion()
    {
        var builder = new JevDecisionPlanBuilder();
        JevNoulHandle handle = builder.AddNoul("hostile", "Is the speaker hostile?", default);
        return (builder.Build(), handle);
    }

    /// <summary>
    /// 假的 System One 端點：攔截 SDK 送出的請求，依路徑回應對應的 wire format。
    /// </summary>
    private sealed class FakeJevHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        public List<string> RequestOrigins { get; } = [];

        public List<string> AuthorizationHeaders { get; } = [];

        public int ResponseStatusCode { get; set; } = 200;

        public string EvaluateResponseBody { get; set; } =
            "{\"model\":\"jev-latest\",\"answers\":{\"hostile\":{\"type\":\"noul\",\"noul\":0.75}}," +
            "\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}";

        public string ModelsResponseBody { get; set; } =
            "{\"models\":[{\"name\":\"jev-latest\",\"description\":\"latest\",\"release_date\":\"2026-01-01\"}," +
            "{\"name\":\"jev-1.13\",\"description\":\"stable\",\"release_date\":\"2025-12-01\"}]}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            lock (RequestPaths)
            {
                RequestPaths.Add(path);
                RequestOrigins.Add(request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "");
                AuthorizationHeaders.Add(
                    request.Headers.TryGetValues("Authorization", out IEnumerable<string> values)
                        ? string.Join(",", values)
                        : "");
            }

            bool isModelList = path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase);
            string body = isModelList ? ModelsResponseBody : EvaluateResponseBody;

            var response = new HttpResponseMessage((HttpStatusCode)ResponseStatusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
