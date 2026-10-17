using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 以 RimLLM 的 <see cref="IChatClient"/> 承接 Mod 兼容性檢查器的 AI 診斷分析流量。
/// </summary>
/// <remarks>
/// 本類別刻意不引用任何 ModCompatChecker 專有型別，以求徹底解耦與高測試性；
/// 它的公開介面只處理中性型別（string）。
/// 所有呼叫端（UnifiedWindow、ErrorAnalysisWindow）都在背景執行緒同步呼叫 AIService.CallAPIWithTimeout，
/// 因此本 Client 也以同步阻塞方式執行。
/// </remarks>
internal sealed class ModCompatCheckerCompatClient : CompatClientBase
{
    /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
    public ModCompatCheckerCompatClient()
        : base(ModCompatCheckerCompatTarget.PackageId)
    {
    }

    /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證訊息建構與參數傳遞。</summary>
    internal ModCompatCheckerCompatClient(IChatClient chat)
        : base(ModCompatCheckerCompatTarget.PackageId, chat)
    {
    }

    /// <summary>
    /// 同步執行 AI 診斷分析並回傳模型回覆的純文字。
    /// </summary>
    /// <remarks>
    /// 取消語意比照原生 <c>AIService.CallAPIWithTimeout</c>（約 100ms 粒度輪詢 <c>cancelFlag</c>）：
    /// 偵測到取消即 signal 並擲出 <see cref="OperationCanceledException"/>，不再等到逾時，
    /// 避免取消後繼續燒 token 且 UI 看似卡死。呼叫端（<c>CallAPIWithTimeoutPrefix</c>）
    /// 會把取消／逾時轉為提示文字並跳過原生，不再跑第二次等待。
    /// </remarks>
    /// <param name="userMessage">使用者問題或診斷提示詞（已由 PromptBuilder 建構）。</param>
    /// <param name="timeoutSeconds">逾時秒數；小於等於 0 視為無限等待（仍響應取消）。</param>
    /// <param name="cancelFlag">來自原 Mod 的取消旗標參照。</param>
    /// <returns>模型回覆的純文字內容。</returns>
    /// <exception cref="OperationCanceledException">偵測到 <c>cancelFlag</c>，或等待中的請求被取消。</exception>
    /// <exception cref="TimeoutException">超過 <c>timeoutSeconds</c> 仍未完成（含供應商無視取消 token 的兜底）。</exception>
    public string CallAPI(string userMessage, int timeoutSeconds, ref bool cancelFlag)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, userMessage ?? string.Empty)
        };

        ChatOptions options = BuildOptions();

        TimeSpan? limit = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : (TimeSpan?)null;
        using var cts = limit.HasValue ? new CancellationTokenSource(limit.Value) : new CancellationTokenSource();
        Task<ChatResponse> task = Task.Run(() => Chat.GetResponseAsync(messages, options, cts.Token));

        // 逾時期限只算一次，迴圈內每次疊代只取一次 UtcNow。
        DateTime deadline = limit.HasValue ? DateTime.UtcNow + limit.Value : DateTime.MaxValue;
        try
        {
            // 以 Wait(50) 取代 IsCompleted + Sleep 輪詢：任務完成時立刻醒來，
            // 不再有最長 50ms 的睡眠 overshoot；取消旗標維持約 50ms 粒度輪詢。
            while (!task.Wait(50))
            {
                if (cancelFlag)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (Exception)
                    {
                        // 取消 signal 盡力而為；取消語意由下面的例外保證。
                    }
                    throw new OperationCanceledException("ModCompatChecker 分析已由使用者取消，不再等待 RimLLM 回應。");
                }
                if (DateTime.UtcNow >= deadline)
                {
                    // 供應商無視取消 token 時 cts 不會讓 task 失敗，此處兜底超時，不無限等待。
                    throw new TimeoutException($"ModCompatChecker 分析經 RimLLM 請求逾時（{timeoutSeconds} 秒）。");
                }
            }
        }
        catch (AggregateException agg)
        {
            // Wait 的錯誤包裝與 IsCompleted 輪詢不同：保留原始例外型別與堆疊，
            // 讓呼叫端的取消／逾時／RimLLMException 分流維持不變。
            ExceptionDispatchInfo.Capture(agg.InnerException ?? (Exception)agg).Throw();
            throw;
        }

        // GetAwaiter().GetResult() 直接解包內層例外（不會是 AggregateException，
        // 那是 .Wait()／.Result 的行為），原始堆疊得以保留，無需轉拋。
        ChatResponse response = task.GetAwaiter().GetResult();
        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// 接管請求的輸出上限，對齊 ModCompatChecker 原生請求的 <c>max_tokens: 1500</c>；
    /// 不明確設定時框架會補 1024，診斷報告的尾段會被截掉。
    /// </summary>
    internal const int MaxOutputTokens = 1500;

    /// <summary>
    /// 診斷分析著重精準度，預設關閉思考鏈（DisableReasoning）並使用溫和的溫度（0.3）。
    /// </summary>
    protected override ChatOptions BuildOptions()
    {
        return new RimLLMChatOptions
        {
            DisableReasoning = true,
            Temperature = 0.3f,
            MaxOutputTokens = MaxOutputTokens
        };
    }
}
