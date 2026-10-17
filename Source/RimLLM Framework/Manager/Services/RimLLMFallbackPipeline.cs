using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;
#pragma warning disable S1066, S2325 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀

namespace RimLLM_Framework.Manager;

/// <summary>
/// 負責備用鏈（Fallback Chain）維護、Provider 失敗後自動嘗試下一個備用 Provider、
/// 備用管道執行與健康帳本 (Health Ledger) 連動等邏輯的服務元件。
/// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
public class RimLLMFallbackPipeline
{
    private readonly IRimLLMSettings _settings;
    private readonly RimLLMHealthLedger _healthLedger;
    private readonly RimLLMUsageTracker _usageTracker;
    private readonly Func<string, ILLMProvider> _providerResolver;
    private readonly Func<string, bool> _isProviderEnabledFunc;

    internal struct ResolvedCandidate
    {
        public string Entry;
        public string ProviderId;
        public ILLMProvider Provider;
        public string ModelName;
    }

    public RimLLMFallbackPipeline(
        IRimLLMSettings settings,
        RimLLMHealthLedger healthLedger,
        RimLLMUsageTracker usageTracker,
        Func<string, ILLMProvider> providerResolver,
        Func<string, bool> isProviderEnabledFunc)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _healthLedger = healthLedger ?? throw new ArgumentNullException(nameof(healthLedger));
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _isProviderEnabledFunc = isProviderEnabledFunc ?? throw new ArgumentNullException(nameof(isProviderEnabledFunc));
    }

    /// <summary>
    /// 解析出本次請求可用的候選，並依路由策略排序。
    /// </summary>
    /// <remarks>
    /// 只做「選誰、依什麼順序」的決策；實際的嘗試、重試與健康記錄由
    /// <see cref="RimLLMFailoverChatClient"/> 負責。
    /// </remarks>
    /// <param name="includeCoolingDown">
    /// 為 true 時不過濾冷卻中的候選。能力查詢用：冷卻會到期，查詢當下被過濾掉的候選
    /// 仍可能在稍後的請求中被路由到。
    /// </param>
    internal List<ResolvedCandidate> ResolveCandidates(string preferredModelId, string minFallbackLevel, bool includeCoolingDown = false)
    {
        if (!TryResolveCandidates(preferredModelId, minFallbackLevel, includeCoolingDown, out List<ResolvedCandidate> candidates, out string failureReason))
        {
            throw new RimLLMException(LLMError.ProviderOffline, failureReason);
        }

        return candidates;
    }

    /// <summary>
    /// 非拋版候選解析，供每秒輪詢的接管判定使用，避免離線時每秒配置例外與堆疊。
    /// 語意與 <see cref="ResolveCandidates"/> 完全一致，僅以傳回值取代擲出。
    /// </summary>
    internal bool TryResolveCandidates(string preferredModelId, string minFallbackLevel, bool includeCoolingDown, out List<ResolvedCandidate> candidates, out string failureReason)
    {
        var fallbackChain = GetFallbackChainSnapshot();
        if (fallbackChain == null || fallbackChain.Count == 0)
        {
            candidates = null;
            failureReason = "No valid API provider fallback chain configured.";
            return false;
        }

        // 同一供應商常在鏈上出現多次（例如三個 OpenRouter 模型），API 金鑰在單次解析內不變，
        // 以區域快取去重，避免每個條目各拿一次設定鎖。單次解析的一致快照，語意不變。
        var apiKeyCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // PreferredModelId（格式 "ProviderId:ModelName"）指定的話，移到 fallback chain 最前面優先嘗試
        var effectiveChain = new List<string>(fallbackChain);
        bool preferredFirst = MovePreferredModelToFrontIfUsable(effectiveChain, preferredModelId, apiKeyCache);

        // 1. 解析所有符合資格的供應商候選
        var resolved = new List<ResolvedCandidate>();
        foreach (string entry in effectiveChain)
        {
            if (TryGetEligibleCandidate(entry, minFallbackLevel, apiKeyCache, out string pId, out ILLMProvider p, out string mName))
            {
                resolved.Add(new ResolvedCandidate { Entry = entry, ProviderId = pId, Provider = p, ModelName = mName });
            }
        }

        if (resolved.Count == 0)
        {
            candidates = null;
            failureReason = "No eligible API providers found in the fallback chain.";
            return false;
        }

        // 2. 過濾處於故障冷卻期的候選（若全部都在冷卻中，則破例放行）
        // 全組共用同一個時間戳：逐候選各取一次 UtcNow 既浪費又讓比較基準漂移。
        DateTime now = DateTime.UtcNow;
        var activeCandidates = includeCoolingDown
            ? resolved
            : resolved.FindAll(c => !_healthLedger.IsInCooldown(HealthKey(c), now));
        if (activeCandidates.Count == 0)
        {
            activeCandidates = resolved;
        }

        // 3. 套用路由與負載均衡策略
        // 呼叫端指定的模型固定在第一位、不參與排序；只有它失敗時才輪到路由策略排出的其餘候選。
        // 前面的步驟都保持順序，所以它若仍可用必定位於索引 0。
        ResolvedCandidate? pinnedPreferred = null;
        if (preferredFirst && string.Equals(activeCandidates[0].Entry, effectiveChain[0], StringComparison.OrdinalIgnoreCase))
        {
            pinnedPreferred = activeCandidates[0];
            activeCandidates.RemoveAt(0);
        }

        switch (_settings.RoutingStrategy)
        {
            case 1: // MinLatency (最小延遲優先)
                // 尚無延遲記錄的候選其平均延遲為 0，會自然排到最前面——這是刻意的探索行為：
                // 每個候選都得先被呼叫一次才會有延遲數字，若把無記錄者排到最後，
                // 第一個拿到記錄的候選就會被永久鎖定，其餘候選永遠沒有機會被測量。
                // LINQ OrderBy 保證穩定排序：同延遲候選維持備用鏈原順序。
                var byLatency = activeCandidates.OrderBy(c => _healthLedger.GetAverageLatency(HealthKey(c))).ToList();
                activeCandidates.Clear();
                activeCandidates.AddRange(byLatency);
                break;

            case 2: // RoundRobin / Random (隨機輪詢負載均衡)
                ShuffleCandidates(activeCandidates);
                break;

            case 3: // LowestCost (成本優先)
                // 直接沿用既有的模型分級（含使用者覆寫）作為成本代理值，由低到高排序。
                // 分級本來就是依 API 費率自動判定的，不需要另外維護一份價格表。
                // LINQ OrderBy 保證穩定排序：同級模型維持備用鏈原順序。
                var byCost = activeCandidates.OrderBy(c => GetModelLevel(c.Entry, c.ProviderId, c.ModelName)).ToList();
                activeCandidates.Clear();
                activeCandidates.AddRange(byCost);
                break;

            default: // 0 = PriorityFailover：保留原始 fallbackChain 順序
                break;
        }

        if (pinnedPreferred.HasValue)
        {
            activeCandidates.Insert(0, pinnedPreferred.Value);
        }

        candidates = activeCandidates;
        failureReason = null;
        return true;
    }


    /// <summary>
    /// 健康帳本的記錄鍵。冷卻以「供應商:模型」為單位，而非只看供應商——
    /// 同一個供應商底下常有多個模型同時掛在備用鏈上（例如三個 OpenRouter 模型），
    /// 只以供應商為鍵會讓其中一個模型限流就把另外兩個健康的模型一起連坐冷卻。
    /// </summary>
    internal static string HealthKey(ResolvedCandidate candidate)
    {
        return string.IsNullOrEmpty(candidate.ModelName)
            ? candidate.ProviderId
            : candidate.ProviderId + ":" + candidate.ModelName;
    }

    /// <summary>
    /// 計算下一次重試前的等待秒數。
    /// 以設定的重試間隔為基準做指數退避（第 n 次重試等待 delay × 2ⁿ）並加上 ±20% 抖動：
    /// 限流後以固定間隔連打只會再次一起撞牆，把重試額度白白耗光。
    /// 伺服器若透過 Retry-After 指定了更長的等待時間則以其為準；
    /// 整體上限 60 秒，免得玩家在遊戲中枯等。
    /// </summary>
    internal static float ResolveRetryDelay(float baseDelay, int attempt, Exception ex)
    {
        const float maxDelaySeconds = 60f;
        const int maxBackoffSteps = 6;

        float delay = baseDelay * (float)Math.Pow(2, Math.Min(attempt, maxBackoffSteps));
        if (delay > 0f)
        {
            delay *= 1f + ((float)NextRandomDouble() * 0.4f - 0.2f);
        }

        if (ex is RimLLMException rimEx && rimEx.RetryAfter.HasValue)
        {
            delay = Math.Max(delay, (float)rimEx.RetryAfter.Value.TotalSeconds);
        }

        return Math.Min(delay, maxDelaySeconds);
    }

    /// <summary>Fisher-Yates 洗牌，用於隨機輪詢負載均衡。</summary>
    private static void ShuffleCandidates(List<ResolvedCandidate> candidates)
    {
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = NextRandomInt(i + 1);
            var temp = candidates[i];
            candidates[i] = candidates[j];
            candidates[j] = temp;
        }
    }

#pragma warning disable S2245 // reason: 僅用於重試抖動與負載均衡輪詢，非安全相關隨機，無需密碼學強度
    private static readonly Random SharedRandom = new();
#pragma warning restore S2245
    private static readonly object RandomLock = new();

    // Random 非執行緒安全，而重試與洗牌都可能來自不同的背景執行緒；
    // 共用一個加鎖實例，同時也避免每次 new Random() 在同一毫秒內產生相同序列。
    private static double NextRandomDouble()
    {
        lock (RandomLock) { return SharedRandom.NextDouble(); }
    }

    private static int NextRandomInt(int maxExclusive)
    {
        lock (RandomLock) { return SharedRandom.Next(maxExclusive); }
    }

    /// <summary>
    /// 把指定模型放到鏈首。已在鏈上時移動既有條目（保留使用者設定的大小寫），否則插入。
    /// 回傳 true 代表鏈首現在就是指定模型。
    /// </summary>
    private bool MovePreferredModelToFrontIfUsable(List<string> effectiveChain, string preferredModelId, Dictionary<string, string> apiKeyCache)
    {
        if (string.IsNullOrEmpty(preferredModelId)) return false;

        // 備援鏈別名：明確要求走整條鏈，不釘選任何候選（與不指定模型同義）。
        if (RimLLMChatOptions.IsFallbackModelId(preferredModelId)) return false;

        string preferredEntry = preferredModelId;
        if (!ResolveFallbackEntry(preferredEntry, out string prefProvider, out string prefModel)
            || string.IsNullOrEmpty(prefModel))
        {
            // 無 provider 前綴的純 model 名視為「不指定 provider」，忽略（交給 fallback chain）
            prefProvider = null;
        }

        if (prefProvider == null || _providerResolver(prefProvider) is not ILLMProvider prefProviderInstance
            || !IsProviderUsable(prefProvider, prefProviderInstance, apiKeyCache))
        {
            return false;
        }

        int existingIndex = effectiveChain.FindIndex(e => string.Equals(e, preferredEntry, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            preferredEntry = effectiveChain[existingIndex];
            effectiveChain.RemoveAt(existingIndex);
        }
        effectiveChain.Insert(0, preferredEntry);
        return true;
    }

    internal bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
    {
        providerId = ProviderIds.ParseProviderId(entry);
        modelName = "";

        if (providerId == null)
        {
            providerId = entry;
            return false;
        }

        int colonIndex = entry.IndexOf(':');
        modelName = colonIndex > 0
            ? entry.Substring(colonIndex + 1)
            // 純供應商：取該供應商的預設模型
            : _settings.GetDefaultModel(providerId, "default");

        return true;
    }

    public void ClearCooldowns()
    {
        _healthLedger.Clear();
    }

    private List<string> GetFallbackChainSnapshot()
    {
        var chain = _settings.FallbackChain;
#pragma warning disable S1168 // reason: null 表示未配置 fallback 鏈，與空集合語意不同，呼叫端需區分
        return chain != null ? new List<string>(chain) : null;
#pragma warning restore S1168
    }

    private bool IsProviderUsable(string providerId, ILLMProvider provider, Dictionary<string, string> apiKeyCache)
    {
        if (!_isProviderEnabledFunc(providerId)) return false;
        if (!provider.RequiresApiKey) return true;
        if (apiKeyCache != null && apiKeyCache.TryGetValue(providerId, out string cachedKey))
        {
            return !string.IsNullOrEmpty(cachedKey);
        }

        string apiKey = _settings.GetApiKey(providerId);
        apiKeyCache?.Add(providerId, apiKey);
        return !string.IsNullOrEmpty(apiKey);
    }

    private bool TryGetEligibleCandidate(string entry, string minFallbackLevel, Dictionary<string, string> apiKeyCache, out string providerId, out ILLMProvider provider, out string modelName)
    {
        provider = null;

        if (!ResolveFallbackEntry(entry, out providerId, out modelName))
            return false;

        provider = _providerResolver(providerId);
        if (provider == null)
            return false;

        if (!IsProviderUsable(providerId, provider, apiKeyCache))
            return false;

        // 評估 MinFallbackLevel 模型分級
        int minLevel = ParseMinFallbackLevel(minFallbackLevel);
        if (minLevel > 0)
        {
            int currentModelLevel = GetModelLevel(entry, providerId, modelName);
            if (currentModelLevel < minLevel)
            {
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Message($"[RimLLM] Skipped fallback entry '{entry}' because its model level ({currentModelLevel}) is lower than MinFallbackLevel ({minLevel}).");
                }
                return false;
            }
        }

        return true;
    }

    private int GetModelLevel(string entry, string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 1;

        // 使用者明確設定的分級覆寫優先於 API 費率判定
        int overrideLevel = _settings.GetModelLevelOverride(entry ?? modelName);
        if (overrideLevel >= RimLLMUsageTracker.ModelLevelLow && overrideLevel <= RimLLMUsageTracker.ModelLevelHigh)
        {
            return overrideLevel;
        }

        // 依據 UsageTracker 中的真實 API 費率客觀判定等級
        return _usageTracker.GetModelLevel(providerId, modelName);
    }

    private static int ParseMinFallbackLevel(string levelStr)
    {
        return (levelStr ?? string.Empty).ToLower() switch
        {
            "high" or "3" => 3,
            "medium" or "2" => 2,
            "low" or "1" => 1,
            _ => 0,
        };
    }

    internal static bool IsRetryableException(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is RimLLMException rimEx)
        {
            return rimEx.Error switch
            {
                LLMError.Timeout or LLMError.RateLimit or LLMError.ProviderOffline or LLMError.NetworkError or LLMError.Unknown => true,
                LLMError.QuotaExceeded => rimEx.HttpStatusCode != 402,// 402 是真的餘額用完：不會在退避的幾十秒內變出來，重試只是白等，直接換手。
                                                                      // 但 429 訊息含 "quota" 的每分鐘限流（Gemini 免費層）也會映成 QuotaExceeded，
                                                                      // 那種等一下就能成功，仍要重試。
                _ => false,
            };
        }

        // OpenRouter 偶爾回傳 finish_reason: "error"（上游供應商暫時性錯誤），OpenAI SDK 解析該值時
        // 擲出 ArgumentOutOfRangeException。這不是呼叫參數有問題，同模型重試通常會成功，
        // 必須在下方的 ArgumentException 判定之前放行。
        if (ex is ArgumentOutOfRangeException &&
            ex.Message.IndexOf("Unknown ChatFinishReason value", StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        // 參數、狀態與解析類例外代表呼叫本身有問題，以相同輸入重試必然再次失敗。
        if (ex is ArgumentException ||
            ex is NotSupportedException ||
            ex is InvalidOperationException ||
            ex is System.Text.Json.JsonException)
        {
            return false;
        }

        return true;
    }
}
#pragma warning restore S101
#pragma warning restore S1066, S2325
