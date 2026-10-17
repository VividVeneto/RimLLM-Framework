
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class RimLLMChatClientExecutorTests
{
    private static ChatOptions NewOptionsWithResponseType(Type responseType)
    {
        return new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RimLLMChatOptions.ResponseTypeKey] = responseType
            }
        };
    }

    [Test]
    public void TestEstimateTokens_DistinguishesCjkFromLatin()
    {
        // 英文約 4 字元一個 token；先前一律 0.8 token／字元，會把 40 字元的英文估成 32。
        ClassicAssert.AreEqual(10, RimLLMChatClientExecutor.EstimateTokens(new string('a', 40)));
        // CJK 約一字一個 token。
        ClassicAssert.AreEqual(4, RimLLMChatClientExecutor.EstimateTokens("殖民地開"));
        // 混合文字逐字累計後無條件進位；空字串至少算 1。
        ClassicAssert.AreEqual(3, RimLLMChatClientExecutor.EstimateTokens("hi 你好"));
        ClassicAssert.AreEqual(1, RimLLMChatClientExecutor.EstimateTokens(""));
        ClassicAssert.AreEqual(1, RimLLMChatClientExecutor.EstimateTokens(null));
    }

    [Test]
    public void TestGenerateAsync_SendsMessagesAndOptions()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello"))
        };

        var callerMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "hi")
        };
        var callerOptions = new ChatOptions { MaxOutputTokens = 256, Temperature = 0.2f };

        var result = RimLLMChatClientExecutor.GenerateAsync(
            client, callerMessages, callerOptions, "gpt-test", useNativeSchema: false, "OpenAI", 30f,
            CancellationToken.None).GetAwaiter().GetResult();

        ClassicAssert.AreEqual("hello", result.Text);
        ClassicAssert.AreEqual(1, client.ReceivedOptions.Count);
        ClassicAssert.AreEqual("gpt-test", client.ReceivedOptions[0].ModelId);
        ClassicAssert.AreEqual(0.2f, client.ReceivedOptions[0].Temperature);
        ClassicAssert.AreEqual(256, client.ReceivedOptions[0].MaxOutputTokens);
        var messages = new List<ChatMessage>(client.ReceivedMessages[0]);
        ClassicAssert.AreEqual(2, messages.Count);
        ClassicAssert.AreEqual(ChatRole.System, messages[0].Role);
        ClassicAssert.AreEqual(ChatRole.User, messages[1].Role);
    }

    [Test]
    public void TestBuildOptions_PassesThroughCallerChatOptionsFields()
    {
        // 回歸測試：BuildOptions 曾經無條件 new 一個乾淨的 ChatOptions，
        // 使得下游只用 MEAI 設定的 TopP / Seed / StopSequences 等欄位靜默失效——
        // 不生效也不報錯，正是「只用 MEAI 方法」的呼叫端最難察覺的失敗形態。
        var sourceOptions = new ChatOptions
        {
            TopP = 0.9f,
            TopK = 40,
            FrequencyPenalty = 0.3f,
            PresencePenalty = 0.4f,
            Seed = 1234L,
            StopSequences = ["STOP"],
            ResponseFormat = ChatResponseFormat.Json,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["caller_key"] = "caller_value" }
        };

        ChatOptions built = RimLLMChatClientExecutor.BuildOptions(
            sourceOptions, "gpt-test", useNativeSchema: false, null);

        ClassicAssert.AreEqual(0.9f, built.TopP);
        ClassicAssert.AreEqual(40, built.TopK);
        ClassicAssert.AreEqual(0.3f, built.FrequencyPenalty);
        ClassicAssert.AreEqual(0.4f, built.PresencePenalty);
        ClassicAssert.AreEqual(1234L, built.Seed);
        ClassicAssert.AreEqual(1, built.StopSequences.Count);
        ClassicAssert.AreEqual("STOP", built.StopSequences[0]);
        ClassicAssert.AreEqual("caller_value", built.AdditionalProperties["caller_key"]);

        // 框架仍必須覆寫自己負責的欄位；Tools 尤其不可沿用呼叫端的複本，
        // 因為 StripUnsupportedTools 可能已依供應商能力把它移除。
        ClassicAssert.AreEqual("gpt-test", built.ModelId);
        ClassicAssert.IsNull(built.Tools);

        // ResponseFormat 反而必須被清掉：原生 schema 被拒後的降級重試也走 useNativeSchema:false，
        // 呼叫端的 response_format 若在此存活，重試會重送剛被拒的那一份而永久失敗。
        ClassicAssert.IsNull(built.ResponseFormat);
    }

    [Test]
    public void TestGenerateAsync_NativeSchemaSetsStrictFlag()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "give data") };
        var options = NewOptionsWithResponseType(typeof(TestDataStructure));

        RimLLMChatClientExecutor.GenerateAsync(
            client, messages, options, "gpt-test", useNativeSchema: true, "OpenAI", 30f, CancellationToken.None).GetAwaiter().GetResult();

        ChatOptions sent = client.ReceivedOptions[0];
        ClassicAssert.IsNotNull(sent.ResponseFormat);
        ClassicAssert.IsTrue((bool)sent.AdditionalProperties["strict"]);
    }

    [Test]
    public void TestGenerateAsync_ContainsOpenEndedMapDisablesStrictMode()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "give data") };
        var options = NewOptionsWithResponseType(typeof(Dictionary<string, string>));

        RimLLMChatClientExecutor.GenerateAsync(
            client, messages, options, "gpt-test", useNativeSchema: true, "OpenAI", 30f, CancellationToken.None).GetAwaiter().GetResult();

        // 與 raw 路徑一致：含 Dictionary 的型別仍送出 response_format，
        // 但 strict 關閉，否則服務端會拒絕開放式 map。
        ClassicAssert.IsNotNull(client.ReceivedOptions[0].ResponseFormat);
        ClassicAssert.IsFalse((bool)client.ReceivedOptions[0].AdditionalProperties["strict"]);
    }

    /// <summary>
    /// 推理內容維持 MEAI 原生的 TextReasoningContent，不再被合成進 Text。
    /// 先前 executor 會把它包成 &lt;think&gt; 塞進文字流，那讓結構化輸出、快取鍵與
    /// 任何讀 Text 的呼叫端都得先剝標籤；&lt;think&gt; 現在只是呈現層的一種表述方式。
    /// </summary>
    [Test]
    public void TestGenerateAsync_KeepsReasoningAsNativeContent()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                [
                    new TextReasoningContent("step one"),
                    new TextContent("final answer")
                ]))
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "solve") };

        var result = RimLLMChatClientExecutor.GenerateAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None).GetAwaiter().GetResult();

        ClassicAssert.AreEqual("final answer", result.Text);

        var reasoning = new List<TextReasoningContent>();
        foreach (ChatMessage message in result.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent r) reasoning.Add(r);
            }
        }
        ClassicAssert.AreEqual(1, reasoning.Count, "推理內容必須原樣保留在 Contents 裡。");
        ClassicAssert.AreEqual("step one", reasoning[0].Text);
    }

    /// <summary>
    /// provider 回的 ChatResponse 必須原樣交還。這些欄位只有 provider 知道，
    /// 先前它們會在「拆成 RimLLMGenerationResult 再重組」的過程中整批消失。
    /// </summary>
    [Test]
    public void TestGenerateAsync_PreservesProviderResponseMetadata()
    {
        var raw = new object();
        var created = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "hello"))
            {
                ResponseId = "resp-123",
                ConversationId = "conv-456",
                CreatedAt = created,
                RawRepresentation = raw,
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 22 },
                AdditionalProperties = new AdditionalPropertiesDictionary { ["provider_flag"] = true }
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        var result = RimLLMChatClientExecutor.GenerateAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None).GetAwaiter().GetResult();

        ClassicAssert.AreEqual("resp-123", result.ResponseId);
        ClassicAssert.AreEqual("conv-456", result.ConversationId);
        ClassicAssert.AreEqual(created, result.CreatedAt);
        ClassicAssert.AreSame(raw, result.RawRepresentation);
        ClassicAssert.AreEqual(ChatFinishReason.Stop, result.FinishReason);
        ClassicAssert.AreEqual(11, result.Usage.InputTokenCount);
        ClassicAssert.AreEqual(22, result.Usage.OutputTokenCount);
        ClassicAssert.IsTrue((bool)result.AdditionalProperties["provider_flag"]);
    }

    [Test]
    public void TestGenerateAsync_InvokesCustomizeOptions()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        bool invoked = false;

        RimLLMChatClientExecutor.GenerateAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None,
            options =>
            {
                invoked = true;
                options.Temperature = 0.9f;
            }).GetAwaiter().GetResult();

        ClassicAssert.IsTrue(invoked);
        ClassicAssert.AreEqual(0.9f, client.ReceivedOptions[0].Temperature);
    }

    [Test]
    public void TestGenerateAsync_FiresUsageCallbackWithReportedUsage()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 }
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        int prompt = -1, completion = -1;

        RimLLMChatClientExecutor.GenerateAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None,
            onUsage: tokens => { prompt = tokens.Prompt; completion = tokens.Completion; }).GetAwaiter().GetResult();

        ClassicAssert.AreEqual(100, prompt);
        ClassicAssert.AreEqual(50, completion);
    }

    [Test]
    public void TestGenerateAsync_SkipsUsageCallbackWhenUnreported()
    {
        var client = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        bool fired = false;

        RimLLMChatClientExecutor.GenerateAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None,
            onUsage: tokens => { fired = true; }).GetAwaiter().GetResult();

        // 供應商沒回報用量時只有估算值，不得交給請求日誌冒充實際值。
        ClassicAssert.IsFalse(fired);
    }

    [Test]
    public void TestGenerateAsync_MapsClientResultException()
    {
        var client = new CapturingChatClient
        {
            ResponseException = new TestClientResultException("bad request", 400)
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        RimLLMException ex = Assert.Throws<RimLLMException>(() =>
            RimLLMChatClientExecutor.GenerateAsync(
                client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None).GetAwaiter().GetResult());

        ClassicAssert.AreEqual(LLMError.InvalidResponse, ex.Error);
    }

    [Test]
    public void TestGenerateAsync_MapsRateLimit()
    {
        var client = new CapturingChatClient
        {
            ResponseException = new TestClientResultException("rate limited", 429)
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        RimLLMException ex = Assert.Throws<RimLLMException>(() =>
            RimLLMChatClientExecutor.GenerateAsync(
                client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI", 30f, CancellationToken.None).GetAwaiter().GetResult());

        ClassicAssert.AreEqual(LLMError.RateLimit, ex.Error);
    }

    /// <summary>
    /// 串流一律原樣轉發 provider 的 update：推理內容維持 TextReasoningContent，
    /// 不再被合成成 &lt;think&gt; 字串 chunk。想要那種扁平表述的呈現層自己用
    /// RimLLMThinkTagFormatter 組。
    /// </summary>
    [Test]
    public void TestStreamAsync_ForwardsUpdatesVerbatim()
    {
        var client = new CapturingChatClient
        {
            StreamUpdates =
            {
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextReasoningContent("think a")
                ]),
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextReasoningContent("think b")
                ]),
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextContent("answer")
                ])
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "solve") };
        var received = new List<ChatResponseUpdate>();

        RimLLMChatClientExecutor.StreamAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI",
            async update => { received.Add(update); await Task.CompletedTask; }, 30f, CancellationToken.None).GetAwaiter().GetResult();

        ClassicAssert.AreEqual(3, received.Count, "每一個 provider update 都應原封不動轉發一次。");
        ClassicAssert.AreSame(client.StreamUpdates[0], received[0]);
        ClassicAssert.AreSame(client.StreamUpdates[1], received[1]);
        ClassicAssert.AreSame(client.StreamUpdates[2], received[2]);
        ClassicAssert.IsInstanceOf<TextReasoningContent>(received[0].Contents[0]);
        ClassicAssert.AreEqual("think a", ((TextReasoningContent)received[0].Contents[0]).Text);
        ClassicAssert.AreEqual("answer", ((TextContent)received[2].Contents[0]).Text);
    }

    /// <summary>
    /// &lt;think&gt; 封裝從框架資料流移到呈現層之後，這裡是它唯一的定義處。
    /// </summary>
    [Test]
    public void TestThinkTagFormatter_WrapsReasoningSegments()
    {
        var formatter = new RimLLMThinkTagFormatter();

        string a = formatter.Append(new ChatResponseUpdate(
            ChatRole.Assistant, [new TextReasoningContent("think a")]));
        string b = formatter.Append(new ChatResponseUpdate(
            ChatRole.Assistant, [new TextReasoningContent("think b")]));
        string c = formatter.Append(new ChatResponseUpdate(
            ChatRole.Assistant, [new TextContent("answer")]));

        ClassicAssert.AreEqual("<think>think a", a);
        ClassicAssert.AreEqual("think b", b);
        ClassicAssert.AreEqual("</think>answer", c);
        ClassicAssert.AreEqual(string.Empty, formatter.Complete(), "已閉合就不該再補標籤。");
    }

    /// <summary>整段都是推理內容時，收尾標籤只能在串流結束後補。</summary>
    [Test]
    public void TestThinkTagFormatter_ClosesUnterminatedReasoning()
    {
        var formatter = new RimLLMThinkTagFormatter();

        ClassicAssert.AreEqual("<think>only thinking", formatter.Append(new ChatResponseUpdate(
            ChatRole.Assistant, [new TextReasoningContent("only thinking")])));
        ClassicAssert.AreEqual("</think>", formatter.Complete());
    }

    [Test]
    public void TestStreamAsync_CapturesUsageContent()
    {
        var usage = new UsageContent(new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 });
        var client = new CapturingChatClient
        {
            StreamUpdates =
            {
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new TextContent("hi")
                ]),
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    usage
                ])
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var received = new List<ChatResponseUpdate>();

        RimLLMChatClientExecutor.StreamAsync(
            client, messages, null, "gpt-test", useNativeSchema: false, "OpenAI",
            async update => { received.Add(update); await Task.CompletedTask; }, 30f, CancellationToken.None).GetAwaiter().GetResult();

        // UsageContent 也原樣轉發，不再被抽出來重組成收尾 update。
        ClassicAssert.AreEqual(2, received.Count);
        ClassicAssert.AreSame(usage, received[1].Contents[0]);
    }

    [Test]
    public void TestBuildMessages_CachedContextBranches()
    {
        var withCache = new RimLLMChatOptions { CachedContext = "lore" };

        // 分支 1: messages 為空 -> 插入一則系統訊息承載 CachedContext
        var msgs1 = RimLLMChatClientExecutor.BuildMessages(null, withCache);
        ClassicAssert.AreEqual(1, msgs1.Count);
        ClassicAssert.AreEqual(ChatRole.System, msgs1[0].Role);
        ClassicAssert.AreEqual("lore", msgs1[0].Text);

        // 分支 2: 已有空文字系統訊息 -> 直接填入
        var msgs2 = RimLLMChatClientExecutor.BuildMessages(
            new List<ChatMessage> { new(ChatRole.System, ""), new(ChatRole.User, "hi") },
            withCache);
        ClassicAssert.AreEqual(2, msgs2.Count);
        ClassicAssert.AreEqual("lore", msgs2[0].Text);

        // 分支 3: 已有非空系統訊息 -> 附加在後面，呼叫端的內容排在前
        var msgs3 = RimLLMChatClientExecutor.BuildMessages(
            new List<ChatMessage> { new(ChatRole.System, "existing"), new(ChatRole.User, "hi") },
            withCache);
        ClassicAssert.AreEqual(2, msgs3.Count);
        ClassicAssert.AreEqual("existing\n\nlore", msgs3[0].Text);

        // 分支 4: 系統訊息已含 CachedContext -> 不重複附加
        var msgs4 = RimLLMChatClientExecutor.BuildMessages(
            new List<ChatMessage> { new(ChatRole.System, "existing\n\nlore"), new(ChatRole.User, "hi") },
            withCache);
        ClassicAssert.AreEqual("existing\n\nlore", msgs4[0].Text);

        // 分支 5: 沒有 CachedContext 且 messages 為空 -> 補上一條空白使用者訊息
        var msgs5 = RimLLMChatClientExecutor.BuildMessages(null, null);
        ClassicAssert.AreEqual(1, msgs5.Count);
        ClassicAssert.AreEqual(ChatRole.User, msgs5[0].Role);
    }
}

public class TestClientResultException : ClientResultException
{
    public TestClientResultException(string message, int status) : base(message)
    {
        Status = status;
    }
}

public class CapturingChatClient : IChatClient
{
    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = [];
    public List<ChatOptions> ReceivedOptions { get; } = [];
    public Func<ChatResponse> ResponseFactory { get; set; } = () => new ChatResponse();
    public List<ChatResponseUpdate> StreamUpdates { get; } = [];
    public Exception ResponseException { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
    {
        ReceivedMessages.Add(messages);
        ReceivedOptions.Add(options);
        if (ResponseException != null)
        {
            var tcs = new TaskCompletionSource<ChatResponse>();
            tcs.SetException(ResponseException);
            return tcs.Task;
        }
        return Task.FromResult(ResponseFactory());
    }

    public System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
    {
        ReceivedMessages.Add(messages);
        ReceivedOptions.Add(options);
        return new TestStreamEnumerable(StreamUpdates);
    }

    private sealed class TestStreamEnumerable : System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
    {
        private readonly List<ChatResponseUpdate> _updates;

        public TestStreamEnumerable(List<ChatResponseUpdate> updates)
        {
            _updates = updates;
        }

        public System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator(_updates);
        }
    }

    private sealed class Enumerator : System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
    {
        private readonly List<ChatResponseUpdate> _updates;
        private int _index;

        public Enumerator(List<ChatResponseUpdate> updates)
        {
            _updates = updates;
        }

        public ChatResponseUpdate Current { get; private set; }

        public System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
        {
            if (_index < _updates.Count)
            {
                Current = _updates[_index];
                _index++;
                return new System.Threading.Tasks.ValueTask<bool>(true);
            }
            Current = null;
            return new System.Threading.Tasks.ValueTask<bool>(false);
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            return new System.Threading.Tasks.ValueTask();
        }
    }

    public object GetService(Type serviceType, object serviceKey = null) => null;

    public void Dispose()
    {
    }
}
