using System.Collections.Generic;

namespace RimLLM_Framework.Manager;

/// <summary>
/// Embedding 供應商的模型清單抓取結果。
///
/// <see cref="Filtered"/> 為 <c>true</c> 代表清單已依供應商回報的能力資訊
/// （或 OpenAI 官方型錄的固定命名）過濾，只含 embedding 模型；
/// 為 <c>false</c> 代表伺服器沒有回報能力資訊，清單只把像 embedding 的名稱排到前面，
/// 仍可能混有對話模型。
/// </summary>
public class RimLLMEmbeddingModelList
{
    public List<string> Models { get; }

    public bool Filtered { get; }

    public RimLLMEmbeddingModelList(List<string> models, bool filtered)
    {
        Models = models ?? [];
        Filtered = filtered;
    }
}
