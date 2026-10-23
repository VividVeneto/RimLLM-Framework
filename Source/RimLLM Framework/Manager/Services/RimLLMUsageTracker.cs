using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 管理並統計 API 呼叫量、Token 使用度、連線日誌記錄、API 計費預估與每日預算審查。
/// 支援對設定檔的磁碟存檔寫入實施節流（防震）保護。
/// </summary>
public class RimLLMUsageTracker
{
    private readonly IRimLLMSettings _settings;
    private static DateTime _lastLogWriteTime = DateTime.MinValue;
    private static readonly object LogLock = new();
    private static readonly object UsageLock = new();
    private static bool _telemetrySaveDeferred;
    private static long _telemetrySaveScheduleVersion;
    // 每筆 reservation 在整個 logical request（含 fallback/retry）結束前都保留一份估算額度。
    private long _reservedBudgetTokens;
    private int _reservedLegacyBudgetRequests;

    /// <summary>日誌保留上限。與 Trim 邏輯共用，避免魔法數字散落。</summary>
    internal const int MaxRetainedLogs = 30;

    /// <summary>
    /// 日誌佇列的近似計數。ConcurrentQueue.Count 在舊 Mono 上是加鎖列舉（O(n)），
    /// 每請求呼叫一次會隨日誌量線性變貴；此計數器以 Interlocked 維護，Trim 只看它。
    /// 多執行緒下為近似值（最終一致），僅影響保留條數上下一兩條，不影響正確性。
    /// </summary>
    private int _logCount;
    private long _unpricedUsageCount;

    /// <summary>本次執行中有用量但查無費率的呼叫數；不把未知費用誤認為免費。</summary>
    public long UnpricedUsageCount => Interlocked.Read(ref _unpricedUsageCount);

    public const int ModelLevelHigh = 3;
    public const int ModelLevelMedium = 2;
    public const int ModelLevelLow = 1;

    private const float HighTierCompletionCostThreshold = 3.00f;
    private const float MediumTierCompletionCostThreshold = 0.50f;

    /// <summary>
    /// 依據模型的 API 費率（每百萬 Token 輸出費率）判定其等級：
    /// Level 3 (High): 輸出費率 >= $3.00 / 1M Tokens (如 GPT-4o, Gemini Pro, Grok, Qwen-Max)
    /// Level 2 (Medium): 輸出費率 >= $0.50 / 1M Tokens (如 Gemini Flash, DeepSeek-V4-Pro, Qwen-Plus)
    /// Level 1 (Low): 輸出費率 < $0.50 / 1M Tokens 或本地免費模型 ($0)
    /// </summary>
    /// <summary>
    /// 模型分級快取：費率表為靜態唯讀，同一 provider:model 的分級永不變化。
    /// 鍵保留原始大小寫，以 OrdinalIgnoreCase 比對，省下每次的 ToLower 配置。
    /// 使用者覆寫走 <see cref="IRimLLMSettings.GetModelLevelOverride"/> 即時查詢，不進快取，
    /// 因此設定頁調整分級立即生效。
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> ModelLevelCache =
        new(StringComparer.OrdinalIgnoreCase);

    public int GetModelLevel(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(modelName) ||
            string.Equals(providerId, "openaicompatible", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerId, "openai-compatible", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase))
        {
            return ModelLevelLow;
        }

        string cacheKey = BuildModelLevelCacheKey(providerId, modelName);
        if (ModelLevelCache.TryGetValue(cacheKey, out int cached))
        {
            return cached;
        }

        int level;
        if (FindModelRate(providerId, modelName, out CostRate rate))
        {
            if (rate.CompletionPerMillion >= HighTierCompletionCostThreshold) level = ModelLevelHigh;
            else if (rate.CompletionPerMillion >= MediumTierCompletionCostThreshold) level = ModelLevelMedium;
            else level = ModelLevelLow;
        }
        else
        {
            level = ModelLevelMedium; // 未知雲端模型預設給予 Medium 評級
        }

        ModelLevelCache[cacheKey] = level;
        return level;
    }

    /// <summary>組分級快取鍵：僅剝除 models/ 前綴，不做大小寫正規化（比對器負責）。</summary>
    private static string BuildModelLevelCacheKey(string providerId, string modelName)
    {
        string model = StripModelsPrefix(modelName?.Trim());
        return (providerId?.Trim() ?? "") + ":" + model;
    }

    private static string StripModelsPrefix(string model)
    {
        if (!string.IsNullOrEmpty(model) && model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            return model.Substring("models/".Length);
        }
        return model ?? "";
    }

    private static readonly Dictionary<string, CostRate> KnownModelRates = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI
        { "openai:gpt-5", new CostRate(1.25f, 10.00f) },
        { "openai:gpt-5-mini", new CostRate(0.25f, 2.00f) },
        { "openai:gpt-5-nano", new CostRate(0.05f, 0.40f) },
        { "openai:gpt-5.1", new CostRate(1.25f, 10.00f) },
        { "openai:gpt-4.1", new CostRate(2.00f, 8.00f) },
        { "openai:gpt-4.1-mini", new CostRate(0.40f, 1.60f) },
        { "openai:gpt-4.1-nano", new CostRate(0.10f, 0.40f) },
        { "openai:gpt-4o", new CostRate(2.50f, 10.00f) },
        { "openai:gpt-4o-mini", new CostRate(0.15f, 0.60f) },
        { "openai:o1", new CostRate(15.00f, 60.00f) },
        { "openai:o1-mini", new CostRate(1.10f, 4.40f) },
        { "openai:o3", new CostRate(2.00f, 8.00f) },
        { "openai:o3-mini", new CostRate(1.10f, 4.40f) },
        { "openai:o3-pro", new CostRate(20.00f, 80.00f) },
        { "openai:o4-mini", new CostRate(1.10f, 4.40f) },
        { "openai:gpt-4-turbo", new CostRate(10.00f, 30.00f) },
        { "openai:gpt-4", new CostRate(30.00f, 60.00f) },
        { "openai:gpt-3.5-turbo", new CostRate(0.50f, 1.50f) },
        { "openai:text-embedding-3-small", new CostRate(0.02f, 0f) },
        { "openai:text-embedding-3-large", new CostRate(0.13f, 0f) },
        { "openai:text-embedding-ada-002", new CostRate(0.10f, 0f) },

        // Google Gemini
        { "gemini:gemini-embedding-001", new CostRate(0.15f, 0f) },
        { "gemini:gemini-2.0-flash", new CostRate(0.10f, 0.40f) },
        { "gemini:gemini-2.0-flash-lite", new CostRate(0.075f, 0.30f) },
        { "gemini:gemini-1.5-flash", new CostRate(0.075f, 0.30f) },
        { "gemini:gemini-1.5-pro", new CostRate(1.25f, 5.00f) },
        { "gemini:gemini-2.5-pro", new CostRate(1.25f, 10.00f) },
        { "gemini:gemini-2.5-flash", new CostRate(0.30f, 2.50f) },
        { "gemini:gemini-2.5-flash-lite", new CostRate(0.10f, 0.40f) },
        { "gemini:gemini-3.1-pro-preview", new CostRate(2.00f, 12.00f) },
        { "gemini:gemini-3.1-flash-lite", new CostRate(0.25f, 1.50f) },
        { "gemini:gemini-3.5-flash", new CostRate(1.50f, 9.00f) },

        // DeepSeek
        { "deepseek:deepseek-chat", new CostRate(0.14f, 0.28f, 0.02f) },
        { "deepseek:deepseek-reasoner", new CostRate(0.14f, 0.28f, 0.02f) },
        { "deepseek:deepseek-v3", new CostRate(0.14f, 0.28f, 0.02f) },
        { "deepseek:deepseek-r1", new CostRate(0.14f, 0.28f, 0.02f) },
        { "deepseek:deepseek-v4-flash", new CostRate(0.14f, 0.28f, 0.02f) },
        { "deepseek:deepseek-v4-pro", new CostRate(0.435f, 0.87f, 0.02f) },

        // Groq（模型 ID 含供應商前綴時整段入鍵）
        { "groq:llama-3.3-70b-versatile", new CostRate(0.59f, 0.79f) },
        { "groq:llama-3.1-8b-instant", new CostRate(0.05f, 0.08f) },
        { "groq:mixtral-8x7b-32768", new CostRate(0.24f, 0.24f) },
        { "groq:gemma2-9b-it", new CostRate(0.20f, 0.20f) },
        { "groq:openai/gpt-oss-120b", new CostRate(0.15f, 0.75f) },
        { "groq:openai/gpt-oss-20b", new CostRate(0.10f, 0.50f) },
        { "groq:qwen/qwen3-32b", new CostRate(0.29f, 0.59f) },
        { "groq:moonshotai/kimi-k2-instruct", new CostRate(1.00f, 3.00f) },

        // Qwen
        { "qwen:qwen-max", new CostRate(2.80f, 8.40f) },
        { "qwen:qwen-plus", new CostRate(0.40f, 1.20f) },
        { "qwen:qwen-turbo", new CostRate(0.10f, 0.20f) },

        // Moonshot / Kimi
        { "kimi:kimi-k2", new CostRate(0.60f, 2.50f) },
        { "kimi:moonshot-v1-8k", new CostRate(1.68f, 1.68f) },
        { "kimi:moonshot-v1-32k", new CostRate(3.36f, 3.36f) },
        { "kimi:moonshot-v1-128k", new CostRate(8.40f, 8.40f) },

        // MiniMax
        { "minimax:minimax-m3", new CostRate(0.30f, 1.20f) },
        { "minimax:minimax-m2", new CostRate(0.30f, 1.20f) },
        { "minimax:abab6.5s", new CostRate(0.14f, 0.28f) },

        // Z.ai (GLM)
        { "z.ai:glm-4.6", new CostRate(0.60f, 2.20f) },
        { "z.ai:glm-4.5", new CostRate(0.60f, 2.20f) },
        { "z.ai:glm-4.5-air", new CostRate(0.20f, 1.10f) },
        { "z.ai:glm-4.5-flash", new CostRate(0f, 0f) },

        // Grok (xAI)
        { "grok:grok-4", new CostRate(3.00f, 15.00f) },
        { "grok:grok-4-fast", new CostRate(0.20f, 0.50f) },
        { "grok:grok-3", new CostRate(3.00f, 15.00f) },
        { "grok:grok-3-mini", new CostRate(0.30f, 0.50f) },
        { "grok:grok-2", new CostRate(2.00f, 10.00f) },
        { "grok:grok-beta", new CostRate(5.00f, 15.00f) },

        // Player2：本地 App 免費，雲端以 joules 而非美元計費，因此 USD 費率記為已知的 0。
        { "player2:player2", new CostRate(0f, 0f) }
    };

    private struct CostRate
    {
        public readonly float PromptPerMillion;
        public readonly float CompletionPerMillion;
        public readonly float CachedPromptPerMillion;

        public CostRate(float promptPerMillion, float completionPerMillion, float cacheReadMultiplier = 0.25f)
        {
            PromptPerMillion = promptPerMillion;
            CompletionPerMillion = completionPerMillion;
            CachedPromptPerMillion = promptPerMillion * cacheReadMultiplier;
        }
    }

    /// <summary>
    /// 存放最近 API 呼叫歷史的執行緒安全佇列。
    /// </summary>
    public readonly ConcurrentQueue<RimLLMManager.RequestLogEntry> RequestLogs =
        new();

    public class ProviderStats
    {
        public int SuccessCount;
        public int FailureCount;
        public int TotalCount => SuccessCount + FailureCount;
        public float SuccessRate => TotalCount > 0 ? (float)SuccessCount / TotalCount : 1f;

        // API-side Context Caching Stats
        public long TotalPromptTokens;
        public long CachedPromptTokens;
        public float ContextCacheHitRate => TotalPromptTokens > 0 ? (float)CachedPromptTokens / TotalPromptTokens : 0f;
    }

    public readonly ConcurrentDictionary<string, ProviderStats> ProviderStatistics =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 呼叫端 modId 的收錄上限。modId 由呼叫端自取、可任意輪換，
    /// 超過上限就不再收錄，避免整場遊戲無限成長、設定頁列出成千上萬列。
    /// </summary>
    public const int MaxCallerModIds = 64;

    /// <summary>本次遊戲實際送出過請求的 modId（值不使用，僅當集合）。</summary>
    private readonly ConcurrentDictionary<string, byte> _callerModIds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>收錄的 modId 數量；設定頁每幀用它判斷是否需要重組，不必複製整個集合。</summary>
    public int CallerModIdCount => _callerModIds.Count;

    /// <summary>收錄的 modId 快照，供設定頁「使用中的模組」。modId 為呼叫端自取的字串，不保證等於 packageId。</summary>
    public IReadOnlyCollection<string> CallerModIds => new List<string>(_callerModIds.Keys);

    /// <summary>
    /// 記錄一次請求的呼叫端。在請求進入守衛時呼叫，而不是在建立 client 時：
    /// 許多 Mod 一啟動就先取 client，但從未真的送出請求。
    /// </summary>
    public void RecordCaller(string modId)
    {
        if (string.IsNullOrEmpty(modId) || _callerModIds.Count >= MaxCallerModIds) return;
        _callerModIds.TryAdd(modId, 0);
    }

    public RimLLMUsageTracker(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (_settings.RequestLogs != null)
        {
            foreach (var log in _settings.RequestLogs)
            {
                RequestLogs.Enqueue(log);
                Interlocked.Increment(ref _logCount);
                CountOutcome(log.Provider, log.Success);
            }
            TrimLogs();
        }
    }

    /// <summary>
    /// 記錄一次請求的日誌與結果，並在背景以節流機制寫入 XML 設定檔中。
    /// token 數由呼叫端在成功且供應商有回報時填入；拿不到時保持 0，顯示層會省略。
    /// </summary>
    public void RecordLog(RimLLMManager.RequestLogEntry entry)
    {
        if (entry == null) return;
        entry.ErrorMessage = RimLLMLog.SanitizeForLog(entry.ErrorMessage, 300);
        entry.PromptTokens = Math.Max(0, entry.PromptTokens);
        entry.CompletionTokens = Math.Max(0, entry.CompletionTokens);

        TelemetrySaveSchedule schedule;
        lock (LogLock)
        {
            RequestLogs.Enqueue(entry);
            Interlocked.Increment(ref _logCount);
            TrimLogs();
            _settings.RequestLogs = new List<RimLLMManager.RequestLogEntry>(RequestLogs.ToArray());
            CountOutcome(entry.Provider, entry.Success);
            // Snapshot 與 dirty 標記同鎖更新，讓背景寫入及關閉 flush 都能看見最新日誌。
            schedule = MarkTelemetryDirtyLocked(immediate: !entry.Success);
        }

        RunTelemetrySaveSchedule(schedule);
    }

    /// <summary>
    /// 將一次請求結果計入該供應商的成功／失敗計數。
    /// </summary>
    private void CountOutcome(string provider, bool success)
    {
        var stats = ProviderStatistics.GetOrAdd(provider, _ => new ProviderStats());
        if (success)
        {
            System.Threading.Interlocked.Increment(ref stats.SuccessCount);
        }
        else
        {
            System.Threading.Interlocked.Increment(ref stats.FailureCount);
        }
    }

    private static readonly object SaveQueueLock = new();
    private static bool _telemetrySaveRunning;
    private static bool _telemetrySaveRequested;

    private readonly struct TelemetrySaveSchedule
    {
        public bool QueueNow { get; }
        public bool QueueLater { get; }
        public TimeSpan Delay { get; }
        public long Version { get; }

        public TelemetrySaveSchedule(bool queueNow, bool queueLater, TimeSpan delay, long version)
        {
            QueueNow = queueNow;
            QueueLater = queueLater;
            Delay = delay;
            Version = version;
        }
    }

    /// <summary>LogLock 必須由呼叫端持有，讓快照、dirty 標記和節流時間保持同一個順序。</summary>
    private TelemetrySaveSchedule MarkTelemetryDirtyLocked(bool immediate)
    {
        _settings.MarkTelemetryDirty();
        DateTime now = DateTime.UtcNow;
        TimeSpan interval = TimeSpan.FromSeconds(15);
        TimeSpan elapsed = _lastLogWriteTime == DateTime.MinValue
            ? interval
            : now - _lastLogWriteTime;

        if (immediate || elapsed >= interval)
        {
            _lastLogWriteTime = now;
            _telemetrySaveDeferred = false;
            _telemetrySaveScheduleVersion++;
            return new TelemetrySaveSchedule(true, false, TimeSpan.Zero, _telemetrySaveScheduleVersion);
        }

        if (!_telemetrySaveDeferred)
        {
            _telemetrySaveDeferred = true;
            long version = ++_telemetrySaveScheduleVersion;
            return new TelemetrySaveSchedule(false, true, interval - elapsed, version);
        }

        return default;
    }

    private void RunTelemetrySaveSchedule(TelemetrySaveSchedule schedule)
    {
        if (schedule.QueueNow)
        {
            QueueTelemetrySave(_settings);
        }
        else if (schedule.QueueLater)
        {
            _ = QueueTelemetrySaveAfterDelay(_settings, schedule.Delay, schedule.Version);
        }
    }

    private static async Task QueueTelemetrySaveAfterDelay(IRimLLMSettings settings, TimeSpan delay, long version)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        bool shouldQueue;
        lock (LogLock)
        {
            shouldQueue = _telemetrySaveDeferred && version == _telemetrySaveScheduleVersion;
            if (shouldQueue)
            {
                _telemetrySaveDeferred = false;
                _lastLogWriteTime = DateTime.UtcNow;
            }
        }

        if (shouldQueue)
        {
            QueueTelemetrySave(settings);
        }
    }

    /// <summary>
    /// 把遙測寫檔排入背景單寫者佇列。同一時間最多一個寫檔在跑，
    /// 執行中若又有新請求，只合併為一次追寫（存檔讀的是執行當下的最新狀態，不會遺失）。
    /// 寫檔本身（Save）為例外安全且以暫存檔原子替換，失敗會保留 IsDirty 由下次 flush 重試。
    /// 背景寫檔持有 <see cref="LogLock"/>，與主線程的記憶體更新、ClearLogs 互斥，
    /// 排序與過去「主線程內寫檔」一致；關閉時的 FlushTelemetryIfDirty 仍同步執行。
    /// </summary>
    internal static void QueueTelemetrySave(IRimLLMSettings settings)
    {
        lock (SaveQueueLock)
        {
            if (_telemetrySaveRunning)
            {
                _telemetrySaveRequested = true;
                return;
            }
            _telemetrySaveRunning = true;
        }

        Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    try
                    {
                        lock (LogLock)
                        {
                            settings.SaveTelemetry();
                        }
                    }
                    catch (Exception ex)
                    {
                        RimLLMLog.Warning($"[RimLLM] Background telemetry write failed: {ex.Message}");
                    }

                    lock (SaveQueueLock)
                    {
                        if (!_telemetrySaveRequested)
                        {
                            _telemetrySaveRunning = false;
                            return;
                        }
                        _telemetrySaveRequested = false;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (SaveQueueLock)
                {
                    _telemetrySaveRunning = false;
                    _telemetrySaveRequested = false;
                }
                RimLLMLog.Warning($"[RimLLM] Background telemetry writer failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 以計數器為準修剪多餘日誌，避免 ConcurrentQueue.Count 的加鎖列舉。
    /// </summary>
    private void TrimLogs()
    {
        while (Volatile.Read(ref _logCount) > MaxRetainedLogs)
        {
            if (!RequestLogs.TryDequeue(out _))
            {
                break;
            }
            Interlocked.Decrement(ref _logCount);
        }
    }

    /// <summary>
    /// 清空所有快取的請求日誌，並儲存設定。
    /// </summary>
    public void ClearLogs()
    {
        lock (LogLock)
        {
            while (RequestLogs.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _logCount, 0);
            ProviderStatistics.Clear();
            _settings.RequestLogs = [];
            _settings.MarkTelemetryDirty();
            InvalidateDeferredTelemetrySaveLocked();
            try
            {
                _settings.SaveTelemetry();
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Clear logs telemetry write failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 檢查並執行跨天重置日預算累計（token 與舊版美元估計值一併重置）。
    /// </summary>
    public void CheckDailyReset()
    {
        string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        lock (LogLock)
        {
            lock (UsageLock)
            {
                if (string.IsNullOrEmpty(_settings.DailyBudgetResetDate) || _settings.DailyBudgetResetDate != todayStr)
                {
                    _settings.DailyAccumulatedCost = 0f;
                    if (_settings is IDailyTokenBudget tokenLedger)
                    {
                        tokenLedger.DailyAccumulatedTokens = 0;
                    }
                    _settings.DailyBudgetResetDate = todayStr;
                }
            }
        }
    }

    /// <summary>
    /// 累加 Token 統計值，並估計該次 API 消耗的美元成本。
    /// </summary>
    /// <param name="promptTokens">本次請求的「輸入 Token 總量」，須包含被快取命中的部分，以反映真實用量。</param>
    /// <param name="cachedPromptTokens">
    /// 輸入 Token 中由上下文快取（cache read / cachedContent）命中的部分；
    /// 這些 Token 以折扣費率計價，是 Context Caching 節省成本的來源。
    /// </param>
    public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
    {
        promptTokens = Math.Max(0, promptTokens);
        completionTokens = Math.Max(0, completionTokens);
        if (promptTokens == 0 && completionTokens == 0) return;

        if (cachedPromptTokens < 0) cachedPromptTokens = 0;
        if (cachedPromptTokens > promptTokens) cachedPromptTokens = promptTokens;

        CheckDailyReset();

        TelemetrySaveSchedule schedule;
        lock (LogLock)
        {
            lock (UsageLock)
            {
                long usageTokens = (long)promptTokens + completionTokens;

                _settings.TotalPromptTokens += promptTokens;
                _settings.TotalCompletionTokens += completionTokens;

                // Token 預算不看費率：免費、本地與查無費率的請求一樣消耗 token。
                if (_settings is IDailyTokenBudget tokenLedger)
                {
                    tokenLedger.DailyAccumulatedTokens += usageTokens;
                }

                if (TryEstimateCost(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens, out float cost))
                {
                    _settings.TotalEstimatedCost += cost;
                    _settings.DailyAccumulatedCost += cost;
                }
                else
                {
                    Interlocked.Increment(ref _unpricedUsageCount);
                }
            }

            // 累加特定供應商的 prompt tokens 與 API 快取 tokens 用量。
            var stats = ProviderStatistics.GetOrAdd(providerId, _ => new ProviderStats());
            lock (stats)
            {
                stats.TotalPromptTokens += promptTokens;
                stats.CachedPromptTokens += cachedPromptTokens;
            }

            schedule = MarkTelemetryDirtyLocked(immediate: false);
        }

        RunTelemetrySaveSchedule(schedule);
    }

    /// <summary>
    /// 重設所有的 Token 與費用計量器。
    /// </summary>
    public void ResetUsage()
    {
        lock (LogLock)
        {
            lock (UsageLock)
            {
                _settings.TotalPromptTokens = 0;
                _settings.TotalCompletionTokens = 0;
                _settings.TotalEstimatedCost = 0f;
                Interlocked.Exchange(ref _unpricedUsageCount, 0);

                foreach (var kvp in ProviderStatistics)
                {
                    lock (kvp.Value)
                    {
                        kvp.Value.TotalPromptTokens = 0;
                        kvp.Value.CachedPromptTokens = 0;
                    }
                }
            }

            _settings.MarkTelemetryDirty();
            InvalidateDeferredTelemetrySaveLocked();
            try
            {
                _settings.SaveTelemetry();
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Reset usage telemetry write failed: {ex.Message}");
            }
        }
    }

    private static void InvalidateDeferredTelemetrySaveLocked()
    {
        _lastLogWriteTime = DateTime.UtcNow;
        _telemetrySaveDeferred = false;
        _telemetrySaveScheduleVersion++;
    }

    /// <summary>
    /// 估算單次呼叫的美元成本。為維持既有 API，查無費率時仍回傳 0；
    /// 需要區分未知費用與已知免費時，應使用 <see cref="TryEstimateCost"/>。
    /// <paramref name="cachedPromptTokens"/> 由呼叫端保證已落在 [0, promptTokens] 範圍內。
    /// </summary>
    public float EstimateCost(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens)
    {
        TryEstimateCost(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens, out float cost);
        return cost;
    }

    /// <summary>回傳是否找到費率，讓記帳區分未知費用與明確的零元費率。</summary>
    internal bool TryEstimateCost(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens, out float cost)
    {
        if (!FindModelRate(providerId, modelName, out CostRate rate))
        {
            cost = 0f;
            return false;
        }

        int fullRatePromptTokens = promptTokens - cachedPromptTokens;
        float promptCost = (fullRatePromptTokens / 1000000f) * rate.PromptPerMillion
                           + (cachedPromptTokens / 1000000f) * rate.CachedPromptPerMillion;
        float completionCost = (completionTokens / 1000000f) * rate.CompletionPerMillion;
        cost = promptCost + completionCost;
        return true;
    }

    private bool FindModelRate(string providerId, string modelName, out CostRate rate)
    {
        // 字典本身為 OrdinalIgnoreCase，直接以未轉小寫的鍵查詢，省下每次的 ToLower 配置。
        string normModel = StripModelsPrefix(modelName?.Trim());
        string exactKey = (providerId?.Trim() ?? "") + ":" + normModel;

        if (KnownModelRates.TryGetValue(exactKey, out rate))
        {
            return true;
        }

        // 前綴模糊匹配（例如 gpt-4o-2024-08-06 匹配 gpt-4o, gemini-2.0-flash-001 匹配 gemini-2.0-flash）。
        // 前綴之後必須是分隔字元或結尾：單純的 StartsWith 會讓 "gpt-4" 吃到 "gpt-4.1-mini"，
        // 把 $0.40 的模型以 $30 計價。
        string bestPrefixMatchKey = null;
        int bestPrefixLen = 0;

        string providerPrefix = (providerId?.Trim() ?? "") + ":";
        foreach (var kvp in KnownModelRates)
        {
            if (kvp.Key.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string candidateModel = kvp.Key.Substring(providerPrefix.Length);
                if (normModel.StartsWith(candidateModel, StringComparison.OrdinalIgnoreCase) &&
                    IsPrefixBoundary(normModel, candidateModel.Length) &&
                    candidateModel.Length > bestPrefixLen)
                {
                    bestPrefixLen = candidateModel.Length;
                    bestPrefixMatchKey = kvp.Key;
                }
            }
        }

        if (bestPrefixMatchKey != null && KnownModelRates.TryGetValue(bestPrefixMatchKey, out rate))
        {
            return true;
        }

        rate = default;
        return false;
    }

    /// <summary>
    /// 前綴比對的版本邊界：緊接在前綴後的字元必須是分隔符（或已到結尾），
    /// 讓 "gpt-4o" 命中 "gpt-4o-2024-08-06" 但 "gpt-4" 不命中 "gpt-4.1"。
    /// </summary>
    private static bool IsPrefixBoundary(string model, int prefixLength)
    {
        if (prefixLength >= model.Length) return true;
        char next = model[prefixLength];
        return next == '-' || next == '_' || next == ':' || next == '@' || next == ' ';
    }

    #region Budget Ledger & Policy Gatekeeping

    /// <summary>
    /// 預算超限應對策略：只警告並照常送出（<c>BudgetPolicy = 1</c>）。
    /// </summary>
    private const int BudgetPolicyWarnOnly = 1;

    /// <summary>
    /// 沒有翻譯資料時（單元測試、headless）使用的英文字串，同時也是日誌內容。
    /// </summary>
    private const string BudgetWarnOnlyFallbackText =
        "Daily budget limit exceeded. Requests are still being sent because the exceeded policy " +
        "is set to 'warn only'. Raise the budget or switch the policy to 'block requests and " +
        "warn' in RimLLM settings -> Budget & Safety.";

    /// <summary>HardBlock 超限警告的英文字串（無翻譯資料時使用，同時也是日誌內容）。</summary>
    private const string BudgetHardBlockFallbackText =
        "Daily budget limit exceeded. RimLLM is blocking AI requests until the budget resets tomorrow. " +
        "Raise the budget or switch the policy to 'warn only' in RimLLM settings -> Budget & Safety.";

    /// <summary>最後一次發出超限警告的重置基準日，讓超限警告每天只吵一次。</summary>
    private string _budgetWarnedDate = "";

    /// <summary>
    /// 原子地檢查預算並保留整個 logical request 的估算 token。每個 fallback/retry attempt
    /// 的實際用量仍會獨立累加；直到請求成功、失敗、取消或串流結束時 Dispose 才釋放 reservation。
    /// </summary>
    internal bool TryReserveBudget(long estimatedTokens, out BudgetReservation reservation)
    {
        CheckDailyReset();

        bool overBudget;
        bool warnOnly;
        reservation = null;
        lock (UsageLock)
        {
            overBudget = IsBudgetExceededLocked();
            warnOnly = _settings.BudgetPolicy == BudgetPolicyWarnOnly;
            if (!overBudget || warnOnly)
            {
                IDailyTokenBudget tokenLedger = _settings as IDailyTokenBudget;
                bool hasTokenLedger = tokenLedger != null;
                long reservedTokens = hasTokenLedger ? Math.Max(1L, estimatedTokens) : 0L;
                if (tokenLedger != null && tokenLedger.DailyTokenBudgetLimit > 0)
                {
                    // 單筆估算不當成精準上限；保留量最多到達門檻，阻止後續請求
                    // 共用同一份剩餘額度，實際超出估算的 token 仍會完整記帳。
                    reservedTokens = Math.Min(reservedTokens, tokenLedger.DailyTokenBudgetLimit);
                }
                _reservedBudgetTokens = SaturatingAdd(_reservedBudgetTokens, reservedTokens);

                bool reserveLegacySlot = !hasTokenLedger && _settings.DailyBudgetLimit > 0f;
                if (reserveLegacySlot && _reservedLegacyBudgetRequests < int.MaxValue)
                {
                    _reservedLegacyBudgetRequests++;
                }

                reservation = new BudgetReservation(this, reservedTokens, reserveLegacySlot);
            }
        }

        if (overBudget)
        {
            try
            {
                WarnBudgetExceeded(
                    warnOnly ? "RimLLM_BudgetWarnOnlyExceeded" : "RimLLM_BudgetHardBlockExceeded",
                    warnOnly ? BudgetWarnOnlyFallbackText : BudgetHardBlockFallbackText);
            }
            catch
            {
                reservation?.Dispose();
                reservation = null;
                throw;
            }
        }

        return !overBudget || warnOnly;
    }

    private bool IsBudgetExceededLocked()
    {
        if (_settings is IDailyTokenBudget tokenLedger)
        {
            long limit = tokenLedger.DailyTokenBudgetLimit;
            if (limit <= 0) return false;

            long accumulated = Math.Max(0, tokenLedger.DailyAccumulatedTokens);
            return accumulated >= limit || _reservedBudgetTokens >= limit - accumulated;
        }

        // 舊版外部設定只有美元帳本，沒有可供估算的 token 上限；保守地一次放行一個
        // 在途請求，待其美元用量結算後再讓下一個請求檢查，維持既有門檻語意。
        return _settings.DailyBudgetLimit > 0f &&
               (_settings.DailyAccumulatedCost >= _settings.DailyBudgetLimit || _reservedLegacyBudgetRequests > 0);
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private void ReleaseBudgetReservation(BudgetReservation reservation)
    {
        lock (UsageLock)
        {
            if (!reservation.TryReleaseAllLocked(out long released)) return;
            _reservedBudgetTokens = Math.Max(0, _reservedBudgetTokens - released);
            if (reservation.HasLegacySlot && _reservedLegacyBudgetRequests > 0)
            {
                _reservedLegacyBudgetRequests--;
            }
        }
    }

    internal sealed class BudgetReservation : IDisposable
    {
        private readonly bool _hasLegacySlot;
        private readonly long _reservedTokens;
        private bool _disposed;

        internal RimLLMUsageTracker Tracker { get; }
        internal bool HasLegacySlot => _hasLegacySlot;

        internal BudgetReservation(RimLLMUsageTracker tracker, long reservedTokens, bool hasLegacySlot)
        {
            Tracker = tracker;
            _reservedTokens = reservedTokens;
            _hasLegacySlot = hasLegacySlot;
        }

        internal void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
        {
            Tracker.RecordUsage(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens);
        }

        internal bool TryReleaseAllLocked(out long released)
        {
            if (_disposed)
            {
                released = 0;
                return false;
            }

            _disposed = true;
            released = _reservedTokens;
            return true;
        }

        public void Dispose()
        {
            Tracker.ReleaseBudgetReservation(this);
        }
    }

    /// <summary>
    /// 審查每日預算限額（輸入＋輸出 token 合計）。上限為 0 代表無限制。
    /// </summary>
    /// <remarks>
    /// WarnOnly 不攔截：超支要不要繼續是使用者自己的取捨，
    /// 框架的責任是把數字講清楚，而不是代替他決定要不要繼續花錢。
    /// </remarks>
    internal bool CheckBudgetLimit()
    {
        CheckDailyReset();

        bool overBudget;
        bool warnOnly;
        lock (UsageLock)
        {
            overBudget = IsBudgetExceededLocked();
            warnOnly = _settings.BudgetPolicy == BudgetPolicyWarnOnly;
        }

        if (!overBudget)
        {
            return true;
        }

        // 0=HardBlock, 1=WarnOnly
        if (warnOnly)
        {
            WarnBudgetExceeded("RimLLM_BudgetWarnOnlyExceeded", BudgetWarnOnlyFallbackText);
            return true;
        }

        // HardBlock 也要讓玩家知道是 RimLLM 預算擋下的，否則只會看到各 Mod 回報 QuotaExceeded。
        WarnBudgetExceeded("RimLLM_BudgetHardBlockExceeded", BudgetHardBlockFallbackText);
        return false;
    }

    /// <summary>
    /// 超限警告，每天只發一次。這個檢查每個請求（含串流）都會跑，
    /// 逐次輸出會把日誌洗成一片空白，反而讓真正要看的警告被淹沒。
    /// </summary>
    private void WarnBudgetExceeded(string translationKey, string fallbackText)
    {
        if (!ShouldWarnBudgetExceeded())
        {
            return;
        }

        RimLLMLog.Warning("[RimLLM] " + fallbackText);
        RimLLMDispatcher.EnqueueOnMainThread(() => ShowBudgetExceededMessage(translationKey, fallbackText));
    }

    /// <summary>
    /// 判斷這次超限是否該再次警告，並記下已警告的基準日。
    /// </summary>
    /// <remarks>
    /// 以重置基準日為去重鍵：跨日後自然會再警告一次。當日手動重置不會重新允許警告，
    /// 因為重置後預算已不再超限，沒有再吵一次的道理。
    /// </remarks>
    internal bool ShouldWarnBudgetExceeded()
    {
        string resetDate = _settings.DailyBudgetResetDate;
        lock (UsageLock)
        {
            if (_budgetWarnedDate == resetDate)
            {
                return false;
            }
            _budgetWarnedDate = resetDate;
            return true;
        }
    }

    /// <summary>
    /// 在主執行緒弹出遊戲訊息。這個檢查可能發生在背景執行緒（呼叫端自行決定 await 續行的位置），
    /// 而 <see cref="Messages"/> 是主執行緒狀態，直接呼叫並不安全。
    /// </summary>
    private static void ShowBudgetExceededMessage(string translationKey, string fallbackText)
    {
        try
        {
            // 語言資料尚未就緒時（單元測試、headless）跳過翻譯，改用英文字串。
            string text = LanguageDatabase.activeLanguage != null
                ? translationKey.Translate().ToString()
                : fallbackText;
            Messages.Message(text, MessageTypeDefOf.CautionInput, false);
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] In-game budget warning unavailable: {ex.Message}");
        }
    }

    #endregion
}
