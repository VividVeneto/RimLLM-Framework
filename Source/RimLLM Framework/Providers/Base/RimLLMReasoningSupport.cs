using System.Collections.Generic;

namespace RimLLM_Framework.Providers;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// 思考參數的線上格式（方言）。各家表達「思考強度」的欄位完全不同，
/// 而且同一家不同世代的模型也可能不一樣（例如 Kimi K3 用 reasoning_effort、K2.x 用 thinking.type），
/// 因此由 provider 宣告自己的方言，再由基底類別統一寫進請求。
/// </summary>
public enum ReasoningWireFormat
{
    /// <summary>不送任何思考參數。</summary>
    None,

    /// <summary>頂層 <c>reasoning_effort</c>（OpenAI、xAI、Groq、Ollama 相容端點等）。</summary>
    OpenAIEffort,

    /// <summary>OpenRouter 的統一 <c>reasoning</c> 物件。</summary>
    OpenRouterReasoning,

    /// <summary><c>thinking: {type}</c> 開關，並在開啟時附上 <c>reasoning_effort</c>（DeepSeek、Z.ai、Kimi K2.x）。</summary>
    ThinkingSwitch,

    /// <summary>頂層 <c>enable_thinking</c> 布林加上 <c>thinking_budget</c> token 預算（Qwen / DashScope 相容模式）。</summary>
    EnableThinkingFlag
}

/// <summary>
/// 記錄哪些 (供應商, 模型) 組合被服務端明確拒絕過思考參數或 temperature。
///
/// 為什麼需要這層：各家對思考參數的支援範圍細到模型層級 —— Groq 的 <c>reasoning_effort</c>
/// 只有特定模型吃、xAI 的推理模型根本不能關閉思考、本地相容端點的模型名更是使用者自訂。
/// 靠模型名前綴窮舉必然腐化（框架先前就因此讓 o1/o3 以外的所有模型靜默失去設定）。
/// 改為樂觀送出，收到服務端的 400 才記下來並去掉參數重打一次，
/// 之後同一個模型就不再送 —— 不支援的模型會優雅退化，而不是硬失敗。
///
/// 記憶只存在於本次遊戲執行期間：模型能力可能隨服務端更新而改變，重開遊戲即重新嘗試。
/// </summary>
public static class RimLLMReasoningSupport
{
    private static readonly object Gate = new();
    // 三種「不支援」記在同一集合，鍵以種類前綴區分（r=思考參數、t=temperature、d=關閉思考）。
    private static readonly HashSet<string> Unsupported = [];

    private static string BuildKey(string kind, string providerId, string model)
    {
        return kind + "|" + (providerId ?? string.Empty) + "|" + (model ?? string.Empty);
    }

    /// <summary>記下此模型不接受思考參數。回傳 true 代表這是新資訊（先前沒記錄過）。</summary>
    public static bool MarkReasoningUnsupported(string providerId, string model)
    {
        lock (Gate)
        {
            return Unsupported.Add(BuildKey("r", providerId, model));
        }
    }

    /// <summary>記下此模型不接受 temperature。回傳 true 代表這是新資訊。</summary>
    public static bool MarkTemperatureUnsupported(string providerId, string model)
    {
        lock (Gate)
        {
            return Unsupported.Add(BuildKey("t", providerId, model));
        }
    }

    /// <summary>
    /// 記下此模型不接受「關閉思考」指令。回傳 true 代表這是新資訊。
    /// 與不接受思考參數分開記：o 系列、Groq 等只接受 low/medium/high 的模型會以 400 拒絕 "none"，
    /// 那只代表「關不掉」，不代表不吃強度參數。
    /// </summary>
    public static bool MarkDisableUnsupported(string providerId, string model)
    {
        lock (Gate)
        {
            return Unsupported.Add(BuildKey("d", providerId, model));
        }
    }

    public static bool IsReasoningUnsupported(string providerId, string model)
    {
        lock (Gate)
        {
            return Unsupported.Contains(BuildKey("r", providerId, model));
        }
    }

    public static bool IsDisableUnsupported(string providerId, string model)
    {
        lock (Gate)
        {
            return Unsupported.Contains(BuildKey("d", providerId, model));
        }
    }

    public static bool IsTemperatureUnsupported(string providerId, string model)
    {
        lock (Gate)
        {
            return Unsupported.Contains(BuildKey("t", providerId, model));
        }
    }

    /// <summary>清空記憶。供測試在案例之間隔離狀態使用。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Unsupported.Clear();
        }
    }
}
#pragma warning restore S101, S2342
