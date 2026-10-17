using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Providers;

/// <summary>
/// Google Gemini API 供應商，經由官方 OpenAI 相容端點存取。
/// 對話、串流、工具呼叫、結構化輸出、思考強度（<c>reasoning_effort</c>）與模型清單
/// 全由基底 <see cref="OpenAIProvider"/> 經 OpenAI SDK 處理，不再使用原生 Google.GenAI 路徑。
/// 既有的供應商識別碼、API 金鑰與模型設定沿用不變。
/// 唯一例外是上下文上限：相容端點不回報，重新整理模型清單時另向原生 <c>/models</c> 讀取。
/// </summary>
public class GeminiProvider : OpenAIProvider
{
    public GeminiProvider(IRimLLMSettings settings)
        : base(settings, ProviderIds.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash")
    {
    }

    /// <summary>
    /// 相容端點的 /models 不回報上限，改向原生 <c>/models</c> 讀 <c>inputTokenLimit</c>。
    /// 兩個請求彼此獨立，同時發出。上限只是附帶資訊：原生端點失敗時模型清單照常回傳。
    /// </summary>
    internal override async Task<ModelCatalog> FetchModelCatalogAsync()
    {
        Task<Dictionary<string, int>> nativeTask = FetchNativeInputTokenLimitsAsync();
        ModelCatalog catalog = await base.FetchModelCatalogAsync().ConfigureAwait(false);
        Dictionary<string, int> windows = await nativeTask.ConfigureAwait(false);
        return windows == null ? catalog : new ModelCatalog(catalog.Models, windows);
    }

    /// <summary>讀取原生端點的輸入上限；無法推得原生端點或請求失敗時回傳 null（失敗會記警告）。</summary>
    private async Task<Dictionary<string, int>> FetchNativeInputTokenLimitsAsync()
    {
        // 相容根位址以 /openai 結尾，去掉即為原生 API 根位址；自訂成其他代理時無從推得，略過。
        string root = NormalizeEndpoint(Settings.GetEndpoint(ProviderId, DefaultEndpoint));
        if (root == null || !root.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        float timeoutSeconds = Settings.ApiTimeout > 0 ? Settings.ApiTimeout : 30f;
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            bool isGoogle = await RimLLMEmbeddingService.ForEachGoogleNativeModelAsync(
                root, Settings.GetActiveApiKey(ProviderId), model => ReadInputTokenLimit(model, windows), cts.Token).ConfigureAwait(false);
            return isGoogle ? windows : null;
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Could not read Gemini context window sizes from the native models endpoint: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            return null;
        }
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
