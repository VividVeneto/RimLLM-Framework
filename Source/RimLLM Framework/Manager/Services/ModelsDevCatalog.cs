using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 以 models.dev 的公開模型資料庫補齊供應商 API 沒回報的上下文上限。
/// 與 LiteLLM 的資料比對官方文件後，models.dev 在互相矛盾的項目上較常正確，因此選用它。
/// </summary>
internal static class ModelsDevCatalog
{
    private const string ApiUrl = "https://models.dev/api.json";

    /// <summary>
    /// 框架供應商 → models.dev 供應商鍵。OpenRouter 的 API 本身就回報全部模型的上限，不需要另外下載；
    /// OpenAICompatible 的模型名稱由使用者自訂，無從對照。兩者皆不列入。
    /// </summary>
    private static readonly Dictionary<string, string> ProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderIds.OpenAI] = "openai",
        [ProviderIds.Gemini] = "google",
        [ProviderIds.DeepSeek] = "deepseek",
        [ProviderIds.Groq] = "groq",
        [ProviderIds.Grok] = "xai",
        [ProviderIds.Kimi] = "moonshotai",
        [ProviderIds.MiniMax] = "minimax",
        [ProviderIds.Qwen] = "alibaba",
        [ProviderIds.Nvidia] = "nvidia",
        [ProviderIds.Zai] = "zai",
    };

    /// <summary>資料庫未壓縮約 5 MB，開啟 gzip 傳輸。逾時由呼叫端的 CancellationToken 控制。</summary>
    private static readonly HttpClient Http = new(
        new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// 下載結果保留的時間。玩家常一次重新整理好幾個供應商，每次重下約 5 MB 的資料庫是浪費。
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly object CacheLock = new();
    private static Task<Dictionary<string, Dictionary<string, int>>> _cached;
    private static DateTime _cachedAt;

    internal static bool IsCovered(string providerId)
    {
        return !string.IsNullOrEmpty(providerId) && ProviderKeys.ContainsKey(providerId);
    }

    /// <summary>
    /// 取得所有已對照供應商的上限（框架供應商 → 模型名稱 → token 數）。
    /// 同時進行的呼叫共用同一次下載；成功結果快取 <see cref="CacheDuration"/>，失敗則下次重試。
    /// 失敗只記警告並回傳 null，上限只是附帶資訊，不讓模型清單的重新整理失敗。
    /// </summary>
    internal static Task<Dictionary<string, Dictionary<string, int>>> GetAllAsync(float timeoutSeconds)
    {
        lock (CacheLock)
        {
            bool reusable = _cached != null
                && DateTime.UtcNow - _cachedAt < CacheDuration
                && !(_cached.IsCompleted && _cached.Result == null);
            if (!reusable)
            {
                _cached = DownloadAsync(timeoutSeconds);
                _cachedAt = DateTime.UtcNow;
            }
            return _cached;
        }
    }

    private static async Task<Dictionary<string, Dictionary<string, int>>> DownloadAsync(float timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 30f));
            using HttpResponseMessage response = await Http.GetAsync(ApiUrl, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            return ReadAll(doc.RootElement);
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Could not read context window sizes from models.dev: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            return null;
        }
    }

    /// <summary>
    /// 一次讀出所有已對照供應商的上限，只保留這一小部分，不保留整份約 5 MB 的文件。
    /// 每個模型有 <c>limit.input</c>（輸入上限）就用它，否則用 <c>limit.context</c>（整個視窗）：
    /// 壓縮對話歷史要控制的是輸入量，輸入上限比含輸出的整個視窗貼切。
    /// </summary>
    internal static Dictionary<string, Dictionary<string, int>> ReadAll(JsonElement root)
    {
        var all = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return all;

        foreach (var mapping in ProviderKeys)
        {
            if (!root.TryGetProperty(mapping.Value, out JsonElement provider)
                || provider.ValueKind != JsonValueKind.Object
                || !provider.TryGetProperty("models", out JsonElement models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            all[mapping.Key] = ReadModelWindows(models);
        }
        return all;
    }

    private static Dictionary<string, int> ReadModelWindows(JsonElement models)
    {
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty model in models.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object
                || !model.Value.TryGetProperty("limit", out JsonElement limit)
                || limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int tokens = ReadPositiveInt(limit, "input");
            if (tokens <= 0) tokens = ReadPositiveInt(limit, "context");
            if (tokens > 0) windows[model.Name] = tokens;
        }
        return windows;
    }

    /// <summary>把該供應商的上限補進 <paramref name="windows"/>，已有的值（供應商 API 回報）不覆寫。</summary>
    internal static void FillMissing(Dictionary<string, Dictionary<string, int>> all, string providerId, Dictionary<string, int> windows)
    {
        if (all == null || windows == null || string.IsNullOrEmpty(providerId)
            || !all.TryGetValue(providerId, out var fromDatabase))
        {
            return;
        }

        foreach (var kvp in fromDatabase.Where(k => !windows.ContainsKey(k.Key)))
        {
            windows[kvp.Key] = kvp.Value;
        }
    }

    private static int ReadPositiveInt(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number > 0
            ? number
            : 0;
    }
}
