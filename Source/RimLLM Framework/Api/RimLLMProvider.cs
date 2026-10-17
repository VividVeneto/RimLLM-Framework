using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// RimLLM SDK 全域靜態入口。第三方 Mod 以 CreateChatClient /
/// CreateEmbeddingGenerator 取得標準 MEAI client 使用框架，無須事先註冊。
/// </summary>
public static class RimLLMProvider
{
    private static RimLLMManager _manager;

    /// <summary>內部存取目前 manager 執行個體（同 assembly 使用）。</summary>
    internal static RimLLMManager Manager
    {
        get
        {
            if (_manager == null)
            {
                throw new InvalidOperationException("[RimLLM] SDK has not been initialized. Please make sure the RimLLM Framework mod is active.");
            }
            return _manager;
        }
    }

    /// <summary>
    /// 嘗試取得 manager。SDK 尚未初始化時回傳 false 而不擲出例外，
    /// 供設定 UI 在框架未載入時安全地略過相依於 manager 的區塊。
    /// </summary>
    internal static bool TryGetManager(out RimLLMManager manager)
    {
        manager = _manager;
        return manager != null;
    }

    internal static void Initialize(RimLLMManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// 建立標準 MEAI <see cref="IChatClient"/>。modId 為呼叫端自取的識別字串，
    /// 用於防濫用節流與遙測歸屬，不需事先註冊。
    /// </summary>
    public static IChatClient CreateChatClient(string modId)
    {
        return Manager.CreateChatClient(modId);
    }

    /// <summary>
    /// 建立標準 MEAI embedding generator。modId 同 <see cref="CreateChatClient"/>。
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string modId)
    {
        return Manager.CreateEmbeddingGenerator(modId);
    }

    /// <summary>註冊外部 LLM 供應商的靜態便捷入口。</summary>
    public static void RegisterProvider(Providers.ILLMProvider provider)
    {
        Manager.RegisterProvider(provider);
    }

    /// <summary>測試指定供應商的連線狀態。</summary>
    public static Task<TestResult> TestProviderAsync(string providerId)
    {
        return Manager.TestProviderAsync(providerId);
    }

    /// <summary>從指定供應商拉取可用模型清單。</summary>
    public static Task<List<string>> FetchProviderModelsAsync(string providerId)
    {
        return Manager.FetchProviderModelsAsync(providerId);
    }

    /// <summary>取得所有已註冊供應商的識別碼（依註冊順序）。</summary>
    public static List<string> GetRegisteredProviderIds()
    {
        return Manager.GetRegisteredProviderIds();
    }

    /// <summary>
    /// 取得使用者設定的備援鏈（格式 "ProviderId:ModelName"，依設定順序）。
    /// 回傳的是副本，修改它不會影響框架設定。
    /// </summary>
    public static List<string> GetFallbackChain()
    {
        var chain = Manager.Settings.FallbackChain;
        return chain != null ? new List<string>(chain) : [];
    }

    /// <summary>
    /// 取得指定模型的上下文上限（token 數）。玩家在備援鏈手動填寫的值優先，其次為玩家重新整理
    /// 模型清單時記下的值：供應商 API 有回報就用它（OpenRouter、Groq、Gemini），否則取自 models.dev 資料庫。
    /// </summary>
    /// <param name="modelId">格式 "ProviderId:ModelName"，同 ChatOptions.ModelId。</param>
    /// <returns>上限不明、或 <paramref name="modelId"/> 不是明確的 "ProviderId:ModelName" 時回傳 null。</returns>
    public static int? GetContextWindow(string modelId)
    {
        return Manager.GetContextWindow(modelId);
    }

    /// <summary>
    /// 依目前設定解析出一個請求可能落到的所有候選，回傳它們能力的交集——
    /// 不論 fallback 最後由誰回答，這裡為 true 的能力都保證成立。
    /// Agent 端在送出帶 Tools 的請求前可據此判斷工具是否會被送達。
    /// </summary>
    /// <param name="preferredModelId">同 ChatOptions.ModelId，格式 "ProviderId:ModelName"，可為 null。</param>
    /// <exception cref="RimLLMException">備援鏈未設定或沒有任何可用候選（<see cref="LLMError.ProviderOffline"/>）。</exception>
    public static LLMProviderCapabilities GetEffectiveCapabilities(string preferredModelId = null)
    {
        return Manager.GetEffectiveCapabilities(preferredModelId);
    }

    /// <summary>
    /// 非拋版能力查詢，供相容層高頻輪詢使用，避免離線時以例外控制流程。
    /// 語意與 <see cref="GetEffectiveCapabilities"/> 一致，僅以傳回值取代擲出。
    /// </summary>
    internal static bool TryGetEffectiveCapabilities(string preferredModelId, out LLMProviderCapabilities capabilities, out string failureReason)
    {
        try
        {
            return Manager.TryGetEffectiveCapabilities(preferredModelId, out capabilities, out failureReason);
        }
        catch (Exception ex)
        {
            // Manager 尚未初始化等啟動順序異常視為不接管，呼叫端以警告節流記錄。
            capabilities = null;
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 非拋版能力查詢（預設模型），供相容層高頻輪詢使用。
    /// </summary>
    internal static bool TryGetEffectiveCapabilities(out LLMProviderCapabilities capabilities, out string failureReason)
    {
        return TryGetEffectiveCapabilities(null, out capabilities, out failureReason);
    }
}
#pragma warning restore S101, S2342
