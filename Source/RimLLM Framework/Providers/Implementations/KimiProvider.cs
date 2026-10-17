
namespace RimLLM_Framework.Providers;

/// <summary>
/// Kimi (月之暗面) 供應商，完全相容 OpenAI API 格式。
/// </summary>
public class KimiProvider : OpenAIProvider
{
    /// <summary>
    /// Kimi API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
    /// </summary>
    protected override bool SupportsNativeJsonSchemaPayload => false;

    /// <summary>
    /// K2.x 以 thinking.type 開關思考，K3 則吃頂層 reasoning_effort。
    /// 兩者併送即可同時涵蓋，服務端會忽略自己不認得的那一個。
    /// </summary>
    protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.ThinkingSwitch;

    /// <summary>
    /// Kimi 的強度詞彙是 low / high / max，沒有 medium。
    /// 中強度對應到 high，是三個可選值裡最接近的一階。
    /// </summary>
    protected override string MapEffortLiteral(Microsoft.Extensions.AI.ReasoningEffort effort)
    {
        if (effort == Microsoft.Extensions.AI.ReasoningEffort.Low) return "low";
        if (effort == Microsoft.Extensions.AI.ReasoningEffort.Medium) return "high";
        if (effort == Microsoft.Extensions.AI.ReasoningEffort.High) return "max";
        return null;
    }

    public KimiProvider(IRimLLMSettings settings)
        : base(settings, ProviderIds.Kimi, "https://api.moonshot.ai/v1", "moonshot-v1-8k")
    {
    }
}
