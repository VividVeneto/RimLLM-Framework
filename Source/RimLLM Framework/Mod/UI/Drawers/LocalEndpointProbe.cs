using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 本地模型端點探測的共用核心：依序 GET 各候選的測試 URL，回傳第一個成功的項目。
/// 供應商分頁與 Embedding 分頁的按鈕狀態與成功回寫各不相同，因此 UI 狀態仍留在各 drawer，
/// 這裡只收斂「逐一探測」的迴圈與 HttpClient。
/// </summary>
internal static class LocalEndpointProbe
{
    internal sealed class Target
    {
        /// <summary>顯示名稱（探測成功訊息用）。</summary>
        public string Name;
        /// <summary>探測成功後寫入設定的端點。</summary>
        public string BaseUrl;
        /// <summary>實際送出探測請求的位址。與 BaseUrl 分開是因為 Ollama 原生位址（/api/tags）
        /// 與 OpenAI 相容位址（/v1）不同：探測打前者，寫入後者，呼叫端傳入時就填好兩邊。</summary>
        public string TestUrl;
    }

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(600)
    };

    /// <summary>回傳第一個可連通的候選（原樣回傳，不改寫任何欄位），全部失敗則回傳 null。</summary>
    internal static async Task<Target> FindReachableAsync(IEnumerable<Target> targets)
    {
        if (targets == null) return null;
        foreach (Target target in targets)
        {
            try
            {
                var response = await SharedClient.GetAsync(target.TestUrl).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return target;
                }
            }
            catch
            {
                // 探測失敗屬正常情形（服務未啟動），繼續試下一個候選端點。
            }
        }
        return null;
    }
}
