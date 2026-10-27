using System;
using System.Collections.Generic;

namespace RimLLM_Framework;

/// <summary>
/// 內建供應商識別碼常數。
/// 集中定義以避免魔法字串散落各處；第三方 Mod 亦可引用。
/// </summary>
public static class ProviderIds
{
    public const string OpenAI = "OpenAI";
    public const string Gemini = "Gemini";
    public const string OpenAICompatible = "OpenAICompatible";
    public const string DeepSeek = "DeepSeek";
    public const string Groq = "Groq";
    public const string Grok = "Grok";
    public const string OpenRouter = "OpenRouter";
    public const string Kimi = "Kimi";
    public const string MiniMax = "MiniMax";
    public const string Qwen = "Qwen";
    public const string Nvidia = "Nvidia";
    public const string Zai = "Z.ai";
    public const string Player2 = "Player2";

    /// <summary>
    /// 全部內建供應商，順序即為設定 UI 與診斷輸出的顯示順序。
    /// SDK 尚未初始化時，UI 以此清單作為後備。
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltIn = new List<string>
    {
        Gemini, OpenAI, DeepSeek, Groq, Grok, Zai,
        OpenRouter, Kimi, MiniMax, Qwen, Nvidia, Player2, OpenAICompatible
    };

    /// <summary>
    /// 該供應商是否必須填寫 API 金鑰才能使用。設定 UI 據此決定是否顯示「缺少金鑰」警告，
    /// 抓取模型與連線測試的金鑰前置檢查也看它；路由層則以各供應商實例的 <c>RequiresApiKey</c> 為準，
    /// 兩者必須由同一個判斷推導（見 <see cref="Player2Provider"/>），以免靜態與實例語義分歧。
    /// 端點相關是因為 Player2 有雙模式：本機 App 免金鑰，雲端（api.player2.game）需 p2Key。
    /// </summary>
    public static bool RequiresApiKey(string providerId, string endpoint)
    {
        if (providerId == OpenAICompatible) return false;
        if (providerId == Player2) return IsPlayer2CloudEndpoint(endpoint);
        return true;
    }

    /// <summary>
    /// 是否為 Player2 雲端端點。以 host 子字串判斷而非反向列舉本機位址：
    /// 自架代理的 host 未知，預設視為本地模式不擋，有填金鑰照樣送出。
    /// </summary>
    public static bool IsPlayer2CloudEndpoint(string endpoint)
    {
        return !string.IsNullOrEmpty(endpoint)
            && endpoint.IndexOf("api.player2.game", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 端點可由玩家自訂的供應商（OpenAICompatible 的本地／第三方位址、Player2 的本機／雲端切換）。
    /// 設定頁據此顯示端點欄位，取代各處散落的 <c>== OpenAICompatible || == Player2</c> 特判。
    /// </summary>
    public static bool HasCustomEndpoint(string providerId)
    {
        return providerId == OpenAICompatible || providerId == Player2 || providerId == Gemini;
    }

    /// <summary>
    /// 需要「中國端點」切換的供應商（各自有中國／國際兩組網域）。
    /// </summary>
    public static bool HasChinaEndpoint(string providerId)
    {
        return providerId == MiniMax || providerId == Qwen || providerId == Kimi;
    }

    /// <summary>
    /// 從 Fallback Chain 條目取出供應商識別碼。
    /// 條目格式為 "ProviderId" 或 "ProviderId:ModelName"；空字串回傳 null。
    /// </summary>
    public static string ParseProviderId(string fallbackEntry)
    {
        if (string.IsNullOrEmpty(fallbackEntry)) return null;

        int colonIndex = fallbackEntry.IndexOf(':');
        return colonIndex > 0 ? fallbackEntry.Substring(0, colonIndex) : fallbackEntry;
    }
}
