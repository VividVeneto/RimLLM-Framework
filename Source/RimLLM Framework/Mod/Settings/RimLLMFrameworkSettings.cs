using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimWorld;
using Verse;
#pragma warning disable S2365, S3260, S3267, S3459 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀

namespace RimLLM_Framework.Mod;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// RimWorld Mod 設定檔。
/// 將複雜的字典結構序列化為單一 JSON 字串儲存，並在序列化時調用 EncryptionUtility 加解密 API 金鑰。
/// </summary>
public class RimLLMFrameworkSettings : ModSettings, IRimLLMSettings, IContextWindowLookup, IDailyTokenBudget
{
    /// <summary>
    /// API 供應商的 Fallback Chain 順序。
    /// </summary>
    private List<string> _fallbackChain = [];

    public List<string> FallbackChain
    {
        get
        {
            lock (_settingsLock)
            {
                return new List<string>(_fallbackChain);
            }
        }
        set
        {
            lock (_settingsLock)
            {
                _fallbackChain = value != null ? new List<string>(value) : [];
            }
        }
    }

    // 可調節項 (全域配置)
    public float ApiTimeout { get; set; } = 30f;       // API 逾時時間 (秒)
    public int MaxRetries { get; set; } = 3;           // 單模型最多重試次數
    public float RetryDelay { get; set; } = 3f;        // 重試間隔 (秒)
    // 是否啟用詳細日誌。預設關閉：每次請求都會寫日誌，而 Verse 的日誌有全域 10000 筆上限，
    // 被高頻 Mod 使用時長時間遊玩會把上限用完，之後所有 Mod 的錯誤都不再被記錄。
    public bool DetailedLogging { get; set; } = false;
    public int MaxConcurrentRequests { get; set; } = 2; // 最大並行限制
    public ReasoningEffort? DefaultReasoningEffort { get; set; } = null;

    // 遙測資料（對話歷史、請求日誌、用量統計）獨立存放於 JSON 檔案，不寫入設定 XML
    private readonly RimLLMTelemetryStore _telemetry = new();
    public List<string> ChatHistory
    {
        get => _telemetry.ChatHistory;
        set => _telemetry.ChatHistory = value ?? [];
    }
    public List<RimLLMManager.RequestLogEntry> RequestLogs
    {
        get => _telemetry.RequestLogs;
        set => _telemetry.RequestLogs = value ?? [];
    }
    public long TotalPromptTokens
    {
        get => _telemetry.TotalPromptTokens;
        set => _telemetry.TotalPromptTokens = value;
    }
    public long TotalCompletionTokens
    {
        get => _telemetry.TotalCompletionTokens;
        set => _telemetry.TotalCompletionTokens = value;
    }
    public float TotalEstimatedCost
    {
        get => _telemetry.TotalEstimatedCost;
        set => _telemetry.TotalEstimatedCost = value;
    }

    /// <summary>每日 token 預算上限（輸入＋輸出合計）。0 代表無限制，存於設定 XML。</summary>
    public long DailyTokenBudgetLimit { get; set; } = 0;

    /// <summary>舊版美元門檻僅為介面相容而保留；內建設定改看 token 預算，恆為 0 且不存檔。</summary>
    float IRimLLMSettings.DailyBudgetLimit { get => 0f; set { /* 介面相容性：已棄用且不儲存 */ } }

    /// <summary>
    /// 最大合法的預算超限應對策略值。新增策略時要同步更新這裡與
    /// <see cref="BudgetSettingsDrawer"/> 的策略名稱清單。
    /// </summary>
    public const int MaxBudgetPolicy = 1; // 0=HardBlock, 1=WarnOnly

    public int BudgetPolicy { get; set; } = 0; // 0 = HardBlock
    public bool EnableAntiAbuse { get; set; } = true;
    public int MaxRequestsPerWindow { get; set; } = 10;
    public int ThrottlingWindowSeconds { get; set; } = 10;
    public int CoolDownDurationSeconds { get; set; } = 60;
    /// <summary>
    /// 最大合法的路由策略值。新增策略時要同步更新這裡、
    /// <see cref="FallbackSettingsDrawer"/> 的策略名稱清單與 RimLLMFallbackPipeline 的分派。
    /// </summary>
    public const int MaxRoutingStrategy = 3; // 0=PriorityFailover, 1=MinLatency, 2=RoundRobin, 3=LowestCost

    public int RoutingStrategy { get; set; } = 2;
    public bool EnableNativeSchema { get; set; } = true;
    public bool EnableJsonRepair { get; set; } = true;

    public const string DisabledProvider = "Disabled";
    public string EmbeddingProvider { get; set; } = DisabledProvider;

    public string EmbeddingModel
    {
        get => GetEmbeddingModel(EmbeddingProvider);
        set => SetEmbeddingModel(EmbeddingProvider, value);
    }

    public string EmbeddingEndpoint
    {
        get => GetEmbeddingEndpoint(EmbeddingProvider);
        set => SetEmbeddingEndpoint(EmbeddingProvider, value);
    }

    public string EmbeddingApiKey
    {
        get => GetEmbeddingApiKey(EmbeddingProvider);
        set => SetEmbeddingApiKey(EmbeddingProvider, value);
    }

    public float DailyAccumulatedCost
    {
        get => _telemetry.DailyAccumulatedCost;
        set => _telemetry.DailyAccumulatedCost = value;
    }
    /// <summary>當日已累計 token（輸入＋輸出合計），存於遙測檔，每日跨天重置。</summary>
    public long DailyAccumulatedTokens
    {
        get => _telemetry.DailyAccumulatedTokens;
        set => _telemetry.DailyAccumulatedTokens = value;
    }
    public string DailyBudgetResetDate
    {
        get => _telemetry.DailyBudgetResetDate;
        set => _telemetry.DailyBudgetResetDate = value ?? "";
    }

    public RimLLMFrameworkSettings()
    {
        _telemetry.Load();
    }

    /// <summary>
    /// 將遙測資料（對話歷史、請求日誌、用量統計）寫入獨立 JSON 檔案。
    /// 設定本體請使用 Write()。
    /// </summary>
    public void SaveTelemetry()
    {
        _telemetry.Save();
    }

    /// <summary>
    /// 把遙測寫檔交給背景單寫者，不在呼叫端執行緒做 AES + JSON + 磁碟寫入。
    /// 先標記待寫入：背景寫檔失敗或來不及完成時，由關閉時的 <see cref="FlushTelemetryIfDirty"/> 補上。
    /// </summary>
    public void QueueTelemetrySave()
    {
        _telemetry.MarkDirty();
        RimLLMUsageTracker.QueueTelemetrySave(this);
    }

    /// <summary>
    /// 清空對話歷史；若先前因 key 不可用而保留了密文，也一併明確捨棄。
    /// </summary>
    public void ClearChatHistory()
    {
        _telemetry.ClearChatHistory();
    }

    /// <summary>
    /// 標記遙測有未寫入的變更（供節流路徑呼叫）。
    /// </summary>
    public void MarkTelemetryDirty()
    {
        _telemetry.MarkDirty();
    }

    /// <summary>
    /// 僅在有未寫入變更時才實際寫檔，供關閉遊戲時的強制 flush 使用。
    /// </summary>
    public void FlushTelemetryIfDirty()
    {
        if (_telemetry.IsDirty)
        {
            _telemetry.Save();
        }
    }

    private readonly object _settingsLock = new();
    private readonly Dictionary<string, string> _apiKeys = [];

    /// <summary>
    /// 載入時無法解密的 provider 金鑰密文（providerId → 原始密文）。
    /// 存檔時原樣寫回，避免無法取得原使用者的 OS 保護金鑰時被靜默清空。
    /// </summary>
    private readonly Dictionary<string, string> _undecryptableApiKeys = [];
    private readonly Dictionary<string, int> _apiKeyIndices = [];
    private readonly Dictionary<string, string> _endpoints = [];
    private readonly Dictionary<string, int> _modelLevelOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 重新整理模型清單時 API 回報的上下文上限（providerId → 模型名稱 → token 數）。
    /// 以供應商分開存放：同名模型在不同供應商的上限可能不同。
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, int>> _providerContextWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>玩家手動填寫的上下文上限（"Provider:Model" → token 數），優先於 API 回報的值。</summary>
    private readonly Dictionary<string, int> _contextWindowOverrides = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _embeddingModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _embeddingEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _embeddingApiKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _undecryptableEmbeddingApiKeys = new(StringComparer.Ordinal);
    private string _undecryptableEmbeddingApiKey;
    private string _undecryptableEmbeddingApiKeyProvider;

    /// <summary>中國端點切換旗標。鍵由 <see cref="ProviderIds.HasChinaEndpoint"/> 決定，不另外手寫一份清單。</summary>
    private readonly Dictionary<string, bool> _chinaModeProviders = BuildChinaModeMap();

    private static Dictionary<string, bool> BuildChinaModeMap()
    {
        var map = new Dictionary<string, bool>();
        foreach (string providerId in ProviderIds.BuiltIn)
        {
            if (ProviderIds.HasChinaEndpoint(providerId)) map[providerId] = false;
        }
        return map;
    }

    /// <summary>
    /// 內建供應商的啟用旗標與模型清單快取。兩份字典的鍵一律等同 <see cref="ProviderIds.BuiltIn"/>，
    /// 因此直接由該清單產生 —— 先前是各自手寫一份 12 筆的字面清單，新增供應商得同步改三處。
    /// </summary>
    private readonly Dictionary<string, bool> _enabledProviders = BuildBuiltInMap(_ => false);

    private readonly Dictionary<string, List<string>> _providerModels = BuildBuiltInMap(_ => new List<string>());

    /// <summary>
    /// 模型清單的變更版本號。清單只在抓取模型或載入設定時變動，設定頁以此判斷
    /// 過濾結果快取是否仍有效，不必每幀複製整份清單來比對。
    /// </summary>
    private int _modelListVersion;
    public int ModelListVersion
    {
        get { lock (_settingsLock) { return _modelListVersion; } }
    }

    /// <summary>
    /// 第三方 Mod 強制接管登錄表（packageId → 是否把該 Mod 的 LLM 流量導向 RimLLM）。
    /// 預設全關；沒有登錄的 Mod 一律視為關閉。鍵由 Compat 層各目標自行宣告。
    /// </summary>
    private readonly Dictionary<string, bool> _compatTakeovers = new(StringComparer.Ordinal);

    private static Dictionary<string, TValue> BuildBuiltInMap<TValue>(Func<string, TValue> valueFactory)
    {
        var map = new Dictionary<string, TValue>();
        foreach (string providerId in ProviderIds.BuiltIn)
        {
            map[providerId] = valueFactory(providerId);
        }
        return map;
    }

    /// <summary>
    /// 用於 JSON 序列化與反序列化的 DTO 結構，避開 RimWorld Scribe 字典嵌套序列化的兼容問題。
    /// ChatHistory / RequestLogs / Total* 欄位僅保留供舊版設定遷移讀取，新版不再寫入。
    /// </summary>
#pragma warning disable 0649 // 遷移用欄位僅由 JSON 反序列化賦值
    private class SettingsDto
    {
        public List<string> FallbackChain;
        public Dictionary<string, string> EncryptedApiKeys;
        public Dictionary<string, string> Endpoints;
        public Dictionary<string, bool> EnabledProviders;
        public Dictionary<string, List<string>> ProviderModels;
        public Dictionary<string, bool> ChinaModeProviders;
        public Dictionary<string, int> ModelLevelOverrides;
        public Dictionary<string, Dictionary<string, int>> ProviderContextWindows;
        public Dictionary<string, int> ContextWindowOverrides;
        public float ApiTimeout;
        public int MaxRetries;
        public float RetryDelay;
        public bool DetailedLogging;
        public int MaxConcurrentRequests;
        public List<string> ChatHistory;
        public List<RimLLMManager.RequestLogEntry> RequestLogs;
        public int DefaultReasoningEffort;
        public long TotalPromptTokens;
        public long TotalCompletionTokens;
        public float TotalEstimatedCost;
        public long DailyTokenBudgetLimit;
        /// <summary>
        /// 舊版美元上限，只讀不寫：新版存檔寫出 null，因此有值即代表這是舊版存檔（見載入處的遷移）。
        /// </summary>
        public float? DailyBudgetLimit;
        public int BudgetPolicy;
        public bool EnableAntiAbuse;
        public int MaxRequestsPerWindow;
        public int ThrottlingWindowSeconds;
        public int CoolDownDurationSeconds;
        public int RoutingStrategy = 2;
        public bool EnableNativeSchema = true;
        public bool EnableJsonRepair = true;
        public string EncryptedEmbeddingApiKey;
        public string EmbeddingProvider = DisabledProvider;
        public string EmbeddingModel = "gemini-embedding-2";
        public string EmbeddingEndpoint = "";
        public string EmbeddingApiKey = "";
        public Dictionary<string, string> EmbeddingModels;
        public Dictionary<string, string> EmbeddingEndpoints;
        public Dictionary<string, string> EncryptedEmbeddingApiKeys;
        public Dictionary<string, bool> CompatTakeovers;

        /// <summary>設定格式版本，供一次性遷移判斷；舊檔沒有這個欄位，讀成 0。</summary>
        public int SettingsVersion;
    }

    /// <summary>
    /// 目前的設定格式版本。
    /// 1：DetailedLogging 預設改為關閉。版本 0 的檔案裡的 true 是舊預設值寫進去的，
    /// 無法與玩家主動開啟區分，因此載入時一律重設為關閉一次。
    /// </summary>
    private const int CurrentSettingsVersion = 1;
#pragma warning restore 0649
#pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本

    public override void ExposeData()
    {
        base.ExposeData();

        string jsonStr = "";
        lock (_settingsLock)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var encryptedKeys = new Dictionary<string, string>();
                foreach (var kvp in _apiKeys)
                {
                    if (TryEncryptForSave(kvp.Value, out string cipher))
                    {
                        encryptedKeys[kvp.Key] = cipher;
                    }
                }

                // 載入時解不開的金鑰原樣寫回密文。若不這麼做，這些 provider 不在 _apiKeys 中，
                // 存檔後其密文就永久消失（使用者換回原本裝置也救不回來）。
                foreach (var kvp in _undecryptableApiKeys)
                {
                    if (!encryptedKeys.ContainsKey(kvp.Key))
                    {
                        encryptedKeys[kvp.Key] = kvp.Value;
                    }
                }

                var encryptedEmbeddingKeys = new Dictionary<string, string>();
                foreach (var kvp in _embeddingApiKeys)
                {
                    if (TryEncryptForSave(kvp.Value, out string cipher))
                    {
                        encryptedEmbeddingKeys[kvp.Key] = cipher;
                    }
                }
                foreach (var kvp in _undecryptableEmbeddingApiKeys)
                {
                    if (!encryptedEmbeddingKeys.ContainsKey(kvp.Key))
                    {
                        encryptedEmbeddingKeys[kvp.Key] = kvp.Value;
                    }
                }
                if (!string.IsNullOrEmpty(_undecryptableEmbeddingApiKey) &&
                    !string.IsNullOrEmpty(_undecryptableEmbeddingApiKeyProvider) &&
                    !_embeddingApiKeys.ContainsKey(_undecryptableEmbeddingApiKeyProvider) &&
                    !encryptedEmbeddingKeys.ContainsKey(_undecryptableEmbeddingApiKeyProvider))
                {
                    // 舊版只有一個 active provider 欄位；切換 provider 後改放入 map，避免密文遺失。
                    encryptedEmbeddingKeys[_undecryptableEmbeddingApiKeyProvider] = _undecryptableEmbeddingApiKey;
                }

                bool preserveUndecryptableEmbeddingApiKey =
                    !string.IsNullOrEmpty(_undecryptableEmbeddingApiKey) &&
                    !string.IsNullOrEmpty(_undecryptableEmbeddingApiKeyProvider) &&
                    string.Equals(this.EmbeddingProvider, _undecryptableEmbeddingApiKeyProvider, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(this.EmbeddingProvider) &&
                    !_embeddingApiKeys.ContainsKey(this.EmbeddingProvider);
                string encryptedEmbeddingApiKey;
                if (preserveUndecryptableEmbeddingApiKey)
                {
                    encryptedEmbeddingApiKey = _undecryptableEmbeddingApiKey;
                }
                else if (!TryEncryptForSave(this.EmbeddingApiKey ?? "", out encryptedEmbeddingApiKey))
                {
                    encryptedEmbeddingApiKey = null;
                }

                var dto = new SettingsDto
                {
                    FallbackChain = this.FallbackChain,
                    EncryptedApiKeys = encryptedKeys,
                    Endpoints = this._endpoints,
                    EnabledProviders = this._enabledProviders,
                    ProviderModels = this._providerModels,
                    ApiTimeout = this.ApiTimeout,
                    MaxRetries = this.MaxRetries,
                    RetryDelay = this.RetryDelay,
                    ChinaModeProviders = this._chinaModeProviders,
                    ModelLevelOverrides = new Dictionary<string, int>(this._modelLevelOverrides),
                    ProviderContextWindows = this._providerContextWindows,
                    ContextWindowOverrides = new Dictionary<string, int>(this._contextWindowOverrides),
                    DetailedLogging = this.DetailedLogging,
                    MaxConcurrentRequests = this.MaxConcurrentRequests,
                    DefaultReasoningEffort = EncodeReasoningEffort(this.DefaultReasoningEffort),
                    DailyTokenBudgetLimit = this.DailyTokenBudgetLimit,
                    BudgetPolicy = this.BudgetPolicy,
                    EnableAntiAbuse = this.EnableAntiAbuse,
                    MaxRequestsPerWindow = this.MaxRequestsPerWindow,
                    ThrottlingWindowSeconds = this.ThrottlingWindowSeconds,
                    CoolDownDurationSeconds = this.CoolDownDurationSeconds,
                    RoutingStrategy = this.RoutingStrategy,
                    EnableNativeSchema = this.EnableNativeSchema,
                    EnableJsonRepair = this.EnableJsonRepair,
                    EmbeddingProvider = this.EmbeddingProvider,
                    EmbeddingModel = this.EmbeddingModel,
                    EmbeddingEndpoint = this.EmbeddingEndpoint,
                    // Embedding 金鑰與 provider 金鑰採同一套加密；明文欄位明確寫 null 以清除舊資料。
                    EncryptedEmbeddingApiKey = encryptedEmbeddingApiKey,
                    EmbeddingApiKey = null,
                    EmbeddingModels = new Dictionary<string, string>(this._embeddingModels),
                    EmbeddingEndpoints = new Dictionary<string, string>(this._embeddingEndpoints),
                    EncryptedEmbeddingApiKeys = encryptedEmbeddingKeys,
                    CompatTakeovers = new Dictionary<string, bool>(this._compatTakeovers),
                    SettingsVersion = CurrentSettingsVersion
                };

                jsonStr = RimLLMJsonHelper.Serialize(dto);
                Scribe_Values.Look(ref jsonStr, "SettingsData", "");
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Values.Look(ref jsonStr, "SettingsData", "");
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    try
                    {
                        var dto = RimLLMJsonHelper.Deserialize<SettingsDto>(jsonStr);
                        if (dto != null)
                        {
                            if (dto.FallbackChain != null)
                            {
                                var fallbackChain = new List<string>(dto.FallbackChain);
                                fallbackChain.RemoveAll(entry => string.IsNullOrEmpty(entry));
                                this.FallbackChain = fallbackChain;
                            }
                            if (dto.Endpoints != null)
                            {
                                // 移除 Clear，直接覆寫，保留新版本預設值
                                foreach (var kvp in dto.Endpoints) this._endpoints[kvp.Key] = kvp.Value;
                            }
                            if (dto.EnabledProviders != null)
                            {
                                // 移除 Clear，直接覆寫，保留新版本預設值
                                foreach (var kvp in dto.EnabledProviders) this._enabledProviders[kvp.Key] = kvp.Value;
                            }
                            if (dto.ProviderModels != null)
                            {
                                // 移除 Clear，直接覆寫，保留新版本預設值
                                foreach (var kvp in dto.ProviderModels) this._providerModels[kvp.Key] = kvp.Value;
                                _modelListVersion++;
                            }

                            // _apiKeys 依然可以 Clear，因為這是完全由用戶配置決定
                            _apiKeys.Clear();
                            _undecryptableApiKeys.Clear();
                            if (dto.EncryptedApiKeys != null)
                            {
                                foreach (var kvp in dto.EncryptedApiKeys)
                                {
                                    string plain = EncryptionUtility.Decrypt(kvp.Value);
                                    if (plain == null)
                                    {
                                        // 解不開（通常是換了裝置）：保留原始密文，不要放進 _apiKeys，
                                        // 以免下次存檔把它覆寫成空字串而永久遺失。
                                        _undecryptableApiKeys[kvp.Key] = kvp.Value;
                                    }
                                    else
                                    {
                                        _apiKeys[kvp.Key] = plain;
                                    }
                                }
                            }

                            if (dto.ChinaModeProviders != null)
                            {
                                // 移除 Clear，直接覆寫，保留新版本預設值
                                foreach (var kvp in dto.ChinaModeProviders) this._chinaModeProviders[kvp.Key] = kvp.Value;
                            }

                            if (dto.ModelLevelOverrides != null)
                            {
                                _modelLevelOverrides.Clear();
                                foreach (var kvp in dto.ModelLevelOverrides) _modelLevelOverrides[kvp.Key] = kvp.Value;
                            }

                            if (dto.ProviderContextWindows != null)
                            {
                                _providerContextWindows.Clear();
                                foreach (var kvp in dto.ProviderContextWindows)
                                {
                                    if (kvp.Value != null) SetProviderContextWindows(kvp.Key, kvp.Value);
                                }
                            }

                            if (dto.ContextWindowOverrides != null)
                            {
                                _contextWindowOverrides.Clear();
                                foreach (var kvp in dto.ContextWindowOverrides) SetContextWindowOverride(kvp.Key, kvp.Value);
                            }

                            this.DefaultReasoningEffort = DecodeReasoningEffort(dto.DefaultReasoningEffort);

                            // 舊版設定 XML 內嵌的遙測資料：若獨立遙測檔尚不存在，執行一次性遷移
                            if (!_telemetry.LoadedFromDisk &&
                                (dto.ChatHistory != null || dto.RequestLogs != null ||
                                 dto.TotalPromptTokens > 0 || dto.TotalCompletionTokens > 0 || dto.TotalEstimatedCost > 0f))
                            {
                                if (dto.ChatHistory != null) this.ChatHistory = dto.ChatHistory;
                                if (dto.RequestLogs != null) this.RequestLogs = dto.RequestLogs;
                                this.TotalPromptTokens = dto.TotalPromptTokens;
                                this.TotalCompletionTokens = dto.TotalCompletionTokens;
                                this.TotalEstimatedCost = dto.TotalEstimatedCost;
                                SaveTelemetry();
                                RimLLMLog.Message("[RimLLM] Migrated telemetry from the legacy settings XML to RimLLM_Telemetry.json.");
                            }

                            // 載入可調節項 (全域配置) 並防呆
                            this.ApiTimeout = dto.ApiTimeout <= 0f ? 30f : dto.ApiTimeout;
                            this.MaxRetries = dto.MaxRetries < 0 ? 3 : dto.MaxRetries;
                            this.RetryDelay = dto.RetryDelay < 0f ? 3f : dto.RetryDelay;
                            // 版本 0 的 true 來自舊預設值（見 CurrentSettingsVersion），重設為新預設一次。
                            this.DetailedLogging = dto.SettingsVersion >= 1 && dto.DetailedLogging;
                            this.MaxConcurrentRequests = dto.MaxConcurrentRequests <= 0 ? 2 : dto.MaxConcurrentRequests;
                            RimLLMLog.Enabled = this.DetailedLogging;

                            this.DailyTokenBudgetLimit = dto.DailyTokenBudgetLimit < 0 ? 0 : dto.DailyTokenBudgetLimit;
                            // 舊版存檔（以美元計的每日預算）：美元無法換算成 token，上限無法沿用，
                            // 必須明確告知玩家預算已失效，不能讓保護在升級後無聲消失。
                            bool legacyBudgetSave = dto.DailyBudgetLimit.HasValue;
                            if (legacyBudgetSave && dto.DailyBudgetLimit.Value > 0f)
                            {
                                RimLLMLog.Warning(
                                    $"[RimLLM] The daily budget is now counted in tokens; the old USD limit ({dto.DailyBudgetLimit.Value:F2}) cannot be converted and was removed. Set a token budget under Budget & Safety.");
                                NotifyBudgetSettingReset("RimLLM_MsgLegacyBudgetLimitDropped");
                            }

                            int storedPolicy = dto.BudgetPolicy;
                            // 舊版的 1 是 SilentMocking（超限不送出），與新版 1 = WarnOnly（照常送出）撞號，不得沿用成放行。
                            bool legacySilentMocking = legacyBudgetSave && storedPolicy == 1;
                            if (legacySilentMocking || SanitizeBudgetPolicy(storedPolicy) != storedPolicy)
                            {
                                // 這是行為變更：原本的軟性策略會變成硬性阻斷，必須讓使用者自己知道原因，
                                // 否則他只會看到 AI 突然回報 QuotaExceeded 而找不到緣由。
                                RimLLMLog.Warning(
                                    $"[RimLLM] Unsupported budget policy {storedPolicy} in settings; reset to HardBlock (0).");
                                NotifyBudgetSettingReset("RimLLM_MsgBudgetPolicyReset");
                            }
                            this.BudgetPolicy = legacySilentMocking ? 0 : SanitizeBudgetPolicy(storedPolicy);
                            this.EnableAntiAbuse = dto.EnableAntiAbuse;
                            // Use 10 as default if the value is <= 0 for MaxRequestsPerWindow and ThrottlingWindowSeconds
                            this.MaxRequestsPerWindow = dto.MaxRequestsPerWindow <= 0 ? 10 : dto.MaxRequestsPerWindow;
                            this.ThrottlingWindowSeconds = dto.ThrottlingWindowSeconds <= 0 ? 10 : dto.ThrottlingWindowSeconds;
                            this.CoolDownDurationSeconds = dto.CoolDownDurationSeconds < 0 ? 60 : dto.CoolDownDurationSeconds;
                            // 超出範圍的策略值退回 PriorityFailover，避免未知值悄悄改變路由行為
                            this.RoutingStrategy = dto.RoutingStrategy < 0 || dto.RoutingStrategy > MaxRoutingStrategy ? 0 : dto.RoutingStrategy;
                            this.EnableNativeSchema = dto.EnableNativeSchema;
                            this.EnableJsonRepair = dto.EnableJsonRepair;
                            this.EmbeddingProvider = string.IsNullOrEmpty(dto.EmbeddingProvider) ? DisabledProvider : dto.EmbeddingProvider;
                            if (dto.EmbeddingModels != null)
                            {
                                foreach (var kvp in dto.EmbeddingModels) this._embeddingModels[kvp.Key] = kvp.Value;
                            }
                            else if (!string.IsNullOrEmpty(dto.EmbeddingModel) && this.EmbeddingProvider != DisabledProvider)
                            {
                                this._embeddingModels[this.EmbeddingProvider] = dto.EmbeddingModel;
                            }

                            if (dto.EmbeddingEndpoints != null)
                            {
                                foreach (var kvp in dto.EmbeddingEndpoints) this._embeddingEndpoints[kvp.Key] = kvp.Value;
                            }
                            else if (!string.IsNullOrEmpty(dto.EmbeddingEndpoint) && this.EmbeddingProvider != DisabledProvider)
                            {
                                this._embeddingEndpoints[this.EmbeddingProvider] = dto.EmbeddingEndpoint;
                            }

                            _compatTakeovers.Clear();
                            if (dto.CompatTakeovers != null)
                            {
                                foreach (var kvp in dto.CompatTakeovers) _compatTakeovers[kvp.Key] = kvp.Value;
                            }

                            _embeddingApiKeys.Clear();
                            _undecryptableEmbeddingApiKeys.Clear();
                            _undecryptableEmbeddingApiKey = null;
                            _undecryptableEmbeddingApiKeyProvider = null;
                            if (dto.EncryptedEmbeddingApiKeys != null)
                            {
                                foreach (var kvp in dto.EncryptedEmbeddingApiKeys)
                                {
                                    string plain = EncryptionUtility.Decrypt(kvp.Value);
                                    if (plain == null)
                                    {
                                        _undecryptableEmbeddingApiKeys[kvp.Key] = kvp.Value;
                                    }
                                    else
                                    {
                                        _embeddingApiKeys[kvp.Key] = plain;
                                    }
                                }
                            }
                            else if (!string.IsNullOrEmpty(dto.EncryptedEmbeddingApiKey))
                            {
                                string embeddingProvider = this.EmbeddingProvider;
                                string plain = EncryptionUtility.Decrypt(dto.EncryptedEmbeddingApiKey);
                                if (plain != null && embeddingProvider != DisabledProvider)
                                {
                                    _embeddingApiKeys[embeddingProvider] = plain;
                                }
                                else if (plain == null &&
                                         !string.IsNullOrEmpty(embeddingProvider) &&
                                         embeddingProvider != DisabledProvider)
                                {
                                    _undecryptableEmbeddingApiKey = dto.EncryptedEmbeddingApiKey;
                                    _undecryptableEmbeddingApiKeyProvider = embeddingProvider;
                                }
                            }
                            else if (!string.IsNullOrEmpty(dto.EmbeddingApiKey) && this.EmbeddingProvider != DisabledProvider)
                            {
                                _embeddingApiKeys[this.EmbeddingProvider] = dto.EmbeddingApiKey;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RimLLMLog.Error($"[RimLLM] Failed to load settings: {ex.Message}");
                    }
                }
            }
        }
    }
#pragma warning restore S3776

    /// <summary>
    /// 設定檔中的預算策略值正規化。已移除的策略（SilentMocking / FallbackToFree / DialogPrompt）
    /// 與未知值一律退回 HardBlock：不得靜默變成放行，否則舊存檔升級後會在無預警的情況下繼續燒 token。
    /// 舊版 SilentMocking 的值 1 與新版 WarnOnly 撞號，單看數值無法分辨，由載入處依舊版存檔另行處理。
    /// </summary>
    internal static int SanitizeBudgetPolicy(int stored)
    {
        return stored < 0 || stored > MaxBudgetPolicy ? 0 : stored;
    }

    /// <summary>
    /// 設定值被重設時的一次性提示。翻譯與訊息佇列都依賴 Defs 與語言資料，
    /// 而 ExposeData 在載入階段就會執行，因此延後到主執行緒的第一幀再送出。
    /// </summary>
    private static void NotifyBudgetSettingReset(string translationKey)
    {
        RimLLMDispatcher.EnqueueOnMainThread(() =>
        {
            try
            {
                Messages.Message(translationKey.Translate(), MessageTypeDefOf.CautionInput, false);
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Budget setting reset notice unavailable: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 存檔路徑專用的加密：失敗時回傳 false 並略過該金鑰，而不是讓例外穿出 <see cref="ExposeData"/>。
    /// 這裡是在 Scribe 存檔中途被呼叫的，例外一旦冒出去，整份設定（備援鏈、端點、開關）都會存不下來；
    /// 系統鑰匙圈可能鎖定或無法連線，DPAPI 也可能因使用者設定檔不可讀寫而失敗。
    /// 金鑰仍留在記憶體中，加密恢復正常後的下一次存檔就會寫回。
    /// </summary>
    internal static bool TryEncryptForSave(string plainText, out string cipherText)
    {
        try
        {
            cipherText = EncryptionUtility.Encrypt(plainText);
            return true;
        }
        catch (Exception ex)
        {
            cipherText = null;
            RimLLMLog.Error($"[RimLLM] API key could not be encrypted and was not written to settings; other settings were saved. Reason: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 預設思考強度的存檔編碼：0 代表 Auto（null），其餘為列舉值 + 1。
    /// 與既有設定檔相容：舊檔的 2／3 仍讀成 Low／Medium。
    /// </summary>
    internal static int EncodeReasoningEffort(ReasoningEffort? effort)
    {
        return effort == null ? 0 : (int)effort.Value + 1;
    }

    /// <summary>
    /// <see cref="EncodeReasoningEffort"/> 的反向。合法範圍以列舉實際定義為準，不再寫死上限：
    /// MEAI 10.10 的 ReasoningEffort 是 None=0…ExtraHigh=4，先前寫死的「&gt; 3 即無效」
    /// 讓 High（存成 4）每次載入都被丟回 Auto。
    /// </summary>
    internal static ReasoningEffort? DecodeReasoningEffort(int encoded)
    {
        if (encoded <= 0) return null;
        int value = encoded - 1;
        return Enum.IsDefined(typeof(ReasoningEffort), value) ? (ReasoningEffort?)(ReasoningEffort)value : null;
    }

    public string GetApiKey(string providerId)
    {
        lock (_settingsLock)
        {
            return _apiKeys.TryGetValue(providerId, out string val) ? val : "";
        }
    }

    /// <summary>
    /// 多把金鑰的分隔字元。輪替與設定頁的金鑰列表必須用同一組：
    /// 先前設定頁只認逗號，含分號的金鑰字串在畫面上是一把、實際卻被當成兩把輪替。
    /// </summary>
    internal static readonly char[] ApiKeySeparators = [',', ';'];

    public string GetActiveApiKey(string providerId)
    {
        lock (_settingsLock)
        {
            if (!_apiKeys.TryGetValue(providerId, out string raw) || string.IsNullOrEmpty(raw)) return "";

            var keys = raw.Split(ApiKeySeparators, StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length == 0) return "";
            if (keys.Length == 1) return keys[0].Trim();

            if (!_apiKeyIndices.TryGetValue(providerId, out int index))
            {
                index = 0;
            }

            string selected = keys[index % keys.Length].Trim();
            _apiKeyIndices[providerId] = (index + 1) % keys.Length;
            return selected;
        }
    }

    public void SetApiKey(string providerId, string val)
    {
        lock (_settingsLock)
        {
            _apiKeys[providerId] = val;
        }
    }

    public string GetEndpoint(string providerId, string defaultVal)
    {
        lock (_settingsLock)
        {
            bool isChina = _chinaModeProviders.TryGetValue(providerId, out bool china) && china;
            string resolvedDefault = ResolveRegionalDefaultEndpoint(providerId, isChina) ?? defaultVal;

            return _endpoints.TryGetValue(providerId, out string val) && !string.IsNullOrEmpty(val)
                ? val
                : resolvedDefault;
        }
    }

    /// <summary>
    /// 有中國／國際兩組網域的供應商的預設端點。其餘供應商回傳 null，由呼叫端沿用傳入的預設值。
    /// </summary>
    private static string ResolveRegionalDefaultEndpoint(string providerId, bool isChina)
    {
        return providerId switch
        {
            ProviderIds.MiniMax => isChina ? "https://api.minimaxi.com/v1" : "https://api.minimax.io/v1",
            ProviderIds.Qwen => isChina
                                    ? "https://dashscope.aliyuncs.com/compatible-mode/v1"
                                    : "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
            ProviderIds.Kimi => isChina ? "https://api.moonshot.cn/v1" : "https://api.moonshot.ai/v1",
            _ => null,
        };
    }

    public bool IsChinaMode(string providerId)
    {
        lock (_settingsLock)
        {
            return _chinaModeProviders.TryGetValue(providerId, out bool val) && val;
        }
    }

    public void SetChinaMode(string providerId, bool val)
    {
        lock (_settingsLock)
        {
            _chinaModeProviders[providerId] = val;
        }
    }

    public void SetEndpoint(string providerId, string val)
    {
        lock (_settingsLock)
        {
            _endpoints[providerId] = val;
        }
    }

    /// <summary>指定第三方 Mod 是否被強制接管。未登錄一律 false。</summary>
    public bool IsCompatTakeoverEnabled(string modId)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(modId) && _compatTakeovers.TryGetValue(modId, out bool enabled) && enabled;
        }
    }

    public void SetCompatTakeoverEnabled(string modId, bool enabled)
    {
        if (string.IsNullOrEmpty(modId)) return;
        lock (_settingsLock)
        {
            _compatTakeovers[modId] = enabled;
        }
    }

    public bool IsProviderEnabled(string providerId)
    {
        lock (_settingsLock)
        {
            return _enabledProviders.TryGetValue(providerId, out bool enabled) && enabled;
        }
    }

    public void SetProviderEnabled(string providerId, bool enabled)
    {
        lock (_settingsLock)
        {
            _enabledProviders[providerId] = enabled;
        }
    }

    public List<string> GetModelList(string providerId)
    {
        lock (_settingsLock)
        {
            if (_providerModels.TryGetValue(providerId, out List<string> models))
                return new List<string>(models); // 回傳複本，保證安全
            return [];
        }
    }

    /// <summary>
    /// 只讀第一筆，不複製整份清單：候選解析對每個純供應商條目都會呼叫一次，
    /// 相容層每秒輪詢也走這裡，OpenRouter 動輒數百筆模型的清單不該每次都複製。
    /// </summary>
    public string GetDefaultModel(string providerId, string defaultVal)
    {
        lock (_settingsLock)
        {
            return _providerModels.TryGetValue(providerId, out List<string> models) && models.Count > 0
                ? models[0]
                : defaultVal;
        }
    }

    /// <summary>只讀筆數，供設定頁每幀顯示用，不複製清單。</summary>
    public int GetModelCount(string providerId)
    {
        lock (_settingsLock)
        {
            return _providerModels.TryGetValue(providerId, out List<string> models) ? models.Count : 0;
        }
    }

    public void SetModelList(string providerId, List<string> models)
    {
        lock (_settingsLock)
        {
            _providerModels[providerId] = models != null ? new List<string>(models) : [];
            _modelListVersion++;
        }
    }

    /// <summary>
    /// 從備援鏈移除條目（含舊存檔殘留的重複），並清掉它專屬的上下文上限覆寫；
    /// 分級覆寫以模型名稱為鍵、可能被其他供應商的同名模型共用，只在鏈上已無同名模型時才清。
    /// </summary>
    public void RemoveFallbackEntry(string entry)
    {
        if (string.IsNullOrEmpty(entry)) return;
        lock (_settingsLock)
        {
            _fallbackChain.RemoveAll(e => e == entry);
            _contextWindowOverrides.Remove(entry);

            int colonIndex = entry.IndexOf(':');
            if (colonIndex < 0) return;
            string modelName = entry.Substring(colonIndex + 1);
            bool stillUsed = _fallbackChain.Exists(e =>
            {
                int i = e.IndexOf(':');
                return i >= 0 && e.Substring(i + 1) == modelName;
            });
            if (!stillUsed) _modelLevelOverrides.Remove(modelName);
        }
    }

    public int GetModelLevelOverride(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return 0;
        lock (_settingsLock)
        {
            if (_modelLevelOverrides.TryGetValue(modelName, out int level))
            {
                return level;
            }
            int colonIndex = modelName.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < modelName.Length - 1)
            {
                string stripped = modelName.Substring(colonIndex + 1);
                if (_modelLevelOverrides.TryGetValue(stripped, out level))
                {
                    return level;
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// 設定模型分級覆寫 (1=低, 2=中, 3=高)。傳入 0 或負值代表移除覆寫。
    /// </summary>
    public void SetModelLevelOverride(string modelName, int level)
    {
        if (string.IsNullOrEmpty(modelName)) return;
        lock (_settingsLock)
        {
            if (level <= 0)
            {
                _modelLevelOverrides.Remove(modelName);
            }
            else
            {
                _modelLevelOverrides[modelName] = Math.Min(level, 3);
            }
        }
    }

    public int? GetContextWindow(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelName)) return null;
        lock (_settingsLock)
        {
            if (_contextWindowOverrides.TryGetValue(providerId + ":" + modelName, out int manual))
            {
                return manual;
            }
            if (_providerContextWindows.TryGetValue(providerId, out var fetched)
                && fetched.TryGetValue(modelName, out int reported))
            {
                return reported;
            }
            return null;
        }
    }

    /// <summary>
    /// 把本次重新整理拉到的上限併入該供應商的既有值：新值覆寫舊值，本次沒拉到的保留。
    /// 不整份取代，是因為 models.dev 或 Gemini 原生端點偶發失敗時本次結果會缺漏，
    /// 整份取代會把先前拿到的有效值一起清掉；已下架模型殘留舊值則無害。
    /// </summary>
    public void SetProviderContextWindows(string providerId, Dictionary<string, int> contextWindows)
    {
        if (string.IsNullOrEmpty(providerId)) return;
        lock (_settingsLock)
        {
            if (!_providerContextWindows.TryGetValue(providerId, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _providerContextWindows[providerId] = map;
            }
            if (contextWindows != null)
            {
                foreach (var kvp in contextWindows)
                {
                    if (kvp.Value > 0) map[kvp.Key] = kvp.Value;
                }
            }
        }
    }

    /// <summary>只取 API 回報的值（不含手動值），供設定頁顯示「自動」選項時使用。</summary>
    public int? GetFetchedContextWindow(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelName)) return null;
        lock (_settingsLock)
        {
            return _providerContextWindows.TryGetValue(providerId, out var fetched)
                && fetched.TryGetValue(modelName, out int reported)
                ? reported
                : (int?)null;
        }
    }

    /// <summary>取得玩家手動填寫的上限，0 代表未填寫。</summary>
    public int GetContextWindowOverride(string entry)
    {
        if (string.IsNullOrEmpty(entry)) return 0;
        lock (_settingsLock)
        {
            return _contextWindowOverrides.TryGetValue(entry, out int manual) ? manual : 0;
        }
    }

    /// <summary>
    /// 設定玩家手動填寫的上限。<paramref name="entry"/> 為 "Provider:Model"；傳入 0 或負值代表移除。
    /// </summary>
    public void SetContextWindowOverride(string entry, int tokens)
    {
        if (string.IsNullOrEmpty(entry)) return;
        lock (_settingsLock)
        {
            if (tokens <= 0)
            {
                _contextWindowOverrides.Remove(entry);
            }
            else
            {
                _contextWindowOverrides[entry] = tokens;
            }
        }
    }

    public static string GetDefaultEmbeddingModel(string provider)
    {
        return provider switch
        {
            "Google" => "gemini-embedding-2",
            "OpenAI" => "text-embedding-3-small",
            "LocalAPI_Ollama" => "nomic-embed-text",
            "LocalAPI_OpenAI" => "text-embedding-3-small",
            _ => "gemini-embedding-2",
        };
    }

    /// <summary>預設端點的唯一來源在 <see cref="RimLLMEmbeddingService"/>，這裡只是轉呼叫，避免兩張表各自漂移。</summary>
    public static string GetDefaultEmbeddingEndpoint(string provider)
    {
        return RimLLMEmbeddingService.GetDefaultEndpointOrEmpty(provider);
    }

    public string GetEmbeddingModel(string provider)
    {
        lock (_settingsLock)
        {
            if (!string.IsNullOrEmpty(provider) && _embeddingModels.TryGetValue(provider, out string val) && !string.IsNullOrEmpty(val))
                return val;
            return GetDefaultEmbeddingModel(provider);
        }
    }

    /// <summary>玩家實際輸入的模型名稱（未設定時為空字串），供設定欄位顯示；使用端請改用 <see cref="GetEmbeddingModel"/>。</summary>
    public string GetEmbeddingModelRaw(string provider)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(provider) && _embeddingModels.TryGetValue(provider, out string val) ? val ?? "" : "";
        }
    }

    public void SetEmbeddingModel(string provider, string model)
    {
        if (string.IsNullOrEmpty(provider)) return;
        lock (_settingsLock)
        {
            _embeddingModels[provider] = model;
        }
    }

    public string GetEmbeddingEndpoint(string provider)
    {
        lock (_settingsLock)
        {
            if (!string.IsNullOrEmpty(provider) && _embeddingEndpoints.TryGetValue(provider, out string val) && !string.IsNullOrEmpty(val))
                return val;
            return GetDefaultEmbeddingEndpoint(provider);
        }
    }

    /// <summary>玩家實際輸入的端點（未設定時為空字串），供設定欄位顯示；使用端請改用 <see cref="GetEmbeddingEndpoint"/>。</summary>
    public string GetEmbeddingEndpointRaw(string provider)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(provider) && _embeddingEndpoints.TryGetValue(provider, out string val) ? val ?? "" : "";
        }
    }

    public void SetEmbeddingEndpoint(string provider, string endpoint)
    {
        if (string.IsNullOrEmpty(provider)) return;
        lock (_settingsLock)
        {
            _embeddingEndpoints[provider] = endpoint;
        }
    }

    public string GetEmbeddingApiKey(string provider)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(provider) && _embeddingApiKeys.TryGetValue(provider, out string val) ? val : "";
        }
    }

    public void SetEmbeddingApiKey(string provider, string key)
    {
        if (string.IsNullOrEmpty(provider)) return;
        lock (_settingsLock)
        {
            _embeddingApiKeys[provider] = key;
        }
    }
}
#pragma warning restore S101, S2342
#pragma warning restore S2365, S3260, S3267, S3459
