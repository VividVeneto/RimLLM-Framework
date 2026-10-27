using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Gemini 原生文字／JSON／SSE 供應商。沿用既有設定與框架的記帳、備援及接管層。
    /// </summary>
    public class GeminiProvider : OpenAIProvider
    {
        public GeminiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Gemini, GeminiNativeChatClient.DefaultEndpoint, "default")
        {
        }

        public override LLMProviderCapabilities Capabilities { get; } = new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = true,
            SupportsStreaming = true,
            SupportsUsageMetadata = true,
            SupportsFunctionCalling = false
        };

        public override IChatClient CreateChatClient(string model)
        {
            var native = new GeminiNativeChatClient(Settings.GetActiveApiKey(ProviderId), model,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint), Settings.ApiTimeout);
            return new OpenAIChatClientAdapter(native, this, model);
        }

        protected override void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            // 原生客戶端直接讀取 MEAI 選項，不套用 OpenAI SDK 專用的 JSON Patch。
        }

        public override async Task<TestResult> TestConnectionAsync()
        {
            if (!string.IsNullOrEmpty(Settings.GetActiveApiKey(ProviderId)) && Settings.GetModelList(ProviderId).Count == 0)
            {
                try
                {
                    List<string> models = await FetchAvailableModelsAsync().ConfigureAwait(false);
                    if (models.Count == 0) return new TestResult { Provider = ProviderId, Success = false,
                        ErrorCode = LLMError.ModelNotFound, ErrorMessage = "The Gemini endpoint exposes no text-generation models." };
                    Settings.SetModelList(ProviderId, models);
                }
                catch (RimLLMException ex)
                {
                    return new TestResult { Provider = ProviderId, Success = false, ErrorCode = ex.Error, ErrorMessage = ex.Message };
                }
                catch (Exception ex)
                {
                    return new TestResult { Provider = ProviderId, Success = false,
                        ErrorCode = LLMError.NetworkError, ErrorMessage = "Could not fetch Gemini models: " + ex.Message };
                }
            }
            return await base.TestConnectionAsync().ConfigureAwait(false);
        }

        internal override async Task<ModelCatalog> FetchModelCatalogAsync()
        {
            string root = GeminiNativeChatClient.NormalizeEndpoint(Settings.GetEndpoint(ProviderId, DefaultEndpoint));
            var models = new List<string>();
            var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.ApiTimeout > 0 ? Settings.ApiTimeout : 30f)))
            {
                bool valid = await RimLLMEmbeddingService.ForEachGoogleNativeModelAsync(root,
                    Settings.GetActiveApiKey(ProviderId), model =>
                    {
                        if (model.TryGetProperty("supportedGenerationMethods", out JsonElement methods))
                        {
                            bool chat = false;
                            foreach (JsonElement method in methods.EnumerateArray())
                                if (method.GetString() == "generateContent") chat = true;
                            if (!chat) return;
                        }
                        if (!model.TryGetProperty("name", out JsonElement nameElement)) return;
                        string name = nameElement.GetString();
                        if (string.IsNullOrEmpty(name)) return;
                        if (name.StartsWith("models/", StringComparison.Ordinal)) name = name.Substring(7);
                        if (!models.Contains(name)) models.Add(name);
                        ReadInputTokenLimit(model, windows);
                    }, cts.Token).ConfigureAwait(false);
                if (!valid) throw new RimLLMException(LLMError.InvalidResponse, "Gemini model list is missing the models array.");
            }
            return new ModelCatalog(models, windows);
        }

        /// <summary>
        /// 把單一原生模型的 <c>inputTokenLimit</c> 寫入 <paramref name="windows"/>。
        /// 名稱去掉 <c>models/</c> 前綴，與相容端點回傳、設定中保存的模型名稱一致。
        /// </summary>
        internal static void ReadInputTokenLimit(JsonElement model, Dictionary<string, int> windows)
        {
            const string namePrefix = "models/";
            if (model.ValueKind == JsonValueKind.Object
                && model.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                && model.TryGetProperty("inputTokenLimit", out JsonElement limit)
                && limit.ValueKind == JsonValueKind.Number
                && limit.TryGetInt32(out int tokens)
                && tokens > 0)
            {
                string name = nameElement.GetString() ?? string.Empty;
                if (name.StartsWith(namePrefix, StringComparison.Ordinal))
                {
                    name = name.Substring(namePrefix.Length);
                }
                if (name.Length > 0) windows[name] = tokens;
            }
        }
    }

    /// <summary>
    /// DeepSeek API 供應商，完全相容 OpenAI API 格式。
    /// 支援 response_format: json_schema，因此沿用基底的原生 Schema 預設值。
    /// </summary>
    public class DeepSeekProvider : OpenAIProvider
    {
        /// <summary>
        /// DeepSeek 以 thinking.type 開關思考，並可同時附上 reasoning_effort 調整強度
        /// （官方範例即為兩者併送）。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.ThinkingSwitch;

        public DeepSeekProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.DeepSeek, "https://api.deepseek.com", "deepseek-v4-flash")
        {
        }
    }

    /// <summary>
    /// Grok (xAI) API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class GrokProvider : OpenAIProvider
    {
        /// <summary>
        /// Grok API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        /// <summary>
        /// xAI 的推理模型無法關閉思考（官方文件明載 reasoning cannot be disabled），
        /// 送出關閉指令只會換來 400，因此關閉請求在此靜默忽略。
        /// </summary>
        protected override bool SupportsDisablingReasoning => false;

        public GrokProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Grok, "https://api.x.ai/v1", "grok-2-1212")
        {
        }
    }

    /// <summary>
    /// Groq API 供應商，提供極速推理，完全相容 OpenAI API 格式。
    /// 支援 response_format: json_schema，因此沿用基底的原生 Schema 預設值。
    /// </summary>
    public class GroqProvider : OpenAIProvider
    {
        public GroqProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Groq, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile")
        {
        }
    }

    /// <summary>
    /// MiniMax 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class MiniMaxProvider : OpenAIProvider
    {
        /// <summary>
        /// MiniMax API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        public MiniMaxProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.MiniMax, "https://api.minimax.io/v1", "MiniMax-M3")
        {
        }
    }

    /// <summary>
    /// NVIDIA API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class NvidiaProvider : OpenAIProvider
    {
        /// <summary>
        /// NVIDIA API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        public NvidiaProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Nvidia, "https://integrate.api.nvidia.com/v1", "meta/llama-3.1-8b-instruct")
        {
        }
    }

    /// <summary>
    /// Qwen (通義千問) 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class QwenProvider : OpenAIProvider
    {
        /// <summary>
        /// Qwen API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        /// <summary>
        /// DashScope 相容模式以頂層 enable_thinking 開關思考，並以 thinking_budget 指定 token 預算。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.EnableThinkingFlag;

        public QwenProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Qwen, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus")
        {
        }
    }

    /// <summary>
    /// Z.ai API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class ZaiProvider : OpenAIProvider
    {
        /// <summary>
        /// Z.ai API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        /// <summary>
        /// GLM 以 thinking.type 開關思考；reasoning_effort 只有部分新模型支援，
        /// 不支援的模型會由服務端忽略或以 400 拒絕，後者交給框架的降級記憶處理。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.ThinkingSwitch;

        public ZaiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Zai, "https://api.z.ai/api/paas/v4", "glm-4.5-flash")
        {
        }
    }
}
