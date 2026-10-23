using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Providers;

/// <summary>
/// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
/// </summary>
public class OpenAIProvider : ILLMProvider
{
    protected readonly IRimLLMSettings Settings;
    private readonly string _providerId;
    private readonly string _defaultEndpoint;
    private readonly string _defaultTestModel;

    public virtual string ProviderId => _providerId;
    protected virtual string DefaultEndpoint => _defaultEndpoint;

    /// <summary>
    /// 此供應商是否必須提供 API Key 才能使用。預設為 true，本地相容介面可覆寫為 false。
    /// </summary>
    public virtual bool RequiresApiKey => true;

    /// <summary>
    /// 衍生 provider 是否支援 OpenAI 相容的 <c>response_format: json_schema</c> 欄位。
    /// 預設為 true：OpenAI 官方支援 strict JSON Schema；不支援的服務端（Grok/Kimi/MiniMax/
    /// Nvidia/Qwen/Zai/OpenAICompatible）應覆寫為 false，讓框架改走提示式 JSON fallback。
    /// </summary>
    protected virtual bool SupportsNativeJsonSchemaPayload => true;

    /// <summary>
    /// 能力描述在第一次存取時建立並重用：相容層每秒輪詢對每個候選都會讀一次，
    /// 每次請求也讀兩次，逐次 new 只是白白製造 GC 壓力。延遲建立而非建構子建立，
    /// 是因為 <see cref="SupportsNativeJsonSchemaPayload"/> 為虛擬屬性，子類覆寫在建構子期間尚不可靠。
    /// </summary>
    private LLMProviderCapabilities _capabilities;

    public virtual LLMProviderCapabilities Capabilities => _capabilities ?? (_capabilities = new LLMProviderCapabilities
    {
        SupportsNativeStructuredOutput = SupportsNativeJsonSchemaPayload,
        SupportsStreaming = true,
        SupportsUsageMetadata = true,
        // 官方 OpenAI SDK 的 IChatClient 會把 ChatOptions.Tools 轉成 tools 欄位送出，
        // OpenAI 相容端點（DeepSeek / Grok / Qwen 等子類）同樣支援。
        SupportsFunctionCalling = true
    });

    public OpenAIProvider(IRimLLMSettings settings)
        : this(settings, ProviderIds.OpenAI, "https://api.openai.com/v1/chat/completions", "gpt-4o-mini")
    {
    }

    protected OpenAIProvider(IRimLLMSettings settings, string providerId, string defaultEndpoint, string defaultTestModel)
    {
        Settings = settings;
        _providerId = providerId;
        _defaultEndpoint = defaultEndpoint;
        _defaultTestModel = defaultTestModel;
    }

    public virtual IChatClient CreateChatClient(string model)
    {
        string apiKey = Settings.GetActiveApiKey(ProviderId);
        if (string.IsNullOrEmpty(apiKey) && !RequiresApiKey)
        {
            apiKey = PlaceholderApiKey;
        }
        string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
        IChatClient rawClient = CreateOpenAiChatClient(apiKey, model, endpoint);
        return new OpenAIChatClientAdapter(rawClient, this, model);
    }

    private static RimLLMException MapClientResultException(ClientResultException exception)
    {
        TimeSpan? retryAfter = null;
        try
        {
            if (exception.GetRawResponse()?.Headers.TryGetValue("Retry-After", out string value) == true)
            {
                retryAfter = LLMErrorMapper.ParseRetryAfter(value);
            }
        }
        catch
        {
            // 無法讀取或解析 Retry-After 時，沿用一般重試延遲。
        }

        return LLMErrorMapper.CreateException(
            exception.Status,
            exception.Message,
            retryAfter,
            exception);
    }

    public static IChatClient CreateOpenAiChatClient(string apiKey, string model, string endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key 不得為空。", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("OpenAI model 不得為空。", nameof(model));

        var options = new OpenAIClientOptions();
        string normalizedEndpoint = NormalizeEndpoint(endpoint);
        if (!string.IsNullOrEmpty(normalizedEndpoint))
        {
            options.Endpoint = new Uri(normalizedEndpoint, UriKind.Absolute);
        }

        var client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
        return client.AsIChatClient();
    }

    private static readonly char[] SlashChars = ['/'];

    public static string NormalizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        string normalized = endpoint.Trim().TrimEnd(SlashChars);
        const string suffix = "/chat/completions";
        if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd(SlashChars);
        }
        return normalized;
    }

    /// <summary>
    /// 依模型類型調整 MEAI ChatOptions：推理模型清空 temperature 並對應 reasoning effort，
    /// 其餘維持 executor 已設定的基礎選項。
    /// </summary>
    protected virtual void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
    {
        bool strict = false;
        if (requestOptions?.AdditionalProperties != null &&
            requestOptions.AdditionalProperties.TryGetValue("strict", out object strictVal) && strictVal is bool strictBool)
        {
            // strict 只認 RimLLMSchemaBuilder 算出的值。此處原本還有一段以字串比對
            // "additionalProperties": true 來推斷 strict 的 heuristic —— 那是死碼：
            // 產生器對開放式 map 輸出的是 value schema 物件而非字面 true，比對永遠不命中。
            strict = strictBool;
            options.AdditionalProperties["strict"] = strictBool;
        }

        // response_format 一律以 Patch 寫入原始 JSON，並確保 MEAI 這一側維持 null。
        // OpenAI SDK 的 ChatCompletionOptions.ResponseFormat 是 JsonPatch 支撐的屬性：
        // 只要框架動過同一個模型的 Patch（max_tokens／reasoning／model），RimWorld 的 Mono
        // 還原該屬性時會拿到基底 ChatResponseFormat，送出請求時序列化就會拋
        // "The WriteCore method should be invoked on an overriding type derived from ChatResponseFormat."
        options.ResponseFormat = null;
        string responseFormatJson = BuildResponseFormatJson(requestOptions, strict);

        ApplyReasoningAndSampling(requestOptions, model, options, responseFormatJson);
    }

    /// <summary>
    /// 組出 <c>response_format</c> 欄位的完整 JSON。schema 優先取框架以
    /// AdditionalProperties 傳遞的字串，其次才回頭讀 MEAI 的 ChatResponseFormatJson。
    /// 兩者都沒有、但呼叫端指定了 JSON 模式時退回 json_object；完全沒有 JSON 需求才回傳 null。
    /// </summary>
    private static string BuildResponseFormatJson(ChatOptions requestOptions, bool strict)
    {
        string schemaJson = RimLLMChatOptions.ReadAdditional<string>(requestOptions, "rimllm_response_schema", null);

        if (string.IsNullOrEmpty(schemaJson) &&
            requestOptions?.ResponseFormat is ChatResponseFormatJson jsonFormat && jsonFormat.Schema.HasValue)
        {
            schemaJson = jsonFormat.Schema.Value.GetRawText();
        }

        if (string.IsNullOrEmpty(schemaJson))
        {
            // 沒有 schema 但呼叫端仍要求 JSON 模式時要送出 json_object，
            // 否則這個選項會被靜默丟棄，模型照樣回散文而呼叫端無從察覺。
            return requestOptions?.ResponseFormat is ChatResponseFormatJson
                ? "{\"type\":\"json_object\"}"
                : null;
        }

        // schema 是直接拼進送出的 JSON 的，不合法就會毀掉整個 request body。
        // 先在本地解析一次，讓錯誤停在組裝階段，而不是換成服務端一句沒有線索的 400。
        using (JsonDocument.Parse(schemaJson))
        { // 刻意空區塊：僅驗證 schemaJson 為合法 JSON
        }

        return "{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"custom_type\"," +
               "\"description\":\"RimLLM structured response\"," +
               "\"strict\":" + (strict ? "true" : "false") + "," +
               "\"schema\":" + schemaJson + "}}";
    }

    /// <summary>
    /// 此供應商表達思考強度的線上格式。預設為 OpenAI 的頂層 <c>reasoning_effort</c>，
    /// 格式不同的供應商覆寫這個屬性即可，不需要各自重寫請求組裝。
    /// </summary>
    protected virtual ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.OpenAIEffort;

    /// <summary>
    /// 此供應商是否允許明確關閉思考。xAI 的推理模型無法關閉，送出關閉指令只會換來 400。
    /// </summary>
    protected virtual bool SupportsDisablingReasoning => true;

    /// <summary>
    /// 已知**不具備**思考能力的模型，對這些模型送思考參數只會白白換來一次 400。
    ///
    /// 刻意用否定表列而非肯定表列：肯定表列漏掉新模型會讓設定永久靜默失效（框架先前的 o1/o3 判斷就是如此），
    /// 否定表列漏掉的模型只會被樂觀地送出參數，服務端若不接受，框架記下來重打一次即可自癒。
    /// 換句話說，這份清單的作用只是省掉一次來回，不影響正確性。
    /// </summary>
    protected virtual bool IsKnownNonReasoningModel(string modelName)
    {
        string name = NormalizeModelName(modelName);
        return name.StartsWith("gpt-3.5") || name.StartsWith("gpt-4") || name.StartsWith("chatgpt-4");
    }

    /// <summary>
    /// 取出可用於前綴比對的模型名：去掉聚合服務端的 "vendor/" 前綴並轉小寫。
    /// null 或空字串一律回傳空字串，讓呼叫端的前綴比對自然地不命中。
    /// </summary>
    private static string NormalizeModelName(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return string.Empty;
        string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// 把框架的思考強度換成此供應商認得的字面值。
    /// 各家的詞彙不完全一致（例如 Kimi 只吃 low/high/max），詞彙不同的供應商覆寫這裡。
    /// </summary>
    protected virtual string MapEffortLiteral(ReasoningEffort effort)
    {
        if (effort == ReasoningEffort.Low) return "low";
        if (effort == ReasoningEffort.Medium) return "medium";
        if (effort == ReasoningEffort.High) return "high";
        return null;
    }

    /// <summary>
    /// 依供應商方言送出思考強度，並一併處理 temperature 與 max_tokens 的改寫。
    ///
    /// 思考參數一律由 Patch 掌控而不交給 MEAI 的 <c>ChatOptions.Reasoning</c>：
    /// 後者只會序列化成 OpenAI 的 <c>reasoning_effort</c>，表達不了其他家的方言。
    /// </summary>
    /// <summary>掛在送出去的 ChatOptions 上，記錄這次請求實際送出的思考強度字面值（null 代表沒送）。</summary>
    private const string SentEffortLiteralKey = "rimllm_sent_effort_literal";

    private void ApplyReasoningAndSampling(ChatOptions requestOptions, string model, ChatOptions options, string responseFormatJson)
    {
        ReasoningEffort? effort = requestOptions?.Reasoning?.Effort;
        string effortLiteral = ResolveOutgoingEffortLiteral(requestOptions, model);
        bool thinkingEnabled = effortLiteral != null && effortLiteral != "none";
        // 把這次實際送出的字面值記在請求上。事後判讀 400 要看的是「送了什麼」，不能重算：
        // 並行請求可能已經改寫學習記憶，重算會得到另一個答案而記錯項目。
        (options.AdditionalProperties ??= [])[SentEffortLiteralKey] = effortLiteral;

        options.Reasoning = null;

        if (IsOpenAiReasoningModel(model) || RimLLMReasoningSupport.IsTemperatureUnsupported(ProviderId, model))
        {
            options.Temperature = null;
        }

        // 對齊 raw 路徑：非 reasoning 模型走 max_tokens（OpenAI SDK 預設一律
        // 序列化為 max_completion_tokens），以 Patch 移除後改寫。
        int maxTokens = requestOptions?.MaxOutputTokens ?? 0;
        bool rewriteMaxTokens = maxTokens > 0 && !IsOpenAiReasoningModel(model);

        ReasoningWireFormat format = ReasoningFormat;
        Func<IChatClient, object> baseFactory = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            var chatCompletionOptions = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(baseFactory, client);

            if (responseFormatJson != null)
            {
                chatCompletionOptions.Patch.Set(
                    OpenAIPatchPaths.ResponseFormat,
                    Encoding.UTF8.GetBytes(responseFormatJson));
            }

            if (rewriteMaxTokens)
            {
                chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.MaxCompletionTokens);
                chatCompletionOptions.Patch.Set(OpenAIPatchPaths.MaxTokens, maxTokens);
            }

            // 只有 OpenAIEffort 方言會自己寫回 reasoning_effort，其餘方言一律先清掉，
            // 避免 SDK 或上一層留下的欄位與方言欄位同時出現而互相矛盾。
            if (format != ReasoningWireFormat.OpenAIEffort || effortLiteral == null)
            {
                chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.ReasoningEffort);
            }

            if (effortLiteral == null) return chatCompletionOptions;

            switch (format)
            {
                case ReasoningWireFormat.OpenAIEffort:
                    chatCompletionOptions.Patch.Set(
                        OpenAIPatchPaths.ReasoningEffort,
                        JsonSerializer.SerializeToUtf8Bytes(effortLiteral));
                    break;

                case ReasoningWireFormat.OpenRouterReasoning:
                    chatCompletionOptions.Patch.Set(
                        OpenAIPatchPaths.Reasoning,
                        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { { "effort", effortLiteral } }));
                    break;

                case ReasoningWireFormat.ThinkingSwitch:
                    chatCompletionOptions.Patch.Set(
                        OpenAIPatchPaths.Thinking,
                        JsonSerializer.SerializeToUtf8Bytes(
                            new Dictionary<string, string> { { "type", thinkingEnabled ? "enabled" : "disabled" } }));
                    if (thinkingEnabled)
                    {
                        chatCompletionOptions.Patch.Set(
                            OpenAIPatchPaths.ReasoningEffort,
                            JsonSerializer.SerializeToUtf8Bytes(effortLiteral));
                    }
                    break;

                case ReasoningWireFormat.EnableThinkingFlag:
                    chatCompletionOptions.Patch.Set(
                        OpenAIPatchPaths.EnableThinking, thinkingEnabled);
                    if (thinkingEnabled && effort.HasValue)
                    {
                        chatCompletionOptions.Patch.Set(
                            OpenAIPatchPaths.ThinkingBudget, ResolveThinkingBudget(effort.Value));
                    }
                    break;
            }

            return chatCompletionOptions;
        };
    }

    /// <summary>
    /// 解析呼叫端是否要求關閉思考。RimLLMChatOptions 直接帶屬性，框架管線則以 AdditionalProperties 轉遞。
    /// </summary>
    private static bool ResolveDisableReasoning(ChatOptions requestOptions)
    {
        return requestOptions is RimLLMChatOptions rimOptions
            ? rimOptions.DisableReasoning
            : RimLLMChatOptions.ReadAdditional(requestOptions, RimLLMChatOptions.DisableReasoningKey, false);
    }

    /// <summary>
    /// 算出這次請求實際會送出的強度字面值。回傳 null 代表不干預，交給服務端自己的預設。
    /// 組裝請求與事後判讀 400 都走這一個函式，兩邊對「送了什麼」的認知才不會分歧。
    /// </summary>
    private string ResolveOutgoingEffortLiteral(ChatOptions requestOptions, string model)
    {
        // 服務端先前明確拒絕過思考參數的模型不再嘗試，避免每次請求都白白換來一次 400。
        bool reasoningAllowed = ReasoningFormat != ReasoningWireFormat.None &&
                                !IsKnownNonReasoningModel(model) &&
                                !RimLLMReasoningSupport.IsReasoningUnsupported(ProviderId, model);
        if (!reasoningAllowed) return null;

        if (ResolveDisableReasoning(requestOptions))
        {
            // 關不掉的模型（靜態宣告或服務端拒絕過）：沿用 Grok 的作法靜默忽略關閉指令，
            // 不送任何思考參數，也不改送強度。
            bool canDisable = SupportsDisablingReasoning &&
                              !RimLLMReasoningSupport.IsDisableUnsupported(ProviderId, model);
            return canDisable ? "none" : null;
        }

        ReasoningEffort? effort = requestOptions?.Reasoning?.Effort;
        return effort.HasValue ? MapEffortLiteral(effort.Value) : null;
    }

    /// <summary>以 token 預算表達強度的方言（Qwen）使用的換算。</summary>
    private static int ResolveThinkingBudget(ReasoningEffort effort)
    {
        if (effort == ReasoningEffort.Low) return 1024;
        if (effort == ReasoningEffort.Medium) return 2048;
        return 4096;
    }

    protected sealed class OpenAIChatClientAdapter : DelegatingChatClient
    {
        private readonly OpenAIProvider _provider;
        private readonly string _model;

        public OpenAIChatClientAdapter(IChatClient innerClient, OpenAIProvider provider, string model)
            : base(innerClient)
        {
            _provider = provider;
            _model = model;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var targetOptions = options?.Clone() ?? new ChatOptions();
            _provider.BuildChatOptions(options, _model, targetOptions);

            ChatResponse response;
            try
            {
                response = await base.GetResponseAsync(messages, targetOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (ClientResultException ex)
            {
                var mapped = MapClientResultException(ex);
                if (_provider.MarkUnsupportedParameters(_model, mapped, targetOptions))
                {
                    var retryOptions = options?.Clone() ?? new ChatOptions();
                    _provider.BuildChatOptions(options, _model, retryOptions);
                    try
                    {
                        response = await base.GetResponseAsync(messages, retryOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ClientResultException retryException)
                    {
                        throw MapClientResultException(retryException);
                    }
                }
                else
                {
                    throw mapped;
                }
            }

            // 若由管線層 RimLLMChatClientExecutor 調度，由管線層集中記帳；
            // 若為外部直接透過 CreateChatClient / GenerateAsync 調用，則在此處記錄用量。
            bool isExecutorManaged = RimLLMChatOptions.ReadAdditional(options, RimLLMChatOptions.ExecutorManagedKey, false);
            if (!isExecutorManaged && response?.Usage != null)
            {
                try
                {
                    var manager = RimLLMProvider.Manager;
                    if (manager != null)
                    {
                        int promptTokens = (int)(response.Usage.InputTokenCount ?? 0);
                        int completionTokens = (int)(response.Usage.OutputTokenCount ?? 0);
                        int cachedTokens = (int)(response.Usage.CachedInputTokenCount ?? 0);
                        manager.RecordUsage(_provider.ProviderId, _model, promptTokens, completionTokens, cachedTokens);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Manager not initialized
                }
            }

            return response;
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return new OpenAiStreamEnumerable(
                (opts, ct) => base.GetStreamingResponseAsync(messages, opts, ct),
                options,
                _provider,
                _model);
        }
    }

    private sealed class OpenAiStreamEnumerable : IAsyncEnumerable<ChatResponseUpdate>
    {
        private readonly Func<ChatOptions, System.Threading.CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamFactory;
        private readonly ChatOptions _originalOptions;
        private readonly OpenAIProvider _provider;
        private readonly string _model;

        public OpenAiStreamEnumerable(
            Func<ChatOptions, System.Threading.CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> streamFactory,
            ChatOptions originalOptions,
            OpenAIProvider provider,
            string model)
        {
            _streamFactory = streamFactory;
            _originalOptions = originalOptions;
            _provider = provider;
            _model = model;
        }

        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
            System.Threading.CancellationToken cancellationToken = default)
        {
            var targetOptions = _originalOptions?.Clone() ?? new ChatOptions();
            _provider.BuildChatOptions(_originalOptions, _model, targetOptions);
            var innerEnumerator = _streamFactory(targetOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
            return new OpenAiStreamEnumerator(_streamFactory, innerEnumerator, _originalOptions, targetOptions, _provider, _model, cancellationToken);
        }
    }

    private sealed class OpenAiStreamEnumerator : IAsyncEnumerator<ChatResponseUpdate>
    {
        private readonly Func<ChatOptions, System.Threading.CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamFactory;
        private IAsyncEnumerator<ChatResponseUpdate> _inner;
        private readonly ChatOptions _originalOptions;
        /// <summary>目前這次嘗試實際送出的 options，判讀 400 時要看它而不是重算。</summary>
        private ChatOptions _sentOptions;
        private readonly OpenAIProvider _provider;
        private readonly string _model;
        private readonly System.Threading.CancellationToken _cancellationToken;
        private readonly bool _isExecutorManaged;
        private bool _hasYieldedAny;
        private UsageDetails _lastUsage;
        private bool _usageRecorded;

        public OpenAiStreamEnumerator(
            Func<ChatOptions, System.Threading.CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> streamFactory,
            IAsyncEnumerator<ChatResponseUpdate> inner,
            ChatOptions originalOptions,
            ChatOptions sentOptions,
            OpenAIProvider provider,
            string model,
            System.Threading.CancellationToken cancellationToken)
        {
            _streamFactory = streamFactory;
            _inner = inner;
            _originalOptions = originalOptions;
            _sentOptions = sentOptions;
            _provider = provider;
            _model = model;
            _cancellationToken = cancellationToken;
            _isExecutorManaged = RimLLMChatOptions.ReadAdditional(
                originalOptions,
                RimLLMChatOptions.ExecutorManagedKey,
                false);
        }

        public ChatResponseUpdate Current => _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            try
            {
                while (true)
                {
                    try
                    {
                        bool hasNext = await _inner.MoveNextAsync().ConfigureAwait(false);
                        if (hasNext)
                        {
                            _hasYieldedAny = true;
                            CaptureUsage(_inner.Current);
                        }
                        return hasNext;
                    }
                    catch (ClientResultException ex)
                    {
                        var mapped = MapClientResultException(ex);
                        if (!_hasYieldedAny && _provider.MarkUnsupportedParameters(_model, mapped, _sentOptions))
                        {
                            // 尚未產出任何分塊且為可自癒參數（如思考強度/格式），在此請求中就地重試
                            await _inner.DisposeAsync().ConfigureAwait(false);
                            var retryOptions = _originalOptions?.Clone() ?? new ChatOptions();
                            _provider.BuildChatOptions(_originalOptions, _model, retryOptions);
                            _sentOptions = retryOptions;
                            _inner = _streamFactory(retryOptions, _cancellationToken).GetAsyncEnumerator(_cancellationToken);
                            continue;
                        }
                        throw mapped;
                    }
                }
            }
            finally
            {
                // 若 provider 在正常結束、中斷或取消前已回報用量，皆只結算一次。
                RecordUsageIfNeeded();
            }
        }

        private void CaptureUsage(ChatResponseUpdate update)
        {
            if (update?.Contents == null) return;

            foreach (AIContent content in update.Contents)
            {
                if (content is UsageContent usage && usage.Details != null)
                {
                    _lastUsage = usage.Details;
                }
            }
        }

        private void RecordUsageIfNeeded()
        {
            if (_usageRecorded || _isExecutorManaged || _lastUsage == null ||
                (!_lastUsage.InputTokenCount.HasValue && !_lastUsage.OutputTokenCount.HasValue))
            {
                return;
            }

            _usageRecorded = true;
            try
            {
                var manager = RimLLMProvider.Manager;
                if (manager != null)
                {
                    manager.RecordUsage(
                        _provider.ProviderId,
                        _model,
                        ToTokenCount(_lastUsage.InputTokenCount),
                        ToTokenCount(_lastUsage.OutputTokenCount),
                        ToTokenCount(_lastUsage.CachedInputTokenCount));
                }
            }
            catch (InvalidOperationException)
            {
                // Manager 尚未初始化時，串流回應仍照常交給呼叫端。
            }
        }

        private static int ToTokenCount(long? count)
        {
            if (!count.HasValue || count.Value <= 0) return 0;
            return count.Value >= int.MaxValue ? int.MaxValue : (int)count.Value;
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }

    /// <summary>
    /// 服務端明確拒絕思考參數或 temperature 時記下來，讓下一次組裝略過該參數。
    /// 回傳 true 代表這次記到了新資訊，值得以去掉參數的請求重打一次；
    /// 回傳 false 代表拒絕與這些參數無關（或先前已記錄過），呼叫端應直接把錯誤拋出去。
    /// </summary>
    private bool MarkUnsupportedParameters(string model, RimLLMException exception, ChatOptions sentOptions)
    {
        bool learned = false;

        if (exception.IsReasoningRejection)
        {
            // 送出的是「關閉」指令時，學到的只是「這個模型關不掉」，不是「不吃思考參數」——
            // 兩者混記的話，連線測試（一律要求關閉）的一次 400 就會讓該模型整個 session
            // 都收不到玩家設定的強度。
            if (RimLLMChatOptions.ReadAdditional<string>(sentOptions, SentEffortLiteralKey, null) == "none")
            {
                if (RimLLMReasoningSupport.MarkDisableUnsupported(ProviderId, model))
                {
                    RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected disabling reasoning; disable requests will be ignored for it from now on.");
                    learned = true;
                }
            }
            else if (RimLLMReasoningSupport.MarkReasoningUnsupported(ProviderId, model))
            {
                RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected reasoning parameters; they will no longer be sent to it.");
                learned = true;
            }
        }

        if (exception.IsTemperatureRejection &&
            RimLLMReasoningSupport.MarkTemperatureUnsupported(ProviderId, model))
        {
            RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected temperature; it will no longer be sent to it.");
            learned = true;
        }

        return learned;
    }

    /// <summary>
    /// 判斷是否為 OpenAI 家的推理模型。這個判斷只用來決定「要不要清掉 temperature」——
    /// 這些模型在思考開啟時會直接以 400 拒絕取樣參數，而不是忽略它。
    ///
    /// 思考強度該不該送**不再**由這裡決定：名稱前綴無法涵蓋各家與未來的模型，
    /// 改由服務端的拒絕來判定（見 <see cref="RimLLMReasoningSupport"/>）。
    /// 這份清單漏掉新模型時，第一次請求會收到 temperature 相關的 400，
    /// 框架會記下來並自動重打，因此漏列的代價是一次重試而不是永久失敗。
    /// </summary>
    protected static bool IsOpenAiReasoningModel(string modelName)
    {
        string name = NormalizeModelName(modelName);
        return name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4") ||
               name.StartsWith("gpt-5");
    }

    public virtual async Task<TestResult> TestConnectionAsync()
    {
        string apiKey = Settings.GetActiveApiKey(ProviderId);
        if (string.IsNullOrEmpty(apiKey) && RequiresApiKey)
        {
            return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "API Key not configured", ErrorCode = LLMError.InvalidKey };
        }

        var result = new TestResult { Provider = ProviderId };
        var stopwatch = Stopwatch.StartNew();
        float timeoutSeconds = Settings.ApiTimeout > 0 ? Settings.ApiTimeout : 30f;
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, "ping") };
            // 輸出上限不能壓到極低：思考型模型會先把額度花在內部推理上，
            // 額度用盡時回傳的 content 是空的，連線其實正常卻會被判成「回傳空白內容」。
            // 因此給足額度並明確要求關閉思考，讓測試只反映「連線與金鑰是否可用」。
            var options = new ChatOptions { MaxOutputTokens = 256 };
            options.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["rimllm_disable_reasoning"] = true
            };
            // 優先使用 DefaultTestModel 作為連線測試模型，因為這是最便宜且穩定的內建對話模型。
            // 只有在 DefaultTestModel 為 "default" (如 OpenAICompatible 本地相容介面) 時，才去讀取快取清單的第一個模型。
            string testModel = DefaultTestModel;
            if (testModel == "default")
            {
                testModel = Settings.GetDefaultModel(ProviderId, DefaultTestModel);
            }

            using IChatClient client = CreateChatClient(testModel);
            await client.GetResponseAsync(messages, options, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            result.Success = true;
            result.Model = testModel;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch (RimLLMException ex)
        {
            stopwatch.Stop();
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.ErrorCode = ex.Error;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            result.Success = false;
            result.ErrorMessage = $"Connection test timed out after {timeoutSeconds} seconds.";
            result.ErrorCode = LLMError.Timeout;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.ErrorCode = LLMError.Unknown;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    protected virtual string DefaultTestModel => _defaultTestModel;

    /// <summary>
    /// 透過官方 SDK 的 /models 端點取得可用模型清單。
    /// 端點正規化與 chat client 共用同一份邏輯，不再手動改寫 URL。
    /// </summary>
    public virtual async Task<List<string>> FetchAvailableModelsAsync()
    {
        return (await FetchModelCatalogAsync().ConfigureAwait(false)).Models;
    }

    /// <summary>
    /// 取得模型清單，並順便從同一份 /models 回應讀出上下文上限。
    /// SDK 只解析 <c>id</c>，上限欄位要從原始 JSON 取。
    /// </summary>
    internal virtual async Task<ModelCatalog> FetchModelCatalogAsync()
    {
        string apiKey = Settings.GetActiveApiKey(ProviderId);
        string endpoint = NormalizeEndpoint(
            Settings.GetEndpoint(ProviderId, DefaultEndpoint));
        float timeoutSeconds = Settings.ApiTimeout > 0 ? Settings.ApiTimeout : 30f;
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
        {
            options.Endpoint = new Uri(endpoint, UriKind.Absolute);
        }

        // 本地相容伺服器多半不驗證金鑰，但 SDK 不接受空憑證。
        var credential = new ApiKeyCredential(
            string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);

        var list = new List<string>();
        Dictionary<string, int> contextWindows;
        try
        {
            ClientResult<OpenAIModelCollection> result = await new OpenAIClient(credential, options)
                .GetOpenAIModelClient()
                .GetModelsAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            foreach (OpenAIModel model in System.Linq.Enumerable.Where(result.Value, m => !string.IsNullOrEmpty(m?.Id)))
            {
                list.Add(model.Id);
            }
            string rawJson = result.GetRawResponse()?.Content?.ToString();
            contextWindows = ParseContextWindows(rawJson);
            if (list.Count > 0 && contextWindows.Count == 0 && string.IsNullOrEmpty(rawJson))
            {
                // SDK 有時不保留原始回應（回傳的 Content 為空），此時模型 id 照常解析，
                // 上下文上限卻會靜默全空。改以裸 HTTP GET 同一個 /models 再解析一次；
                // 原本就有回應內容、只是該服務端不回報上限時（如 OpenAI 官方）不走這條路。
                contextWindows = await FetchRawContextWindowsAsync(
                    endpoint,
                    string.IsNullOrEmpty(apiKey) ? null : apiKey,
                    timeoutSeconds: Settings.ApiTimeout).ConfigureAwait(false);
            }
        }
        catch (ClientResultException ex)
        {
            throw LLMErrorMapper.CreateException(
                ex.Status,
                $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new RimLLMException(
                LLMError.Timeout,
                $"Fetching {ProviderId} models list timed out after {timeoutSeconds} seconds.",
                ex);
        }
        catch (Exception ex)
        {
            throw new RimLLMException(
                LLMError.InvalidResponse,
                $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
        }
        return new ModelCatalog(list, contextWindows);
    }

    /// <summary>
    /// /models 回應中各家表示上下文上限的欄位名稱：
    /// OpenRouter 用 <c>context_length</c>、Groq 用 <c>context_window</c>、vLLM 用 <c>max_model_len</c>。
    /// OpenAI 官方不回報，這類供應商的結果為空。
    /// </summary>
    private static readonly string[] ContextWindowFields = ["context_length", "context_window", "max_model_len"];

    /// <summary>
    /// 從 OpenAI 相容 /models 原始 JSON 讀出「模型 id → 上下文上限」。
    /// 上限只是附帶資訊，格式不符時回傳空集合，不讓整個模型清單的重新整理失敗。
    /// </summary>
    internal static Dictionary<string, int> ParseContextWindows(string json)
    {
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(json)) return windows;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return windows;
            }

            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object
                    || !model.TryGetProperty("id", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string id = idElement.GetString();
                if (string.IsNullOrEmpty(id)) continue;

                int tokens = TryReadContextWindowTokens(model);
                if (tokens > 0)
                {
                    windows[id] = tokens;
                }
            }
        }
        catch (JsonException)
        {
            windows.Clear();
        }
        return windows;
    }

    private static int TryReadContextWindowTokens(JsonElement model)
    {
        foreach (string field in ContextWindowFields)
        {
            if (model.TryGetProperty(field, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int tokens)
                && tokens > 0)
            {
                return tokens;
            }
        }
        return 0;
    }

    /// <summary>
    /// SDK 未保留 /models 原始回應時的補救：以裸 HTTP GET 直接讀取並解析上下文上限。
    /// 解析沿用 <see cref="ParseContextWindows"/>，上限只是附帶資訊，失敗只記警告，
    /// 不讓整個模型清單的重新整理失敗。<paramref name="handler"/> 僅供測試注入，正式路徑傳 null。
    /// </summary>
    internal static async Task<Dictionary<string, int>> FetchRawContextWindowsAsync(
        string endpoint, string apiKey, float timeoutSeconds, HttpMessageHandler handler = null)
    {
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(endpoint)) return windows;

        bool disposeClient = handler != null;
        HttpClient http = handler != null
            ? new HttpClient(handler, false)
            : ModelListHttp;
        try
        {
            string url = endpoint.Trim().TrimEnd(SlashChars) + "/models";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 30f));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            using HttpResponseMessage response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseContextWindows(body);
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Could not read context window sizes from {endpoint}/models: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            return windows;
        }
        finally
        {
            if (disposeClient) http.Dispose();
        }
    }

    private static readonly HttpClient ModelListHttp = new(
        new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate });

    /// <summary>
    /// 本地相容伺服器未設定金鑰時使用的佔位憑證。
    /// </summary>
    private const string PlaceholderApiKey = "not-required";
}
