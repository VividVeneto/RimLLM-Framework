namespace RimLLM_Framework;

/// <summary>
/// 每日 token 預算帳本。刻意不併入公開的 <see cref="IRimLLMSettings"/>：
/// 替公開介面加成員，會讓依舊版編譯、自行實作該介面的外部 Mod 在載入時擲出 TypeLoadException。
/// 用量追蹤與備援管線優先使用此介面；遇到未實作的外來設定時退回舊版美元門檻。
/// </summary>
internal interface IDailyTokenBudget
{
    /// <summary>
    /// 每日 token 預算上限（輸入＋輸出合計）。0 代表無限制。
    /// </summary>
    long DailyTokenBudgetLimit { get; set; }

    /// <summary>
    /// 當日已累計 token（輸入＋輸出合計，含查無費率與免費模型的請求）。
    /// </summary>
    long DailyAccumulatedTokens { get; set; }
}
