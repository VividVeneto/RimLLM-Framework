extern alias bclasync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
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

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard(messages);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            // 刻意在建立列舉器之前就檢查，與非串流路徑同時計入同一個視窗：
            // 若延後到第一次 MoveNextAsync 才檢查，尚未開始列舉的請求就不會計數，
            // 送出大量請求卻不列舉即可繞過節流。
            Guard(messages);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        private void Guard(IEnumerable<ChatMessage> messages)
        {
            _usageTracker.RecordCaller(_modId);

            if (_settings.EnableAntiAbuse)
            {
                _throttleStore.CheckAntiAbuse(_modId, countTowardWindow: !IsToolLoopContinuation(messages));
            }

            if (!_usageTracker.CheckBudgetLimit())
            {
                throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
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
#pragma warning restore S101
}
