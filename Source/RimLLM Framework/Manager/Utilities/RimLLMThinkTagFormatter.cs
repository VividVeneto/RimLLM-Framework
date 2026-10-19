using System.Text;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 把串流 update 攤成可顯示的文字，並用 <c>&lt;think&gt;</c> 標記包住推理段落。
/// </summary>
/// <remarks>
/// 這是呈現層的工具，不是框架資料流的一部分。框架本身一律以 MEAI 原生的
/// <see cref="TextReasoningContent"/> 傳遞推理內容——先前它在 executor 裡就被合成成
/// <c>&lt;think&gt;</c> 文字塞進文字流，於是每一個讀 <c>Text</c> 的呼叫端（結構化輸出、
/// 快取鍵、JSON 解析）都得先把標籤剝掉。想要那種扁平表述的介面自己呼叫這裡即可。
///
/// 有狀態：跨 update 記住是否還在推理段落中，因此一個串流配一個實例。
/// </remarks>
internal sealed class RimLLMThinkTagFormatter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private bool _inReasoning;

    /// <summary>
    /// 取出一個 update 的可顯示文字。推理內容首次出現時前置 <c>&lt;think&gt;</c>，
    /// 回到一般文字時補上 <c>&lt;/think&gt;</c>。沒有可顯示內容時回傳空字串。
    /// </summary>
    public string Append(ChatResponseUpdate update)
    {
        if (update?.Contents == null) return string.Empty;

        var sb = new StringBuilder();
        foreach (AIContent part in update.Contents)
        {
            if (part is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
            {
                if (!_inReasoning)
                {
                    _inReasoning = true;
                    sb.Append(OpenTag);
                }
                sb.Append(reasoning.Text);
            }
            else if (part is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                if (_inReasoning)
                {
                    _inReasoning = false;
                    sb.Append(CloseTag);
                }
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 串流結束時呼叫。回應整段都是推理內容時，收尾標籤只能在這裡補。
    /// </summary>
    public string Complete()
    {
        if (!_inReasoning) return string.Empty;
        _inReasoning = false;
        return CloseTag;
    }
}
