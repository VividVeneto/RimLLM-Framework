using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager;

/// <summary>
/// SDK 的核心管理器。
/// 統一調度 API 供應商註冊、生命週期管理與公開外觀轉發。
/// 內部核心職責委託給：
/// - 對話中介層堆疊（見 <see cref="CreateChatClient"/>）
/// - 嵌入向量服務 (RimLLMEmbeddingService)
/// - 使用量與日誌追蹤器 (RimLLMUsageTracker)
/// - 備用管道 (RimLLMFallbackPipeline)
/// - 請求佇列 (RimLLMRequestQueue)
/// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
public class RimLLMManager
{
    private readonly IRimLLMSettings _settings;
    private readonly Dictionary<string, ILLMProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _providerOrder = [];
    private readonly HashSet<string> _builtInProviderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _providerLock = new();

    private readonly RimLLMUsageTracker _usageTracker;
    private readonly RimLLMEmbeddingService _embeddingService;
    private readonly RimLLMFallbackPipeline _fallbackPipeline;
    private readonly RimLLMThrottleStore _throttleStore;
    private readonly RimLLMRequestQueue _requestQueue;
    private readonly RimLLMHealthLedger _healthLedger;

    public RimLLMEmbeddingService EmbeddingService => _embeddingService;
    public RimLLMUsageTracker UsageTracker => _usageTracker;
    internal IRimLLMSettings Settings => _settings;

    /// <summary>
    /// 使用量統計日誌實體，保持結構以相容 Scribe 序列化。
    /// </summary>
    public class RequestLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ModId { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public long LatencyMs { get; set; }
        /// <summary>本次請求的輸入 token（供應商有回報才有值，否則為 0）。</summary>
        public int PromptTokens { get; set; }
        /// <summary>本次請求的輸出 token（供應商有回報才有值，否則為 0）。</summary>
        public int CompletionTokens { get; set; }
    }

    /// <summary>
    /// 提供外部 UI 查詢的呼叫日誌歷史記錄轉發。
    /// </summary>
    public ConcurrentQueue<RequestLogEntry> RequestLogs => _usageTracker.RequestLogs;

    public RimLLMManager(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        RimLLMLog.Enabled = _settings.DetailedLogging;

        // 建立子模組
        _requestQueue = new RimLLMRequestQueue(settings);
        _healthLedger = new RimLLMHealthLedger();
        _usageTracker = new RimLLMUsageTracker(settings);

        _fallbackPipeline = new RimLLMFallbackPipeline(
            settings,
            _healthLedger,
            _usageTracker,
            providerId => TryGetProvider(providerId, out var provider) ? provider : null,
            IsProviderEnabled);

        _throttleStore = new RimLLMThrottleStore(settings);
        // 節流狀態必須跨 client 共用：CreateChatClient 每次呼叫都組出全新的
        // 一疊中介層，狀態若跟著中介層走，換一個 client 就等同重置。

        // 初始化並註冊內建供應商
        RegisterBuiltInProvider(new OpenAIProvider(settings));
        RegisterBuiltInProvider(new GeminiProvider(settings));
        RegisterBuiltInProvider(new OpenAICompatibleProvider(settings));
        RegisterBuiltInProvider(new DeepSeekProvider(settings));
        RegisterBuiltInProvider(new GroqProvider(settings));
        RegisterBuiltInProvider(new GrokProvider(settings));
        RegisterBuiltInProvider(new OpenRouterProvider(settings));
        RegisterBuiltInProvider(new KimiProvider(settings));
        RegisterBuiltInProvider(new MiniMaxProvider(settings));
        RegisterBuiltInProvider(new QwenProvider(settings));
        RegisterBuiltInProvider(new NvidiaProvider(settings));
        RegisterBuiltInProvider(new ZaiProvider(settings));
        RegisterBuiltInProvider(new Player2Provider(settings));

        _embeddingService = new RimLLMEmbeddingService(settings);
    }

    private void RegisterBuiltInProvider(ILLMProvider provider)
    {
        lock (_providerLock)
        {
            _providers[provider.ProviderId] = provider;
            _providerOrder.Add(provider.ProviderId);
            _builtInProviderIds.Add(provider.ProviderId);
        }
    }

    /// <summary>
    /// 註冊外部供應商，供第三方 Mod 擴充自訂的 LLM 供應商。
    /// 外部供應商註冊後即視為啟用，使用者透過 Fallback Chain 控制其參與。
    /// </summary>
    /// <exception cref="InvalidOperationException">當 ProviderId 與既有供應商重複時擲出，防止覆蓋內建供應商。</exception>
    public void RegisterProvider(ILLMProvider provider)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrEmpty(provider.ProviderId))
            throw new ArgumentException("ProviderId cannot be empty or null", nameof(provider));

        lock (_providerLock)
        {
            if (_providers.ContainsKey(provider.ProviderId))
            {
                throw new InvalidOperationException($"[RimLLM] Provider ID '{provider.ProviderId}' is already registered and cannot be overridden.");
            }

            _providers[provider.ProviderId] = provider;
            _providerOrder.Add(provider.ProviderId);
        }
        RimLLMLog.Message($"[RimLLM] Registered external provider: {provider.ProviderId}");
    }

    /// <summary>
    /// 取得所有已註冊供應商的識別碼（依註冊順序）。
    /// </summary>
    public List<string> GetRegisteredProviderIds()
    {
        lock (_providerLock)
        {
            return new List<string>(_providerOrder);
        }
    }

    /// <summary>
    /// 所有符合資格候選能力的交集。與 <see cref="RimLLMFailoverChatClient"/> 用同一套
    /// 候選解析，但不做冷卻過濾：冷卻會到期，查詢當下被濾掉的候選仍可能在稍後的
    /// 請求中被路由到，交集若漏掉它就會過度樂觀。候選為空時例外原樣上拋。
    /// </summary>
    internal LLMProviderCapabilities GetEffectiveCapabilities(string preferredModelId)
    {
        if (!TryGetEffectiveCapabilities(preferredModelId, out LLMProviderCapabilities effective, out string failureReason))
        {
            throw new RimLLMException(LLMError.ProviderOffline, failureReason);
        }

        return effective;
    }

    /// <summary>
    /// 非拋版能力查詢，供相容層每秒輪詢使用，避免離線時每秒配置例外。
    /// 語意與 <see cref="GetEffectiveCapabilities(string)"/> 一致，僅以傳回值取代擲出。
    /// </summary>
    internal bool TryGetEffectiveCapabilities(string preferredModelId, out LLMProviderCapabilities effective, out string failureReason)
    {
        if (!_fallbackPipeline.TryResolveCandidates(
                preferredModelId,
                null,
                includeCoolingDown: true,
                out List<RimLLMFallbackPipeline.ResolvedCandidate> candidates,
                out failureReason,
                advanceRoundRobin: false))
        {
            effective = null;
            return false;
        }

        var result = new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = true,
            SupportsStreaming = true,
            SupportsUsageMetadata = true,
            SupportsFunctionCalling = true
        };
        foreach (RimLLMFallbackPipeline.ResolvedCandidate candidate in candidates)
        {
            LLMProviderCapabilities caps = candidate.Provider?.Capabilities ?? new LLMProviderCapabilities();
            result.SupportsNativeStructuredOutput &= caps.SupportsNativeStructuredOutput;
            result.SupportsStreaming &= caps.SupportsStreaming;
            result.SupportsUsageMetadata &= caps.SupportsUsageMetadata;
            result.SupportsFunctionCalling &= caps.SupportsFunctionCalling;
        }

        effective = result;
        failureReason = null;
        return true;
    }

    /// <summary>
    /// 取得指定供應商的能力描述。
    /// </summary>
    public LLMProviderCapabilities GetProviderCapabilities(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return new LLMProviderCapabilities();
        }

        lock (_providerLock)
        {
            if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
            {
                return new LLMProviderCapabilities();
            }

            // 供應商內部重用同一個能力實例，公開 API 交出複本，避免下游改動污染路由判斷。
            LLMProviderCapabilities caps = provider.Capabilities;
            return caps == null
                ? new LLMProviderCapabilities()
                : new LLMProviderCapabilities
                {
                    SupportsNativeStructuredOutput = caps.SupportsNativeStructuredOutput,
                    SupportsStreaming = caps.SupportsStreaming,
                    SupportsUsageMetadata = caps.SupportsUsageMetadata,
                    SupportsFunctionCalling = caps.SupportsFunctionCalling
                };
        }
    }

    /// <summary>
    /// 檢查供應商是否啟用。內建供應商由設定 UI 控制；外部註冊的供應商視為註冊即啟用。
    /// </summary>
    public bool IsProviderEnabled(string providerId)
    {
        bool isBuiltIn;
        lock (_providerLock)
        {
            if (!_providers.ContainsKey(providerId))
                return false;
            isBuiltIn = _builtInProviderIds.Contains(providerId);
        }
        return !isBuiltIn || _settings.IsProviderEnabled(providerId);
    }

    /// <summary>
    /// 執行緒安全地查找已註冊的供應商。
    /// </summary>
    private bool TryGetProvider(string providerId, out ILLMProvider provider)
    {
        lock (_providerLock)
        {
            return _providers.TryGetValue(providerId, out provider);
        }
    }

    internal IChatClient CreateChatClient(string modId)
    {
        if (string.IsNullOrEmpty(modId))
        {
            throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
        }

        // 由內往外堆疊。層序是語意性的，不可調換：
        // 正規化在最外層，下游各層看到的都是已套用預設值的選項；
        // 守衛（防濫用在先、預算在後）排在佇列之前，被擋下的請求不該佔用併發名額；
        // 併發名額不在這裡疊：由路由對每一個候選嘗試各包一層佇列，名額只留給真正
        // 要打 API 的那一次呼叫，重試前的指數退避不會占著名額讓其他 Mod 排隊。
        IChatClient client = RimLLMFailoverChatClient.Create(
            _settings, _healthLedger, _usageTracker, _fallbackPipeline, modId, _requestQueue);
        client = new RimLLMGuardChatClient(client, _settings, _throttleStore, modId, _usageTracker);
        client = new RimLLMOptionsNormalizingChatClient(client, _settings);
        return client;
    }

    /// <summary>建立綁定指定 Mod 的 embedding generator。modId 用於防濫用節流與遙測歸屬。</summary>
    internal RimLLMEmbeddingClient CreateEmbeddingGenerator(string modId)
    {
        if (string.IsNullOrEmpty(modId))
        {
            throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
        }
        return new RimLLMEmbeddingClient(this, modId);
    }

    /// <summary>執行指定 Mod 的防濫用檢查（供 embedding facade 於每次呼叫時使用）。</summary>
    internal void CheckAntiAbuseForMod(string modId)
    {
        if (_settings.EnableAntiAbuse)
        {
            _throttleStore.CheckAntiAbuse(modId);
        }
    }

    public async Task<TestResult> TestProviderAsync(string providerId)
    {
        if (!TryGetProvider(providerId, out ILLMProvider provider))
        {
            return new TestResult
            {
                Success = false,
                Provider = providerId,
                ErrorMessage = $"Unknown provider ID: {providerId}",
                ErrorCode = LLMError.ProviderOffline
            };
        }

        return await provider.TestConnectionAsync().ConfigureAwait(false);
    }

    public async Task<List<string>> FetchProviderModelsAsync(string providerId)
    {
        if (!TryGetProvider(providerId, out ILLMProvider provider))
        {
            throw new RimLLMException(LLMError.ProviderOffline, $"Unknown provider ID: {providerId}");
        }

        return await provider.FetchAvailableModelsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 同 <see cref="FetchProviderModelsAsync"/>，另附上下文上限：優先採用供應商 API 回報的值，
    /// 沒有的再以 models.dev 資料庫補齊。不是 <see cref="OpenAIProvider"/> 衍生的外部供應商沒有這項資訊。
    /// </summary>
    internal async Task<ModelCatalog> FetchProviderModelCatalogAsync(string providerId)
    {
        if (!TryGetProvider(providerId, out ILLMProvider provider))
        {
            throw new RimLLMException(LLMError.ProviderOffline, $"Unknown provider ID: {providerId}");
        }

        // models.dev 與供應商清單彼此獨立，先開始下載，不必等清單回來才發出請求
        var modelsDevTask = ModelsDevCatalog.IsCovered(providerId)
            ? ModelsDevCatalog.GetAllAsync(_settings.ApiTimeout)
            : null;

        ModelCatalog catalog = provider is OpenAIProvider openAiProvider && !OverridesModelList(provider)
            ? await openAiProvider.FetchModelCatalogAsync().ConfigureAwait(false)
            : new ModelCatalog(await provider.FetchAvailableModelsAsync().ConfigureAwait(false), null);

        // 供應商 API 已回報每個模型的上限時不等 models.dev：下載照樣完成並留在快取，但不拖慢這次重新整理
        if (modelsDevTask != null && !catalog.Models.TrueForAll(catalog.ContextWindows.ContainsKey))
        {
            ModelsDevCatalog.FillMissing(await modelsDevTask.ConfigureAwait(false), providerId, catalog.ContextWindows);
        }
        return catalog;
    }

    /// <summary>
    /// 外部 Mod 繼承 <see cref="OpenAIProvider"/> 並覆寫了 <see cref="ILLMProvider.FetchAvailableModelsAsync"/>
    /// （例如過濾或改名）時，必須走它的覆寫，不能改走基底的 <see cref="OpenAIProvider.FetchModelCatalogAsync"/>。
    /// </summary>
    private static bool OverridesModelList(ILLMProvider provider)
    {
        var method = provider.GetType().GetMethod(nameof(ILLMProvider.FetchAvailableModelsAsync), Type.EmptyTypes);
        return method != null && method.DeclaringType != typeof(OpenAIProvider);
    }

    /// <summary>
    /// 查詢 "ProviderId:ModelName" 的上下文上限。只接受明確指定的模型，其餘一律回傳 null。
    /// </summary>
    internal int? GetContextWindow(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        int colonIndex = modelId.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= modelId.Length - 1) return null;

        return (_settings as IContextWindowLookup)?.GetContextWindow(modelId.Substring(0, colonIndex), modelId.Substring(colonIndex + 1));
    }

    public void ClearLogs()
    {
        _usageTracker.ClearLogs();
    }

    public void ClearCooldowns()
    {
        _fallbackPipeline.ClearCooldowns();
        _throttleStore.ClearCooldowns();
    }

    public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
    {
        _usageTracker.RecordUsage(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens);
    }

    public void ResetUsage()
    {
        _usageTracker.ResetUsage();
    }
}
#pragma warning restore S101
