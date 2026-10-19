namespace RimLLM_Framework;

/// <summary>
/// API 連線測試回傳結果。
/// </summary>
public class TestResult
{
    /// <summary>
    /// 是否測試成功。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 實際連線的供應商 ID。
    /// </summary>
    public string Provider { get; set; }

    /// <summary>
    /// 回應此測試的模型名稱。
    /// </summary>
    public string Model { get; set; }

    /// <summary>
    /// 連線往返延遲時間 (單位：毫秒)。
    /// </summary>
    public long LatencyMs { get; set; }

    /// <summary>
    /// 錯誤訊息 (如果測試失敗)。
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// 對應的統一錯誤碼。
    /// </summary>
    public LLMError ErrorCode { get; set; } = LLMError.None;
}
