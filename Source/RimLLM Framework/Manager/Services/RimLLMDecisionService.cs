using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
using RimLLM_Framework;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 決策請求的結果＋在請求開始時捕捉的歸屬資訊。供應商與模型在連線解析當下就固定下來，
/// await 之後不得重讀設定：請求途中玩家切換啟用供應商時，用量才不會記到新供應商
/// （與 RimLLMEmbeddingClient 對 RimLLMEmbeddingResult 的同一個考量）。
/// </summary>
internal sealed record DecisionEvaluation(JevResult Result, string ProviderId, string ModelName);

/// <summary>
/// 以 Kkdev92.Jev（TypeSafe System One / Jev）為後端的決策服務。
/// 僅提供通用的 EvaluateAsync 與模型清單查詢；不內建任何情境判斷、分流或安全閘門。
/// 所有失敗都收斂為 <see cref="RimLLMException"/>（對話與 embedding 的同一份契約），
/// 只有呼叫端取消維持 <see cref="OperationCanceledException"/>。
/// </summary>
public class RimLLMDecisionService
{
    private readonly IRimLLMSettings _settings;

    public RimLLMDecisionService(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>模型清單在設定檔中的快取鍵，與 Embedding 的清單分開存放。</summary>
    public static string GetModelListKey(string providerId)
    {
        return "decision:" + (providerId ?? "");
    }

    /// <summary>把使用者貼上的完整 /v1/systemone 位址縮回 JevClient 需要的 origin。</summary>
    internal static string NormalizeBaseEndpoint(string endpoint, string providerId)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = RimLLMDecisionProviderCatalog.GetDefaultEndpoint(providerId);
        }

        string normalized = endpoint.Trim().TrimEnd('/');
        const string suffix = "/v1/systemone";
        if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd('/');
        }
        return normalized;
    }

    private static HttpClient CreateHttpClient(string endpoint)
    {
        // JevClient 自己管理 deadline；HttpClient 的逾時必須關掉，否則會比 SDK 先中斷。
        HttpMessageHandler handler = MessageHandlerFactory(endpoint);
        if (DecisionEndpointRouting.IsPlaintext(endpoint))
        {
            // SDK 組出的是 https URI，這裡在送上線前改寫回玩家設定的明文端點。
            handler = new PlaintextEndpointRewriteHandler(DecisionEndpointRouting.GetTransportOrigin(endpoint))
            {
                InnerHandler = handler
            };
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// 建立送出決策請求的底層 HTTP 處理器。預設走真實網路；
    /// 測試以這個內部接縫換掉它，讓假端點不必連網就能攔截請求並回應。
    /// </summary>
    internal static Func<string, HttpMessageHandler> MessageHandlerFactory { get; set; } =
        _ => new SocketsHttpHandler { AllowAutoRedirect = false };

    private JevClientOptions CreateOptions(string endpoint, string model, string apiKey, string providerId = null)
    {
        string effectiveProviderId = string.IsNullOrWhiteSpace(providerId) ? _settings.DecisionProvider : providerId;
        return new JevClientOptions
        {
            // SDK 只接受 https origin；明文本機端點在這裡先換成同 host／port 的 https，
            // 實際連線由 CreateHttpClient 的改寫處理器接回明文。
            BaseAddress = DecisionEndpointRouting.GetSdkBaseAddress(endpoint),
            DefaultModel = string.IsNullOrWhiteSpace(model)
                ? RimLLMDecisionProviderCatalog.GetDefaultModel(effectiveProviderId)
                : model,
            Credential = new SettingsCredential(() => apiKey ?? ""),
            Timeout = TimeSpan.FromSeconds(_settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30)
        };
    }

    /// <summary>
    /// 解析指定供應商的連線資訊，並一併在這裡捕捉供歸屬用的供應商 id。
    /// <paramref name="providerId"/> 為空時取啟用中的供應商；
    /// 未啟用、供應商不存在或沒有金鑰都在此拒絕，不把無效請求送出去。
    /// </summary>
    /// <param name="providerId">要使用的決策供應商 id；null 或空字串代表使用啟用中的供應商。</param>
    /// <param name="modelOverride">逐次覆寫的模型名稱；為空時用該供應商設定中的模型。</param>
    private DecisionConnection ResolveConnection(string providerId, string modelOverride = null)
    {
        string resolvedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? _settings.DecisionProvider
            : providerId.Trim();

        if (string.IsNullOrEmpty(resolvedProviderId) ||
            string.Equals(resolvedProviderId, RimLLMDecisionProviderCatalog.Disabled, StringComparison.Ordinal))
        {
            throw new RimLLMException(
                LLMError.Unknown,
                "No decision provider is enabled; enable one in the Decision settings tab first.");
        }

        if (!RimLLMDecisionProviderCatalog.TryGet(resolvedProviderId, out _))
        {
            throw new RimLLMException(
                LLMError.Unknown,
                $"Unknown decision provider '{resolvedProviderId}'.");
        }

        string endpoint = NormalizeBaseEndpoint(_settings.GetDecisionEndpoint(resolvedProviderId), resolvedProviderId);
        string apiKey = _settings.GetDecisionApiKey(resolvedProviderId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // 本機架設的服務多半不驗證金鑰；遠端供應商少了金鑰就是不能送。
            if (!DecisionEndpointRouting.IsPlaintext(endpoint))
            {
                throw new RimLLMException(
                    LLMError.InvalidKey,
                    $"No API key is configured for the decision provider '{resolvedProviderId}'.");
            }
            apiKey = DecisionEndpointRouting.PlaintextPlaceholderKey;
        }

        string model = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : _settings.GetDecisionModel(resolvedProviderId);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = RimLLMDecisionProviderCatalog.GetDefaultModel(resolvedProviderId);
        }
        return new DecisionConnection(resolvedProviderId, endpoint, model, apiKey);
    }

    /// <summary>
    /// 把使用者輸入的端點與金鑰做基本檢查；參數化的 UI 路徑（抓清單、測試連線）走這裡，未啟用也可預先連線。
    /// 明文本機端點不要求金鑰，改用佔位值。
    /// </summary>
    private static string EnsureUsableKey(string apiKey, string endpoint)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        if (DecisionEndpointRouting.IsPlaintext(endpoint))
        {
            return DecisionEndpointRouting.PlaintextPlaceholderKey;
        }

        throw new RimLLMException(
            LLMError.InvalidKey,
            "No API key is configured for this decision provider.");
    }

    /// <summary>抓取目前啟用供應商的可用模型；模型清單查詢不消耗推論額度。</summary>
    public async Task<IReadOnlyList<JevModel>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DecisionConnection connection = ResolveConnection(null);
            return await FetchModelsCoreAsync(
                    connection.Endpoint, connection.Model, connection.ApiKey, connection.ProviderId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>
    /// 抓取指定供應商的可用模型清單（回傳模型名稱字串）。
    /// 端點與金鑰取自該供應商在框架設定中保存的值；
    /// <paramref name="providerId"/> 為空時用啟用中的供應商。
    /// 模型清單查詢不消耗推論額度，也不計入每日預算。
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchDecisionModelsAsync(
        string providerId, CancellationToken cancellationToken = default)
    {
        try
        {
            DecisionConnection connection = ResolveConnection(providerId);
            IReadOnlyList<JevModel> models = await FetchModelsCoreAsync(
                    connection.Endpoint, connection.Model, connection.ApiKey, connection.ProviderId, cancellationToken)
                .ConfigureAwait(false);

            List<string> names = new List<string>(models?.Count ?? 0);
            if (models != null)
            {
                foreach (JevModel model in models)
                {
                    if (model != null && !string.IsNullOrEmpty(model.Name))
                    {
                        names.Add(model.Name);
                    }
                }
            }
            return names;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>以指定端點與金鑰抓取模型清單，供設定頁在尚未啟用時預覽。</summary>
    public async Task<IReadOnlyList<JevModel>> FetchModelsAsync(
        string endpoint,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string normalizedEndpoint = NormalizeBaseEndpoint(endpoint, _settings.DecisionProvider);
            string usableKey = EnsureUsableKey(apiKey, normalizedEndpoint);
            return await FetchModelsCoreAsync(
                    normalizedEndpoint, model, usableKey, _settings.DecisionProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    private async Task<IReadOnlyList<JevModel>> FetchModelsCoreAsync(
        string normalizedEndpoint, string model, string apiKey, string providerId, CancellationToken cancellationToken)
    {
        using HttpClient http = CreateHttpClient(normalizedEndpoint);
        var client = new JevClient(http, CreateOptions(normalizedEndpoint, model, apiKey, providerId));
        IReadOnlyList<JevModel> models =
            await client.GetModelsAsync(new JevRequestOptions(), cancellationToken).ConfigureAwait(false);
        return models ?? Array.Empty<JevModel>();
    }

    /// <summary>
    /// 送出一個最小的 Noul 問題，實際驗證端點、金鑰與回應格式。
    /// 與只查 /v1/models 相比，這能抓到「模型清單可用但決策端點不相容」的情況。
    /// </summary>
    public async Task TestConnectionAsync(
        string endpoint,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var builder = new JevDecisionPlanBuilder();
            builder.AddNoul("reachable", "Is this request reachable?", default);
            JevDecisionPlan plan = builder.Build();

            string normalizedEndpoint = NormalizeBaseEndpoint(endpoint, _settings.DecisionProvider);
            string usableKey = EnsureUsableKey(apiKey, normalizedEndpoint);
            using HttpClient http = CreateHttpClient(normalizedEndpoint);
            var client = new JevClient(http, CreateOptions(normalizedEndpoint, model, usableKey));
            var requestOptions = new JevRequestOptions { Model = model };
            await client
                .EvaluateAsync("RimLLM decision connection test", plan, requestOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>執行一次決策請求。</summary>
    public async Task<JevResult> EvaluateAsync(
        string state,
        JevDecisionPlan plan,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        return (await EvaluateDetailedAsync(state, plan, model, cancellationToken).ConfigureAwait(false)).Result;
    }

    /// <summary>執行一次決策請求，並帶回在請求開始時捕捉的供應商／模型歸屬資訊。</summary>
    internal async Task<DecisionEvaluation> EvaluateDetailedAsync(
        string state,
        JevDecisionPlan plan,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateWithProviderDetailedAsync(
                state, plan, _settings.DecisionProvider, model, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 以指定供應商執行一次決策請求，並帶回在請求開始時捕捉的供應商／模型歸屬資訊。
    /// 端點與金鑰取自該供應商在框架設定中保存的值；
    /// <paramref name="providerId"/> 為空時等同 <see cref="EvaluateDetailedAsync"/>（使用啟用中的供應商）。
    /// </summary>
    internal async Task<DecisionEvaluation> EvaluateWithProviderDetailedAsync(
        string state,
        JevDecisionPlan plan,
        string providerId,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(state)) throw new ArgumentNullException(nameof(state));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            DecisionConnection connection = ResolveConnection(providerId, model);
            string selectedModel = connection.Model;

            using HttpClient http = CreateHttpClient(connection.Endpoint);
            var client = new JevClient(http, CreateOptions(connection.Endpoint, selectedModel, connection.ApiKey, connection.ProviderId));
            var requestOptions = new JevRequestOptions { Model = selectedModel };
            JevResult result = await client
                .EvaluateAsync(state, plan, requestOptions, cancellationToken)
                .ConfigureAwait(false);
            return new DecisionEvaluation(result, connection.ProviderId, selectedModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>
    /// 把 Jev SDK 與 HTTP 層的例外收斂成 <see cref="RimLLMException"/>，與對話、embedding 的錯誤契約一致：
    /// 呼叫端 mod 依 README 以 <see cref="RimLLMException"/> + <see cref="LLMError"/> 分流，而不是按 SDK 的型別。
    /// 呼叫端取消（OperationCanceledException）與已是 RimLLMException 的例外由呼叫端的 catch 過濾器先行放行，
    /// 不會進到這裡。
    /// </summary>
    internal static Exception TranslateDecisionError(Exception ex)
    {
        switch (ex)
        {
            // HTTP 狀態碼 → LLMError 的對照由既有 LLMErrorMapper 單一來源處理；
            // JevHttpException 帶 StatusCode 與 Retry-After，正好可以完整重用。
            case JevHttpException jevHttp:
                return LLMErrorMapper.CreateException(
                    jevHttp.StatusCode,
                    $"Decision API: {RimLLMLog.SanitizeForLog(jevHttp.Message, 300)}",
                    retryAfter: jevHttp.RetryAfter,
                    innerException: jevHttp);

            case JevTimeoutException jevTimeout:
                return new RimLLMException(
                    LLMError.Timeout,
                    $"Decision API: {RimLLMLog.SanitizeForLog(jevTimeout.Message, 300)}",
                    innerException: jevTimeout);

            case JevTransportException jevTransport:
                return new RimLLMException(
                    LLMError.NetworkError,
                    $"Decision API: {RimLLMLog.SanitizeForLog(jevTransport.Message, 300)}",
                    innerException: jevTransport);

            case JevProtocolException jevProtocol:
                return new RimLLMException(
                    LLMError.InvalidResponse,
                    $"Decision API: {RimLLMLog.SanitizeForLog(jevProtocol.Message, 300)}",
                    innerException: jevProtocol);

            // 本機限制（body 上限、佇列額度）不是伺服器限流也不是回應問題，不硬套其他代碼。
            case JevLimitException jevLimit:
                return new RimLLMException(
                    LLMError.Unknown,
                    $"Decision API: {RimLLMLog.SanitizeForLog(jevLimit.Message, 300)}",
                    innerException: jevLimit);

            case JevException jevBase:
                return new RimLLMException(
                    LLMError.Unknown,
                    $"Decision API: {RimLLMLog.SanitizeForLog(jevBase.Message, 300)}",
                    innerException: jevBase);

            case TimeoutException:
                return new RimLLMException(
                    LLMError.Timeout,
                    $"Decision API: {RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                    innerException: ex);

            // JevClient 內部的金鑰可用性檢查：與 ResolveActiveConnection 的前置檢查同一語意，
            // 但 SDK 訊息是英文原文，統一成 InvalidKey 並保留原文供除錯。
            case InvalidOperationException invalid when invalid.Message != null &&
                                                        invalid.Message.Contains("API key", StringComparison.Ordinal):
                return new RimLLMException(
                    LLMError.InvalidKey,
                    $"Decision API: {RimLLMLog.SanitizeForLog(invalid.Message, 300)}",
                    innerException: invalid);

            case UriFormatException badUri:
                return new RimLLMException(
                    LLMError.Unknown,
                    "Decision API: the configured endpoint is not a valid absolute URI. " +
                    RimLLMLog.SanitizeForLog(badUri.Message, 300),
                    innerException: badUri);

            default:
                return new RimLLMException(
                    LLMError.Unknown,
                    $"Decision API: {RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                    innerException: ex);
        }
    }

    private sealed record DecisionConnection(string ProviderId, string Endpoint, string Model, string ApiKey);

    private sealed class SettingsCredential : IJevCredential
    {
        private readonly Func<string> _getKey;

        public SettingsCredential(Func<string> getKey)
        {
            _getKey = getKey;
        }

        public ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken)
        {
            return new ValueTask<string>(_getKey() ?? "");
        }
    }
}
