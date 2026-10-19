namespace RimLLM_Framework;

/// <summary>
/// 描述 LLM 供應商可使用的原生功能，讓共用服務層不必依賴 provider-specific SDK。
/// </summary>
public sealed class LLMProviderCapabilities
{
    /// <summary>是否支援由服務端原生驗證結構化輸出 Schema。</summary>
    public bool SupportsNativeStructuredOutput { get; set; }

    /// <summary>是否支援串流輸出。</summary>
    public bool SupportsStreaming { get; set; }

    /// <summary>是否會回傳可用的 Token 使用量 metadata。</summary>
    public bool SupportsUsageMetadata { get; set; }

    /// <summary>是否支援原生工具呼叫 (Function Calling)。</summary>
    public bool SupportsFunctionCalling { get; set; }
}
