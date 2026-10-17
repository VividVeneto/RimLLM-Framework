using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using RimTalk.Client;
using RimTalk.Client.OpenAI;
using RimTalk.Data;
using RimTalk.Error;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// RimTalk 相容層的純邏輯部分：角色對映、訊息轉換、日誌描述與設定登錄表。
/// Harmony 攔截與實際串流需要遊戲執行期，交由實機驗證。
/// </summary>
[TestFixture]
public class CompatRimTalkTests
{
    // 1x1 像素的 JPEG 檔頭片段，只要是合法 base64 即可；轉換器不解碼圖片內容。
    private const string SampleImageBase64 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==";

    [Test]
    public void ToChatRole_MapsAllRimTalkRoles()
    {
        ClassicAssert.AreEqual(ChatRole.System, RimTalkCompatClient.MessageConverter.Instance.ToChatRole(Role.System));
        ClassicAssert.AreEqual(ChatRole.User, RimTalkCompatClient.MessageConverter.Instance.ToChatRole(Role.User));
        ClassicAssert.AreEqual(ChatRole.Assistant, RimTalkCompatClient.MessageConverter.Instance.ToChatRole(Role.AI));
    }

    [Test]
    public void Build_PreservesOrderAndMergesConsecutiveSameRole()
    {
        var prefix = new List<(Role role, string message)>
        {
            (Role.System, "Base instruction"),
            (Role.System, "Output JSONL."),
            (Role.User, "Context")
        };
        var messages = new List<(Role role, string message)>
        {
            (Role.User, "Dialogue prompt"),
            (Role.AI, "{\"name\":\"A\",\"text\":\"hi\"}")
        };

        List<ChatMessage> result = RimTalkCompatClient.MessageConverter.Instance.Build(prefix, messages, null);

        ClassicAssert.AreEqual(3, result.Count);
        ClassicAssert.AreEqual(ChatRole.System, result[0].Role);
        ClassicAssert.AreEqual("Base instruction\n\nOutput JSONL.", result[0].Text);
        ClassicAssert.AreEqual(ChatRole.User, result[1].Role);
        ClassicAssert.AreEqual("Context\n\nDialogue prompt", result[1].Text);
        ClassicAssert.AreEqual(ChatRole.Assistant, result[2].Role);
    }

    [Test]
    public void Build_ToleratesNullLists()
    {
        List<ChatMessage> result = RimTalkCompatClient.MessageConverter.Instance.Build(null, null, null);
        ClassicAssert.IsEmpty(result);
    }

    [Test]
    public void Build_AttachesImageToTrailingUserMessage()
    {
        var prefix = new List<(Role role, string message)> { (Role.System, "sys"), (Role.User, "look at this") };

        List<ChatMessage> result = RimTalkCompatClient.MessageConverter.Instance.Build(prefix, null, SampleImageBase64);

        ClassicAssert.AreEqual(2, result.Count);
        ChatMessage last = result[1];
        ClassicAssert.AreEqual(ChatRole.User, last.Role);
        ClassicAssert.AreEqual("look at this", last.Text);
        DataContent image = last.Contents.OfType<DataContent>().Single();
        ClassicAssert.AreEqual("image/jpeg", image.MediaType);
        CollectionAssert.AreEqual(Convert.FromBase64String(SampleImageBase64), image.Data.ToArray());
    }

    [Test]
    public void Build_AddsSeparateUserMessageWhenLastIsNotUser()
    {
        var messages = new List<(Role role, string message)> { (Role.User, "q"), (Role.AI, "a") };

        List<ChatMessage> result = RimTalkCompatClient.MessageConverter.Instance.Build(null, messages, SampleImageBase64);

        ClassicAssert.AreEqual(3, result.Count);
        ClassicAssert.AreEqual(ChatRole.Assistant, result[1].Role);
        ClassicAssert.AreEqual(ChatRole.User, result[2].Role);
        ClassicAssert.IsTrue(result[2].Contents.OfType<DataContent>().Any());
        ClassicAssert.IsFalse(result[1].Contents.OfType<DataContent>().Any());
    }

    [Test]
    public void DescribeRequest_OmitsBase64AndKeepsOpenAIShape()
    {
        var prefix = new List<(Role role, string message)> { (Role.System, "sys"), (Role.User, "hello") };
        List<ChatMessage> built = RimTalkCompatClient.MessageConverter.Instance.Build(prefix, null, SampleImageBase64);

        string json = RimTalkCompatClient.MessageConverter.DescribeRequest(built, stream: true, "test-model");

        StringAssert.Contains("\"model\":\"test-model\"", json.Replace(" ", string.Empty));
        StringAssert.Contains("\"stream\":true", json.Replace(" ", string.Empty));
        StringAssert.Contains("\"role\":\"system\"", json.Replace(" ", string.Empty));
        StringAssert.Contains("\"role\":\"user\"", json.Replace(" ", string.Empty));
        StringAssert.Contains("image/jpeg", json);
        StringAssert.DoesNotContain(SampleImageBase64, json);
    }

    [Test]
    public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
    {
        var settings = new RimLLMFrameworkSettings();

        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId));
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled("never.registered"));
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(null));

        settings.SetCompatTakeoverEnabled(RimTalkCompatTarget.PackageId, true);
        ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId));
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled("never.registered"));

        settings.SetCompatTakeoverEnabled(RimTalkCompatTarget.PackageId, false);
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId));

        // 空鍵不得寫入也不得擲例外
        settings.SetCompatTakeoverEnabled(null, true);
        settings.SetCompatTakeoverEnabled(string.Empty, true);
    }

    /// <summary>
    /// 模擬真實情境：模型以 JSONL 回三個 pawn 的對話，串流切在物件邊界的任意位置。
    /// 每湊齊一行就要回呼一次（氣泡逐個出現），而不是等整段結束。
    /// </summary>
    [Test]
    public async Task Streaming_FeedsRimTalkParserPerChunkAndReportsEachLineAsItCompletes()
    {
        var fake = new CapturingChatClient();
        fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "{\"name\":\"Alice\",\"te") { ModelId = "fake-model" });
        fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant,
            "xt\":\"Hi Bob\"}\n{\"name\":\"Bob\",\"text\":\"Hi {Alice}\",\"act\":\"Chat\",\"target\":\"Alice\"}\n{\"name\":\"Ca"));
        fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "rol\",\"text\":\"...\"}"));
        var usage = new ChatResponseUpdate { Contents = { new UsageContent(new UsageDetails { TotalTokenCount = 321 }) } };
        fake.StreamUpdates.Add(usage);

        var client = new RimTalkCompatClient(fake);
        var seen = new List<string>();
        var prefix = new List<(Role role, string message)> { (Role.System, "Output JSONL."), (Role.User, "talk") };
        Payload prepared = null;

        Payload payload = await StreamParsed<TalkResponse>(client, prefix, [],
            onResponseParsed: r => seen.Add(r.Name),
            onRequestPrepared: p => prepared = p);

        ClassicAssert.IsNotNull(prepared);
        StringAssert.Contains("Output JSONL.", prepared.Request);
        ClassicAssert.AreEqual(3, seen.Count);
        CollectionAssert.AreEqual(new[] { "Alice", "Bob", "Carol" }, seen);
        ClassicAssert.AreEqual("fake-model", payload.Model);
        ClassicAssert.AreEqual(321, payload.TokenCount);
        ClassicAssert.AreEqual(200, payload.StatusCode);
        StringAssert.Contains("\"name\":\"Carol\"", payload.Response);
        ClassicAssert.AreEqual("rimllm://cj.rimtalk", payload.URL);

        // 轉接器把 RimTalk 訊息原樣送給 RimLLM client，且關閉思考。
        ChatMessage[] sent = [.. fake.ReceivedMessages.Single()];
        ClassicAssert.AreEqual(ChatRole.System, sent[0].Role);
        ClassicAssert.IsTrue(fake.ReceivedOptions.Single() is RimLLMChatOptions opts && opts.DisableReasoning);
        // RimTalk 原生不設 max_tokens；多 pawn JSONL 不能被框架預設的 1024 截斷。
        ClassicAssert.AreEqual(RimTalkCompatClient.MaxOutputTokens, fake.ReceivedOptions.Single().MaxOutputTokens);
        ClassicAssert.GreaterOrEqual(fake.ReceivedOptions.Single().MaxOutputTokens, 2048);
    }

    [Test]
    public async Task Streaming_IgnoresReasoningContentWhenSplittingJsonl()
    {
        var fake = new CapturingChatClient();
        var reasoning = new ChatResponseUpdate { Contents = { new TextReasoningContent("{\"name\":\"Ghost\",\"text\":\"should not surface\"}") } };
        fake.StreamUpdates.Add(reasoning);
        fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "{\"name\":\"Real\",\"text\":\"ok\"}"));

        var client = new RimTalkCompatClient(fake);
        var names = new List<string>();
        Payload payload = await StreamParsed<TalkResponse>(client, null, null, r => names.Add(r.Name));

        CollectionAssert.AreEqual(new[] { "Real" }, names);
        StringAssert.DoesNotContain("Ghost", payload.Response);
    }

    [Test]
    public async Task NonStreaming_ReturnsPayloadWithModelTextAndTokens()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"name\":\"A\",\"text\":\"b\"}"))
            {
                ModelId = "fake-model",
                Usage = new UsageDetails { TotalTokenCount = 42 }
            }
        };
        var client = new RimTalkCompatClient(fake);

        Payload payload = await client.GetChatCompletionAsync(
            [(Role.User, "q")], [], null, null);

        ClassicAssert.AreEqual("fake-model", payload.Model);
        ClassicAssert.AreEqual(42, payload.TokenCount);
        ClassicAssert.AreEqual("{\"name\":\"A\",\"text\":\"b\"}", payload.Response);
        ClassicAssert.IsNull(payload.ErrorMessage);
    }

    [Test]
    public void Errors_AreWrappedAsAIRequestExceptionWithPayload()
    {
        var fake = new CapturingChatClient
        {
            // 帶 InnerException：Wrap 必須拿到 RimLLMException 本身，不能被挖到內層的 socket 例外。
            ResponseException = new RimLLMException(LLMError.RateLimit, "slow down", new InvalidOperationException("socket closed"))
        };
        var client = new RimTalkCompatClient(fake);

        var ex = Assert.ThrowsAsync<AIRequestException>(async () =>
            await client.GetChatCompletionAsync(null, [(Role.User, "q")], null, null));

        StringAssert.Contains("RateLimit", ex.Message);
        StringAssert.Contains("slow down", ex.Message);
        ClassicAssert.IsNotNull(ex.Payload);
        ClassicAssert.AreEqual(ex.Message, ex.Payload.ErrorMessage);
        StringAssert.Contains("\"q\"", ex.Payload.Request);
    }

    /// <summary>
    /// 攔截只認哨兵：RimTalk 交來的 OpenAIClient 若是我們在掛載時建的那一個就改走 RimLLM，
    /// 玩家自己的原生 client 一律放行。這是接管不干擾「開關關閉」情境的關鍵。
    /// </summary>
    [Test]
    public async Task Prefixes_RouteOnlyTheSentinelInstance()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "routed"))
        };
        fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "chunk"));
        RimTalkCompatClient previousClient = RimTalkCompatPatch.Client;
        object previousSentinel = RimTalkCompatPatch.Sentinel;
        try
        {
            RimTalkCompatPatch.Client = new RimTalkCompatClient(fake);
            var sentinel = new OpenAIClient(null, "RimLLM");
            var native = new OpenAIClient(null, "native");
            RimTalkCompatPatch.Sentinel = sentinel;
            var messages = new List<(Role role, string message)> { (Role.User, "q") };

            object completion = null;
            ClassicAssert.IsTrue(RimTalkCompatPatch.GetChatCompletionAsyncPrefix(native, null, messages, null, null, ref completion));
            ClassicAssert.IsNull(completion);
            ClassicAssert.IsFalse(RimTalkCompatPatch.GetChatCompletionAsyncPrefix(sentinel, null, messages, null, null, ref completion));
            ClassicAssert.AreEqual("routed", (await (Task<Payload>)completion).Response);

            object stream = null;
            var chunks = new List<string>();
            ClassicAssert.IsTrue(RimTalkCompatPatch.StreamAsyncPrefix(native, null, messages, null, chunks.Add, null, ref stream));
            ClassicAssert.IsNull(stream);
            ClassicAssert.IsFalse(RimTalkCompatPatch.StreamAsyncPrefix(sentinel, null, messages, null, chunks.Add, null, ref stream));
            ClassicAssert.AreEqual("chunk", (await (Task<Payload>)stream).Response);
            CollectionAssert.AreEqual(new[] { "chunk" }, chunks);
        }
        finally
        {
            RimTalkCompatPatch.Client = previousClient;
            RimTalkCompatPatch.Sentinel = previousSentinel;
        }
    }

    /// <summary>
    /// 鎖住相容層的型別載入防線：框架 DLL 裡任何型別（含編譯器生成的 async 狀態機、closure、快取的 lambda
    /// 委派）都不得以 RimTalk 型別當基底類別、介面或欄位型別。
    /// 前兩者由 RimWorld 載入 DLL 時的 Assembly.GetTypes() 解析，RimTalk 缺席時整顆框架被拒載；
    /// 欄位由 DevMode 啟動時 StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes 的 GetFields()
    /// 解析（Mono 會解析全部欄位型別），RimTalk 缺席時每次啟動一條紅字。
    /// 方法簽章與本體延遲到 JIT 才解析，允許出現。
    /// </summary>
    [Test]
    public void FrameworkAssembly_HasNoRimTalkTypesInBaseInterfacesOrFields()
    {
        var offenders = new List<string>();
        foreach (Type type in typeof(RimTalkCompatPatch).Assembly.GetTypes())
        {
            foreach ((string where, Type dependency) in TypeLoadDependencies(type))
            {
                if (dependency.Assembly.GetName().Name == "RimTalk")
                {
                    offenders.Add(type.FullName + " " + where + " -> " + dependency.FullName);
                }
            }
        }

        ClassicAssert.IsEmpty(offenders,
            "這些型別在基底／介面／欄位層級參考了 RimTalk，RimTalk 未安裝時框架會被 RimWorld 拒載或在 DevMode 啟動時報錯。" +
            "async 方法的 RimTalk 型別參數與區域變數會變成狀態機欄位，改成非 async 薄殼（見 RimTalkCompatClient）。");
    }

    private static IEnumerable<(string where, Type type)> TypeLoadDependencies(Type type)
    {
        const BindingFlags allFields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        if (type.BaseType != null)
        {
            foreach (Type t in Expand(type.BaseType)) yield return ("base", t);
        }
        foreach (Type iface in type.GetInterfaces())
        {
            foreach (Type t in Expand(iface)) yield return ("interface", t);
        }
        foreach (FieldInfo field in type.GetFields(allFields))
        {
            foreach (Type t in Expand(field.FieldType)) yield return ("field " + field.Name, t);
        }
    }

    /// <summary>一個型別及其陣列元素／泛型引數（遞迴）——Mono 解析欄位型別時會一路解析到底。</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (Type t in Expand(type.GetElementType())) yield return t;
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type t in Expand(argument)) yield return t;
            }
        }
    }

    /// <summary>
    /// 對應 RimTalk OpenAIClient.GetStreamingChatCompletionAsync&lt;T&gt; 的包裝：JSONL 解析在 RimTalk 那層，
    /// 轉接器只吐文字塊。測試在這裡重現該包裝，驗證逐塊餵 parser 的行為。
    /// </summary>
    private static Task<Payload> StreamParsed<T>(
        RimTalkCompatClient client,
        List<(Role role, string message)> prefix,
        List<(Role role, string message)> messages,
        Action<T> onResponseParsed,
        Action<Payload> onRequestPrepared = null) where T : class
    {
        var parser = new RimTalk.Util.JsonStreamParser<T>();
        return client.StreamAsync(prefix, messages, null, chunk =>
        {
            foreach (T item in parser.Parse(chunk)) onResponseParsed?.Invoke(item);
        }, onRequestPrepared);
    }

    [Test]
    public void RimTalkTarget_DeclaresStablePackageId()
    {
        ClassicAssert.AreEqual("cj.rimtalk", RimTalkCompatTarget.PackageId);
    }
}
