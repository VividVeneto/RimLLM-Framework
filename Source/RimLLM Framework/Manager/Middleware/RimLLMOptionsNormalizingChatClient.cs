using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 把玩家在設定中指定的預設思考強度套進未指定的請求。
/// </summary>
/// <remarks>
/// 這一層是整條堆疊的最外層，下游各層看到的都是已套用預設值的選項。
/// </remarks>
internal sealed class RimLLMOptionsNormalizingChatClient : DelegatingChatClient
{
    private readonly IRimLLMSettings _settings;

    public RimLLMOptionsNormalizingChatClient(IChatClient innerClient, IRimLLMSettings settings)
        : base(innerClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(messages, Normalize(options), cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetStreamingResponseAsync(messages, Normalize(options), cancellationToken);
    }

    /// <summary>
    /// 呼叫端已指定思考強度、或玩家未設定預設值時原樣放行。
    /// 需要改寫時一律複製，不可就地修改呼叫端傳進來的物件。
    /// </summary>
    private ChatOptions Normalize(ChatOptions options)
    {
        if (_settings.DefaultReasoningEffort == null) return options;
        if (options?.Reasoning?.Effort != null) return options;

        ChatOptions clone = options?.Clone() ?? new ChatOptions();
        clone.Reasoning = new ReasoningOptions { Effort = _settings.DefaultReasoningEffort };
        return clone;
    }
}
