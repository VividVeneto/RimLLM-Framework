namespace RimLLM_Framework;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// RimLLM 統一錯誤碼列舉。
/// </summary>
public enum LLMError
{
    /// <summary>
    /// 無錯誤。
    /// </summary>
    None,

    /// <summary>
    /// 連線超時。
    /// </summary>
    Timeout,

    /// <summary>
    /// 觸發 API 頻率限制 (Rate Limit)。
    /// </summary>
    RateLimit,

    /// <summary>
    /// API Key 無效或已過期。
    /// </summary>
    InvalidKey,

    /// <summary>
    /// 供應商目前離線或無法連線。
    /// </summary>
    ProviderOffline,

    /// <summary>
    /// API 回傳無效或損毀的內容，無法正確解析。
    /// </summary>
    InvalidResponse,

    /// <summary>
    /// 本地網路異常或主機無網際網路連線。
    /// </summary>
    NetworkError,

    /// <summary>
    /// 找不到指定的模型名稱。
    /// </summary>
    ModelNotFound,

    /// <summary>
    /// 因安全策略或內容過濾遭到拒絕。
    /// </summary>
    ContentFilter,

    /// <summary>
    /// API 帳戶餘額或配額已耗盡。
    /// </summary>
    QuotaExceeded,

    /// <summary>
    /// 操作被使用者或系統取消。
    /// </summary>
    Cancelled,

    /// <summary>
    /// 未知錯誤。
    /// </summary>
    Unknown,

    /// <summary>
    /// 提示詞長度超出模型的上下文視窗上限。
    /// 與 <see cref="InvalidResponse"/> 分開是因為處置方式不同：
    /// 這類錯誤要縮短提示詞或改用視窗更大的模型，重打同一份 payload 沒有意義。
    /// 列舉值必須追加在最後，插在中間會改動既有成員的數值而破壞已編譯的下游 Mod。
    /// </summary>
    ContextWindowExceeded
}
#pragma warning restore S101, S2342
