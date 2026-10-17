using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 相容層 Client 的共用基礎：延遲取得 IChatClient、同步阻塞橋接、取消與逾時處理。
/// 三個相容層各自繼承或組合此基礎，避免重複的啟動樣板。
/// </summary>
internal abstract class CompatClientBase
{
    private readonly string _packageId;
    private IChatClient _chat;

    protected CompatClientBase(string packageId)
    {
        _packageId = packageId;
    }

    /// <summary>測試用：注入假的 <see cref="IChatClient"/>。</summary>
    protected CompatClientBase(string packageId, IChatClient chat)
    {
        _packageId = packageId;
        _chat = chat;
    }

    /// <summary>延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
    protected IChatClient Chat => _chat ?? (_chat = RimLLMProvider.CreateChatClient(_packageId));

    /// <summary>
    /// 同步執行 async 任務並回傳結果，保留原始堆疊。
    /// Auto Translation 與 ModCompatChecker 的背景執行緒同步呼叫用。
    /// </summary>
    protected static T RunSync<T>(Func<Task<T>> asyncFunc)
    {
        Task<T> task = Task.Run(asyncFunc);
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (AggregateException agg) when (agg.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(agg.Flatten().InnerExceptions[0]).Throw();
            throw; // unreachable
        }
    }

    /// <summary>建構該相容層專用的 ChatOptions（MaxOutputTokens、DisableReasoning、Temperature 等）。</summary>
    protected abstract ChatOptions BuildOptions();
}
