using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Providers;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 思考強度在各供應商的線上格式，以及服務端拒絕後的自動降級行為。
///
/// 這組測試存在的理由：框架先前只在模型名以 o1/o3 開頭時才送思考強度，
/// 導致其餘所有供應商與模型的設定被靜默丟棄。修正後由供應商宣告方言，
/// 涵蓋範圍必須以測試釘住，否則同樣的腐化會再次發生。
/// </summary>
[TestFixture]
public class ReasoningDialectTests
{
    private static readonly List<ChatMessage> UserMessages =
        [new ChatMessage(ChatRole.User, "hi")];

    [SetUp]
    public void ResetLearnedCapabilities()
    {
        // 降級記憶是行程層級的靜態狀態，案例之間必須隔離。
        RimLLMReasoningSupport.Reset();
    }

    private static MockSettings SettingsWithKey(string providerId)
    {
        var settings = new MockSettings();
        settings.ApiKeys[providerId] = "mock-key";
        return settings;
    }

    // ---------- OpenAI 家：頂層 reasoning_effort ----------

    [Test]
    public void UnknownFutureOpenAiModelStillReceivesReasoningEffort()
    {
        // 這是先前那個 bug 的核心案例：名稱不在硬編清單裡的模型，設定不該被丟掉。
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        provider.GenerateAsync(UserMessages, options, "o5-preview").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("high", (string)payload["reasoning_effort"]);
    }

    [Test]
    public void Gpt5ClearsTemperatureAndSendsEffort()
    {
        // gpt-5 系列在思考開啟時會以 400 拒絕 temperature，而不是忽略它。
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            Temperature = 0.7f
        };

        provider.GenerateAsync(UserMessages, options, "gpt-5").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("low", (string)payload["reasoning_effort"]);
        ClassicAssert.IsNull(payload["temperature"]);
    }

    [Test]
    public void KnownNonReasoningModelSkipsTheParameterEntirely()
    {
        // 否定表列只是為了省掉一次註定失敗的來回，不影響正確性。
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        provider.GenerateAsync(UserMessages, options, "gpt-4o").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsNull(payload["reasoning_effort"]);
    }

    [Test]
    public void DisableReasoningSendsEffortNone()
    {
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        var options = new RimLLMChatOptions { DisableReasoning = true };

        provider.GenerateAsync(UserMessages, options, "o5-preview").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("none", (string)payload["reasoning_effort"]);
    }

    [Test]
    public void AutoEffortSendsNothingAndLeavesProviderDefault()
    {
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));

        provider.GenerateAsync(UserMessages, new ChatOptions(), "o5-preview").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsNull(payload["reasoning_effort"]);
        ClassicAssert.IsNull(payload["reasoning"]);
    }

    // ---------- xAI：不能關閉思考 ----------

    [Test]
    public void GrokReceivesEffort()
    {
        var provider = new TestGrokProvider(SettingsWithKey(ProviderIds.Grok));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        provider.GenerateAsync(UserMessages, options, "grok-4.5").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("high", (string)payload["reasoning_effort"]);
    }

    [Test]
    public void GrokSilentlyIgnoresDisableBecauseReasoningCannotBeTurnedOff()
    {
        // xAI 官方文件明載推理無法關閉，送出關閉指令只會換來 400。
        var provider = new TestGrokProvider(SettingsWithKey(ProviderIds.Grok));
        var options = new RimLLMChatOptions { DisableReasoning = true };

        provider.GenerateAsync(UserMessages, options, "grok-4.5").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsNull(payload["reasoning_effort"]);
    }

    // ---------- thinking.type 方言：DeepSeek / Z.ai / Kimi ----------

    [Test]
    public void DeepSeekSendsThinkingSwitchWithEffort()
    {
        var provider = new TestDeepSeekPayloadProvider(SettingsWithKey(ProviderIds.DeepSeek));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium } };

        provider.GenerateAsync(UserMessages, options, "deepseek-v4-pro").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.CapturedPayload).AsObject();
        ClassicAssert.AreEqual("enabled", (string)payload["thinking"].AsObject()["type"]);
        ClassicAssert.AreEqual("medium", (string)payload["reasoning_effort"]);
    }

    [Test]
    public void DeepSeekDisableSendsThinkingDisabledWithoutEffort()
    {
        var provider = new TestDeepSeekPayloadProvider(SettingsWithKey(ProviderIds.DeepSeek));
        var options = new RimLLMChatOptions { DisableReasoning = true };

        provider.GenerateAsync(UserMessages, options, "deepseek-v4-pro").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.CapturedPayload).AsObject();
        ClassicAssert.AreEqual("disabled", (string)payload["thinking"].AsObject()["type"]);
        ClassicAssert.IsNull(payload["reasoning_effort"], "關閉思考時不該再附上強度，兩者語意矛盾。");
    }

    [Test]
    public void ZaiSendsThinkingSwitch()
    {
        var provider = new TestZaiProvider(SettingsWithKey(ProviderIds.Zai));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };

        provider.GenerateAsync(UserMessages, options, "glm-4.6").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("enabled", (string)payload["thinking"].AsObject()["type"]);
        ClassicAssert.AreEqual("low", (string)payload["reasoning_effort"]);
    }

    [Test]
    public void KimiMapsEffortOntoItsOwnVocabulary()
    {
        // Kimi 的詞彙是 low / high / max，沒有 medium。
        var settings = SettingsWithKey(ProviderIds.Kimi);

        var mediumProvider = new TestKimiPayloadProvider(settings);
        mediumProvider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium } },
            "kimi-k3").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("high", (string)JsonNode.Parse(mediumProvider.CapturedPayload).AsObject()["reasoning_effort"]);

        var highProvider = new TestKimiPayloadProvider(settings);
        highProvider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "kimi-k3").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("max", (string)JsonNode.Parse(highProvider.CapturedPayload).AsObject()["reasoning_effort"]);
    }

    // ---------- Qwen：enable_thinking + thinking_budget ----------

    [Test]
    public void QwenSendsEnableThinkingAndBudget()
    {
        var provider = new TestQwenProvider(SettingsWithKey(ProviderIds.Qwen));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium } };

        provider.GenerateAsync(UserMessages, options, "qwen-plus").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsTrue((bool)payload["enable_thinking"]);
        ClassicAssert.AreEqual(2048, (int)payload["thinking_budget"]);
        ClassicAssert.IsNull(payload["reasoning_effort"], "Qwen 不認得 reasoning_effort，送出只會是雜訊。");
    }

    [Test]
    public void QwenDisableSendsEnableThinkingFalseWithoutBudget()
    {
        var provider = new TestQwenProvider(SettingsWithKey(ProviderIds.Qwen));
        var options = new RimLLMChatOptions { DisableReasoning = true };

        provider.GenerateAsync(UserMessages, options, "qwen-plus").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsFalse((bool)payload["enable_thinking"]);
        ClassicAssert.IsNull(payload["thinking_budget"]);
    }

    // ---------- 其餘 OpenAI 相容供應商 ----------

    [Test]
    public void OpenAiCompatibleProvidersReceiveReasoningEffort()
    {
        // Groq、NVIDIA、MiniMax、本地相容端點都宣稱 OpenAI 相容，先以標準欄位送出；
        // 不支援的模型會由降級記憶自動退化，不會壞掉。
        var groq = new TestGroqProvider(SettingsWithKey(ProviderIds.Groq));
        groq.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } },
            "qwen/qwen3.6-27b").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("low", (string)JsonNode.Parse(groq.InterceptedPayload).AsObject()["reasoning_effort"]);

        var local = new TestOpenAICompatibleProvider(SettingsWithKey(ProviderIds.OpenAICompatible));
        local.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "my-local-model").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("high", (string)JsonNode.Parse(local.InterceptedPayload).AsObject()["reasoning_effort"]);
    }

    [Test]
    public void OpenRouterUsesItsUnifiedReasoningObject()
    {
        var provider = new TestOpenRouterProvider(SettingsWithKey(ProviderIds.OpenRouter));
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        provider.GenerateAsync(UserMessages, options, "google/gemini-3.5-flash-lite").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("high", (string)payload["reasoning"]["effort"]);
        ClassicAssert.IsNull(payload["reasoning_effort"]);
    }

    // ---------- Gemini：經 OpenAI 相容端點，頂層 reasoning_effort ----------

    [Test]
    public void GeminiSendsReasoningEffortForAnyModel()
    {
        // Gemini 改走 OpenAI 相容端點後，不再以模型名靜態排除思考參數：
        // 認不出來的未來模型也樂觀送出，不支援時由服務端的 400 觸發自癒重試。
        var settings = SettingsWithKey(ProviderIds.Gemini);
        var provider = new TestGeminiProvider(settings);

        provider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "gemini-5-flash").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("high", (string)JsonNode.Parse(provider.InterceptedPayload).AsObject()["reasoning_effort"]);
    }

    [Test]
    public void GeminiOmitsReasoningEffortWhenAuto()
    {
        var provider = new TestGeminiProvider(SettingsWithKey(ProviderIds.Gemini));

        provider.GenerateAsync(
            UserMessages,
            new ChatOptions(),
            "gemini-2.0-flash").GetAwaiter().GetResult();

        ClassicAssert.IsNull(JsonNode.Parse(provider.InterceptedPayload).AsObject()["reasoning_effort"]);
    }

    [Test]
    public void RememberedGeminiRejectionSkipsReasoningEffort()
    {
        RimLLMReasoningSupport.MarkReasoningUnsupported(ProviderIds.Gemini, "gemini-5-flash");
        var provider = new TestGeminiProvider(SettingsWithKey(ProviderIds.Gemini));

        provider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "gemini-5-flash").GetAwaiter().GetResult();

        ClassicAssert.AreEqual(1, provider.WireHandler.RequestBodies.Count);
        ClassicAssert.IsNull(JsonNode.Parse(provider.InterceptedPayload).AsObject()["reasoning_effort"]);
    }

    // ---------- 服務端拒絕後的自動降級 ----------

    [Test]
    public void ReasoningRejectionIsRememberedAndRequestIsRetriedWithoutIt()
    {
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        provider.WireHandler.ScriptResponse(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Unrecognized request argument supplied: reasoning_effort\"}}");

        string text = provider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "some-legacy-model").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("ok", text, "去掉不支援的參數後應該要成功，而不是把錯誤丟給使用者。");
        ClassicAssert.AreEqual(2, provider.WireHandler.RequestBodies.Count);
        ClassicAssert.AreEqual("high", (string)JsonNode.Parse(provider.WireHandler.RequestBodies[0]).AsObject()["reasoning_effort"]);
        ClassicAssert.IsNull(JsonNode.Parse(provider.WireHandler.RequestBodies[1]).AsObject()["reasoning_effort"]);
        ClassicAssert.IsTrue(RimLLMReasoningSupport.IsReasoningUnsupported("OpenAI", "some-legacy-model"));
    }

    /// <summary>
    /// 被拒的是「關閉」指令時，只能學到「這個模型關不掉」，之後帶強度的請求仍要送出參數。
    /// 連線測試一律要求關閉思考，先前一次 400 就把 o 系列整個 session 的強度設定都吃掉了。
    /// </summary>
    [Test]
    public void DisableRejectionOnlyDisablesTheDisableSwitch_EffortIsStillSentLater()
    {
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        provider.WireHandler.ScriptResponse(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Invalid value: 'none'. Supported values are: 'low', 'medium', and 'high'.\",\"param\":\"reasoning_effort\"}}");

        string text = provider.GenerateAsync(
            UserMessages, new RimLLMChatOptions { DisableReasoning = true }, "o5-preview").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("ok", text);
        ClassicAssert.AreEqual(2, provider.WireHandler.RequestBodies.Count);
        ClassicAssert.AreEqual("none", (string)JsonNode.Parse(provider.WireHandler.RequestBodies[0]).AsObject()["reasoning_effort"]);
        ClassicAssert.IsNull(JsonNode.Parse(provider.WireHandler.RequestBodies[1]).AsObject()["reasoning_effort"],
            "重打時關閉指令要被拿掉");
        ClassicAssert.IsTrue(RimLLMReasoningSupport.IsDisableUnsupported("OpenAI", "o5-preview"));
        ClassicAssert.IsFalse(RimLLMReasoningSupport.IsReasoningUnsupported("OpenAI", "o5-preview"),
            "拒絕關閉不等於不支援思考參數");

        provider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "o5-preview").GetAwaiter().GetResult();

        ClassicAssert.AreEqual(3, provider.WireHandler.RequestBodies.Count);
        ClassicAssert.AreEqual("high", (string)JsonNode.Parse(provider.WireHandler.RequestBodies[2]).AsObject()["reasoning_effort"],
            "之後帶強度的請求仍必須送出 reasoning_effort");

        // 學到關不掉之後，再次要求關閉不該再浪費一次來回。
        provider.GenerateAsync(
            UserMessages, new RimLLMChatOptions { DisableReasoning = true }, "o5-preview").GetAwaiter().GetResult();
        ClassicAssert.AreEqual(4, provider.WireHandler.RequestBodies.Count);
        ClassicAssert.IsNull(JsonNode.Parse(provider.WireHandler.RequestBodies[3]).AsObject()["reasoning_effort"]);
    }

    [Test]
    public void RememberedRejectionSkipsTheParameterOnLaterRequests()
    {
        var settings = SettingsWithKey("OpenAI");
        RimLLMReasoningSupport.MarkReasoningUnsupported("OpenAI", "some-legacy-model");

        var provider = new TestOpenAIProvider(settings);
        provider.GenerateAsync(
            UserMessages,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
            "some-legacy-model").GetAwaiter().GetResult();

        ClassicAssert.AreEqual(1, provider.WireHandler.RequestBodies.Count, "已知不支援就不該再浪費一次來回。");
        ClassicAssert.IsNull(JsonNode.Parse(provider.WireHandler.RequestBodies[0]).AsObject()["reasoning_effort"]);
    }

    [Test]
    public void TemperatureRejectionIsRememberedAndRetriedWithoutTemperature()
    {
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        provider.WireHandler.ScriptResponse(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0.7 with this model.\"}}");

        string text = provider.GenerateAsync(
            UserMessages,
            new ChatOptions { Temperature = 0.7f },
            "some-new-reasoning-model").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("ok", text);
        ClassicAssert.AreEqual(2, provider.WireHandler.RequestBodies.Count);
        ClassicAssert.IsNotNull(JsonNode.Parse(provider.WireHandler.RequestBodies[0]).AsObject()["temperature"]);
        ClassicAssert.IsNull(JsonNode.Parse(provider.WireHandler.RequestBodies[1]).AsObject()["temperature"]);
        ClassicAssert.IsTrue(RimLLMReasoningSupport.IsTemperatureUnsupported("OpenAI", "some-new-reasoning-model"));
    }

    [Test]
    public void UnrelatedRejectionIsNotRetried()
    {
        // 只有明確指向這些參數的拒絕才降級，否則會把真正的錯誤掩蓋掉並多花一次費用。
        var provider = new TestOpenAIProvider(SettingsWithKey("OpenAI"));
        provider.WireHandler.ScriptResponse(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Invalid value for 'messages': too many items\"}}");

        Assert.Throws<RimLLMException>(() =>
        {
            try
            {
                provider.GenerateAsync(
                    UserMessages,
                    new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } },
                    "o5-preview").GetAwaiter().GetResult();
            }
            catch (System.AggregateException ex)
            {
                throw ex.InnerException;
            }
        });

        ClassicAssert.AreEqual(1, provider.WireHandler.RequestBodies.Count);
    }
}

public class TestGrokProvider : GrokProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public TestGrokProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings, ProviderId, Settings.GetEndpoint(ProviderId, DefaultEndpoint), model, WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestQwenProvider : QwenProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public TestQwenProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings, ProviderId, Settings.GetEndpoint(ProviderId, DefaultEndpoint), model, WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestGroqProvider : GroqProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public TestGroqProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings, ProviderId, Settings.GetEndpoint(ProviderId, DefaultEndpoint), model, WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestOpenAICompatibleProvider : OpenAICompatibleProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public TestOpenAICompatibleProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings, ProviderId, "https://localhost:1234/v1", model, WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}
