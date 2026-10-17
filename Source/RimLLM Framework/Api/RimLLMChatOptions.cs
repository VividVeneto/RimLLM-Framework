using System;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// RimLLM 框架專屬選項（繼承 MEAI ChatOptions）。
/// 純 MEAI 使用者不需知道此型別；不設定時全部使用框架預設值。
///
/// 本類別的每個屬性都只是 <see cref="ChatOptions.AdditionalProperties"/> 的糖衣讀寫，
/// 自身不持有任何欄位。框架內部一律透過 AdditionalProperties 取值，因此
/// 呼叫端用 <see cref="RimLLMChatOptions"/> 或直接對純 ChatOptions 塞鍵，效果完全相同；
/// 也因為沒有自有欄位，MEAI 的 base.Clone() 就能完整複製，不需要覆寫 Clone。
/// </summary>
public class RimLLMChatOptions : ChatOptions
{
    internal const string DisableReasoningKey = "rimllm_disable_reasoning";
    internal const string ExecutorManagedKey = "rimllm_executor_managed";
    internal const string PriorityKey = "rimllm_priority";
    internal const string MinFallbackLevelKey = "rimllm_min_fallback_level";
    internal const string CachedContextKey = "rimllm_cached_context";
    internal const string EnableContextCachingKey = "rimllm_enable_context_caching";

    /// <summary>
    /// 結構化輸出的目標型別。由 RimLLMClientExtensions 標註，路由層據此依供應商方言
    /// 產生原生 schema——放在 AdditionalProperties 是為了讓結構化輸出能走完整的
    /// IChatClient 鏈，而不是繞過中介層直接呼叫 facade。
    /// </summary>
    internal const string ResponseTypeKey = "rimllm_response_type";

    /// <summary>請求優先權。數值越高，在全域請求佇列中越先執行。</summary>
    public int Priority
    {
        get => GetPriority(this);
        set => WriteAdditional(PriorityKey, value);
    }

    /// <summary>最低相容模型等級（High/Medium/Low 或 3/2/1），供 Fallback 決定降級下限。</summary>
    public string MinFallbackLevel
    {
        get => GetMinFallbackLevel(this);
        set => WriteAdditional(MinFallbackLevelKey, value);
    }

    /// <summary>可重複使用的大型穩定上下文（如世界觀規則、輸出 Schema）。啟用 Context Caching 時 provider 優先快取此內容。</summary>
    public string CachedContext
    {
        get => GetCachedContext(this);
        set => WriteAdditional(CachedContextKey, value);
    }

    /// <summary>是否啟用長上下文快取（Context Caching）。當 CachedContext 不為空時預設為 true。</summary>
    public bool EnableContextCaching
    {
        get => GetEnableContextCaching(this);
        set => WriteAdditional(EnableContextCachingKey, value);
    }

    /// <summary>關閉思考（對應舊 LLMReasoningEffort.None；Auto 以不設定 Reasoning 表達）。</summary>
    public bool DisableReasoning
    {
        get => GetDisableReasoning(this);
        set => WriteAdditional(DisableReasoningKey, value);
    }

    /// <summary>
    /// 備援鏈的模型別名。呼叫端把 <see cref="ChatOptions.ModelId"/> 設為此值（大小寫不限），
    /// 等同不指定模型：走玩家設定的整條備援鏈，不釘選任何候選。
    /// 對外而言備援鏈就是一個叫 fallback 的模型；回應的 ModelId 仍是實際回答者的
    /// "Provider:Model"，可據此得知最後是誰回答的。
    /// </summary>
    public const string FallbackModelId = "fallback";

    /// <summary>是否為備援鏈別名（大小寫不限）。路由層據此跳過釘選，直接走整條鏈。</summary>
    public static bool IsFallbackModelId(string modelId)
    {
        return string.Equals(modelId, FallbackModelId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>寫入框架私有欄位，必要時延遲建立 AdditionalProperties。</summary>
    private void WriteAdditional(string key, object value)
    {
        if (AdditionalProperties == null)
        {
            AdditionalProperties = [];
        }
        AdditionalProperties[key] = value;
    }

    /// <summary>
    /// 從 <see cref="ChatOptions.AdditionalProperties"/> 取出框架私有欄位。
    /// 框架把這些欄位以 AdditionalProperties 轉遞給 provider（呼叫端未使用
    /// <see cref="RimLLMChatOptions"/> 時的唯一管道），而「不存在或型別不符就用預設值」
    /// 的三段式判斷先前散落在 executor 與各 provider，逐字重複了六次。
    /// </summary>
    internal static T ReadAdditional<T>(ChatOptions options, string key, T fallback)
    {
        return options?.AdditionalProperties != null &&
               options.AdditionalProperties.TryGetValue(key, out object value) &&
               value is T typed
            ? typed
            : fallback;
    }

    // 以下讀取器接受任意 ChatOptions，讓框架內部不必再對 RimLLMChatOptions 做型別轉換
    // ——下游若直接對純 ChatOptions 塞鍵，走的是同一條路徑。

    internal static int GetPriority(ChatOptions options) =>
        ReadAdditional(options, PriorityKey, 0);

    internal static string GetMinFallbackLevel(ChatOptions options) =>
        ReadAdditional<string>(options, MinFallbackLevelKey, null);

    internal static string GetCachedContext(ChatOptions options) =>
        ReadAdditional<string>(options, CachedContextKey, null);

    internal static bool GetDisableReasoning(ChatOptions options) =>
        ReadAdditional(options, DisableReasoningKey, false);

    internal static System.Type GetResponseType(ChatOptions options) =>
        ReadAdditional<System.Type>(options, ResponseTypeKey, null);

    /// <summary>
    /// 三態語意：鍵不存在時回退成「CachedContext 非空即啟用」，
    /// 因此不能用 ReadAdditional 的單一預設值表達。
    /// </summary>
    internal static bool GetEnableContextCaching(ChatOptions options)
    {
        if (options?.AdditionalProperties != null &&
            options.AdditionalProperties.TryGetValue(EnableContextCachingKey, out object value) &&
            value is bool explicitValue)
        {
            return explicitValue;
        }
        return !string.IsNullOrEmpty(GetCachedContext(options));
    }
}
#pragma warning restore S101, S2342
