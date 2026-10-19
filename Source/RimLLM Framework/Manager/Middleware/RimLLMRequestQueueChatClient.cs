using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 優先權佇列與並行限流。高 Priority 的請求先取得名額，同優先級則先到先得。
/// </summary>
/// <remarks>
/// 每個候選嘗試各包一層；名額只在真正打 API 的期間持有，退避與換手不佔用名額。
/// </remarks>
internal sealed class RimLLMRequestQueueChatClient : DelegatingChatClient
{
    private readonly RimLLMRequestQueue _queue;

    /// <summary>路由層會扣除排隊時間，讓健康帳本只記錄供應商本身的延遲。</summary>
    public long QueueWaitMilliseconds { get; private set; }

    public RimLLMRequestQueueChatClient(IChatClient innerClient, RimLLMRequestQueue queue)
        : base(innerClient)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        var wait = Stopwatch.StartNew();
        using var slot = await _queue
            .AcquireSlotAsync(RimLLMChatOptions.GetPriority(options), cancellationToken)
            .ConfigureAwait(false);
        QueueWaitMilliseconds = wait.ElapsedMilliseconds;
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
        => StreamQueuedAsync(messages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamQueuedAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Iterator 在第一次 MoveNextAsync 才取得名額；僅建立 enumerable／enumerator 不會送出請求。
        // EnumeratorCancellation 由編譯器合併方法與列舉端的 token，不必手動管理 linked CTS。
        var wait = Stopwatch.StartNew();
        using var slot = await _queue
            .AcquireSlotAsync(RimLLMChatOptions.GetPriority(options), cancellationToken)
            .ConfigureAwait(false);
        QueueWaitMilliseconds = wait.ElapsedMilliseconds;

        // using 與 await foreach 共同保證正常結束、例外、取消及提早停止時歸還名額並釋放內層。
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
