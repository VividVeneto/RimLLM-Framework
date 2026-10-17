using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class ToolCallingTests
{
    private sealed class MockToolCallingProvider : ILLMProvider
    {
        public string ProviderId { get; set; } = "ToolMock";
        public bool RequiresApiKey { get; set; } = true;
        public LLMProviderCapabilities Capabilities { get; set; } = new LLMProviderCapabilities
        {
            SupportsStreaming = true,
            SupportsFunctionCalling = true,
            SupportsUsageMetadata = true
        };

        public Func<IEnumerable<ChatMessage>, ChatOptions, Task<ChatResponse>> GetResponseHandler { get; set; }

        public IChatClient CreateChatClient(string model)
        {
            return new MockCustomChatClient
            {
                GetResponseHandler = GetResponseHandler
            };
        }

        public Task<TestResult> TestConnectionAsync() => Task.FromResult(new TestResult { Success = true });
        public Task<List<string>> FetchAvailableModelsAsync() => Task.FromResult(new List<string> { "mock-model" });
    }

    [Test]
    public async Task TestRawToolCalling_ProviderReturnsFunctionCall_PreservedInResponse()
    {
        var settings = new MockSettings { FallbackChain = ["ToolMock:mock-model"] };
        settings.EnabledProviders["ToolMock"] = true;
        settings.ApiKeys["ToolMock"] = "mock-key";
        var manager = new RimLLMManager(settings);

        var toolProvider = new MockToolCallingProvider
        {
            GetResponseHandler = (messages, options) =>
            {
                var funcCall = new FunctionCallContent("call_123", "GetWeather", new Dictionary<string, object>
                {
                    ["location"] = "RimWorld Colony"
                });
                var respMessage = new ChatMessage(ChatRole.Assistant, [funcCall]);
                return Task.FromResult(new ChatResponse(respMessage)
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                    ModelId = "ToolMock:mock-model"
                });
            }
        };
        manager.RegisterProvider(toolProvider);

        var client = manager.CreateChatClient("test.toolcalling.mod");
        var weatherFunc = AIFunctionFactory.Create((string location) => $"Sunny at {location}", "GetWeather", "Gets weather");

        var chatOptions = new ChatOptions
        {
            Tools = [weatherFunc]
        };

        var response = await client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "What is the weather?") },
            chatOptions);

        ClassicAssert.IsNotNull(response);
        var assistantMsg = response.Messages.FirstOrDefault();
        ClassicAssert.IsNotNull(assistantMsg);
        ClassicAssert.AreEqual(1, assistantMsg.Contents.Count);

        var call = assistantMsg.Contents[0] as FunctionCallContent;
        ClassicAssert.IsNotNull(call);
        ClassicAssert.AreEqual("GetWeather", call.Name);
        ClassicAssert.AreEqual("call_123", call.CallId);
        ClassicAssert.AreEqual("RimWorld Colony", call.Arguments["location"]);
    }

    [Test]
    public async Task TestAutoInvokingToolCalling_ExecutesMainThreadAndCompletesLoop()
    {
        var settings = new MockSettings { FallbackChain = ["ToolMock:mock-model"] };
        settings.EnabledProviders["ToolMock"] = true;
        settings.ApiKeys["ToolMock"] = "mock-key";
        var manager = new RimLLMManager(settings);

        int round = 0;
        var toolProvider = new MockToolCallingProvider
        {
            GetResponseHandler = (messages, options) =>
            {
                round++;
                if (round == 1)
                {
                    // 第一輪回傳 FunctionCall
                    var funcCall = new FunctionCallContent("call_abc", "AddNumbers", new Dictionary<string, object>
                    {
                        ["a"] = 10,
                        ["b"] = 32
                    });
                    var callMsg = new ChatMessage(ChatRole.Assistant, [funcCall]);
                    return Task.FromResult(new ChatResponse(callMsg)
                    {
                        FinishReason = ChatFinishReason.ToolCalls,
                        ModelId = "ToolMock:mock-model"
                    });
                }
                else
                {
                    // 第二輪檢查前一輪的 FunctionResult
                    var lastMsg = messages.LastOrDefault();
                    string resultVal = "unknown";
                    if (lastMsg?.Contents != null)
                    {
                        foreach (var c in lastMsg.Contents)
                        {
                            if (c is FunctionResultContent frc)
                            {
                                resultVal = frc.Result?.ToString();
                            }
                        }
                    }
                    var finalMsg = new ChatMessage(ChatRole.Assistant, $"The sum is {resultVal}");
                    return Task.FromResult(new ChatResponse(finalMsg)
                    {
                        FinishReason = ChatFinishReason.Stop,
                        ModelId = "ToolMock:mock-model"
                    });
                }
            }
        };
        manager.RegisterProvider(toolProvider);

        var baseClient = manager.CreateChatClient("test.toolcalling.mod");
        var invokingClient = baseClient.AsMainThreadFunctionInvokingClient(maxIterations: 5);

        bool functionExecuted = false;
        var addFunc = AIFunctionFactory.Create((int a, int b) =>
        {
            functionExecuted = true;
            return a + b;
        }, "AddNumbers", "Adds two numbers");

        var options = new ChatOptions
        {
            Tools = [addFunc]
        };

        var finalResponse = await invokingClient.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "Calculate 10 + 32") },
            options);

        ClassicAssert.IsNotNull(finalResponse);
        ClassicAssert.IsTrue(functionExecuted, "AIFunction 應該被執行");
        ClassicAssert.AreEqual("The sum is 42", finalResponse.Text);
        ClassicAssert.AreEqual(2, round, "應該執行 2 輪對話");
    }

    /// <summary>組出「第 1 輪回工具呼叫、之後回最終文字」的處理器，供多個迴圈測試共用。</summary>
    private static Func<IEnumerable<ChatMessage>, ChatOptions, Task<ChatResponse>> ToolLoopHandler(
        string modelId, Action<int, IEnumerable<ChatMessage>, ChatOptions> onRound = null)
    {
        int round = 0;
        return (messages, options) =>
        {
            round++;
            onRound?.Invoke(round, messages, options);
            if (round == 1)
            {
                var call = new FunctionCallContent("call_1", "Ping", new Dictionary<string, object>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                    ModelId = modelId
                });
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = modelId
            });
        };
    }

    private static RimLLMManager BuildManager(MockSettings settings, params ILLMProvider[] providers)
    {
        foreach (ILLMProvider provider in providers)
        {
            settings.EnabledProviders[provider.ProviderId] = true;
            settings.ApiKeys[provider.ProviderId] = "mock-key";
        }
        var manager = new RimLLMManager(settings);
        foreach (ILLMProvider provider in providers)
        {
            manager.RegisterProvider(provider);
        }
        return manager;
    }

    private static List<ChatMessage> UserSays(string text)
    {
        return [new(ChatRole.User, text)];
    }

    private static ChatOptions WithPingTool()
    {
        return new ChatOptions { Tools = [AIFunctionFactory.Create(() => "pong", "Ping")] };
    }

    [Test]
    public async Task ToolLoopContinuations_DoNotCountTowardAntiAbuseWindow()
    {
        // 視窗上限 2 次：若每輪都計數，一個 3 輪的工具迴圈第 3 輪就會被判濫用。
        var settings = new MockSettings
        {
            FallbackChain = ["ToolMock:mock-model"],
            MaxRequestsPerWindow = 2,
            ThrottlingWindowSeconds = 60,
            CoolDownDurationSeconds = 60
        };
        int round = 0;
        var provider = new MockToolCallingProvider
        {
            GetResponseHandler = (messages, options) =>
            {
                round++;
                if (round < 3)
                {
                    var call = new FunctionCallContent("call_" + round, "Ping", new Dictionary<string, object>());
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                    {
                        FinishReason = ChatFinishReason.ToolCalls
                    });
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }
        };
        var manager = BuildManager(settings, provider);
        var client = manager.CreateChatClient("test.throttle.loop").AsMainThreadFunctionInvokingClient(maxIterations: 5);

        var response = await client.GetResponseAsync(UserSays("go"), WithPingTool());
        ClassicAssert.AreEqual("done", response.Text);
        ClassicAssert.AreEqual(3, round, "3 輪迴圈應全部完成而不觸發節流");

        // 只有第 1 輪計入視窗：再送一個新請求（第 2 次）仍可過，第 3 次才被擋。
        var plain = manager.CreateChatClient("test.throttle.loop");
        await plain.GetResponseAsync(UserSays("again"));
        var ex = Assert.ThrowsAsync<RimLLMException>(() => plain.GetResponseAsync(UserSays("third")));
        ClassicAssert.AreEqual(LLMError.RateLimit, ex.Error);
    }

    [Test]
    public void ToolLoopContinuation_StillBlockedDuringCooldown()
    {
        var settings = new MockSettings
        {
            FallbackChain = ["ToolMock:mock-model"],
            MaxRequestsPerWindow = 1,
            ThrottlingWindowSeconds = 60,
            CoolDownDurationSeconds = 60
        };
        var manager = BuildManager(settings, new MockToolCallingProvider());
        var client = manager.CreateChatClient("test.throttle.cooldown");

        client.GetResponseAsync(UserSays("a")).GetAwaiter().GetResult();
        Assert.ThrowsAsync<RimLLMException>(() => client.GetResponseAsync(UserSays("a"))); // 進入冷卻

        var continuation = new List<ChatMessage>
        {
            new(ChatRole.User, "a"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "Ping", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "pong")])
        };
        ClassicAssert.IsTrue(RimLLMGuardChatClient.IsToolLoopContinuation(continuation));
        var ex = Assert.ThrowsAsync<RimLLMException>(() => client.GetResponseAsync(continuation));
        ClassicAssert.AreEqual(LLMError.RateLimit, ex.Error, "冷卻中的續輪也要擋");
    }

    [Test]
    public async Task FrameworkOptions_SurviveFunctionInvokingLoop()
    {
        var settings = new MockSettings { FallbackChain = ["ToolMock:mock-model"] };
        var seen = new List<int>();
        var provider = new MockToolCallingProvider
        {
            GetResponseHandler = ToolLoopHandler("ToolMock:mock-model",
                (round, messages, options) => seen.Add(RimLLMChatOptions.GetPriority(options)))
        };
        var manager = BuildManager(settings, provider);
        var client = manager.CreateChatClient("test.options.loop").AsMainThreadFunctionInvokingClient();

        var options = new RimLLMChatOptions
        {
            Priority = 7,
            Tools = [AIFunctionFactory.Create(() => "pong", "Ping")]
        };
        await client.GetResponseAsync(UserSays("go"), options);

        ClassicAssert.AreEqual(new List<int> { 7, 7 }, seen, "框架選項在工具迴圈每一輪都要抵達供應商");
    }

    [Test]
    public async Task FallbackMidToolLoop_NextProviderReceivesForeignToolCallId()
    {
        var settings = new MockSettings
        {
            FallbackChain = ["ProviderA:model-a", "ProviderB:model-b"],
            RetryDelay = 0f
        };
        int roundA = 0;
        var providerA = new MockToolCallingProvider
        {
            ProviderId = "ProviderA",
            GetResponseHandler = (messages, options) =>
            {
                roundA++;
                if (roundA == 1)
                {
                    var call = new FunctionCallContent("call_from_A", "Ping", new Dictionary<string, object>());
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                    {
                        FinishReason = ChatFinishReason.ToolCalls
                    });
                }
                // 第 2 輪 A 掛掉（非重試類錯誤，直接換手）
                throw new RimLLMException(LLMError.InvalidKey, "A is down");
            }
        };
        string idSeenByB = null;
        var providerB = new MockToolCallingProvider
        {
            ProviderId = "ProviderB",
            GetResponseHandler = (messages, options) =>
            {
                var last = messages.LastOrDefault();
                idSeenByB = last?.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "B finished")));
            }
        };
        var manager = BuildManager(settings, providerA, providerB);
        var client = manager.CreateChatClient("test.fallback.loop").AsMainThreadFunctionInvokingClient();

        var response = await client.GetResponseAsync(UserSays("go"), WithPingTool());

        ClassicAssert.AreEqual("B finished", response.Text);
        ClassicAssert.AreEqual("ProviderB:model-b", response.ModelId);
        ClassicAssert.AreEqual("call_from_A", idSeenByB, "B 應收到 A 產生的 tool_call_id 對應的工具結果");
    }

    [Test]
    public async Task ToolsStripped_FlaggedOnResponse_WhenProviderLacksFunctionCalling()
    {
        var settings = new MockSettings { FallbackChain = ["NoTools:m"] };
        ChatOptions seenOptions = null;
        var provider = new MockToolCallingProvider
        {
            ProviderId = "NoTools",
            Capabilities = new LLMProviderCapabilities { SupportsStreaming = true, SupportsFunctionCalling = false },
            GetResponseHandler = (messages, options) =>
            {
                seenOptions = options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "prose")));
            }
        };
        var manager = BuildManager(settings, provider);
        var client = manager.CreateChatClient("test.strip");

        var response = await client.GetResponseAsync(UserSays("go"), WithPingTool());

        ClassicAssert.IsTrue(response.WereToolsStripped());
        ClassicAssert.IsTrue(seenOptions?.Tools == null || seenOptions.Tools.Count == 0);

        var plain = await client.GetResponseAsync(UserSays("go"));
        ClassicAssert.IsFalse(plain.WereToolsStripped(), "沒帶工具的請求不該被標記");
    }

    [Test]
    public void GetEffectiveCapabilities_IsIntersectionAcrossEligibleCandidates()
    {
        var settings = new MockSettings { FallbackChain = ["WithTools:m", "NoTools:m"] };
        var withTools = new MockToolCallingProvider { ProviderId = "WithTools" };
        var noTools = new MockToolCallingProvider
        {
            ProviderId = "NoTools",
            Capabilities = new LLMProviderCapabilities { SupportsStreaming = true, SupportsFunctionCalling = false }
        };
        var manager = BuildManager(settings, withTools, noTools);

        LLMProviderCapabilities effective = manager.GetEffectiveCapabilities(null);
        ClassicAssert.IsFalse(effective.SupportsFunctionCalling, "任一候選不支援即視為不可依賴");
        ClassicAssert.IsTrue(effective.SupportsStreaming);

        // 只剩支援工具的候選時，交集就是它自己。
        settings.FallbackChain = ["WithTools:m"];
        ClassicAssert.IsTrue(manager.GetEffectiveCapabilities(null).SupportsFunctionCalling);

        settings.FallbackChain = [];
        var ex = Assert.Throws<RimLLMException>(() => manager.GetEffectiveCapabilities(null));
        ClassicAssert.AreEqual(LLMError.ProviderOffline, ex.Error);
    }

    /// <summary>
    /// 工具結果必須是工具本身的回傳值，而不是包住它的 ValueTask。
    /// 先前 EnqueueOnMainThreadAsync 的多載解析選到 Func&lt;T&gt;（T = ValueTask&lt;object&gt;），
    /// 整顆 ValueTask 被裝箱送給模型，序列化成 {"isCompleted":…,"result":42}；
    /// 舊測試只比對 ToString()，而 ValueTask&lt;T&gt;.ToString() 恰好回傳內層值，因此抓不到。
    /// </summary>
    [Test]
    public async Task MainThreadFunctionInvoker_UnwrapsToolResult_ForSyncAndAsyncTools()
    {
        var settings = new MockSettings { FallbackChain = ["ToolMock:mock-model"] };
        var seenResults = new List<object>();
        int round = 0;
        var provider = new MockToolCallingProvider
        {
            GetResponseHandler = (messages, options) =>
            {
                round++;
                if (round == 1)
                {
                    var calls = new List<AIContent>
                    {
                        new FunctionCallContent("c-sync", "SyncAdd", new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 }),
                        new FunctionCallContent("c-async", "AsyncEcho", new Dictionary<string, object> { ["text"] = "pong" })
                    };
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls))
                    {
                        FinishReason = ChatFinishReason.ToolCalls
                    });
                }

                foreach (ChatMessage message in messages)
                {
                    seenResults.AddRange(message.Contents.OfType<FunctionResultContent>().Select(r => r.Result));
                }
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }
        };
        var manager = BuildManager(settings, provider);
        var client = manager.CreateChatClient("test.tool.unwrap").AsMainThreadFunctionInvokingClient();

        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create((int a, int b) => a + b, "SyncAdd"),
                AIFunctionFactory.Create(async (string text) =>
                {
                    await Task.Delay(10);
                    return text + "!";
                }, "AsyncEcho")
            ]
        };

        var response = await client.GetResponseAsync(UserSays("go"), options);

        ClassicAssert.AreEqual("done", response.Text);
        ClassicAssert.AreEqual(2, seenResults.Count, "兩個工具結果都要回到模型");
        foreach (object result in seenResults)
        {
            ClassicAssert.IsNotNull(result);
            StringAssert.DoesNotContain("ValueTask", result.GetType().FullName,
                "工具結果不得是裝箱的 ValueTask");
        }
        ClassicAssert.AreEqual("3", seenResults[0].ToString());
        ClassicAssert.AreEqual("pong!", seenResults[1].ToString(), "async 工具的結果也要被完整等待並解開");
    }

    [Test]
    public void GeminiSendsToolDefinitionsThroughOpenAiCompatibleEndpoint()
    {
        // Gemini 改走 OpenAI 相容端點後，工具定義沿用 OpenAI 家族的 wire 格式；
        // 翻譯正確性由 OpenAI 路徑的既有測試覆蓋，此處只釘住 Gemini 發出的請求形狀。
        var mockSettings = new MockSettings();
        mockSettings.ApiKeys[ProviderIds.Gemini] = "mock-key";
        var provider = new TestGeminiProvider(mockSettings);

        var addFunc = AIFunctionFactory.Create((int a, int b) => a + b, "AddNumbers", "Adds two numbers");
        var options = new ChatOptions
        {
            Tools = [addFunc]
        };

        string result = provider.GenerateAsync(
            new List<ChatMessage> { new(ChatRole.User, "Calculate 10 + 32") },
            options,
            "gemini-2.5-flash").GetAwaiter().GetResult();

        ClassicAssert.AreEqual("ok", result);
        var payload = JsonNode.Parse(provider.InterceptedPayload).AsObject();
        ClassicAssert.IsNotNull(payload["tools"], "工具定義應隨請求送出。");
        ClassicAssert.AreEqual("gemini-2.5-flash", (string)payload["model"]);
    }
}
