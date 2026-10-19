using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework;

/// <summary>
/// 綁定單一 Mod 的決策服務 facade。與 embedding facade 一樣，每次呼叫都先經過
/// 呼叫者記錄、防濫用節流與每日預算守衛，再把供應商回報的 token 記入用量帳本。
/// 透過 <see cref="RimLLMProvider.CreateDecisionService"/> 取得。
/// </summary>
public sealed class RimLLMDecisionClient
{
    private readonly RimLLMManager _manager;
    private readonly string _modId;

    internal RimLLMDecisionClient(RimLLMManager manager, string modId)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
    }

    /// <summary>
    /// 對 <paramref name="state"/> 執行一個已建好的決策計畫。
    /// 金鑰、端點與模型取自玩家在 Decision 分頁的設定；<paramref name="model"/> 可覆寫模型。
    /// 供應端失敗一律以 <see cref="RimLLMException"/>（含 <see cref="LLMError"/> 代碼）呈現；
    /// 呼叫端取消維持 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<JevResult> EvaluateAsync(
        string state,
        JevDecisionPlan plan,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        _manager.UsageTracker.RecordCaller(_modId);
        _manager.CheckAntiAbuseForMod(_modId);
        if (!_manager.UsageTracker.CheckBudgetLimit())
        {
            throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
        }

        DecisionEvaluation evaluation = await _manager.DecisionService
            .EvaluateDetailedAsync(state, plan, model, cancellationToken)
            .ConfigureAwait(false);

        return RecordUsage(evaluation);
    }

    /// <summary>
    /// 以指定供應商執行一個已建好的決策計畫；端點與金鑰取自該供應商在框架設定中保存的值。
    /// <paramref name="providerId"/> 為 null 或空字串時，等同 <see cref="EvaluateAsync"/>（使用目前啟用的供應商）。
    /// </summary>
    /// <remarks>
    /// 這是單次呼叫的覆寫，不會改動玩家的設定；用量則記到實際使用的供應商帳上。
    /// 缺金鑰、供應商不存在或供應商未啟用時，會在送出任何網路請求之前就擲出
    /// <see cref="RimLLMException"/>（<see cref="LLMError.InvalidKey"/> 或 <see cref="LLMError.Unknown"/>）。
    /// </remarks>
    public async Task<JevResult> EvaluateWithProviderAsync(
        string state,
        JevDecisionPlan plan,
        string providerId,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        _manager.UsageTracker.RecordCaller(_modId);
        _manager.CheckAntiAbuseForMod(_modId);
        if (!_manager.UsageTracker.CheckBudgetLimit())
        {
            throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
        }

        DecisionEvaluation evaluation = await _manager.DecisionService
            .EvaluateWithProviderDetailedAsync(state, plan, providerId, model, cancellationToken)
            .ConfigureAwait(false);

        return RecordUsage(evaluation);
    }

    /// <summary>
    /// 取得指定供應商可用的決策模型清單（型別為模型名稱字串，避免第三方 Mod 依賴 Kkdev92.Jev）。
    /// 端點與金鑰取自該供應商在設定中保存的值；<paramref name="providerId"/> 為空時用啟用中的供應商。
    /// 模型清單查詢不消耗推論額度，也不計入每日預算。
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        _manager.UsageTracker.RecordCaller(_modId);
        _manager.CheckAntiAbuseForMod(_modId);

        return await _manager.DecisionService
            .FetchDecisionModelsAsync(providerId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把供應商回報的 token 記入用量帳本。
    /// 歸屬資訊來自請求開始時的快照（DecisionEvaluation），await 之後不得重讀設定：
    /// 請求途中玩家切換啟用供應商，token 若記到新供應商就會污染帳本（與 RimLLMEmbeddingClient 同一考量）。
    /// </summary>
    private JevResult RecordUsage(DecisionEvaluation evaluation)
    {
        JevResult result = evaluation.Result;
        string modelId = result.ActualModel;
        if (string.IsNullOrEmpty(modelId))
        {
            modelId = evaluation.ModelName;
        }

        if (result.Usage is { } usage)
        {
            _manager.RecordUsage(
                evaluation.ProviderId,
                modelId,
                usage.InputTokens > int.MaxValue ? int.MaxValue : (int)usage.InputTokens,
                usage.OutputTokens > int.MaxValue ? int.MaxValue : (int)usage.OutputTokens);
        }

        return result;
    }
}
