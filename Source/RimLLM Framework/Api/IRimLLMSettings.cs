using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework;

/// <summary>
/// 定義 RimLLM Framework 的設定檔介面。
/// 藉由介面隔離，解除核心邏輯與 RimWorld ModSettings 本體的直接耦合。
/// </summary>
public interface IRimLLMSettings
{
    /// <summary>
    /// API 供應商的 Fallback Chain 順序。
    /// </summary>
    List<string> FallbackChain { get; }

    /// <summary>
    /// API 逾時時間 (秒)。
    /// </summary>
    float ApiTimeout { get; }

    /// <summary>
    /// 是否啟用詳細日誌輸出。
    /// </summary>
    bool DetailedLogging { get; }

    /// <summary>
    /// 全域預設思考強度。請求未指定時由 Manager 套用。null 代表 Auto。
    /// </summary>
    ReasoningEffort? DefaultReasoningEffort { get; }

    /// <summary>
    /// 單一模型最多重試次數。
    /// </summary>
    int MaxRetries { get; }

    /// <summary>
    /// 重試間隔 (秒)。
    /// </summary>
    float RetryDelay { get; }

    /// <summary>
    /// 獲取指定供應商的 API 金鑰。
    /// </summary>
    string GetApiKey(string providerId);

    /// <summary>
    /// 獲取指定供應商當前輪詢啟用的單一 API 金鑰。
    /// </summary>
    string GetActiveApiKey(string providerId);

    /// <summary>
    /// 獲取指定供應商的 API 端點。
    /// </summary>
    string GetEndpoint(string providerId, string defaultVal);

    /// <summary>
    /// 檢查指定供應商是否啟用。
    /// </summary>
    bool IsProviderEnabled(string providerId);

    /// <summary>
    /// 獲取指定供應商的可用模型清單。
    /// </summary>
    List<string> GetModelList(string providerId);

    /// <summary>
    /// 獲取指定供應商的預設模型名稱。
    /// </summary>
    string GetDefaultModel(string providerId, string defaultVal);

    /// <summary>
    /// 設定指定供應商的可用模型清單。
    /// </summary>
    void SetModelList(string providerId, List<string> models);

    /// <summary>
    /// 查詢使用者設定的模型分級覆寫 (1=低, 2=中, 3=高)。
    /// 回傳 0 代表未設定，將改用內建關鍵字啟發式判斷。
    /// </summary>
    int GetModelLevelOverride(string modelName);

    /// <summary>
    /// 最大並行限制。
    /// </summary>
    int MaxConcurrentRequests { get; }

    /// <summary>
    /// 累計輸入 Token 數。
    /// </summary>
    long TotalPromptTokens { get; set; }

    /// <summary>
    /// 累計輸出 Token 數。
    /// </summary>
    long TotalCompletionTokens { get; set; }

    /// <summary>
    /// 估計累計花費 (USD)。
    /// </summary>
    float TotalEstimatedCost { get; set; }

    /// <summary>
    /// 舊版今日預算上限 (USD)。保留此成員以維持既有外部 Mod 的二進位相容（移除會讓讀取它的呼叫端擲 MissingMethodException）。
    /// 內建設定已改以 token 計（見 <see cref="IDailyTokenBudget"/>），此值恆為 0；
    /// 只有未實作 <see cref="IDailyTokenBudget"/> 的外部設定才會以它搭配 <see cref="DailyAccumulatedCost"/> 作為門檻。
    /// </summary>
    float DailyBudgetLimit { get; set; }

    /// <summary>
    /// 預算超限應對策略 (0=HardBlock 直接阻斷並警告, 1=WarnOnly 只警告並照常送出)。
    /// </summary>
    int BudgetPolicy { get; set; }

    /// <summary>
    /// 是否啟用防爆限制。
    /// </summary>
    bool EnableAntiAbuse { get; set; }

    /// <summary>
    /// 時間窗口內最大請求次數。
    /// </summary>
    int MaxRequestsPerWindow { get; set; }

    /// <summary>
    /// 防護監測時間窗口 (秒)。
    /// </summary>
    int ThrottlingWindowSeconds { get; set; }

    /// <summary>
    /// 超限後強制冷卻時間 (秒)。
    /// </summary>
    int CoolDownDurationSeconds { get; set; }

    /// <summary>
    /// 舊版今日累計估計消耗 (USD)。每日預算已改以 token 計（見 <see cref="IDailyTokenBudget"/>），
    /// 此值僅保留作費用顯示，不再作為門檻。
    /// </summary>
    float DailyAccumulatedCost { get; set; }

    /// <summary>
    /// 日預算重置日期的日期字串。
    /// </summary>
    string DailyBudgetResetDate { get; set; }

    /// <summary>
    /// 智慧路由與負載均衡策略 (0=PriorityFailover, 1=MinLatency, 2=RoundRobin, 3=LowestCost)。
    /// </summary>
    int RoutingStrategy { get; set; }

    /// <summary>
    /// 是否啟用原生 JSON Schema 強制執行。
    /// </summary>
    bool EnableNativeSchema { get; set; }

    /// <summary>
    /// 是否啟用 JSON 回傳格式修復輔助。
    /// </summary>
    bool EnableJsonRepair { get; set; }

    /// <summary>
    /// Embedding 供應商名稱。
    /// </summary>
    string EmbeddingProvider { get; set; }

    /// <summary>
    /// Embedding 模型名稱。
    /// </summary>
    string EmbeddingModel { get; set; }

    /// <summary>
    /// Embedding 自訂端點。
    /// </summary>
    string EmbeddingEndpoint { get; set; }

    /// <summary>
    /// Embedding 自訂 API 金鑰。
    /// </summary>
    string EmbeddingApiKey { get; set; }

    string DecisionProvider { get; set; }
    string DecisionModel { get; set; }
    string DecisionEndpoint { get; set; }
    string DecisionApiKey { get; set; }

    /// <summary>
    /// 獲取指定決策供應商保存的 API 金鑰。
    /// 供應商的端點與金鑰在框架設定中是分開儲存的，第三方 Mod 指定供應商做決策時
    /// 必須讀得到該供應商自己的金鑰，而不是啟用中那一家的。
    /// </summary>
    /// <remarks>
    /// 預設實作只在 <paramref name="providerId"/> 指向啟用中的供應商時才回傳啟用中供應商的值，
    /// 其他供應商一律回空字串：既有的外部設定實作者只維護「啟用中供應商」一份資料，
    /// 補上預設實作才不會讓它們在執行期炸掉；但若把啟用中供應商的金鑰錯當成別家供應商的，
    /// 請求會打到錯誤的主機、用量也會記到錯誤的供應商帳上，因此寧可回空字串讓呼叫被拒絕。
    /// 框架內建的 <c>RimLLMFrameworkSettings</c> 覆寫了這三個方法，逐供應商各自取值。
    /// </remarks>
    string GetDecisionApiKey(string providerId) =>
        string.Equals(providerId, DecisionProvider, StringComparison.Ordinal) ? DecisionApiKey : "";

    /// <summary>
    /// 獲取指定決策供應商的模型名稱（未設定時由供應商目錄提供預設值）。
    /// </summary>
    /// <inheritdoc cref="GetDecisionApiKey" path="/remarks"/>
    string GetDecisionModel(string providerId) =>
        string.Equals(providerId, DecisionProvider, StringComparison.Ordinal) ? DecisionModel : "";

    /// <summary>
    /// 獲取指定決策供應商的端點（未設定時由供應商目錄提供預設值）。
    /// </summary>
    /// <inheritdoc cref="GetDecisionApiKey" path="/remarks"/>
    string GetDecisionEndpoint(string providerId) =>
        string.Equals(providerId, DecisionProvider, StringComparison.Ordinal) ? DecisionEndpoint : "";

    /// <summary>
    /// 歷史請求日誌記錄清單。
    /// </summary>
    List<Manager.RimLLMManager.RequestLogEntry> RequestLogs { get; set; }

    /// <summary>
    /// 標記遙測資料有未寫入的變更。
    /// </summary>
    void MarkTelemetryDirty();

    /// <summary>
    /// 儲存遙測資料。
    /// </summary>
    void SaveTelemetry();

    /// <summary>
    /// 將設定寫入/持久化。
    /// </summary>
    void Write();
}
