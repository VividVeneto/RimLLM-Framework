using System;
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

        // 歸屬資訊來自請求開始時的快照（DecisionEvaluation），await 之後不得重讀設定：
        // 請求途中玩家切換啟用供應商，token 若記到新供應商就會污染帳本（與 RimLLMEmbeddingClient 同一考量）。
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
