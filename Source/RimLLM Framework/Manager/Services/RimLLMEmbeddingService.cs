using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Embeddings;
using OpenAI.Models;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
#pragma warning disable S3267 // reason: foreach+if 在此可讀性高於 Where，刻意保留現狀
/// <summary>
/// Embedding 向量運算服務。線上供應商一律透過 OpenAI SDK 呼叫
/// （Google 走官方 OpenAI 相容端點，Ollama 與自架服務走 OpenAI 相容的 EmbeddingClient）。
/// </summary>
public class RimLLMEmbeddingService
{
    /// <summary>
    /// 代表「尚未設定 Embedding 供應商」的代號，選用時向量運算一律擲回例外。
    /// </summary>
    public const string DisabledProviderId = "Disabled";

    /// <summary>
    /// Google Gemini 經官方 OpenAI 相容端點存取時的預設服務根位址。
    /// </summary>
    private const string GoogleOpenAiCompatibleEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";

    /// <summary>
    /// 本地相容伺服器通常不驗證金鑰，但 OpenAI SDK 不接受空憑證，
    /// 因此在未設定金鑰時填入佔位字串。
    /// </summary>
    private const string PlaceholderApiKey = "not-required";

    /// <summary>
    /// 各 Embedding 供應商的 OpenAI 相容預設服務根位址。
    /// 模型清單與向量運算兩條路徑共用同一張表，新增供應商時只改一處。
    /// </summary>
    private static readonly Dictionary<string, string> OpenAiCompatibleDefaultEndpoints =
        new(StringComparer.Ordinal)
        {
            { "Google", GoogleOpenAiCompatibleEndpoint },
            { "OpenAI", "https://api.openai.com/v1" },
            { "LocalAPI_Ollama", "http://localhost:11434/v1" },
            { "LocalAPI_OpenAI", "http://localhost:1234/v1" },
        };

    /// <summary>
    /// 查表取得供應商的預設服務根位址；不支援的供應商直接擲回例外，
    /// 呼叫端不再各自寫 switch 分派。
    /// </summary>
    internal static string ResolveDefaultEndpoint(string provider)
    {
        string defaultEndpoint;
        if (!OpenAiCompatibleDefaultEndpoints.TryGetValue(provider ?? string.Empty, out defaultEndpoint))
        {
            throw new RimLLMException(LLMError.Unknown, $"Unsupported embedding provider: {provider}");
        }
        return defaultEndpoint;
    }

    /// <summary>
    /// 同一張表的不擲例外版本，供設定頁顯示預設端點提示；不支援的供應商回傳空字串。
    /// </summary>
    public static string GetDefaultEndpointOrEmpty(string provider)
    {
        return OpenAiCompatibleDefaultEndpoints.TryGetValue(provider ?? string.Empty, out string defaultEndpoint)
            ? defaultEndpoint
            : string.Empty;
    }

    /// <summary>
    /// 把 SDK 與 HTTP 層之外的未預期例外收斂為 Unknown。
    /// ClientResultException（帶狀態碼）由呼叫端先行以 CreateException 精細映射，
    /// 落到這裡的代表真的無從分類。
    /// </summary>
    private static RimLLMException WrapUnknownEmbeddingError(string operation, Exception ex)
    {
        return new RimLLMException(
            LLMError.Unknown,
            $"{operation}: {Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
            innerException: ex);
    }

    private readonly IRimLLMSettings _settings;

    public RimLLMEmbeddingService(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 計算單筆文字的 embedding 向量，並帶回供應商回報的輸入 token 數。
    /// </summary>
    /// <exception cref="RimLLMException">
    /// 當 EmbeddingProvider 尚未設定、供應商不支援或 API 回傳錯誤時拋出。
    /// </exception>
    public async Task<RimLLMEmbeddingResult> ComputeEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Text to embed cannot be empty.", nameof(text));
        }

        IReadOnlyList<RimLLMEmbeddingResult> results =
            await ComputeEmbeddingsAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <summary>
    /// 單一批次送往 OpenAI 相容端點的最大筆數。OpenAI 官方上限為 2048，
    /// Gemini 的 OpenAI 相容端點與本地伺服器保守得多，100 是各家都吃得下的數字。
    /// </summary>
    internal const int MaxEmbeddingBatchSize = 100;

    /// <summary>
    /// 批次計算多筆文字的 embedding 向量。同一批文字合成一次 API 呼叫（超過
    /// <see cref="MaxEmbeddingBatchSize"/> 時分批），先前逐筆序列呼叫讓 N 筆文字變成 N 次 HTTP 來回。
    /// 供應商回報的用量是整批的總和，這裡平均分攤到每一筆，加總後仍等於供應商回報的值。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="texts"/> 為 null。</exception>
    /// <exception cref="ArgumentException">任一筆文字為空。</exception>
    /// <exception cref="RimLLMException">
    /// 當 EmbeddingProvider 尚未設定、供應商不支援或 API 回傳錯誤時拋出。
    /// </exception>
    public async Task<IReadOnlyList<RimLLMEmbeddingResult>> ComputeEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts == null) throw new ArgumentNullException(nameof(texts));

        var inputs = new List<string>(texts);
        var results = new List<RimLLMEmbeddingResult>(inputs.Count);
        if (inputs.Count == 0) return results;

        foreach (string text in inputs)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("Text to embed cannot be empty.", nameof(texts));
            }
        }

        string provider = _settings.EmbeddingProvider;
        if (string.IsNullOrEmpty(provider) || provider == DisabledProviderId)
        {
            throw new RimLLMException(
                LLMError.Unknown,
                "No embedding provider is configured; select one in the RimLLM settings first.");
        }

        string model = _settings.EmbeddingModel;
        string apiKey = string.IsNullOrEmpty(_settings.EmbeddingApiKey)
            ? _settings.GetActiveApiKey(GetMainProviderIdForEmbedding(provider))
            : _settings.EmbeddingApiKey;
        string endpoint = _settings.EmbeddingEndpoint;
        string defaultEndpoint = ResolveDefaultEndpoint(provider);

        // 以 ApiTimeout 建立逾時來源，並與呼叫端的取消 Token 連動。逾時以整批為單位。
        float timeoutSeconds = _settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30f;
        var batchContext = new EmbeddingBatchContext(model, provider, apiKey, endpoint, defaultEndpoint, timeoutSeconds);
        for (int offset = 0; offset < inputs.Count; offset += MaxEmbeddingBatchSize)
        {
            List<string> batch = inputs.GetRange(offset, Math.Min(MaxEmbeddingBatchSize, inputs.Count - offset));
            var batchResults = await ComputeBatchAsync(batch, batchContext, cancellationToken).ConfigureAwait(false);
            results.AddRange(batchResults);
        }

        return results;
    }

    private readonly struct EmbeddingBatchContext
    {
        public string Model { get; }
        public string Provider { get; }
        public string ApiKey { get; }
        public string Endpoint { get; }
        public string DefaultEndpoint { get; }
        public float TimeoutSeconds { get; }

        public EmbeddingBatchContext(string model, string provider, string apiKey, string endpoint, string defaultEndpoint, float timeoutSeconds)
        {
            Model = model;
            Provider = provider;
            ApiKey = apiKey;
            Endpoint = endpoint;
            DefaultEndpoint = defaultEndpoint;
            TimeoutSeconds = timeoutSeconds;
        }
    }

    private static async Task<IReadOnlyList<RimLLMEmbeddingResult>> ComputeBatchAsync(
        List<string> batch, EmbeddingBatchContext ctx, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ctx.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            return await ComputeOpenAiCompatibleEmbeddingsAsync(
                batch, ctx.Model, ctx.Provider, ctx.ApiKey, ctx.Endpoint, ctx.DefaultEndpoint, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 呼叫端沒有取消，代表是 ApiTimeout 觸發的逾時。
            throw new RimLLMException(LLMError.Timeout, $"Embedding request timed out after {ctx.TimeoutSeconds} seconds.");
        }
        catch (ClientResultException ex)
        {
            throw LLMErrorMapper.CreateException(
                ex.Status,
                $"Embedding API: {Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                innerException: ex);
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapUnknownEmbeddingError("Embedding API", ex);
        }
    }

    /// <summary>
    /// 取得目前 Embedding 供應商可用的模型清單。
    /// </summary>
    public Task<RimLLMEmbeddingModelList> FetchAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        string provider = _settings.EmbeddingProvider;
        string apiKey = _settings.EmbeddingApiKey;
        string endpoint = _settings.EmbeddingEndpoint;
        return FetchAvailableModelsAsync(provider, endpoint, apiKey, cancellationToken);
    }

    /// <summary>
    /// 取得指定 Embedding 供應商可用的模型清單，並盡可能只留下真正的 embedding 模型。
    ///
    /// OpenAI 相容的 <c>/v1/models</c> 不回傳能力資訊，因此改走各供應商的原生清單：
    /// Google 看 <c>supportedGenerationMethods</c>、Ollama 看 <c>/api/show</c> 的 <c>capabilities</c>、
    /// LM Studio 看 <c>/api/v0/models</c> 的 <c>type</c>；OpenAI 官方型錄的 embedding 模型一律以
    /// <c>text-embedding-</c> 命名，依名稱過濾即可。只有兩者皆不可得的通用相容伺服器
    /// 才退回「只排序不過濾」，並以 <see cref="RimLLMEmbeddingModelList.Filtered"/> 告知呼叫端。
    /// </summary>
    public async Task<RimLLMEmbeddingModelList> FetchAvailableModelsAsync(
        string provider, string endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(provider) || provider == DisabledProviderId)
        {
            throw new RimLLMException(LLMError.Unknown, "No embedding provider is configured; cannot fetch the model list.");
        }

        string effectiveApiKey = string.IsNullOrEmpty(apiKey)
            ? _settings.GetActiveApiKey(GetMainProviderIdForEmbedding(provider))
            : apiKey;
        string root = NormalizeEmbeddingEndpoint(endpoint) ?? ResolveDefaultEndpoint(provider);

        float timeoutSeconds = _settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30f;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            switch (provider)
            {
                case "Google":
                    return await FetchGoogleEmbeddingModelsAsync(root, effectiveApiKey, linkedCts.Token).ConfigureAwait(false);
                case "LocalAPI_Ollama":
                    return await FetchOllamaEmbeddingModelsAsync(root, linkedCts.Token).ConfigureAwait(false);
                case "LocalAPI_OpenAI":
                    return await FetchLocalEmbeddingModelsAsync(root, effectiveApiKey, linkedCts.Token).ConfigureAwait(false);
                default:
                    List<string> ids = await FetchOpenAiCompatibleModelsAsync(effectiveApiKey, root, linkedCts.Token).ConfigureAwait(false);
                    return new RimLLMEmbeddingModelList(ids.FindAll(LooksLikeEmbeddingModel), filtered: true);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RimLLMException(LLMError.Timeout, $"Embedding model list request timed out after {timeoutSeconds} seconds.");
        }
        catch (ClientResultException ex)
        {
            throw LLMErrorMapper.CreateException(
                ex.Status,
                $"Embedding model list: {Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RimLLMException(
                LLMError.ProviderOffline,
                $"Embedding model list: {Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                innerException: ex);
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapUnknownEmbeddingError("Embedding model list", ex);
        }
    }

    /// <summary>
    /// 原生清單端點共用的 HttpClient。逾時由呼叫端的 CancellationToken 控制。
    /// </summary>
    private static readonly HttpClient ModelListHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Google 原生 <c>/models</c> 端點回報每個模型支援的方法，只留下支援 <c>embedContent</c> 的。
    /// OpenAI 相容根位址以 <c>/openai</c> 結尾，去掉即為原生 API 根位址；
    /// 回傳的 <c>models/</c> 前綴一併去除，與預設清單和模型欄位的寫法一致。
    /// 自訂端點若是非 Google 的相容代理，回應不會有 <c>models</c> 陣列，此時退回通用清單。
    /// </summary>
    private static async Task<RimLLMEmbeddingModelList> FetchGoogleEmbeddingModelsAsync(
        string root, string apiKey, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        bool isGoogle = await ForEachGoogleNativeModelAsync(root, apiKey, model =>
        {
            string name = GetString(model, "name");
            if (!string.IsNullOrEmpty(name) &&
                ArrayContains(model, "supportedGenerationMethods", "embedContent"))
            {
                ids.Add(TrimPrefix(name, "models/"));
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!isGoogle)
        {
            List<string> generic = await FetchOpenAiCompatibleModelsAsync(apiKey, root, cancellationToken).ConfigureAwait(false);
            return new RimLLMEmbeddingModelList(OrderEmbeddingCandidatesFirst(generic), filtered: false);
        }
        return new RimLLMEmbeddingModelList(ids, filtered: true);
    }

    /// <summary>
    /// 逐頁列舉 Google 原生 <c>/models</c> 的每個模型，供 embedding 模型過濾與 Gemini 上下文上限共用。
    /// <paramref name="compatRoot"/> 是 OpenAI 相容根位址，去掉結尾的 <c>/openai</c> 即為原生 API 根位址。
    /// 回傳 false 代表回應沒有 <c>models</c> 陣列，也就是自訂端點不是 Google 本身（例如相容代理）。
    /// </summary>
    internal static async Task<bool> ForEachGoogleNativeModelAsync(
        string compatRoot, string apiKey, Action<JsonElement> onModel, CancellationToken cancellationToken)
    {
        string nativeRoot = TrimSuffix(compatRoot, "/openai");
        string pageToken = null;
        do
        {
            string url = nativeRoot + "/models?pageSize=1000" +
                (pageToken == null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            }
            using JsonDocument doc = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (JsonElement model in models.EnumerateArray())
            {
                onModel(model);
            }
            pageToken = GetString(doc.RootElement, "nextPageToken");
        } while (!string.IsNullOrEmpty(pageToken));

        return true;
    }

    /// <summary>
    /// Ollama 的 <c>/api/tags</c> 只列名稱，能力要逐一向 <c>/api/show</c> 查。
    /// 舊版 Ollama 沒有 <c>capabilities</c> 欄位；那種情況無從判斷，退回只排序不過濾。
    /// 模型很多時以固定 worker pool 限流，避免一次開出與模型數相同的連線。
    /// </summary>
    private static async Task<RimLLMEmbeddingModelList> FetchOllamaEmbeddingModelsAsync(
        string root, CancellationToken cancellationToken)
    {
        string nativeRoot = TrimSuffix(root, "/v1");
        List<string> names = await FetchOllamaTagNamesAsync(nativeRoot, cancellationToken).ConfigureAwait(false);
        bool?[] verdicts = await ProbeOllamaModelsAsync(nativeRoot, names, cancellationToken).ConfigureAwait(false);

        var ids = new List<string>();
        bool allKnown = true;
        for (int i = 0; i < names.Count; i++)
        {
            if (verdicts[i] == null) allKnown = false;
            if (verdicts[i] != false) ids.Add(names[i]);
        }
        return allKnown
            ? new RimLLMEmbeddingModelList(ids, filtered: true)
            : new RimLLMEmbeddingModelList(OrderEmbeddingCandidatesFirst(ids), filtered: false);
    }

    private static async Task<List<string>> FetchOllamaTagNamesAsync(string nativeRoot, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        using (var request = new HttpRequestMessage(HttpMethod.Get, nativeRoot + "/api/tags"))
        using (JsonDocument doc = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (doc.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            {
                int totalModels = models.GetArrayLength();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement model in models.EnumerateArray())
                {
                    string name = GetString(model, "name");
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                    names.Add(name);
                    if (names.Count >= MaxOllamaModelsToProbe)
                    {
                        break;
                    }
                }
                if (totalModels > names.Count)
                {
                    Core.RimLLMLog.Warning(
                        $"[RimLLM] Ollama model library has {totalModels} entries; only the first {names.Count} were probed (MaxOllamaModelsToProbe={MaxOllamaModelsToProbe}). Models beyond this limit will not appear in the list.");
                }
            }
        }
        return names;
    }

    private static async Task<bool?[]> ProbeOllamaModelsAsync(string nativeRoot, List<string> names, CancellationToken cancellationToken)
    {
        var verdicts = new bool?[names.Count];
        int nextIndex = -1;
        var workers = new List<Task>(MaxOllamaProbeConcurrency);
        for (int w = 0; w < MaxOllamaProbeConcurrency; w++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref nextIndex);
                    if (i >= names.Count) return;
                    bool? verdict = await IsOllamaEmbeddingModelAsync(nativeRoot, names[i], cancellationToken).ConfigureAwait(false);
                    verdicts[i] = verdict;
                }
            }, cancellationToken));
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        return verdicts;
    }

    /// <summary>Ollama 模型清單最多探測的模型數，避免異常大的本地模型庫造成連線尖峰。超過時只取前 N 個並寫警告日誌。</summary>
    internal const int MaxOllamaModelsToProbe = 128;

    /// <summary>Ollama <c>/api/show</c> 探測的同時並行數上限。</summary>
    internal const int MaxOllamaProbeConcurrency = 8;

    /// <summary>
    /// 回傳 null 代表無從判斷：伺服器沒有回報能力資訊，或這一個模型的查詢失敗。
    /// 單一模型失敗（中途被刪、暫時 500）不該讓整份清單抓不到，因此在這裡吞掉。
    /// </summary>
    private static async Task<bool?> IsOllamaEmbeddingModelAsync(string nativeRoot, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, nativeRoot + "/api/show");
            request.Content = new StringContent(
                RimLLMJsonHelper.Serialize(new Dictionary<string, string> { { "model", name } }),
                Encoding.UTF8, "application/json");
            using JsonDocument doc = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("capabilities", out JsonElement caps) || caps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return ArrayContains(doc.RootElement, "capabilities", "embedding");
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is RimLLMException || ex is JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// OpenAI 相容分頁的目標可能是 LM Studio、Ollama 或任何相容伺服器。
    /// 先探 LM Studio 原生的 <c>/api/v0/models</c>（有 <c>type</c> 欄位），
    /// 再探 Ollama；兩者都不是才退回無能力資訊的 <c>/v1/models</c>。
    /// </summary>
    private static async Task<RimLLMEmbeddingModelList> FetchLocalEmbeddingModelsAsync(
        string root, string apiKey, CancellationToken cancellationToken)
    {
        RimLLMEmbeddingModelList lmStudio = await TryFetchLmStudioEmbeddingModelsAsync(root, cancellationToken).ConfigureAwait(false);
        if (lmStudio != null) return lmStudio;

        try
        {
            return await FetchOllamaEmbeddingModelsAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is RimLLMException || ex is JsonException)
        {
            // 不是 Ollama，繼續退回通用路徑
        }

        List<string> ids = await FetchOpenAiCompatibleModelsAsync(apiKey, root, cancellationToken).ConfigureAwait(false);
        return new RimLLMEmbeddingModelList(OrderEmbeddingCandidatesFirst(ids), filtered: false);
    }

    /// <summary>LM Studio 的原生清單以 <c>type</c> 區分 llm / vlm / embeddings。不是 LM Studio 時回傳 null。</summary>
    private static async Task<RimLLMEmbeddingModelList> TryFetchLmStudioEmbeddingModelsAsync(
        string root, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TrimSuffix(root, "/v1") + "/api/v0/models");
            using JsonDocument doc = await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var ids = new List<string>();
            bool anyTyped = false;
            foreach (JsonElement model in data.EnumerateArray())
            {
                string type = GetString(model, "type");
                if (type == null) continue;
                anyTyped = true;
                string id = GetString(model, "id");
                if (!string.IsNullOrEmpty(id) && string.Equals(type, "embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }
            // 沒有任何 type 欄位：不是 LM Studio 的格式，交給後續路徑。
            return anyTyped ? new RimLLMEmbeddingModelList(ids, filtered: true) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is RimLLMException || ex is JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ModelListHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw LLMErrorMapper.CreateException(
                (int)response.StatusCode,
                $"{request.RequestUri.AbsolutePath} responded {(int)response.StatusCode}: {Core.RimLLMLog.SanitizeForLog(body, 200)}",
                detectionText: body);
        }
        return JsonDocument.Parse(body);
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ArrayContains(JsonElement element, string property, string expected)
    {
        if (!element.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string TrimSuffix(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - suffix.Length)
            : value;
    }

    private static string TrimPrefix(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value.Substring(prefix.Length) : value;
    }

    /// <summary>
    /// 測試指定供應商的連線與向量生成能力。
    /// </summary>
    public async Task<RimLLMEmbeddingResult> TestEmbeddingAsync(
        string provider, string model, string endpoint, string apiKey, string text = "RimWorld LLM Embedding Test", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(provider) || provider == DisabledProviderId)
        {
            throw new RimLLMException(LLMError.Unknown, "Select an embedding provider first.");
        }

        if (string.IsNullOrEmpty(model))
        {
            throw new RimLLMException(LLMError.Unknown, "Specify an embedding model name first.");
        }

        string effectiveApiKey = string.IsNullOrEmpty(apiKey)
            ? _settings.GetActiveApiKey(GetMainProviderIdForEmbedding(provider))
            : apiKey;

        float timeoutSeconds = _settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30f;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            List<RimLLMEmbeddingResult> results = await ComputeOpenAiCompatibleEmbeddingsAsync(
                new[] { text }, model, provider, effectiveApiKey, endpoint, ResolveDefaultEndpoint(provider), linkedCts.Token).ConfigureAwait(false);
            return results[0];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RimLLMException(LLMError.Timeout, $"Embedding connection test timed out after {timeoutSeconds} seconds.");
        }
        catch (ClientResultException ex)
        {
            throw LLMErrorMapper.CreateException(
                ex.Status,
                $"Embedding test failed: {Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                innerException: ex);
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapUnknownEmbeddingError("Embedding test", ex);
        }
    }

    /// <summary>
    /// 設定中快取此 Embedding 供應商模型清單所用的鍵。
    /// 與對話供應商共用同一份持久化字典，因此加上前綴避免與 providerId 相撞。
    /// </summary>
    public static string GetModelListKey(string embeddingProvider)
    {
        return "Embedding:" + (embeddingProvider ?? string.Empty);
    }

    /// <summary>
    /// 建立 OpenAI 相容端點的連線參數：端點正規化、套用預設值、補上佔位金鑰。
    /// </summary>
    private static void BuildOpenAiCompatibleClientArgs(
        string apiKey, string endpoint, string defaultEndpoint,
        out ApiKeyCredential credential, out OpenAIClientOptions options)
    {
        options = new OpenAIClientOptions
        {
            Endpoint = new Uri(NormalizeEmbeddingEndpoint(endpoint) ?? defaultEndpoint, UriKind.Absolute)
        };
        if (TransportOverride != null)
        {
            options.Transport = TransportOverride;
        }
        credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);
    }

    /// <summary>
    /// 讓單元測試以假的 HTTP 傳輸攔截 embedding 請求（與 <c>EncryptionUtility.SecureKeyPathResolver</c>
    /// 同一種測試接縫）；正式環境一律 null，走 SDK 預設傳輸。
    /// </summary>
    internal static System.ClientModel.Primitives.PipelineTransport TransportOverride { get; set; }

    /// <summary>
    /// 沒有能力資訊的通用 <c>/v1/models</c>。<paramref name="root"/> 已正規化並套用過預設值。
    /// </summary>
    private static async Task<List<string>> FetchOpenAiCompatibleModelsAsync(string apiKey, string root, CancellationToken cancellationToken)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(root, UriKind.Absolute) };
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);

        OpenAIModelCollection models = await new OpenAIClient(credential, options)
            .GetOpenAIModelClient()
            .GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        var ids = new List<string>();
        foreach (OpenAIModel model in models)
        {
            if (!string.IsNullOrEmpty(model?.Id))
            {
                ids.Add(model.Id);
            }
        }
        return ids;
    }

    /// <summary>
    /// 常見的 embedding 模型命名片段。伺服器沒有能力資訊時只用來排序不過濾 ——
    /// 本地伺服器的模型名由使用者自訂，過濾會把合法選項藏起來。
    /// OpenAI 官方型錄命名固定，才用它直接過濾。
    /// </summary>
    private static readonly string[] EmbeddingNameHints =
    [
        "embed", "bge", "gte", "e5-", "nomic", "minilm", "mxbai", "jina", "qwen3-emb"
    ];

    internal static List<string> OrderEmbeddingCandidatesFirst(IEnumerable<string> modelIds)
    {
        var likely = new List<string>();
        var others = new List<string>();
        if (modelIds != null)
        {
            foreach (string id in modelIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                (LooksLikeEmbeddingModel(id) ? likely : others).Add(id);
            }
        }
        likely.AddRange(others);
        return likely;
    }

    internal static bool LooksLikeEmbeddingModel(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        string lower = modelId.ToLowerInvariant();
        return Array.Exists(EmbeddingNameHints, hint => lower.IndexOf(hint, StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// 一批文字一次 API 呼叫。usage 掛在整個集合上（不分筆），因此平均分攤到每一筆：
    /// 各筆的 <see cref="RimLLMEmbeddingResult.InputTokenCount"/> 加總即為供應商回報的總量。
    /// <paramref name="model"/> 與 <paramref name="provider"/> 是請求開始時捕捉的實際
    /// 供應商身分，帶進每一筆結果供呼叫端標記向量與記帳，避免 await 之後設定被改。
    /// </summary>
    private static async Task<List<RimLLMEmbeddingResult>> ComputeOpenAiCompatibleEmbeddingsAsync(
        IReadOnlyList<string> texts, string model, string providerId, string apiKey, string endpoint, string defaultEndpoint, CancellationToken cancellationToken)
    {
        BuildOpenAiCompatibleClientArgs(apiKey, endpoint, defaultEndpoint, out var credential, out var options);
        var client = new EmbeddingClient(model, credential, options);

        OpenAIEmbeddingCollection embeddings = await client
            .GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (embeddings == null || embeddings.Count != texts.Count)
        {
            throw new RimLLMException(
                LLMError.InvalidResponse,
                $"The OpenAI-compatible embedding response contained {embeddings?.Count ?? 0} vector(s) for {texts.Count} input(s).");
        }

        return DistributeUsage(embeddings, embeddings.Usage?.InputTokenCount, model, providerId);
    }

    /// <summary>
    /// 把整批的輸入 token 數平均分攤到每一筆（餘數給前幾筆），沒回報用量時每筆都是 null。
    /// 每一筆都帶上實際使用的模型與供應商（由請求開始時捕捉，見 <see cref="ComputeEmbeddingsAsync"/>）。
    /// </summary>
    private static List<RimLLMEmbeddingResult> DistributeUsage(OpenAIEmbeddingCollection embeddings, long? totalInputTokens, string modelId, string providerId)
    {
        var results = new List<RimLLMEmbeddingResult>(embeddings.Count);
        long share = totalInputTokens.HasValue ? totalInputTokens.Value / embeddings.Count : 0;
        long remainder = totalInputTokens.HasValue ? totalInputTokens.Value % embeddings.Count : 0;
        for (int i = 0; i < embeddings.Count; i++)
        {
            long? perItem = null;
            if (totalInputTokens.HasValue)
            {
                long extra = i < remainder ? 1 : 0;
                perItem = share + extra;
            }
            results.Add(new RimLLMEmbeddingResult(embeddings[i].ToFloats().ToArray(), perItem, modelId, providerId));
        }
        return results;
    }

    private static readonly char[] SlashTrimChars = ['/'];

    /// <summary>
    /// SDK 需要的是服務根位址（如 http://localhost:11434/v1），
    /// 因此把使用者可能填入的完整 embeddings 路徑收斂回根位址。
    /// </summary>
    internal static string NormalizeEmbeddingEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        string normalized = endpoint.Trim().TrimEnd(SlashTrimChars);
        foreach (string suffix in new[] { "/embeddings", "/api/embed" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd(SlashTrimChars);
                break;
            }
        }
        return normalized.Length == 0 ? null : normalized;
    }

    public static string GetMainProviderIdForEmbedding(string embeddingProvider)
    {
        return embeddingProvider switch
        {
            "Google" => ProviderIds.Gemini,
            "OpenAI" => ProviderIds.OpenAI,
            "LocalAPI_OpenAI" => ProviderIds.OpenAICompatible,
            "LocalAPI_Ollama" => ProviderIds.OpenAICompatible,
            _ => ProviderIds.OpenAI,
        };
    }
}
#pragma warning restore S3267
#pragma warning restore S101, S2342
