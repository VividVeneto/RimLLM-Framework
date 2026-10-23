using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework;

/// <summary>
/// 綁定單一 Mod 的 IEmbeddingGenerator facade。內部接到既有 RimLLMEmbeddingService
/// （線上供應商 + 防濫用）。透過 RimLLMProvider.CreateEmbeddingGenerator(modId) 取得。
/// </summary>
internal class RimLLMEmbeddingClient : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly RimLLMManager _manager;
    private readonly string _modId;

    internal RimLLMEmbeddingClient(RimLLMManager manager, string modId)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions options = null,
        CancellationToken cancellationToken = default)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));

        _manager.UsageTracker.RecordCaller(_modId);
        _manager.CheckAntiAbuseForMod(_modId);

        var inputValues = new List<string>(values);
        long estimatedTokens = 0;
        foreach (string value in inputValues)
        {
            int valueEstimate = RimLLMChatClientExecutor.EstimateTokens(value);
            estimatedTokens = estimatedTokens > long.MaxValue - valueEstimate
                ? long.MaxValue
                : estimatedTokens + valueEstimate;
        }

        if (!_manager.UsageTracker.TryReserveBudget(estimatedTokens, out RimLLMUsageTracker.BudgetReservation reservation))
        {
            throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
        }

        using (reservation)
        {
            IReadOnlyList<RimLLMEmbeddingResult> results = await _manager.EmbeddingService
                .ComputeEmbeddingsAsync(inputValues, options, batchResults =>
                {
                    long? batchTokens = SumInputTokens(batchResults);
                    if (batchTokens.HasValue)
                    {
                        RimLLMEmbeddingResult batchResult = batchResults[0];
                        reservation.RecordUsage(
                            RimLLMEmbeddingService.GetMainProviderIdForEmbedding(batchResult.ProviderId),
                            batchResult.ModelId,
                            batchTokens.Value > int.MaxValue ? int.MaxValue : (int)batchTokens.Value,
                            0);
                    }
                }, cancellationToken)
                .ConfigureAwait(false);

            // 標上實際算出向量的模型與供應商——由服務在請求開始時捕捉（見
            // RimLLMEmbeddingResult.ModelId），而不是 await 之後重新讀設定：
            // 請求期間玩家切換設定，若這裡重讀，向量會被標成新模型、用量記到新供應商，
            // 污染向量索引與帳本。少了它，呼叫端把不同模型的向量存進同一個索引
            // 也不會發現——維度相同但語意空間不同的向量，比對結果只會是雜訊。
            string modelId = results.Count > 0
                ? results[0].ModelId
                : (!string.IsNullOrWhiteSpace(options?.ModelId) ? options.ModelId : _manager.Settings.EmbeddingModel);
            string providerId = results.Count > 0 ? results[0].ProviderId : _manager.Settings.EmbeddingProvider;
            var embeddings = new List<Embedding<float>>(results.Count);
            long? inputTokens = null;
            foreach (RimLLMEmbeddingResult result in results)
            {
                embeddings.Add(new Embedding<float>(result.Vector) { ModelId = result.ModelId ?? modelId });
                if (result.InputTokenCount.HasValue) inputTokens = SaturatingAdd(inputTokens ?? 0L, result.InputTokenCount.Value);
            }

            var generated = new GeneratedEmbeddings<Embedding<float>>(embeddings);
            if (inputTokens.HasValue)
            {
                // Embedding 沒有輸出 token，總量即輸入量。沒有任何一筆回報時整個
                // Usage 維持 null，讓呼叫端分得出「供應商沒說」與「真的是 0」。
                generated.Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    TotalTokenCount = inputTokens
                };
            }
            return generated;
        }
    }

    private static long? SumInputTokens(IReadOnlyList<RimLLMEmbeddingResult> results)
    {
        long? total = null;
        foreach (RimLLMEmbeddingResult result in results)
        {
            if (result.InputTokenCount.HasValue)
            {
                total = SaturatingAdd(total ?? 0L, result.InputTokenCount.Value);
            }
        }
        return total;
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    public object GetService(Type serviceType, object serviceKey = null)
    {
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        if (serviceKey != null) return null;

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata(
                _manager.Settings.EmbeddingProvider,
                null,
                _manager.Settings.EmbeddingModel);
        }
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}
