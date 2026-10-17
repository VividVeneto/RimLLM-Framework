namespace RimLLM_Framework.Manager;

#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，與其餘公開型別命名一致
/// <summary>
/// 單筆 embedding 的計算結果：向量本體、供應商回報的輸入 token 數，
/// 以及實際算出這筆向量的供應商與模型。
///
/// <see cref="InputTokenCount"/> 為 <c>null</c> 代表供應商沒有回報用量，
/// 這與「0 token」是兩回事 —— 後者會讓呼叫端誤以為這次呼叫不耗配額。
/// 目前經由 OpenAI 相容端點回報；供應商未回報時為 <c>null</c>。
///
/// <see cref="ModelId"/> 與 <see cref="ProviderId"/> 由服務在請求開始時捕捉
/// （而非呼叫端在 await 之後重新讀設定），確保請求期間切換設定不會讓向量被標成
/// 新模型、或把用量記到新供應商。
/// </summary>
public class RimLLMEmbeddingResult
{
    public float[] Vector { get; }

    public long? InputTokenCount { get; }

    /// <summary>實際計算這筆向量的模型名稱。</summary>
    public string ModelId { get; }

    /// <summary>實際計算這筆向量的供應商識別。</summary>
    public string ProviderId { get; }

    public RimLLMEmbeddingResult(float[] vector, long? inputTokenCount)
        : this(vector, inputTokenCount, null, null)
    {
    }

    public RimLLMEmbeddingResult(float[] vector, long? inputTokenCount, string modelId, string providerId)
    {
        Vector = vector;
        InputTokenCount = inputTokenCount;
        ModelId = modelId;
        ProviderId = providerId;
    }
}
#pragma warning restore S101
