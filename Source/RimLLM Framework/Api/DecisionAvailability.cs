namespace RimLLM_Framework;

/// <summary>
/// 目前決策供應商的可用狀態快照。
///
/// 這是給第三方 Mod 判斷「玩家到底有沒有設定決策模型」用的純查詢結果：
/// 框架未初始化時不應該讓呼叫端去猜，而框架設定的實際內容（金鑰）也絕不外洩——
/// DTO 只回報「有沒有金鑰」，不回報金鑰本身。
/// </summary>
public sealed class DecisionAvailability
{
    /// <summary>
    /// 是否啟用了決策供應商（<c>DecisionProvider != Disabled</c>）。
    /// 為 false 時其餘欄位一律是空值，代表玩家還沒在 Decision 分頁選供應商。
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 目前啟用的供應商 id；未啟用時為 <c>Disabled</c>。
    /// </summary>
    public string ProviderId { get; init; } = "";

    /// <summary>
    /// 解析後的模型名稱（玩家設定值，沒設定時用供應商目錄的預設值）；未啟用時為空字串。
    /// </summary>
    public string Model { get; init; } = "";

    /// <summary>
    /// 該供應商是否已保存金鑰。為 false 但 <see cref="Enabled"/> 為 true 時，
    /// 代表玩家選了供應商卻還沒填金鑰——呼叫端可以據此提示玩家。
    /// </summary>
    public bool HasKey { get; init; }

    /// <summary>
    /// 解析後的端點（已縮成 origin，不含 <c>/v1/systemone</c> 後綴）；未啟用時為空字串。
    /// </summary>
    public string Endpoint { get; init; } = "";
}
