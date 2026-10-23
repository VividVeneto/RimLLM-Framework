using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;
using RimTalk.Client;
using RimTalk.Data;
using RimTalk.Error;
using RimTalk.Service;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 以 RimLLM 的 <see cref="IChatClient"/> 承接 RimTalk <see cref="IAIClient"/> 的流量。
/// </summary>
/// <remarks>
/// <para>
/// 型別載入防線（RimTalk 缺席時框架仍要能載入）：RimTalk 型別<b>只能出現在方法本體與實例方法簽章</b>（靜態方法簽章也不行：Debug 動作選單會對所有靜態方法做 <c>IsDefined</c> 而解析簽章），
/// 那些要等到方法被 JIT 才解析，而 <see cref="RimTalkCompatPatch"/> 保證只在 RimTalk 存在時才會走到。
/// 不得出現在：
/// <list type="bullet">
/// <item>基底類別或介面——RimWorld 載入 DLL 時的 <c>Assembly.GetTypes()</c> 會解析，整顆框架 DLL 被拒載
/// （所以本類別刻意不實作 <see cref="IAIClient"/>）；</item>
/// <item>任何欄位——DevMode 啟動時 <c>StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes</c>
/// 對每個型別跑 <c>GetFields()</c>，Mono 會解析全部欄位型別；</item>
/// <item>async 方法的參數與區域變數、lambda 捕捉的變數——它們會被編譯器提升成狀態機／closure 的欄位，
/// 等同上一條。所以面向 RimTalk 的 <see cref="GetChatCompletionAsync"/> 與 <see cref="StreamAsync"/>
/// 是<b>非 async 的薄殼</b>：同步把 RimTalk 型別換成 MEAI 型別後，交給零 RimTalk 型別的 async 核心，
/// 再以 <see cref="ToPayload"/> 的 continuation 包回 <see cref="Payload"/>。</item>
/// </list>
/// <c>CompatRimTalkTests.FrameworkAssembly_HasNoRimTalkTypesInBaseInterfacesOrFields</c> 鎖住這三條。
/// 流量的入口是 <see cref="RimTalkCompatPatch"/> 對 RimTalk 自家 <c>OpenAIClient</c> 兩個非泛型
/// 漏斗方法的 Harmony 攔截，再轉呼叫這裡的同名方法。
/// </para>
/// <para>
/// 提示工程完全沿用 RimTalk：<c>prefixMessages</c> 裡已含「Output JSONL」的系統指示，原樣送出即可，
/// 模型回來的就是 RimTalk 期望的格式。
/// </para>
/// <para>
/// 串流粒度與原生一致：RimTalk 的回應是 JSONL（多行、一行一個 pawn 氣泡），<see cref="StreamAsync"/>
/// 把 MEAI 串流的文字塊逐塊交給 RimTalk 自己的 <see cref="JsonStreamParser{T}"/>（在 RimTalk 的
/// <c>GetStreamingChatCompletionAsync<T></c> 包裝層裡），每湊齊一個完整物件就回呼一次——
/// 第一個氣泡不必等整段對話生成完。
/// </para>
/// <para>
/// 錯誤一律包成 <see cref="AIRequestException"/> 並附上 <see cref="Payload"/>，RimTalk 的重試與
/// API Log 才能照常運作；取消則透過 <see cref="AIService.IsCancellationRequested"/> 逐塊輪詢。
/// </para>
/// </remarks>
internal sealed class RimTalkCompatClient
{
    /// <summary>寫進 RimTalk API Log 的端點識別。實際 URL 由 RimLLM fallback 鏈決定，這裡只標示流量歸屬。</summary>
    private const string Endpoint = "rimllm://" + RimTalkCompatTarget.PackageId;

    /// <summary>模型尚未由回應揭露時（請求準備階段、失敗時）寫進日誌的佔位名稱。</summary>
    private const string PendingModel = "RimLLM fallback chain";

    private IChatClient _chat;

    private IChatClient Chat => _chat ?? (_chat = RimLLMProvider.CreateChatClient(RimTalkCompatTarget.PackageId));

    /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
    public RimTalkCompatClient()
    {
    }

    /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證訊息轉換、串流切分與錯誤包裝。</summary>
    internal RimTalkCompatClient(IChatClient chat)
    {
        _chat = chat;
    }

    /// <summary>async 核心回傳的中性結果；不含任何 RimTalk 型別，可安全成為狀態機欄位。</summary>
    private sealed class Completion
    {
        public string ModelId;
        public string Text;
        public int Tokens;
    }

    /// <summary>非串流漏斗，簽章對應 RimTalk <c>OpenAIClient.GetChatCompletionAsync</c>。非 async，見型別註解。</summary>
    public Task<Payload> GetChatCompletionAsync(
        List<(Role role, string message)> prefixMessages,
        List<(Role role, string message)> messages,
        string imageBase64,
        Action<Payload> onRequestPrepared)
    {
        List<ChatMessage> chatMessages = MessageConverter.Instance.Build(prefixMessages, messages, imageBase64);
        string requestJson = MessageConverter.DescribeRequest(chatMessages, stream: false, PendingModel);
        onRequestPrepared?.Invoke(new Payload(Endpoint, PendingModel, requestJson, null, 0));

        return ToPayload(CompleteAsync(chatMessages), requestJson);
    }

    /// <summary>
    /// 串流漏斗，簽章對應 RimTalk <c>OpenAIClient.StreamAsync</c>：每個文字塊原樣交給 <paramref name="onChunk"/>，
    /// JSONL 解析由 RimTalk 自己的包裝層負責。非 async，見型別註解。
    /// </summary>
    public Task<Payload> StreamAsync(
        List<(Role role, string message)> prefixMessages,
        List<(Role role, string message)> messages,
        string imageBase64,
        Action<string> onChunk,
        Action<Payload> onRequestPrepared)
    {
        List<ChatMessage> chatMessages = MessageConverter.Instance.Build(prefixMessages, messages, imageBase64);
        string requestJson = MessageConverter.DescribeRequest(chatMessages, stream: true, PendingModel);
        onRequestPrepared?.Invoke(new Payload(Endpoint, PendingModel, requestJson, null, 0));

        return ToPayload(StreamCoreAsync(chatMessages, onChunk), requestJson);
    }

    private async Task<Completion> CompleteAsync(List<ChatMessage> chatMessages)
    {
        CancellationToken cancellationToken = GetCancellationTokenFromRimTalk();
        ChatResponse response = await Chat.GetResponseAsync(chatMessages, BuildOptions(), cancellationToken);
        return new Completion
        {
            ModelId = response.ModelId,
            Text = response.Text,
            Tokens = (int)(response.Usage?.TotalTokenCount ?? 0)
        };
    }

    private async Task<Completion> StreamCoreAsync(List<ChatMessage> chatMessages, Action<string> onChunk)
    {
        var fullText = new StringBuilder();
        string modelId = null;
        int tokens = 0;
        CancellationToken cancellationToken = GetCancellationTokenFromRimTalk();

        await foreach (ChatResponseUpdate update in Chat.GetStreamingResponseAsync(chatMessages, BuildOptions(), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (update == null) continue;

            if (modelId == null && !string.IsNullOrEmpty(update.ModelId))
            {
                modelId = update.ModelId;
            }
            int updateTokens = ExtractTokens(update);
            if (updateTokens > 0)
            {
                tokens = updateTokens;
            }

            // update.Text 只串接 TextContent，推理內容（TextReasoningContent）不會混進來污染 JSONL 解析。
            string text = update.Text;
            if (string.IsNullOrEmpty(text)) continue;

            fullText.Append(text);
            onChunk?.Invoke(text);
        }

        return new Completion { ModelId = modelId, Text = fullText.ToString(), Tokens = tokens };
    }

    private static int ExtractTokens(ChatResponseUpdate update)
    {
        foreach (AIContent part in update.Contents)
        {
            if (part is UsageContent usage && usage.Details?.TotalTokenCount > 0)
            {
                return (int)usage.Details.TotalTokenCount.Value;
            }
        }
        return 0;
    }

    /// <summary>
    /// 取得目前 RimTalk 請求的 token，讓 provider 能在等待回應或串流資料時直接取消。
    /// 獨立成非 async 方法，讓 RimTalk 型別留在方法本體、不進入狀態機。
    /// </summary>
    private static CancellationToken GetCancellationTokenFromRimTalk()
    {
        // The checked-in RimTalk reference predates this property, but the current
        // runtime API exposes it. Reflect so the mod still compiles against the
        // reference DLL and gracefully falls back with older installed versions.
        try
        {
            PropertyInfo property = typeof(AIService).GetProperty(
                "CurrentCancellationToken",
                BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null) is CancellationToken token
                ? token
                : CancellationToken.None;
        }
        catch (TargetInvocationException)
        {
            return CancellationToken.None;
        }
        catch (MemberAccessException)
        {
            return CancellationToken.None;
        }
    }

    /// <summary>
    /// 把中性結果包回 RimTalk 的 <see cref="Payload"/>；失敗一律包成 <see cref="AIRequestException"/> 並附上
    /// <see cref="Payload"/>，RimTalk 的重試與 API Log 才能照常運作；取消原樣往外丟。
    /// lambda 必須捕捉 <paramref name="requestJson"/>：不捕捉的 lambda 會被編譯器快取成
    /// <c>Func<Task<Completion>, Payload></c> 型別的靜態欄位，違反欄位不得含 RimTalk 型別的防線。
    /// 本方法與 <see cref="Wrap"/> 刻意是實例方法：Debug 動作選單只掃靜態方法的簽章，靜態版會因簽章含 RimTalk 型別而讓選單炸開。
    /// </summary>
    private Task<Payload> ToPayload(Task<Completion> core, string requestJson)
    {
        return core.ContinueWith(task =>
        {
            if (task.IsCanceled)
            {
                throw new OperationCanceledException("Request canceled by RimTalk.");
            }
            if (task.IsFaulted)
            {
                // 取第一層例外而非 GetBaseException()：後者會挖到最內層，把帶 InnerException 的 RimLLMException 剝掉。
                Exception ex = task.Exception.Flatten().InnerExceptions[0];
                if (ex is OperationCanceledException) throw ex;
                throw Wrap(ex, requestJson);
            }

            Completion result = task.Result;
            return new Payload(Endpoint, result.ModelId ?? PendingModel, requestJson, result.Text, result.Tokens)
            {
                StatusCode = 200
            };
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// 接管請求的輸出上限。RimTalk 原生 client 不設 max_tokens；框架對未指定的請求預設補 1024，
    /// 多 pawn 的 JSONL 對話會被截斷，因此在此明確給足；但不能給到 4096：
    /// 有些伺服器（vLLM 等）會以「輸入 + max_tokens ≤ 上下文」驗證，4k 上下文的本地模型會直接 400。
    /// </summary>
    internal const int MaxOutputTokens = 2048;

    /// <summary>
    /// RimTalk 原生 client 的 thinking ladder 第一階就是「關閉思考」（對話講求速度與成本），
    /// 這裡直接以框架的 DisableReasoning 表達同一意圖；溫度沿用 RimLLM 全域設定。
    /// </summary>
    private static ChatOptions BuildOptions()
    {
        return new RimLLMChatOptions { DisableReasoning = true, MaxOutputTokens = MaxOutputTokens };
    }

    private AIRequestException Wrap(Exception ex, string requestJson)
    {
        string message = ex is RimLLMException llmEx ? $"[RimLLM:{llmEx.Error}] {ex.Message}" : ex.Message;
        return new AIRequestException(message, new Payload(Endpoint, PendingModel, requestJson, null, 0, message));
    }

    /// <summary>
    /// RimTalk 訊息轉換器：把 <c>(Role, string)</c> 列表轉成 MEAI <see cref="ChatMessage"/>。
    /// 內部實作，供單元測試直接驗證。
    /// </summary>
    internal sealed class MessageConverter
    {
        /// <summary>
        /// 轉換方法含 RimTalk 型別（<see cref="Role"/>）故為實例方法，經此單例呼叫；理由同 <see cref="ToPayload"/>。
        /// </summary>
        internal static readonly MessageConverter Instance = new();

        /// <summary>RimTalk 截圖一律以 JPEG base64 傳遞（見其 ChatMessage.ToPayload 的 data:image/jpeg）。</summary>
        private const string ImageMediaType = "image/jpeg";

        internal ChatRole ToChatRole(Role role)
        {
            return role switch
            {
                Role.System => ChatRole.System,
                Role.AI => ChatRole.Assistant,
                _ => ChatRole.User,
            };
        }

        /// <summary>
        /// 合併 prefix 與 messages 後轉為 MEAI 訊息。
        /// 與 RimTalk 原生 OpenAIClient.BuildMessages 行為一致：連續同角色訊息以空行合併成一則
        /// （部分供應商拒絕連續同角色訊息）；圖片掛在最後一則 user 訊息上，若最後一則不是 user
        /// 則另外補一則只含圖片的 user 訊息。
        /// </summary>
        internal List<ChatMessage> Build(
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            string imageBase64)
        {
            var merged = new List<(ChatRole role, string text)>();
            AppendMerged(merged, prefixMessages);
            AppendMerged(merged, messages);

            var result = new List<ChatMessage>(merged.Count + 1);
            foreach ((ChatRole role, string text) in merged)
            {
                result.Add(new ChatMessage(role, text));
            }

            if (!string.IsNullOrEmpty(imageBase64))
            {
                var image = new DataContent(Convert.FromBase64String(imageBase64), ImageMediaType);
                ChatMessage last = result.Count > 0 ? result[result.Count - 1] : null;
                if (last != null && last.Role == ChatRole.User)
                {
                    last.Contents.Add(image);
                }
                else
                {
                    result.Add(new ChatMessage(ChatRole.User, [image]));
                }
            }
            return result;
        }

        private void AppendMerged(List<(ChatRole role, string text)> target, List<(Role role, string message)> source)
        {
            if (source == null) return;
            foreach ((Role role, string message) in source)
            {
                ChatRole chatRole = ToChatRole(role);
                string text = message ?? string.Empty;
                if (target.Count > 0 && target[target.Count - 1].role == chatRole)
                {
                    (ChatRole _, string existing) = target[target.Count - 1];
                    target[target.Count - 1] = (chatRole, existing + "\n\n" + text);
                }
                else
                {
                    target.Add((chatRole, text));
                }
            }
        }

        /// <summary>
        /// 產生寫進 RimTalk API Log 的請求描述。維持 OpenAI 風格的 {model, stream, messages[{role, content}]}
        /// 形狀，讓 RimTalk 的 Debug 視窗照常可讀；圖片只標記存在，不把整段 base64 塞進日誌。
        /// </summary>
        internal static string DescribeRequest(IReadOnlyList<ChatMessage> messages, bool stream, string model)
        {
            var payloadMessages = new List<Dictionary<string, object>>(messages.Count);
            foreach (ChatMessage message in messages)
            {
                var entry = new Dictionary<string, object>
                {
                    ["role"] = message.Role.Value,
                    ["content"] = message.Text
                };
                foreach (AIContent content in message.Contents)
                {
                    if (content is DataContent data && data.HasTopLevelMediaType("image"))
                    {
                        entry["image"] = $"<{data.MediaType}, {data.Data.Length} bytes>";
                        break;
                    }
                }
                payloadMessages.Add(entry);
            }

            return RimLLMJsonHelper.Serialize(new Dictionary<string, object>
            {
                ["model"] = model,
                ["stream"] = stream,
                ["messages"] = payloadMessages
            });
        }
    }
}
