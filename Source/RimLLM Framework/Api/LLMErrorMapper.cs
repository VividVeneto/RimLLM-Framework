using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace RimLLM_Framework;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// HTTP 狀態碼 → <see cref="LLMError"/> 的單一對照來源。
/// 官方 SDK 路徑（ClientResultException）、raw HTTP 路徑與 embedding 路徑共用同一份語意，
/// 避免相同的狀態碼判斷散落多處而逐漸走樣。
/// </summary>
public static class LLMErrorMapper
{
    /// <summary>
    /// 解析 Retry-After 標頭值。RFC 7231 允許「延遲秒數」與「HTTP 日期」兩種格式，
    /// 交由 <see cref="RetryConditionHeaderValue"/> 一併處理，避免各路徑各自實作而漏掉日期格式。
    /// 無法解析或已過期時回傳 null。
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        return RetryConditionHeaderValue.TryParse(headerValue, out RetryConditionHeaderValue parsed)
            ? ToDelay(parsed)
            : null;
    }

    /// <summary>把 Retry-After 的兩種表示法統一換算成剩餘等待時間；非正值視為無建議。</summary>
    private static TimeSpan? ToDelay(RetryConditionHeaderValue retryAfter)
    {
        if (retryAfter == null) return null;

        if (retryAfter.Delta.HasValue)
        {
            return retryAfter.Delta.Value > TimeSpan.Zero ? retryAfter.Delta : null;
        }
        if (retryAfter.Date.HasValue)
        {
            TimeSpan delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? (TimeSpan?)delta : null;
        }
        return null;
    }

    /// <summary>
    /// 依 HTTP 狀態碼建立對應的 <see cref="RimLLMException"/>。
    /// </summary>
    /// <param name="statusCode">HTTP 狀態碼；無法取得時傳入 null，會對應為 <see cref="LLMError.Unknown"/>。</param>
    /// <param name="friendlyMessage">已淨化、可直接顯示給使用者的錯誤訊息。</param>
    /// <param name="retryAfter">伺服器透過 Retry-After 建議的等待時間（若有）。</param>
    /// <param name="innerException">原始例外（若有）。</param>
    /// <param name="detectionText">
    /// 用於關鍵字偵測（配額不足、Schema 遭拒）的文字。
    /// 省略時沿用 <paramref name="friendlyMessage"/>；當原始回應比淨化後的訊息更完整時可另外傳入。
    /// </param>
    public static RimLLMException CreateException(
        int? statusCode,
        string friendlyMessage,
        TimeSpan? retryAfter = null,
        Exception innerException = null,
        string detectionText = null)
    {
        RimLLMException exception = MapStatusCode(statusCode, friendlyMessage, retryAfter, innerException, detectionText);
        exception.HttpStatusCode = statusCode;
        return exception;
    }

    private static RimLLMException MapStatusCode(
        int? statusCode,
        string friendlyMessage,
        TimeSpan? retryAfter,
        Exception innerException,
        string detectionText)
    {
        string probe = detectionText ?? friendlyMessage;

        switch (statusCode)
        {
            case 401:
            case 403:
                return Create(LLMError.InvalidKey,
                    $"Invalid API key or authorization failed: {friendlyMessage}", null, innerException);

            case 402:
                return Create(LLMError.QuotaExceeded,
                    $"Payment required, please check your account balance: {friendlyMessage}", null, innerException);

            case 404:
                // 模型或端點不存在屬於不可重試錯誤，重試只會空耗延遲。
                return Create(LLMError.ModelNotFound,
                    $"Model or endpoint not found: {friendlyMessage}", null, innerException);

            case 408:
                return Create(LLMError.Timeout,
                    $"Request timed out on the server side: {friendlyMessage}", retryAfter, innerException);

            case 400:
            case 413:
            case 422:
                // 請求本身有問題，以同一份 payload 重試必然再次失敗。
                RimLLMException rejected = Create(ResolveRejectionError(statusCode, probe),
                    $"The request was rejected by the provider: {friendlyMessage}", null, innerException);
                rejected.IsSchemaRejection = LooksLikeSchemaRejection(probe);
                rejected.IsReasoningRejection = LooksLikeReasoningRejection(probe);
                rejected.IsTemperatureRejection = LooksLikeTemperatureRejection(probe);
                return rejected;

            case 429:
                return ContainsIgnoreCase(probe, "quota") || ContainsIgnoreCase(probe, "insufficient")
                    ? Create(LLMError.QuotaExceeded,
                        "API insufficient quota (insufficient_quota), please check your account balance.", retryAfter, innerException)
                    : Create(LLMError.RateLimit,
                        $"Rate limit triggered: {friendlyMessage}", retryAfter, innerException);
        }

        if (statusCode.HasValue && statusCode.Value >= 500)
        {
            return Create(LLMError.ProviderOffline,
                $"Internal server error: {friendlyMessage}", retryAfter, innerException);
        }

        return Create(LLMError.Unknown, $"API request failed: {friendlyMessage}", null, innerException);
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受原生 JSON Schema」，
    /// 供框架決定是否降級為提示式 JSON 重打一次。
    /// </summary>
    public static bool LooksLikeSchemaRejection(string message)
    {
        // "json_schema" 是 "schema" 的子字串，不需要另外列。
        return ContainsIgnoreCase(message, "response_format") ||
               ContainsIgnoreCase(message, "schema");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受思考相關參數」。
    /// 各家的欄位名不同（reasoning_effort、reasoning、thinking、enable_thinking、thinking_budget），
    /// 而且支援範圍細到模型層級，因此以服務端回報的欄位名判定，再由框架去掉參數重打一次。
    /// </summary>
    public static bool LooksLikeReasoningRejection(string message)
    {
        // reasoning_effort / enable_thinking / thinking_budget 都含有下列兩個子字串之一，
        // 逐一列出只是重複比對，不會多命中任何訊息。
        return ContainsIgnoreCase(message, "reasoning") ||
               ContainsIgnoreCase(message, "thinking");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受 temperature」。
    /// 推理模型多半禁用取樣參數，OpenAI gpt-5 系列會直接回 400 而非忽略。
    /// </summary>
    public static bool LooksLikeTemperatureRejection(string message)
    {
        return ContainsIgnoreCase(message, "temperature");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「提示詞超出模型的上下文視窗」。
    /// 各家用詞不同，但都會提到上下文長度或 token 數量的上限。
    /// 只列具體到不會誤傷的字串；例如單獨的 "too long" 太寬鬆，不納入。
    /// </summary>
    public static bool LooksLikeContextWindowRejection(string message)
    {
        // context_length_exceeded / context_window_exceeded 都含有 "context"，
        // 但單獨的 "context" 會誤傷 context caching 相關訊息，因此逐項列出。
        return ContainsIgnoreCase(message, "context_length") ||
               ContainsIgnoreCase(message, "context length") ||
               ContainsIgnoreCase(message, "context_window") ||
               ContainsIgnoreCase(message, "context window") ||
               ContainsIgnoreCase(message, "maximum context") ||
               ContainsIgnoreCase(message, "too many tokens") ||
               ContainsIgnoreCase(message, "prompt is too long");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「內容被安全策略或內容過濾擋下」。
    /// OpenAI 協定家族一律以 400 回報，若不分辨就會和「請求組壞了」混為一談，
    /// 呼叫端無從得知該換一家還是該修請求。
    /// </summary>
    public static bool LooksLikeContentPolicyRejection(string message)
    {
        return ContainsIgnoreCase(message, "content_policy") ||
               ContainsIgnoreCase(message, "content policy") ||
               ContainsIgnoreCase(message, "content_filter") ||
               ContainsIgnoreCase(message, "content filter") ||
               ContainsIgnoreCase(message, "safety") ||
               ContainsIgnoreCase(message, "moderation");
    }

    /// <summary>
    /// 400／413／422 的細分。
    /// Schema、reasoning 與 temperature 遭拒各自有旗標與「去掉參數重打一次」的流程，
    /// 錯誤碼維持 <see cref="LLMError.InvalidResponse"/> 以免影響既有的降級路徑；
    /// 其餘才依 413 的定義與訊息關鍵字判定為上下文超長或內容過濾。
    /// </summary>
    private static LLMError ResolveRejectionError(int? statusCode, string probe)
    {
        if (LooksLikeSchemaRejection(probe) ||
            LooksLikeReasoningRejection(probe) ||
            LooksLikeTemperatureRejection(probe))
        {
            return LLMError.InvalidResponse;
        }

        // 413 Payload Too Large 依定義就是酬載過大，不必再比對訊息。
        if (statusCode == 413 || LooksLikeContextWindowRejection(probe))
        {
            return LLMError.ContextWindowExceeded;
        }

        return LooksLikeContentPolicyRejection(probe) ? LLMError.ContentFilter : LLMError.InvalidResponse;
    }

    public static bool ContainsIgnoreCase(string haystack, string needle)
    {
        return !string.IsNullOrEmpty(haystack) &&
               haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static RimLLMException Create(LLMError error, string message, TimeSpan? retryAfter, Exception innerException)
    {
        RimLLMException exception = innerException == null
            ? new RimLLMException(error, message)
            : new RimLLMException(error, message, innerException);
        exception.RetryAfter = retryAfter;
        return exception;
    }
}
#pragma warning restore S101, S2342
