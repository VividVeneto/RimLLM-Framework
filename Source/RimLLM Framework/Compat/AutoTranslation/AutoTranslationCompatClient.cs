using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 以 RimLLM 的 <see cref="IChatClient"/> 承接 Auto Translation 的文本與批次翻譯流量。
/// </summary>
/// <remarks>
/// 本類別刻意不引用任何 Auto Translation 專有型別，以求徹底解耦與高測試性；
/// 它的公開介面只處理中性型別（string、JSON）。
/// </remarks>
internal sealed class AutoTranslationCompatClient : CompatClientBase
{
    /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
    public AutoTranslationCompatClient()
        : base(AutoTranslationCompatTarget.PackageId)
    {
    }

    /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證訊息建構、參數傳遞與 JSON 回傳格式。</summary>
    internal AutoTranslationCompatClient(IChatClient chat)
        : base(AutoTranslationCompatTarget.PackageId, chat)
    {
    }

    /// <summary>
    /// 同步執行翻譯請求並回傳 OpenAI 相容 JSON 回應。
    /// Auto Translation 的背景工作線程透過此方法取得翻譯結果。
    /// </summary>
    /// <param name="text">待翻譯文字或 XML 批次文件。</param>
    /// <param name="prompt">系統提示詞（包含翻譯規則與佔位符防護說明）。</param>
    /// <returns>包含 content、prompt_tokens 與 completion_tokens 的 JSON 字串。</returns>
    public string GetResponseUnsafe(string text, string prompt)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, prompt ?? string.Empty),
            new(ChatRole.User, text ?? string.Empty)
        };

        ChatOptions options = BuildOptions();

        ChatResponse response = RunSync(() => Chat.GetResponseAsync(messages, options, CancellationToken.None));

        string responseText = response.Text ?? string.Empty;
        int inputTokens = ToInt32Saturating(response.Usage?.InputTokenCount);
        int outputTokens = ToInt32Saturating(response.Usage?.OutputTokenCount);

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            content = responseText,
            prompt_tokens = inputTokens,
            completion_tokens = outputTokens
        }, SharedJsonOptions);
    }

    internal static readonly System.Text.Json.JsonSerializerOptions SharedJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>用量 token 轉 int：飽和截斷，避免 unchecked 靜默溢位。</summary>
    private static int ToInt32Saturating(long? value)
    {
        if (!value.HasValue || value.Value <= 0) return 0;
        return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
    }

    /// <summary>
    /// 接管請求的輸出上限。Auto Translation 原生的 OpenAI 相容翻譯器不設 max_tokens，
    /// 而框架對未指定的請求預設補 1024；哨兵開的是 2000 token 的批次翻譯，譯文常比原文長，
    /// 1024 會把批次 XML 截斷、解析失敗後整批回原文。給到與批次同大小：
    /// 有些伺服器（vLLM 等）會以「輸入 + max_tokens ≤ 上下文」驗證，4k 上下文的本地模型
    /// 2000 輸入 + 2048 輸出剛好塞得下，再大就會被直接 400 拒絕。
    /// </summary>
    internal const int MaxOutputTokens = 2048;

    /// <summary>
    /// 翻譯任務著重精準度與低延遲，預設關閉思考鏈（DisableReasoning）並使用溫和的溫度（0.3）。
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
