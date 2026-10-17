namespace RimLLM_Framework;

/// <summary>
/// 上下文上限查詢。刻意不併入公開的 <see cref="IRimLLMSettings"/>：替公開介面加成員，
/// 會讓依舊版編譯、自行實作該介面的外部 Mod 在載入時擲出 TypeLoadException。
/// </summary>
internal interface IContextWindowLookup
{
    /// <summary>
    /// 查詢模型的上下文上限（token 數）：玩家手動填寫的值優先，其次為重新整理模型清單時
    /// 記下的值；都沒有則回傳 null。
    /// </summary>
    int? GetContextWindow(string providerId, string modelName);
}
