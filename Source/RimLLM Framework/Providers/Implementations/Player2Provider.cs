using System.Collections.Generic;
using System.Threading.Tasks;

namespace RimLLM_Framework.Providers;

/// <summary>
/// Player2 (https://player2.game/) 供應商，經由 OpenAI 相容的 <c>/chat/completions</c> 端點存取。
/// 預設連向本機 Player2 App（<c>http://127.0.0.1:4315/v1</c>）：玩家只要開著 App 就能用，
/// 實際模型由 App 內的 AI Selection 決定，不需 API 金鑰也不提供 <c>/models</c> 清單，
/// 因此模型清單固定回傳單一佔位模型。雲端模式（<c>https://api.player2.game/v1</c> + p2Key）
/// 可在設定頁把端點改掉並填入金鑰，沿用同一條 OpenAI 相容路徑。
/// 對話、串流、工具呼叫與結構化輸出全由基底 <see cref="OpenAIProvider"/> 經 OpenAI SDK 處理。
/// </summary>
public class Player2Provider : OpenAIProvider
{
    /// <summary>本機 Player2 App 的預設服務根位址，同時是設定頁端點欄位的預設值。</summary>
    public const string LocalDefaultEndpoint = "http://127.0.0.1:4315/v1";

    /// <summary>
    /// 唯一的模型佔位名稱。Player2 實際用哪個模型由 App／網站的 AI Selection 決定，
    /// API 不接受模型選擇也不提供清單，這個名稱只用來滿足 OpenAI SDK 的必填欄位與框架的備援鏈格式。
    /// </summary>
    public const string DefaultModelName = "player2";

    /// <summary>
    /// 是否需要金鑰取決於端點：本機 App 模式免金鑰，雲端模式需 p2Key。
    /// 判斷邏輯與 <see cref="ProviderIds.RequiresApiKey"/> 為同一份，實例只是代入解析後的端點，
    /// 因此路由層與 UI 前置檢查看到的語義永遠一致。
    /// </summary>
    public override bool RequiresApiKey =>
        ProviderIds.RequiresApiKey(ProviderId, Settings.GetEndpoint(ProviderId, LocalDefaultEndpoint));

    public Player2Provider(IRimLLMSettings settings)
        : base(settings, ProviderIds.Player2, LocalDefaultEndpoint, DefaultModelName)
    {
    }

    /// <summary>
    /// Player2 沒有 <c>/models</c> 端點（雲端回 404，本機 App 同樣不提供），
    /// 基底經 OpenAI SDK 抓清單的路徑走不通，直接回傳固定的單一模型。
    /// 覆寫此方法也讓 <c>RimLLMManager</c> 改走靜態清單分支，不再呼叫基底的 catalog 路徑。
    /// </summary>
    public override Task<List<string>> FetchAvailableModelsAsync()
    {
        return Task.FromResult(new List<string> { DefaultModelName });
    }
}
