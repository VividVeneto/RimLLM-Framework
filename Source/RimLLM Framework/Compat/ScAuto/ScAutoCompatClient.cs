using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 以 RimLLM 的 <see cref="IChatClient"/> 承接簡中自動化 One 的 AI 翻譯與評審流量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 該 Mod 的傳輸層把請求組成 OpenAI 的 Chat Completions 或 Responses 兩種 JSON 之一再 POST 出去，
    /// 並以自家解析器把回應轉成內部結果。本類別把「請求 JSON → RimLLM → 同協議的回應 JSON」整段做成純字串轉換，
    /// 讓補丁層只需把合成的回應 JSON 交還給該 Mod 自己的解析器，不必引用或建構它的任何型別
    /// （其類別多為 <c>internal</c>）。失敗同樣合成為該協議的錯誤 JSON，使其原有的重試與降級邏輯照常運作。
    /// </para>
    /// <para>
    /// 提示工程與結構化輸出約束完全沿用該 Mod 的請求：訊息原樣轉送，不另外設定回應格式。
    /// </para>
    /// </remarks>
    internal sealed class ScAutoCompatClient : CompatClientBase
    {
        /// <summary>請求未指定輸出上限時的預設值，對齊該 Mod 自家請求建構器的預設（8192）。</summary>
        internal const int DefaultMaxOutputTokens = 8192;

        /// <summary>寫進合成回應的識別，僅用於除錯。</summary>
        private const string ResponseId = "rimllm-takeover";

        /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
        public ScAutoCompatClient()
            : base(ScAutoCompatTarget.PackageId)
        {
        }

        /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證請求轉換與回應合成。</summary>
        internal ScAutoCompatClient(IChatClient chat)
            : base(ScAutoCompatTarget.PackageId, chat)
        {
        }

        /// <summary>
        /// 把該 Mod 的請求 JSON 交給 RimLLM，回傳同協議的回應 JSON（成功或錯誤）。
        /// </summary>
        /// <param name="payloadJson">該 Mod 傳輸層即將 POST 的請求本文。</param>
        /// <param name="chatProtocol">true 為 Chat Completions，false 為 Responses。</param>
        /// <param name="cancellationToken">該 Mod 的取消 token；觸發時原樣擲出 <see cref="OperationCanceledException"/>。</param>
        /// <remarks>
        /// 除了呼叫端取消以外的失敗一律不擲出，而是轉成錯誤 JSON：該 Mod 的解析器會把它還原成帶 ErrorType 的失敗結果，
        /// 暫時性錯誤（timeout／network／rate_limit／server）由它自己重試。
        /// </remarks>
        public async Task<string> CompleteAsync(string payloadJson, bool chatProtocol, CancellationToken cancellationToken)
        {
            try
            {
                RequestSpec spec = ParsePayload(payloadJson, chatProtocol);
                ChatOptions options = BuildOptions();
                if (spec.MaxOutputTokens.HasValue) options.MaxOutputTokens = spec.MaxOutputTokens;
                if (spec.Temperature.HasValue) options.Temperature = spec.Temperature;
                if (spec.TopP.HasValue) options.TopP = spec.TopP;

                ChatResponse response = await Chat.GetResponseAsync(spec.Messages, options, cancellationToken).ConfigureAwait(false);
                return BuildSuccessJson(response.Text ?? string.Empty, chatProtocol);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                return BuildErrorJson("timeout", ex.Message, chatProtocol);
            }
            catch (RimLLMException ex)
            {
                return BuildErrorJson(MapErrorType(ex.Error), $"[RimLLM:{ex.Error}] {ex.Message}", chatProtocol);
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException)
            {
                return BuildErrorJson("parse", ex.Message, chatProtocol);
            }
            catch (Exception ex)
            {
                return BuildErrorJson("server", ex.Message, chatProtocol);
            }
        }

        /// <summary>
        /// RimLLM 錯誤分類 → 該 Mod 的 <c>ApiErrorTypes</c>。暫時性（可重試）的只有 network／timeout／rate_limit／server，
        /// 金鑰、模型、內容過濾等永久性錯誤用不含這些字樣的名稱，避免它白白重試。
        /// </summary>
        internal static string MapErrorType(LLMError error)
        {
            switch (error)
            {
                case LLMError.Timeout: return "timeout";
                case LLMError.RateLimit: return "rate_limit";
                case LLMError.NetworkError:
                case LLMError.ProviderOffline: return "network";
                case LLMError.ContextWindowExceeded: return "context_exceeded";
                case LLMError.InvalidResponse: return "parse";
                case LLMError.QuotaExceeded: return "quota_exceeded";
                case LLMError.InvalidKey: return "invalid_key";
                case LLMError.ModelNotFound: return "model_not_found";
                case LLMError.ContentFilter: return "content_filter";
                case LLMError.Cancelled: return "cancelled";
                default: return "unknown_error";
            }
        }

        /// <summary>
        /// 預設關閉思考鏈：該 Mod 自家請求固定帶 <c>thinking: disabled</c> 與 <c>reasoning_effort: none</c>，
        /// 批量翻譯講求速度與成本。輸出上限與取樣參數再由請求內容覆寫。
        /// </summary>
        protected override ChatOptions BuildOptions()
        {
            return new RimLLMChatOptions { DisableReasoning = true, MaxOutputTokens = DefaultMaxOutputTokens };
        }

        /// <summary>合成 OpenAI 風格的成功回應：Chat 為 <c>choices[0].message.content</c>，Responses 為 <c>output[].content[].output_text</c>。</summary>
        internal static string BuildSuccessJson(string text, bool chatProtocol)
        {
            if (chatProtocol)
            {
                return RimLLMJsonHelper.Serialize(new Dictionary<string, object>
                {
                    ["choices"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 0,
                            ["message"] = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = text },
                            ["finish_reason"] = "stop"
                        }
                    }
                });
            }

            return RimLLMJsonHelper.Serialize(new Dictionary<string, object>
            {
                ["id"] = ResponseId,
                ["status"] = "completed",
                ["output"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object> { ["type"] = "output_text", ["text"] = text }
                        }
                    }
                }
            });
        }

        /// <summary>合成 OpenAI 風格的錯誤回應；該 Mod 的解析器會讀 <c>error.type</c> 與 <c>error.message</c>。</summary>
        internal static string BuildErrorJson(string errorType, string message, bool chatProtocol)
        {
            var error = new Dictionary<string, object> { ["type"] = errorType, ["message"] = message ?? string.Empty };
            var root = new Dictionary<string, object> { ["error"] = error };
            if (!chatProtocol)
            {
                root["id"] = ResponseId;
                root["status"] = "failed";
            }
            return RimLLMJsonHelper.Serialize(root);
        }

        /// <summary>請求 JSON 解出的中性結果。</summary>
        internal sealed class RequestSpec
        {
            public List<ChatMessage> Messages = new List<ChatMessage>();
            public int? MaxOutputTokens;
            public float? Temperature;
            public float? TopP;
        }

        /// <summary>
        /// 解析請求 JSON。Chat：<c>messages[{role, content}]</c>；Responses：<c>instructions</c>（可選）加 <c>input</c>
        /// （字串，或 <c>{role, content}</c> 項目的陣列）。<c>content</c> 可為字串或內容片段陣列（文字、圖片 data URL）。
        /// </summary>
        internal static RequestSpec ParsePayload(string payloadJson, bool chatProtocol)
        {
            var spec = new RequestSpec();
            using (JsonDocument document = JsonDocument.Parse(payloadJson ?? string.Empty))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Request payload is not an object.");
                }

                if (chatProtocol)
                {
                    AppendItems(spec.Messages, root, "messages");
                }
                else
                {
                    if (root.TryGetProperty("instructions", out JsonElement instructions) && instructions.ValueKind == JsonValueKind.String)
                    {
                        spec.Messages.Add(new ChatMessage(ChatRole.System, instructions.GetString()));
                    }
                    if (root.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.String)
                    {
                        spec.Messages.Add(new ChatMessage(ChatRole.User, input.GetString()));
                    }
                    else
                    {
                        AppendItems(spec.Messages, root, "input");
                    }
                }

                if (spec.Messages.Count == 0)
                {
                    throw new FormatException("Request payload contains no messages.");
                }

                spec.MaxOutputTokens = ReadInt(root, "max_output_tokens") ?? ReadInt(root, "max_completion_tokens") ?? ReadInt(root, "max_tokens");
                spec.Temperature = ReadSingle(root, "temperature");
                spec.TopP = ReadSingle(root, "top_p");
            }
            return spec;
        }

        private static void AppendItems(List<ChatMessage> target, JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("content", out JsonElement content))
                {
                    continue;
                }

                string role = item.TryGetProperty("role", out JsonElement roleElement) && roleElement.ValueKind == JsonValueKind.String
                    ? roleElement.GetString()
                    : null;
                List<AIContent> parts = ReadContent(content);
                if (parts.Count > 0)
                {
                    target.Add(new ChatMessage(ToChatRole(role), parts));
                }
            }
        }

        private static ChatRole ToChatRole(string role)
        {
            switch ((role ?? string.Empty).ToLowerInvariant())
            {
                case "system":
                case "developer": return ChatRole.System;
                case "assistant": return ChatRole.Assistant;
                default: return ChatRole.User;
            }
        }

        private static List<AIContent> ReadContent(JsonElement content)
        {
            var parts = new List<AIContent>();
            if (content.ValueKind == JsonValueKind.String)
            {
                parts.Add(new TextContent(content.GetString()));
                return parts;
            }
            if (content.ValueKind != JsonValueKind.Array)
            {
                return parts;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;

                if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(new TextContent(text.GetString()));
                }
                else if (part.TryGetProperty("image_url", out JsonElement image))
                {
                    // Chat 為 {"url": ...}，Responses 的 input_image 直接是字串。
                    string url = image.ValueKind == JsonValueKind.String
                        ? image.GetString()
                        : image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() : null;
                    DataContent data = ToDataContent(url);
                    if (data != null) parts.Add(data);
                }
            }
            return parts;
        }

        /// <summary>只處理 <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c>；該 Mod 的預覽圖一律是這種形式。</summary>
        private static DataContent ToDataContent(string url)
        {
            const string prefix = "data:";
            const string marker = ";base64,";
            if (string.IsNullOrEmpty(url) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            int split = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (split < 0) return null;

            string mediaType = url.Substring(prefix.Length, split - prefix.Length);
            return new DataContent(Convert.FromBase64String(url.Substring(split + marker.Length)), mediaType);
        }

        private static int? ReadInt(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number > 0
                ? number
                : (int?)null;
        }

        private static float? ReadSingle(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
                ? (float)number
                : (float?)null;
        }
    }
}
