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
/// 本機架設決策服務的測試：Ollama、Unsloth 等自架服務只提供明文 HTTP，
/// 而 Jev SDK 只接受 HTTPS origin。框架在傳輸層把兩者接起來，
/// 這裡驗證改寫確實發生、明文端點不要求金鑰，而且遠端供應商的行為完全沒變。
/// </summary>
[TestFixture]
[NonParallelizable]
public class DecisionLocalEndpointTests
{
    private const string TestModId = "rimllm.tests.localdecision";

    private RecordingHandler _handler;
    private Func<string, HttpMessageHandler> _originalMessageHandlerFactory;

    [SetUp]
    public void SetUp()
    {
        _handler = new RecordingHandler();
        _originalMessageHandlerFactory = RimLLMDecisionService.MessageHandlerFactory;
        RimLLMDecisionService.MessageHandlerFactory = _ => _handler;
    }

    [TearDown]
    public void TearDown()
    {
        RimLLMDecisionService.MessageHandlerFactory = _originalMessageHandlerFactory;
        _handler = null;
    }

    // ---------- 端點路由 ----------

    [Test]
    public void PlaintextEndpoint_IsDetectedByScheme()
    {
        ClassicAssert.IsTrue(DecisionEndpointRouting.IsPlaintext("http://localhost:11434"));
        ClassicAssert.IsTrue(DecisionEndpointRouting.IsPlaintext("http://192.168.1.5:8888"));
        ClassicAssert.IsFalse(DecisionEndpointRouting.IsPlaintext("https://api.typesafe.ai"));
        ClassicAssert.IsFalse(DecisionEndpointRouting.IsPlaintext("not a uri"));
        ClassicAssert.IsFalse(DecisionEndpointRouting.IsPlaintext(""));
    }

    [Test]
    public void SdkBaseAddress_ForPlaintextEndpoint_IsAnHttpsOriginTheSdkAccepts()
    {
        Uri sdkBase = DecisionEndpointRouting.GetSdkBaseAddress("http://localhost:11434");

        // SDK 的驗證：絕對 URI、https、沒有 userinfo／query／fragment、路徑是 origin。
        ClassicAssert.AreEqual(Uri.UriSchemeHttps, sdkBase.Scheme);
        ClassicAssert.AreEqual("localhost", sdkBase.Host);
        ClassicAssert.AreEqual(11434, sdkBase.Port);
        ClassicAssert.AreEqual("/", sdkBase.AbsolutePath);
        ClassicAssert.IsEmpty(sdkBase.UserInfo);
        ClassicAssert.IsEmpty(sdkBase.Query);
        ClassicAssert.IsEmpty(sdkBase.Fragment);
    }

    [Test]
    public void SdkBaseAddress_ForHttpsEndpoint_IsUnchanged()
    {
        Uri sdkBase = DecisionEndpointRouting.GetSdkBaseAddress("https://api.typesafe.ai");

        ClassicAssert.AreEqual("https://api.typesafe.ai/", sdkBase.ToString());
    }

    [Test]
    public void SdkBaseAddress_ForAnUnsupportedScheme_IsRejected()
    {
        ClassicAssert.Throws<UriFormatException>(() =>
            DecisionEndpointRouting.GetSdkBaseAddress("ftp://example.com"));
        ClassicAssert.Throws<UriFormatException>(() =>
            DecisionEndpointRouting.GetSdkBaseAddress("not a uri"));
    }

    [Test]
    public void TransportOrigin_KeepsThePlaintextSchemeAndPort()
    {
        Uri origin = DecisionEndpointRouting.GetTransportOrigin("http://localhost:8888");

        ClassicAssert.AreEqual(Uri.UriSchemeHttp, origin.Scheme);
        ClassicAssert.AreEqual("localhost", origin.Host);
        ClassicAssert.AreEqual(8888, origin.Port);
    }

    [Test]
    public void EndpointRouting_HandlesDefaultPortsIpv6AndLanHosts()
    {
        // 沒寫 port 的端點要落在 scheme 的預設埠。
        Uri defaultPort = DecisionEndpointRouting.GetTransportOrigin("http://localhost");
        ClassicAssert.AreEqual(Uri.UriSchemeHttp, defaultPort.Scheme);
        ClassicAssert.AreEqual(80, defaultPort.Port);
        ClassicAssert.AreEqual("localhost", defaultPort.Authority);

        // IPv6：主機名稱自帶方括號，不能被重複包一次。
        Uri ipv6 = DecisionEndpointRouting.GetTransportOrigin("http://[::1]:8888");
        ClassicAssert.AreEqual("[::1]:8888", ipv6.Authority);
        ClassicAssert.AreEqual(
            "https://[::1]:8888/",
            DecisionEndpointRouting.GetSdkBaseAddress("http://[::1]:8888").ToString());

        // 區網主機：玩家自己架在別台機器上，同樣照明文送。
        Uri lan = DecisionEndpointRouting.GetTransportOrigin("http://192.168.1.5:11434");
        ClassicAssert.AreEqual("192.168.1.5:11434", lan.Authority);
    }

    // ---------- 明文端點的實際請求 ----------

    [Test]
    public async Task EvaluateWithProviderAsync_AgainstPlaintextEndpoint_ActuallyUsesHttp()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "Ollama";
        settings.DecisionEndpoints["Ollama"] = "http://localhost:11434";
        // 本機服務不需要金鑰：這裡刻意不設定金鑰。

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        (JevDecisionPlan plan, JevNoulHandle handle) = OneQuestion();
        JevResult result = await client.EvaluateWithProviderAsync("state", plan, "Ollama");

        ClassicAssert.AreEqual(0.75, result.Get(handle).Probability, 1e-9);
        ClassicAssert.AreEqual(1, _handler.RequestUris.Count);
        // 送出的必須是明文 http，而不是 SDK 組出來的 https。
        ClassicAssert.AreEqual("http://localhost:11434/v1/systemone", _handler.RequestUris[0].ToString());
        ClassicAssert.AreEqual(Uri.UriSchemeHttp, _handler.RequestUris[0].Scheme);
    }

    [Test]
    public async Task EvaluateWithProviderAsync_AgainstPlaintextEndpoint_SendsAPlaceholderKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "Ollama";
        settings.DecisionEndpoints["Ollama"] = "http://localhost:11434";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "Ollama");

        // SDK 拒絕空金鑰，所以框架送佔位值過去。
        ClassicAssert.AreEqual(
            $"Bearer {DecisionEndpointRouting.PlaintextPlaceholderKey}",
            _handler.AuthorizationHeaders[0]);
    }

    [Test]
    public async Task FetchModelsAsync_AgainstPlaintextEndpoint_UsesHttpModelsPath()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "Unsloth";
        settings.DecisionEndpoints["Unsloth"] = "http://localhost:8888";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        IReadOnlyList<string> models = await client.FetchModelsAsync("Unsloth");

        ClassicAssert.AreEqual(2, models.Count);
        ClassicAssert.AreEqual("http://localhost:8888/v1/models", _handler.RequestUris[0].ToString());
    }

    [Test]
    public async Task PlaintextEndpoint_WithoutKey_DoesNotThrowInvalidKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "Local";
        settings.DecisionEndpoints["Local"] = "http://localhost:8000";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        JevResult result = await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "Local");

        ClassicAssert.IsNotNull(result);
        ClassicAssert.AreEqual(1, _handler.RequestUris.Count);
    }

    // ---------- 遠端供應商行為不變 ----------

    [Test]
    public async Task HttpsEndpoint_WithoutKey_StillRefusesWithInvalidKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionEndpoints["TypeSafe"] = "https://api.typesafe.ai";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        RimLLMException ex = ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "TypeSafe"));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        ClassicAssert.IsEmpty(_handler.RequestUris, "缺金鑰時不得送出任何網路請求");
    }

    [Test]
    public async Task HttpsEndpoint_KeepsUsingHttpsOnTheWire()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";
        settings.DecisionApiKeys["TypeSafe"] = "type-safe-key";
        settings.DecisionEndpoints["TypeSafe"] = "https://api.typesafe.ai";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "TypeSafe");

        ClassicAssert.AreEqual("https://api.typesafe.ai/v1/systemone", _handler.RequestUris[0].ToString());
        ClassicAssert.AreEqual("Bearer type-safe-key", _handler.AuthorizationHeaders[0]);
    }

    [Test]
    public async Task PlaintextEndpoint_WithARealKey_SendsThatKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "Unsloth";
        settings.DecisionApiKeys["Unsloth"] = "sk-unsloth-key";
        settings.DecisionEndpoints["Unsloth"] = "http://localhost:8888";

        RimLLMManager manager = new(settings);
        RimLLMDecisionClient client = new(manager, TestModId);

        await client.EvaluateWithProviderAsync("state", OneQuestionPlan(), "Unsloth");

        ClassicAssert.AreEqual("Bearer sk-unsloth-key", _handler.AuthorizationHeaders[0]);
    }

    // ---------- 可用狀態查詢 ----------

    [Test]
    public void GetDecisionAvailability_ForPlaintextProvider_ReportsThatNoKeyIsRequired()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "Ollama";

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsTrue(availability.Enabled);
        ClassicAssert.AreEqual("Ollama", availability.ProviderId);
        ClassicAssert.IsFalse(availability.HasKey);
        ClassicAssert.IsFalse(availability.RequiresKey);
        ClassicAssert.AreEqual("http://localhost:11434", availability.Endpoint);
        ClassicAssert.AreEqual("tev1:4b", availability.Model);
    }

    [Test]
    public void GetDecisionAvailability_ForHttpsProvider_StillRequiresAKey()
    {
        MockSettings settings = NewSettings();
        settings.DecisionProvider = "TypeSafe";

        DecisionAvailability availability = AvailabilityFor(settings);

        ClassicAssert.IsTrue(availability.Enabled);
        ClassicAssert.IsFalse(availability.HasKey);
        ClassicAssert.IsTrue(availability.RequiresKey);
    }

    // ---------- 測試輔助 ----------

    private static MockSettings NewSettings()
    {
        return new MockSettings { EnableAntiAbuse = false };
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

    private static (JevDecisionPlan Plan, JevNoulHandle Handle) OneQuestion()
    {
        var builder = new JevDecisionPlanBuilder();
        JevNoulHandle handle = builder.AddNoul("hostile", "Is the speaker hostile?", default);
        return (builder.Build(), handle);
    }

    /// <summary>攔截 SDK 送出的請求，記下最終的 URI 與授權標頭。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        public List<string> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestUris)
            {
                RequestUris.Add(request.RequestUri);
                AuthorizationHeaders.Add(
                    request.Headers.TryGetValues("Authorization", out IEnumerable<string> values)
                        ? string.Join(",", values)
                        : "");
            }

            string path = request.RequestUri?.AbsolutePath ?? "";
            bool isModelList = path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase);
            string body = isModelList
                ? "{\"models\":[{\"name\":\"laya\",\"description\":\"d\",\"release_date\":\"2026-01-01\"}," +
                  "{\"name\":\"laya-multilingual\",\"description\":\"d\",\"release_date\":\"2026-01-01\"}]}"
                : "{\"model\":\"tev1:4b\",\"answers\":{\"hostile\":{\"type\":\"noul\",\"noul\":0.75}}," +
                  "\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
