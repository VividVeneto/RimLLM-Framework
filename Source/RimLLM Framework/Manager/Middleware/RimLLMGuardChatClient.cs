using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 前置守衛：防濫用節流在先、每日預算在後，通過才放行到下游。
/// 排在佇列之前：被守衛擋下的請求不該佔用併發名額。
/// </summary>
/// <remarks>
/// modId 在建立 client 時就綁定，不是逐次請求帶進來的，因此這一層是 per-mod 實例；
/// 節流狀態則放在所有實例共用的 <see cref="RimLLMThrottleStore"/>。
/// </remarks>
internal sealed class RimLLMGuardChatClient : DelegatingChatClient
{
    private readonly IRimLLMSettings _settings;
    private readonly RimLLMThrottleStore _throttleStore;
    private readonly RimLLMUsageTracker _usageTracker;
    private readonly string _modId;

    public RimLLMGuardChatClient(
        IChatClient innerClient,
        IRimLLMSettings settings,
        RimLLMThrottleStore throttleStore,
        string modId,
        RimLLMUsageTracker usageTracker)
        : base(innerClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _throttleStore = throttleStore ?? throw new ArgumentNullException(nameof(throttleStore));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> bufferedMessages = messages?.ToList();
        Guard(bufferedMessages);
        using RimLLMUsageTracker.BudgetReservation reservation = ReserveBudget(bufferedMessages, options);
        return await base.GetResponseAsync(bufferedMessages, options, cancellationToken).ConfigureAwait(false);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> bufferedMessages = messages?.ToList();

        // 防濫用檢查維持在取得列舉器時執行。預算 reservation 延到第一次列舉，
        // 避免呼叫端建立串流後從未列舉，永久占住每日額度。
        Guard(bufferedMessages);
        return StreamWithBudgetReservationAsync(bufferedMessages, options, cancellationToken);
    }

    private void Guard(IEnumerable<ChatMessage> messages)
    {
        _usageTracker.RecordCaller(_modId);

        if (_settings.EnableAntiAbuse)
        {
            _throttleStore.CheckAntiAbuse(_modId, countTowardWindow: !IsToolLoopContinuation(messages));
        }

    }

    private RimLLMUsageTracker.BudgetReservation ReserveBudget(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        long estimate = EstimateBudgetTokens(messages, options);
        if (!_usageTracker.TryReserveBudget(estimate, out RimLLMUsageTracker.BudgetReservation reservation))
        {
            throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
        }
        return reservation;
    }

    private static long EstimateBudgetTokens(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        long estimate = 0;
        if (messages != null)
        {
            foreach (ChatMessage message in messages)
            {
                if (!string.IsNullOrEmpty(message?.Text))
                {
                    estimate = SaturatingAdd(estimate, RimLLMChatClientExecutor.EstimateTokens(message.Text));
                }
            }
        }

        string cachedContext = RimLLMChatOptions.GetCachedContext(options);
        if (!string.IsNullOrEmpty(cachedContext))
        {
            estimate = SaturatingAdd(estimate, RimLLMChatClientExecutor.EstimateTokens(cachedContext));
        }

        // BuildOptions supplies 1024 when the caller leaves MaxOutputTokens unset.
        estimate = SaturatingAdd(estimate, Math.Max(0, options?.MaxOutputTokens ?? 1024));
        return Math.Max(1, estimate);
    }

    private static long SaturatingAdd(long left, long right)
    {
        return right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithBudgetReservationAsync(
        List<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken,
        [EnumeratorCancellation] CancellationToken enumerationCancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumerationCancellationToken);
        using RimLLMUsageTracker.BudgetReservation reservation = ReserveBudget(messages, options);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, linkedCts.Token)
            .WithCancellation(linkedCts.Token)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// 最後一則訊息是工具結果，代表這是工具迴圈的續輪而非新請求。
    /// 以訊息形狀判定而非依賴框架自己的包裝器，呼叫端直接使用 MEAI 的
    /// FunctionInvokingChatClient 時同樣適用。
    /// </summary>
    internal static bool IsToolLoopContinuation(IEnumerable<ChatMessage> messages)
    {
        if (messages == null) return false;

        ChatMessage last = null;
        foreach (ChatMessage message in messages)
        {
            last = message;
        }

        if (last == null || last.Role != ChatRole.Tool) return false;

        return last.Contents != null && last.Contents.Any(content => content is FunctionResultContent);
    }
}
