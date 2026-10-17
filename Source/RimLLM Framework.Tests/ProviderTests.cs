using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using OpenAI;
using OpenAI.Chat;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class ProviderTests
{
    [Test]
    public void TestOpenRouterFallbackPayload()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["OpenRouter"] = "mock-key";

        var provider = new TestOpenRouterProvider(mockSettings);


        // 1. 測試單一模型
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        provider.GenerateAsync(messages, null, "model-a").GetAwaiter().GetResult();
        var payloadSingle = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("model-a", (string)payloadSingle["model"]);
        ClassicAssert.IsNull(payloadSingle["models"]);

        // 2. 測試多個模型 (逗號分隔)
        provider.GenerateAsync(messages, null, "model-a, model-b , model-c").GetAwaiter().GetResult();
        var payloadMultiple = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsNull(payloadMultiple["model"]);
        ClassicAssert.IsNotNull(payloadMultiple["models"]);

        var modelsArray = payloadMultiple["models"] as JsonArray;
        ClassicAssert.IsNotNull(modelsArray);
        ClassicAssert.AreEqual(3, modelsArray.Count);
        ClassicAssert.AreEqual("model-a", (string)modelsArray[0]);
        ClassicAssert.AreEqual("model-b", (string)modelsArray[1]);
        ClassicAssert.AreEqual("model-c", (string)modelsArray[2]);
    }

    [Test]
    public void TestOpenAIProviderCacheUsage()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["OpenAI"] = "mock-key";
        var manager = new RimLLMManager(mockSettings);

        // 初始化 SDK 入口，讓 OpenAIProvider 能 RecordUsage
        RimLLMProvider.Initialize(manager);

        var provider = new TestOpenAIProviderWithUsage(mockSettings);

        // 測試 1：標準 OpenAI 的 prompt_tokens_details.cached_tokens
        provider.MockResponse = "{" +
            "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"hello\"}}]," +
            "\"usage\": {" +
            "  \"prompt_tokens\": 1000," +
            "  \"completion_tokens\": 200," +
            "  \"prompt_tokens_details\": {" +
            "    \"cached_tokens\": 600" +
            "  }" +
            "}" +
            "}";

        var messages = new List<ChatMessage> { new(ChatRole.User, "ping") };
        string res = provider.GenerateAsync(messages, null, "gpt-4o").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("hello", res);
        ClassicAssert.AreEqual(1000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(200, mockSettings.TotalCompletionTokens);

        // 驗證 UsageTracker 內部的統計數據
        var stats = manager.UsageTracker.ProviderStatistics["OpenAI"];
        ClassicAssert.AreEqual(1000, stats.TotalPromptTokens);
        ClassicAssert.AreEqual(600, stats.CachedPromptTokens);
        ClassicAssert.AreEqual(0.6f, stats.ContextCacheHitRate, 0.0001f);

        // 測試 2：OpenAI 標準格式的 cached_tokens 位於 prompt_tokens_details
        mockSettings.TotalPromptTokens = 0;
        mockSettings.TotalCompletionTokens = 0;
        stats.TotalPromptTokens = 0;
        stats.CachedPromptTokens = 0;

        provider.MockResponse = "{" +
            "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"hello 2\"}}]," +
            "\"usage\": {" +
            "  \"prompt_tokens\": 2000," +
            "  \"completion_tokens\": 300," +
            "  \"prompt_tokens_details\": {" +
            "    \"cached_tokens\": 800" +
            "  }" +
            "}" +
            "}";

        string res2 = provider.GenerateAsync(messages, null, "gpt-4o").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("hello 2", res2);
        ClassicAssert.AreEqual(2000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(300, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(2000, stats.TotalPromptTokens);
        ClassicAssert.AreEqual(800, stats.CachedPromptTokens);
        ClassicAssert.AreEqual(0.4f, stats.ContextCacheHitRate, 0.0001f);
    }

    [Test]
    public void TestGeminiConnectionTestUsesGemini35Flash()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["Gemini"] = "mock-key";

        var provider = new TestGeminiProvider(mockSettings);
        TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

        ClassicAssert.IsTrue(result.Success);
        ClassicAssert.AreEqual("gemini-3.5-flash", result.Model);
        ClassicAssert.IsNotNull(provider.InterceptedPayload);
        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("gemini-3.5-flash", (string)payload["model"]);
    }

    [Test]
    public void TestGeminiRequestsGoToOpenAiCompatibleEndpoint()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["Gemini"] = "mock-key";

        var provider = new TestGeminiProvider(mockSettings);
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        string response = provider.GenerateAsync(messages, null, "gemini-2.5-flash").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("ok", response);
        ClassicAssert.IsTrue(
            provider.InterceptedUrl.StartsWith("https://generativelanguage.googleapis.com/v1beta/openai"),
            "Gemini 應經官方 OpenAI 相容端點存取，實際：" + provider.InterceptedUrl);
    }

    [Test]
    public void TestOpenRouterConnectionTestUsesFreeRouter()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["OpenRouter"] = "mock-key";

        var provider = new TestOpenRouterProvider(mockSettings);
        TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

        ClassicAssert.IsTrue(result.Success);
        ClassicAssert.AreEqual("openrouter/free", result.Model);
        ClassicAssert.IsNotNull(provider.InterceptedPayload);
        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("openrouter/free", (string)payload["model"]);

        // 連線測試必須留足輸出額度並關閉思考：額度太低時思考型模型會把額度全花在內部推理上，
        // content 變成空的，明明連得上卻回報「回傳空白內容」。
        ClassicAssert.AreEqual("none", (string)payload["reasoning"].AsObject()["effort"]);
        ClassicAssert.GreaterOrEqual((int)payload["max_tokens"], 64);
    }

    [Test]
    public void TestZaiConnectionTestUsesFreeFlashModel()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys[ProviderIds.Zai] = "mock-key";

        var provider = new TestZaiProvider(mockSettings);
        TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

        ClassicAssert.IsTrue(result.Success);
        ClassicAssert.AreEqual("glm-4.5-flash", result.Model);
        ClassicAssert.AreEqual("https://api.z.ai/api/paas/v4/chat/completions", provider.InterceptedUrl);
        ClassicAssert.IsNotNull(provider.InterceptedPayload);
        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual("glm-4.5-flash", (string)payload["model"]);
    }

    [Test]
    public void TestManagerRegistersZaiProvider()
    {
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        ClassicAssert.Contains(ProviderIds.Zai, manager.GetRegisteredProviderIds());
    }

    [Test]
    public void TestManagerRegistersPlayer2Provider()
    {
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        ClassicAssert.Contains(ProviderIds.Player2, manager.GetRegisteredProviderIds());
    }

    [Test]
    public void TestPlayer2UsesLocalEndpointAndFixedModel()
    {
        var mockSettings = new MockSettings();

        var provider = new TestPlayer2Provider(mockSettings);
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        string response = provider.GenerateAsync(messages, null, Player2Provider.DefaultModelName).GetAwaiter().GetResult();

        ClassicAssert.AreEqual("ok", response);
        ClassicAssert.IsTrue(
            provider.InterceptedUrl.StartsWith(Player2Provider.LocalDefaultEndpoint),
            "Player2 預設應連向本機 App，實際：" + provider.InterceptedUrl);
        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.AreEqual(Player2Provider.DefaultModelName, (string)payload["model"]);
    }

    [Test]
    public void TestPlayer2ConnectionTestNeedsNoApiKey()
    {
        // 本機 App 模式不需要金鑰：未填金鑰時連線測試仍應送出請求，而非直接回報未設定。
        var mockSettings = new MockSettings();

        var provider = new TestPlayer2Provider(mockSettings);
        TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

        ClassicAssert.IsTrue(result.Success);
        ClassicAssert.AreEqual(Player2Provider.DefaultModelName, result.Model);
    }

    [Test]
    public void TestPlayer2FetchModelsReturnsFixedList()
    {
        // Player2 沒有 /models 端點，固定回傳單一佔位模型，不發網路請求。
        var mockSettings = new MockSettings();

        var provider = new Player2Provider(mockSettings);
        List<string> models = provider.FetchAvailableModelsAsync().GetAwaiter().GetResult();

        ClassicAssert.AreEqual(1, models.Count);
        ClassicAssert.AreEqual(Player2Provider.DefaultModelName, models[0]);
    }

    [Test]
    public void TestPlayer2RequiresKeyOnlyOnCloudEndpoint()
    {
        // 本機 App 模式免金鑰，雲端（api.player2.game）需 p2Key；其餘供應商不受端點影響。
        ClassicAssert.IsFalse(ProviderIds.RequiresApiKey(ProviderIds.Player2, Player2Provider.LocalDefaultEndpoint));
        ClassicAssert.IsFalse(ProviderIds.RequiresApiKey(ProviderIds.Player2, null));
        ClassicAssert.IsFalse(ProviderIds.RequiresApiKey(ProviderIds.Player2, "http://localhost:4315/v1/"));
        ClassicAssert.IsTrue(ProviderIds.RequiresApiKey(ProviderIds.Player2, "https://api.player2.game/v1"));
        ClassicAssert.IsTrue(ProviderIds.RequiresApiKey(ProviderIds.Player2, "https://api.player2.game/v1/chat/completions"));
        ClassicAssert.IsFalse(ProviderIds.RequiresApiKey(ProviderIds.OpenAICompatible, "https://api.player2.game/v1"));
        ClassicAssert.IsTrue(ProviderIds.RequiresApiKey(ProviderIds.OpenAI, "https://api.openai.com/v1"));
    }

    [Test]
    public void TestPlayer2InstanceRequiresKeyFollowsEndpoint()
    {
        // 實例屬性與靜態 helper 為同一判斷：路由層與 UI 前置檢查看到的語義一致。
        var localSettings = new MockSettings();
        var localProvider = new Player2Provider(localSettings);
        ClassicAssert.IsFalse(localProvider.RequiresApiKey);

        var cloudSettings = new MockSettings();
        cloudSettings.Endpoints[ProviderIds.Player2] = "https://api.player2.game/v1";
        var cloudProvider = new Player2Provider(cloudSettings);
        ClassicAssert.IsTrue(cloudProvider.RequiresApiKey);
    }

    [Test]
    public void TestReasoningEffortPayloads()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["OpenAI"] = "mock-key";
        mockSettings.ApiKeys["Gemini"] = "mock-key";
        mockSettings.ApiKeys["OpenRouter"] = "mock-key";

        var userMsgs = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // 1. OpenAI: o1 Model with ReasoningEffort.Medium
        {
            var provider = new TestOpenAIProvider(mockSettings);
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                MaxOutputTokens = 1500
            };
            string response = provider.GenerateAsync(userMsgs, options, "o1-mini").GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(provider.InterceptedPayload);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.AreEqual("medium", (string)payload["reasoning_effort"]);
            ClassicAssert.AreEqual(1500, (int)payload["max_completion_tokens"]);
            ClassicAssert.IsNull(payload["temperature"]);
            ClassicAssert.IsNull(payload["max_tokens"]);
        }

        // 2. OpenAI: gpt-4o Model with ReasoningEffort.Medium (should NOT include reasoning_effort)
        {
            var provider = new TestOpenAIProvider(mockSettings);
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                Temperature = 0.7f,
                MaxOutputTokens = 1000
            };
            string response = provider.GenerateAsync(userMsgs, options, "gpt-4o").GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(provider.InterceptedPayload);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.IsNull(payload["reasoning_effort"]);
            ClassicAssert.AreEqual(0.7f, (float)payload["temperature"]);
            ClassicAssert.AreEqual(1000, (int)payload["max_tokens"]);
            ClassicAssert.IsNull(payload["max_completion_tokens"]);
        }

        // 4. Gemini：經 OpenAI 相容端點，以頂層 reasoning_effort 表達思考強度
        {
            var provider = new TestGeminiProvider(mockSettings);
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                MaxOutputTokens = 2000
            };
            string response = provider.GenerateAsync(userMsgs, options, "gemini-2.5-flash").GetAwaiter().GetResult();
            ClassicAssert.AreEqual("ok", response);
            ClassicAssert.IsNotNull(provider.InterceptedPayload);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.AreEqual("low", (string)payload["reasoning_effort"]);
            ClassicAssert.AreEqual("gemini-2.5-flash", (string)payload["model"]);
        }

        // 4b. Gemini：明確關閉思考對應 effort "none"，由服務端套用預設行為
        {
            var provider = new TestGeminiProvider(mockSettings);
            var options = new RimLLMChatOptions { DisableReasoning = true };
            string response = provider.GenerateAsync(userMsgs, options, "gemini-2.5-flash").GetAwaiter().GetResult();
            ClassicAssert.AreEqual("ok", response);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.AreEqual("none", (string)payload["reasoning_effort"]);
        }

        // 5. OpenRouter: DeepSeek R1 with ReasoningEffort.Medium
        {
            var provider = new TestOpenRouterProvider(mockSettings);
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
            };
            string response = provider.GenerateAsync(userMsgs, options, "deepseek/deepseek-r1").GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(provider.InterceptedPayload);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.AreEqual("medium", (string)payload["reasoning"].AsObject()["effort"]);
            ClassicAssert.IsNull(payload["reasoning_effort"], "reasoning 與 reasoning_effort 只能擇一，否則服務端會看到矛盾設定。");
        }

        // 5b. OpenRouter: 非 R1 的思考型模型也要送出思考強度。
        // 舊實作把強度限定在 deepseek R1，其餘模型（例如 Gemini）的設定會被靜默丟棄。
        {
            var provider = new TestOpenRouterProvider(mockSettings);
            var options = new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High }
            };
            string response = provider.GenerateAsync(userMsgs, options, "google/gemini-3.5-flash-lite").GetAwaiter().GetResult();
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.AreEqual("high", (string)payload["reasoning"].AsObject()["effort"]);
        }

        // 5c. OpenRouter: 明確關閉思考對應 effort "none"。
        {
            var provider = new TestOpenRouterProvider(mockSettings);
            var options = new RimLLMChatOptions { DisableReasoning = true };
            string response = provider.GenerateAsync(userMsgs, options, "google/gemini-3.5-flash-lite").GetAwaiter().GetResult();
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.AreEqual("none", (string)payload["reasoning"].AsObject()["effort"]);
        }

        // 6. Test ReasoningEffort? = null (Auto) and DisableReasoning = true (None) payloads

        // 6a. OpenAI Auto
        {
            var provider = new TestOpenAIProvider(mockSettings);
            var options = new ChatOptions(); // Reasoning null
            string response = provider.GenerateAsync(userMsgs, options, "o1-mini").GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(provider.InterceptedPayload);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.IsNull(payload["reasoning_effort"]);
        }

        // 6b. Gemini Auto -> 不干預，完全不送 reasoning_effort
        {
            var provider = new TestGeminiProvider(mockSettings);
            var options = new ChatOptions(); // Reasoning null
            string response = provider.GenerateAsync(userMsgs, options, "gemini-2.5-flash").GetAwaiter().GetResult();
            ClassicAssert.AreEqual("ok", response);
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.IsNull(payload["reasoning_effort"]);
        }

        // 6i. OpenRouter Auto -> 不干預，完全不送 reasoning
        {
            var provider = new TestOpenRouterProvider(mockSettings);
            var options = new ChatOptions();
            string response = provider.GenerateAsync(userMsgs, options, "deepseek/deepseek-r1").GetAwaiter().GetResult();
            var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
            ClassicAssert.IsNull(payload["reasoning"]);
        }
    }

    [Test]
    public void TestUnifiedStreamingInGenerateAsync()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockStream:model-s"]
        };
        mockSettings.EnabledProviders["MockStream"] = true;
        mockSettings.ApiKeys["MockStream"] = "mock-key";

        var manager = new RimLLMManager(mockSettings);

        var chunksReceived = new List<string>();
        var mockStream = new MockStreamProvider
        {
            ProviderId = "MockStream",
            StreamHandler = (msgs, opts, model, onChunk) =>
            {
                onChunk("Hello ");
                onChunk("World");
                onChunk("!");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        };
        manager.RegisterProvider(mockStream);

        const string modId = "test.stream.unified.id";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "test prompt") };
        var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
        string result = "";
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                if (!string.IsNullOrEmpty(enumerator.Current.Text))
                {
                    result += enumerator.Current.Text;
                    chunksReceived.Add(enumerator.Current.Text);
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ClassicAssert.AreEqual("Hello World!", result);
        ClassicAssert.AreEqual(3, chunksReceived.Count);
        ClassicAssert.AreEqual("Hello ", chunksReceived[0]);
        ClassicAssert.AreEqual("World", chunksReceived[1]);
        ClassicAssert.AreEqual("!", chunksReceived[2]);
    }

    [Test]
    public void TestReasoningThoughtsPackaging()
    {
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys["OpenAI"] = "mock-key";
        mockSettings.ApiKeys["Gemini"] = "mock-key";

        var userMsgs = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // 1. 測試 OpenAIProvider (DeepSeek-R1 格式 reasoning_content)
        {
            var provider = new TestOpenAIProviderWithReasoning(mockSettings);
            provider.WireHandler.ResponseBody = "{" +
                "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"Hello, user!\", " +
                "\"reasoning_content\": \"Assessing the situation...\"}}]}";
            string result = provider.GenerateAsync(userMsgs, null, "deepseek-reasoning").GetAwaiter().GetResult();
            ClassicAssert.IsTrue(result.Contains("<think>"));
            ClassicAssert.IsTrue(result.Contains("</think>"));
            ClassicAssert.IsTrue(result.Contains("Assessing the situation..."));
            ClassicAssert.IsTrue(result.Contains("Hello, user!"));
        }

        // 2. Gemini 經 OpenAI 相容端點：reasoning_content 同樣包裝成 <think>
        {
            var provider = new TestGeminiProvider(mockSettings);
            provider.WireHandler.ResponseBody = "{" +
                "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"Response from Gemini\", " +
                "\"reasoning_content\": \"Thinking deeply...\"}}]}";
            string result = provider.GenerateAsync(userMsgs, null, "gemini-2.5-flash").GetAwaiter().GetResult();
            ClassicAssert.IsTrue(result.Contains("<think>"));
            ClassicAssert.IsTrue(result.Contains("</think>"));
            ClassicAssert.IsTrue(result.Contains("Thinking deeply..."));
            ClassicAssert.IsTrue(result.Contains("Response from Gemini"));
        }
    }

    [Test]
    public void TestExternalProviderRegistration()
    {
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        // 1. 註冊外部供應商成功，且出現在已註冊清單中
        var external = new MockTestProvider { ProviderId = "MyCustomProvider" };
        manager.RegisterProvider(external);
        ClassicAssert.IsTrue(manager.GetRegisteredProviderIds().Contains("MyCustomProvider"));

        // 2. 外部供應商視為註冊即啟用
        ClassicAssert.IsTrue(manager.IsProviderEnabled("MyCustomProvider"));

        // 3. 重複 ProviderId 應擲出，防止覆蓋既有供應商（含內建）
        Assert.Throws<InvalidOperationException>(() =>
            manager.RegisterProvider(new MockTestProvider { ProviderId = "MyCustomProvider" }));
        Assert.Throws<InvalidOperationException>(() =>
            manager.RegisterProvider(new MockTestProvider { ProviderId = "OpenAI" }));

        // 4. 內建供應商仍依設定啟用狀態
        mockSettings.EnabledProviders["OpenAI"] = false;
        ClassicAssert.IsFalse(manager.IsProviderEnabled("OpenAI"));
    }

    [Test]
    public void TestHttpStatusCodeMapping()
    {
        var probe = new HttpErrorProbeProvider(new MockSettings());

        var notFound = Assert.Throws<RimLLMException>(() =>
            probe.Probe(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"model does not exist\"}}"));
        ClassicAssert.AreEqual(LLMError.ModelNotFound, notFound.Error, "404 應對應 ModelNotFound 而非可重試的 Unknown");

        var badRequest = Assert.Throws<RimLLMException>(() =>
            probe.Probe(System.Net.HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad input\"}}"));
        ClassicAssert.AreEqual(LLMError.InvalidResponse, badRequest.Error, "400 屬於請求本身的問題，應對應 InvalidResponse");
        ClassicAssert.IsFalse(badRequest.IsSchemaRejection, "與 schema 無關的 400 不應標記為 schema 拒絕");

        var timeout = Assert.Throws<RimLLMException>(() =>
            probe.Probe(System.Net.HttpStatusCode.RequestTimeout, "timeout"));
        ClassicAssert.AreEqual(LLMError.Timeout, timeout.Error, "408 應對應 Timeout");

        var paymentRequired = Assert.Throws<RimLLMException>(() =>
            probe.Probe((System.Net.HttpStatusCode)402, "payment required"));
        ClassicAssert.AreEqual(LLMError.QuotaExceeded, paymentRequired.Error, "402 應對應 QuotaExceeded");
    }

    [Test]
    public void TestSchemaRejectionIsMarkedOnRelevant400()
    {
        var probe = new HttpErrorProbeProvider(new MockSettings());

        var ex = Assert.Throws<RimLLMException>(() =>
            probe.Probe(System.Net.HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"response_format json_schema is not supported\"}}"));

        ClassicAssert.AreEqual(LLMError.InvalidResponse, ex.Error);
        ClassicAssert.IsTrue(ex.IsSchemaRejection, "提及 response_format／json_schema 的 400 應標記為 schema 拒絕");
    }

    [Test]
    public void TestQuotaDetectionIsCaseInsensitive()
    {
        var probe = new HttpErrorProbeProvider(new MockSettings());

        var ex = Assert.Throws<RimLLMException>(() =>
            probe.Probe((System.Net.HttpStatusCode)429, "Insufficient_Quota for this account"));

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex.Error, "配額關鍵字比對必須大小寫不敏感");
    }

    [Test]
    public void TestDerivedProviderDoesNotSendNativeSchemaPayload()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["Kimi:moonshot-v1-8k"],
            EnableNativeSchema = true
        };
        mockSettings.EnabledProviders["Kimi"] = true;
        mockSettings.ApiKeys["Kimi"] = "key";

        var manager = new RimLLMManager(mockSettings);
        var provider = new TestKimiPayloadProvider(mockSettings);


        provider.GenerateAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new RimLLMChatOptions(),
            "moonshot-v1-8k").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.CapturedPayload).AsObject();
        ClassicAssert.IsNull(payload["response_format"],
            "未宣告支援原生 schema 的衍生供應商不應收到 response_format");
    }

    [Test]
    public void TestWhitelistedDerivedProviderSendsNativeSchemaPayload()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["DeepSeek:deepseek-chat"],
            EnableNativeSchema = true
        };
        mockSettings.EnabledProviders["DeepSeek"] = true;
        mockSettings.ApiKeys["DeepSeek"] = "key";

        var provider = new TestDeepSeekPayloadProvider(mockSettings);


        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var options = new ChatOptions { AdditionalProperties = [] };
        RimLLMSchemaResult schema = RimLLMSchemaBuilder.Build(typeof(TestDataStructure));
        options.AdditionalProperties["rimllm_response_schema"] = schema.Json;
        options.AdditionalProperties["strict"] = schema.StrictCompatible;
        provider.GenerateAsync(messages, options, "deepseek-chat").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.CapturedPayload).AsObject();
        ClassicAssert.IsNotNull(payload["response_format"], "已驗證支援的衍生供應商應收到 response_format");
        ClassicAssert.AreEqual("custom_type", (string)payload["response_format"]?.AsObject()?["json_schema"]?.AsObject()?["name"]);
        ClassicAssert.IsTrue(payload["response_format"]?.AsObject()?["json_schema"]?.AsObject()?["strict"]?.GetValue<bool>() == true,
            "不含 Dictionary 的型別應維持 strict 模式");
    }

    [Test]
    public void TestDictionaryTypeDisablesStrictMode()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["DeepSeek:deepseek-chat"],
            EnableNativeSchema = true
        };
        mockSettings.EnabledProviders["DeepSeek"] = true;
        mockSettings.ApiKeys["DeepSeek"] = "key";

        var provider = new TestDeepSeekPayloadProvider(mockSettings);


        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var options = new ChatOptions { AdditionalProperties = [] };
        RimLLMSchemaResult schema = RimLLMSchemaBuilder.Build(typeof(ComplexTestDataStructure));
        options.AdditionalProperties["rimllm_response_schema"] = schema.Json;
        options.AdditionalProperties["strict"] = schema.StrictCompatible;
        provider.GenerateAsync(messages, options, "deepseek-chat").GetAwaiter().GetResult();

        var payload = JsonNode.Parse(provider.CapturedPayload).AsObject();
        ClassicAssert.IsFalse(payload["response_format"]?.AsObject()?["json_schema"]?.AsObject()?["strict"]?.GetValue<bool>() == true,
            "含 Dictionary 的型別必須關閉 strict，否則服務端會拒絕開放式 map");
    }



    /// <summary>
    /// response_format 必須只由 Patch 的原始 JSON 供應，MEAI 的 ChatOptions.ResponseFormat 要保持 null。
    /// 交給 SDK 的 ChatCompletionOptions.ResponseFormat 屬性的話，該屬性是 JsonPatch 支撐的，
    /// 在框架動過同一個模型的 Patch 之後，RimWorld 的 Mono 會把它還原成基底 ChatResponseFormat，
    /// 送出請求時序列化直接拋 "The WriteCore method should be invoked on an overriding type..."。
    /// </summary>
    [Test]
    public void TestResponseFormatIsSentThroughPatchOnly()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["DeepSeek:deepseek-chat"],
            EnableNativeSchema = true
        };
        mockSettings.EnabledProviders["DeepSeek"] = true;
        mockSettings.ApiKeys["DeepSeek"] = "key";

        var provider = new TestDeepSeekPayloadProvider(mockSettings);

        RimLLMSchemaResult schema = RimLLMSchemaBuilder.Build(typeof(TestDataStructure));
        var requestOptions = new ChatOptions { AdditionalProperties = [] };
        requestOptions.AdditionalProperties["rimllm_response_schema"] = schema.Json;
        requestOptions.AdditionalProperties["strict"] = schema.StrictCompatible;

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        using (var client = provider.CreateChatClient("deepseek-chat"))
        {
            client.GetResponseAsync(messages, requestOptions).GetAwaiter().GetResult();
        }

        var payload = JsonNode.Parse(provider.CapturedPayload).AsObject();
        ClassicAssert.IsNotNull(payload["response_format"]?["json_schema"]?["schema"],
            "response_format 仍必須完整出現在送出的 payload 中");
        ClassicAssert.AreEqual("custom_type", payload["response_format"]?["json_schema"]?["name"]?.ToString());
    }

    [Test]
    public void TestStreamRequestChargesAntiAbuseOnce()
    {
        var mockSettings = new MockSettings
        {
            FallbackChain = ["MockStreamOnce:model-a"],
            EnableAntiAbuse = true,
            MaxRequestsPerWindow = 1,
            ThrottlingWindowSeconds = 60
        };
        mockSettings.EnabledProviders["MockStreamOnce"] = true;
        mockSettings.ApiKeys["MockStreamOnce"] = "key";

        var manager = new RimLLMManager(mockSettings);
        manager.RegisterProvider(new MockStreamProvider
        {
            ProviderId = "MockStreamOnce",
            StreamHandler = (msgs, opts, model, onChunk) =>
            {
                onChunk("ok");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        });

        const string modId = "test.stream.antiabuse";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
        string result = "";
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                result += enumerator.Current.Text;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ClassicAssert.AreEqual("ok", result, "單一請求不得重複計入防濫用額度");
    }

}

// Shared mock and helper classes used by test suites
public class TestDataStructure
{
    public int Value { get; set; } = 100;
    public string Message { get; set; } = "default";
}

public class MockSettings : IRimLLMSettings, IDailyTokenBudget
{
    public List<string> FallbackChain { get; set; } = [];
    public float ApiTimeout { get; set; } = 30f;
    public bool DetailedLogging { get; set; } = true;
    public ReasoningEffort? DefaultReasoningEffort { get; set; } = null;
    public int MaxRetries { get; set; } = 3;
    public float RetryDelay { get; set; } = 3f;
    public int MaxConcurrentRequests { get; set; } = 2;
    public long TotalPromptTokens { get; set; } = 0;
    public long TotalCompletionTokens { get; set; } = 0;
    public float TotalEstimatedCost { get; set; } = 0f;
    public float DailyBudgetLimit { get; set; } = 0f;
    public long DailyTokenBudgetLimit { get; set; } = 0;
    public int BudgetPolicy { get; set; } = 0;
    public bool EnableAntiAbuse { get; set; } = true;
    public int MaxRequestsPerWindow { get; set; } = 10;
    public int ThrottlingWindowSeconds { get; set; } = 10;
    public int CoolDownDurationSeconds { get; set; } = 60;
    public float DailyAccumulatedCost { get; set; } = 0f;
    public long DailyAccumulatedTokens { get; set; } = 0;
    public string DailyBudgetResetDate { get; set; } = "";
    public int RoutingStrategy { get; set; } = 0;
    public bool EnableNativeSchema { get; set; } = true;
    public bool EnableJsonRepair { get; set; } = true;

    public string EmbeddingProvider { get; set; } = "Disabled";
    public string EmbeddingModel { get; set; } = "text-embedding-004";
    public string EmbeddingEndpoint { get; set; } = "";
    public string EmbeddingApiKey { get; set; } = "";

    public Dictionary<string, string> ApiKeys = [];
    public Dictionary<string, string> Endpoints = [];
    public Dictionary<string, bool> EnabledProviders = [];
    public Dictionary<string, List<string>> ModelLists = [];

    public string GetApiKey(string providerId) => ApiKeys.TryGetValue(providerId, out var val) ? val : "";
    public string GetActiveApiKey(string providerId) => GetApiKey(providerId);
    public string GetEndpoint(string providerId, string defaultVal) => Endpoints.TryGetValue(providerId, out var val) ? val : defaultVal;
    public bool IsProviderEnabled(string providerId) => EnabledProviders.TryGetValue(providerId, out var val) ? val : true;
    public List<string> GetModelList(string providerId) => ModelLists.TryGetValue(providerId, out var val) ? val : [];

    public string GetDefaultModel(string providerId, string defaultVal)
    {
        var list = GetModelList(providerId);
        return list.Count > 0 ? list[0] : defaultVal;
    }

    public void SetModelList(string providerId, List<string> models) => ModelLists[providerId] = models;

    public Dictionary<string, int> ModelLevelOverrides = new(StringComparer.OrdinalIgnoreCase);
    public int GetModelLevelOverride(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        if (ModelLevelOverrides.TryGetValue(modelName, out var level)) return level;
        int colonIndex = modelName.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < modelName.Length - 1)
        {
            if (ModelLevelOverrides.TryGetValue(modelName.Substring(colonIndex + 1), out level)) return level;
        }
        return 0;
    }

    public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = [];

    public void MarkTelemetryDirty() { }

    public void SaveTelemetry() { }

    public void Write() { }
}

public class MockTestProvider : ILLMProvider
{
    public string ProviderId { get; set; }
    public bool RequiresApiKey { get; set; } = true;
    public LLMProviderCapabilities Capabilities { get; set; } = new LLMProviderCapabilities { SupportsStreaming = true, SupportsNativeStructuredOutput = false, SupportsUsageMetadata = true };

    public Func<IEnumerable<ChatMessage>, ChatOptions, string, System.Threading.Tasks.Task<string>> GenerateHandler { get; set; }
    public Func<IEnumerable<ChatMessage>, ChatOptions, string, Action<string>, System.Threading.Tasks.Task> StreamHandler { get; set; }

    public IChatClient CreateChatClient(string model)
    {
        return new MockCustomChatClient
        {
            GetResponseHandler = async (msgs, opts) =>
            {
                string text = GenerateHandler != null ? await GenerateHandler(msgs, opts, model) : "";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
            },
            StreamHandler = async (msgs, opts, cb) =>
            {
                if (StreamHandler != null)
                {
                    await StreamHandler(msgs, opts, model, cb);
                }
                else
                {
                    string text = GenerateHandler != null ? await GenerateHandler(msgs, opts, model) : "mock-text";
                    cb(text);
                }
            }
        };
    }

    public System.Threading.Tasks.Task<TestResult> TestConnectionAsync()
    {
        return System.Threading.Tasks.Task.FromResult(new TestResult { Success = true });
    }

    public System.Threading.Tasks.Task<List<string>> FetchAvailableModelsAsync()
    {
        return System.Threading.Tasks.Task.FromResult(new List<string> { "model-test" });
    }
}

public class TestGeminiProvider : GeminiProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public string InterceptedUrl => WireHandler.LastRequestUrl;

    public TestGeminiProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class CapturingHttpMessageHandler : System.Net.Http.HttpMessageHandler
{
    public System.Collections.Generic.List<string> RequestBodies { get; } = [];

    public System.Collections.Generic.List<string> RequestUrls { get; } = [];

    public string ResponseBody { get; set; } = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

    public string ResponseContentType { get; set; } = "application/json";

    public string LastRequestBody => RequestBodies.Count == 0 ? null : RequestBodies[RequestBodies.Count - 1];

    public string FirstRequestBody => RequestBodies.Count == 0 ? null : RequestBodies[0];

    public string LastRequestUrl => RequestUrls.Count == 0 ? null : RequestUrls[RequestUrls.Count - 1];

    /// <summary>
    /// 依序套用的回應腳本（狀態碼與 body）。用完後回到預設的 200 + <see cref="ResponseBody"/>。
    /// 供「服務端先拒絕某個參數、框架去掉後才接受」這類降級行為的測試使用。
    ///
    /// 用 List 當佇列而非 Queue&lt;T&gt;：這個測試組件載入 Queue&lt;T&gt; 會因為
    /// netstandard 轉送與 mscorlib 的衝突而丟 TypeLoadException（與專案既有的 Stack&lt;T&gt; 地雷同源）。
    /// </summary>
    public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<System.Net.HttpStatusCode, string>> ScriptedResponses { get; } =
        [];

    public void ScriptResponse(System.Net.HttpStatusCode status, string body)
    {
        ScriptedResponses.Add(new System.Collections.Generic.KeyValuePair<System.Net.HttpStatusCode, string>(status, body));
    }

    protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request,
        System.Threading.CancellationToken cancellationToken)
    {
        string body = request.Content != null
            ? request.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            : null;
        RequestBodies.Add(body ?? string.Empty);
        RequestUrls.Add(request.RequestUri != null ? request.RequestUri.ToString() : null);

        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK;
        string responseBody = ResponseBody;
        if (ScriptedResponses.Count > 0)
        {
            var scripted = ScriptedResponses[0];
            ScriptedResponses.RemoveAt(0);
            status = scripted.Key;
            responseBody = scripted.Value;
        }

        var response = new System.Net.Http.HttpResponseMessage(status);
        response.Content = new System.Net.Http.StringContent(
            responseBody ?? string.Empty,
            System.Text.Encoding.UTF8,
            ResponseContentType);
        return System.Threading.Tasks.Task.FromResult(response);
    }
}

public static class WireChatClientFactory
{
    public static IChatClient Create(
        IRimLLMSettings settings,
        string providerId,
        string endpoint,
        string model,
        CapturingHttpMessageHandler handler)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
        {
            options.Endpoint = new Uri(endpoint, UriKind.Absolute);
        }
        // 免金鑰供應商（Player2 本機模式等）測項不填金鑰：與正式版 CreateChatClient 一致改用佔位憑證，
        // 其餘測項本來就有金鑰，不受影響。
        string apiKey = settings.GetActiveApiKey(providerId);
        if (string.IsNullOrEmpty(apiKey)) apiKey = "not-required";
        options.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler));
        var client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
        return client.AsIChatClient();
    }
}

public class MockCustomChatClient : IChatClient
{
    public Func<IEnumerable<ChatMessage>, ChatOptions, System.Threading.Tasks.Task<ChatResponse>> GetResponseHandler { get; set; }
    public Func<IEnumerable<ChatMessage>, ChatOptions, Action<string>, System.Threading.Tasks.Task> StreamHandler { get; set; }

    public ChatClientMetadata Metadata => new("MockCustomClient");

    public void Dispose() { }

    public System.Threading.Tasks.Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (GetResponseHandler != null) return GetResponseHandler(messages, options);
        return System.Threading.Tasks.Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "mock-text")));
    }

    public System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        return new MockStreamEnumerable(this, messages, options, cancellationToken);
    }

    private sealed class MockStreamEnumerable : System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
    {
        private readonly MockCustomChatClient _client;
        private readonly IEnumerable<ChatMessage> _messages;
        private readonly ChatOptions _options;
        private readonly System.Threading.CancellationToken _cancellationToken;

        public MockStreamEnumerable(MockCustomChatClient client, IEnumerable<ChatMessage> messages, ChatOptions options, System.Threading.CancellationToken cancellationToken)
        {
            _client = client;
            _messages = messages;
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(System.Threading.CancellationToken cancellationToken = default)
        {
            var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
            var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_client.StreamHandler != null)
                    {
                        await _client.StreamHandler(_messages, _options, chunk => channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, chunk)));
                    }
                    else
                    {
                        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, "mock-stream-text"));
                    }
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, linkedCts.Token);

            return new MockStreamEnumerator(channel.Reader.ReadAllAsync(linkedCts.Token).GetAsyncEnumerator(linkedCts.Token), linkedCts);
        }
    }

    private sealed class MockStreamEnumerator : System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
    {
        private readonly System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> _inner;
        private readonly System.Threading.CancellationTokenSource _linkedCts;

        public MockStreamEnumerator(System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> inner, System.Threading.CancellationTokenSource linkedCts)
        {
            _inner = inner;
            _linkedCts = linkedCts;
        }

        public ChatResponseUpdate Current => _inner.Current;

        public async System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
        {
            try
            {
                return await _inner.MoveNextAsync().ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            _linkedCts.Dispose();
            return _inner.DisposeAsync();
        }
    }

    public object GetService(System.Type serviceType, object key = null) => null;
}

public class TestOpenAIProvider : OpenAIProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public TestOpenAIProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestOpenRouterProvider : OpenRouterProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public TestOpenRouterProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestZaiProvider : ZaiProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public string InterceptedUrl => WireHandler.LastRequestUrl;

    public TestZaiProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestPlayer2Provider : Player2Provider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string InterceptedPayload => WireHandler.LastRequestBody;

    public string InterceptedUrl => WireHandler.LastRequestUrl;

    public TestPlayer2Provider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class MockStreamProvider : ILLMProvider
{
    public string ProviderId { get; set; }
    public bool RequiresApiKey { get; set; } = true;
    public LLMProviderCapabilities Capabilities => new() { SupportsStreaming = true };
    public Func<IEnumerable<ChatMessage>, ChatOptions, string, Action<string>, System.Threading.Tasks.Task> StreamHandler { get; set; }

    public IChatClient CreateChatClient(string model)
    {
        return new MockCustomChatClient
        {
            StreamHandler = (msgs, opts, onChunk) => StreamHandler != null ? StreamHandler(msgs, opts, model, onChunk) : System.Threading.Tasks.Task.CompletedTask
        };
    }

    public System.Threading.Tasks.Task<TestResult> TestConnectionAsync()
    {
        return System.Threading.Tasks.Task.FromResult(new TestResult { Success = true });
    }

    public System.Threading.Tasks.Task<List<string>> FetchAvailableModelsAsync()
    {
        return System.Threading.Tasks.Task.FromResult(new List<string> { "model-stream" });
    }
}

public class ComplexTestDataStructure
{
    public string Name { get; set; }
    public int Age { get; set; }
    public bool IsActive { get; set; }
    public List<string> Skills { get; set; }
    public Dictionary<string, int> Mapping { get; set; }
    public NestedData Nested { get; set; }

    public ComplexTestDataStructure(string dummyParam)
    {
    }
}

public class NestedData
{
    public float Weight { get; set; }
    public ComplexTestDataStructure SelfRef { get; set; }
}

public class NullableTestDataStructure
{
    public string Name { get; set; }
    public int? OptionalCount { get; set; }
}

public class TestOpenAIProviderWithReasoning : OpenAIProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public TestOpenAIProviderWithReasoning(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestOpenAIProviderWithUsage : OpenAIProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string MockResponse
    {
        get { return WireHandler.ResponseBody; }
        set { WireHandler.ResponseBody = value; }
    }

    public TestOpenAIProviderWithUsage(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

/// <summary>
/// 探測 HTTP 狀態碼 → LLMError 的對照。框架已無 raw HTTP 路徑，
/// 此處直接驗證所有路徑共用的 LLMErrorMapper。
/// </summary>
public class HttpErrorProbeProvider : OpenAIProvider
{
    public HttpErrorProbeProvider(IRimLLMSettings settings) : base(settings) { }

    public void Probe(System.Net.HttpStatusCode statusCode, string responseBody)
    {
        using var response = new System.Net.Http.HttpResponseMessage(statusCode);
        throw LLMErrorMapper.CreateException(
            (int)statusCode,
            responseBody);
    }
}

public class TestKimiPayloadProvider : KimiProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string CapturedPayload => WireHandler.FirstRequestBody;

    public TestKimiPayloadProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}

public class TestDeepSeekPayloadProvider : DeepSeekProvider
{
    public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

    public string CapturedPayload => WireHandler.FirstRequestBody;

    public TestDeepSeekPayloadProvider(IRimLLMSettings settings) : base(settings) { }

    public override IChatClient CreateChatClient(string model)
    {
        var rawClient = WireChatClientFactory.Create(
            Settings,
            ProviderId,
            Settings.GetEndpoint(ProviderId, DefaultEndpoint),
            model,
            WireHandler);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }
}
#pragma warning restore S2701
