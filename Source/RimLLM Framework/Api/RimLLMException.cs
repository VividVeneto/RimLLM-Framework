using System;

namespace RimLLM_Framework;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// RimLLM SDK 拋出的專屬例外。
/// 包含統一錯誤碼，利於呼叫端 Mod 進行錯誤處理與 Fallback。
/// </summary>
public class RimLLMException : Exception
{
    /// <summary>
    /// 統一錯誤碼。
    /// </summary>
    public LLMError Error { get; }

    /// <summary>
    /// 伺服器透過 Retry-After Header 建議的重試等待時間（若有提供）。
    /// 重試邏輯會以此值與使用者設定的重試延遲取較大者。
    /// </summary>
    public TimeSpan? RetryAfter { get; set; }

    /// <summary>
    /// 原始 HTTP 狀態碼（錯誤來自 HTTP 回應時才有）。同一個 <see cref="LLMError"/> 可能對應不同狀態碼，
    /// 重試判定需要區分：402 與 429 都會映成 QuotaExceeded，只有前者是真的餘額用完。
    /// </summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>
    /// 標記此錯誤是否為「服務端拒絕原生 JSON Schema」。
    /// 只有明確標記為 true 的錯誤才會觸發框架的非原生 Schema 降級重打，
    /// 避免一般的 InvalidResponse 失敗被誤判而掩蓋真正的錯誤。
    /// </summary>
    public bool IsSchemaRejection { get; set; }

    /// <summary>
    /// 標記此錯誤是否為「服務端不接受思考相關參數」（reasoning_effort、thinking、enable_thinking 等）。
    /// 各家對同一參數的支援範圍差到模型層級，光靠模型名無法窮舉，
    /// 因此改由服務端的拒絕來判定，並讓框架去掉該參數重打一次。
    /// </summary>
    public bool IsReasoningRejection { get; set; }

    /// <summary>
    /// 標記此錯誤是否為「服務端不接受 temperature」。
    /// 推理模型多半禁用取樣參數（OpenAI gpt-5 系列在 effort 非 none 時直接回 400）。
    /// </summary>
    public bool IsTemperatureRejection { get; set; }

    public RimLLMException(LLMError error, string message) : base(message)
    {
        Error = error;
    }

    public RimLLMException(LLMError error, string message, Exception innerException) : base(message, innerException)
    {
        Error = error;
    }
}
#pragma warning restore S101, S2342
