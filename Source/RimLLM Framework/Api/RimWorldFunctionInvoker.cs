using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Api;

/// <summary>
/// 提供 RimWorld / Unity 環境專用的主執行緒安全工具呼叫擴充方法。
/// </summary>
public static class RimWorldFunctionInvoker
{
    /// <summary>
    /// 將 IChatClient 包裝為支援自動多輪工具呼叫（Function Invoking）的客戶端。
    /// 所有工具（AIFunction）的叫用委派都會自動透過 RimLLMDispatcher 排入 Unity 主執行緒執行，
    /// 防止背景執行緒存取遊戲狀態時引發 Unity 跨執行緒崩潰。
    /// </summary>
    /// <param name="innerClient">底層聊天客戶端。</param>
    /// <param name="maxIterations">單次請求允許工具自動執行的最大迴圈次數（預設 10）。</param>
    /// <returns>具備自動主執行緒工具執行能力的 IChatClient。</returns>
    public static IChatClient AsMainThreadFunctionInvokingClient(
        this IChatClient innerClient,
        int maxIterations = 10)
    {
        if (innerClient == null) throw new ArgumentNullException(nameof(innerClient));

        return new FunctionInvokingChatClient(innerClient)
        {
            MaximumIterationsPerRequest = maxIterations > 0 ? maxIterations : 10,
            AllowConcurrentInvocation = false, // Unity 為單執行緒環境，禁止工具並行搶佔
            // 工具擲出的例外訊息回給模型，讓它能修正引數重試；預設只回「Error: Function failed.」，
            // 模型無從得知錯在哪裡。
            IncludeDetailedErrors = true,
            FunctionInvoker = async (FunctionInvocationContext context, CancellationToken cancellationToken) =>
            {
                if (context?.Function == null)
                {
                    return null;
                }

                // 確保工具執行調度至 Unity 主執行緒。
                // 內層刻意不加 ConfigureAwait(false)：工具若含 await，續行必須沿著主執行緒的
                // SynchronizationContext 回到主線程，否則後半段仍會在執行緒池碰遊戲狀態。
                //
                // 必須 AsTask()：InvokeAsync 回傳 ValueTask<object>，少了轉換，多載解析會選到
                // EnqueueOnMainThreadAsync<T>(Func<T>) 且 T = ValueTask<object>，整顆 ValueTask
                // 被裝箱成工具結果送給模型（{"isCompleted":…,"result":42}），async 工具更會在
                // 序列化時阻塞。
                return await RimLLMDispatcher.EnqueueOnMainThreadAsync(
                    () => context.Function.InvokeAsync(context.Arguments, cancellationToken).AsTask()
                ).ConfigureAwait(false);
            }
        };
    }
}
