using System;
using System.IO;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RimLLM_Framework.Mod;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// 基於 Markdig AST 解析引擎，把 Markdown 轉成 Unity 舊版 IMGUI 能顯示的 rich text。
///
/// 舊版 IMGUI rich text 只認得 b、i、size、color、material、quad 六個標籤。
/// 本實作透過 Markdig 構建 CommonMark 語法樹，精準將各語法節點映射至支援的標籤與縮排符號。
/// </summary>
public static class RimLLMMarkdown
{
    private const string CodeColor = "#4ec9b0";
    private const string LinkColor = "#6cb6ff";
    private const string MutedColor = "#9aa0a6";
    private const string ColorTagOpen = "<color=";
    private const string ColorTagClose = "</color>";
    private const string BoldOpenMarker = "\uE000";
    private const string BoldCloseMarker = "\uE001";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly int[] HeadingSizes = [20, 17, 15];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static readonly System.Text.RegularExpressions.Regex BoldPattern =
        new(@"\*\*\s*([^\*\n]+?)\s*\*\*", System.Text.RegularExpressions.RegexOptions.Compiled, RegexTimeout);

    /// <summary>
    /// 粗體預轉換不得碰的區域：``` 或 ~~~ 圍籬（含串流中尚未閉合的尾巴）、行內碼、四空格／tab 縮排的程式碼行。
    /// 這些地方的 ** 是程式碼（Python 的 **kwargs、a ** b），不是 Markdown 標記。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProtectedRegion =
        new(
            @"(?:^|(?<=\n))(`{3,}|~{3,})[\s\S]*?(?:\n\1[ \t]*(?=\r?\n|\z)|\z)" +
            @"|`+[^`\n]*`+" +
            @"|(?:^|(?<=\n))(?:[ ]{4}|\t)[^\n]*",
            System.Text.RegularExpressions.RegexOptions.Compiled, RegexTimeout);

    private static readonly System.Text.RegularExpressions.Regex UnityTagPattern =
        new(@"<(/?(?:b|i|size|color|material|quad)(?:=[^>]*)?)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled, RegexTimeout);

    private static readonly System.Text.RegularExpressions.Regex RawHtmlTagPattern =
        new(
            @"</?[A-Za-z][^>\r\n]*>|<!--[\s\S]*?-->|<![^>\r\n]*>|<\?[^>\r\n]*\?>",
            System.Text.RegularExpressions.RegexOptions.Compiled,
            RegexTimeout);

    /// <summary>
    /// 預先處理 Markdown 中的粗體標記。
    /// 繁簡中文環境下，全形括號或標點與粗體星號相鄰（例如 "**快速排序法（Quick Sort）**的"）時，
    /// 會觸發 CommonMark 規範中關於標點符號側翼（flanking delimiter）的判定缺陷而拒絕閉合。
    /// 本方法在排除 <see cref="ProtectedRegion"/>（圍籬、行內碼、縮排程式碼）後，
    /// 將一般文本中的 **內容** 預先轉換為 Unity 原生支援的 &lt;b&gt;內容&lt;/b&gt;。
    /// </summary>
    private static string PreprocessMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var sb = new StringBuilder(markdown.Length + 32);
        int last = 0;
        foreach (System.Text.RegularExpressions.Match region in ProtectedRegion.Matches(markdown))
        {
            sb.Append(BoldPattern.Replace(markdown.Substring(last, region.Index - last), BoldOpenMarker + "$1" + BoldCloseMarker));
            sb.Append(region.Value);
            last = region.Index + region.Length;
        }
        sb.Append(BoldPattern.Replace(markdown.Substring(last), BoldOpenMarker + "$1" + BoldCloseMarker));
        return sb.ToString();
    }

    /// <summary>
    /// 把程式碼裡剛好長得像 Unity rich text 標籤的片段（&lt;b&gt;、&lt;/i&gt;、&lt;color=red&gt;…）改成全形角括號。
    /// Unity 舊版 IMGUI 只認得 b/i/size/color/material/quad 六種標籤，其餘如 &lt;iostream&gt;、vector&lt;int&gt;、a &lt; b
    /// 都會原樣顯示（實機驗證過），不需要也不該動它們；但程式碼裡的 HTML 標籤若不跳脫，會被當成格式套用，
    /// 甚至提早關閉包住整段程式碼的 &lt;color&gt;。剪貼簿複製時會把全形角括號還原成 ASCII。
    /// </summary>
    public static string EscapeCode(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
        return UnityTagPattern.Replace(text, "＜$1＞");
    }


    private static string SanitizeUnityTags(string markdown, bool preserveTrustedColorWrapper)
    {
        if (string.IsNullOrEmpty(markdown) || markdown.IndexOf('<') < 0)
        {
            return markdown;
        }

        var sb = new StringBuilder(markdown.Length);
        int last = 0;
        bool trustedColorOpen = false;
        foreach (System.Text.RegularExpressions.Match match in UnityTagPattern.Matches(markdown))
        {
            sb.Append(markdown, last, match.Index - last);
            string tag = match.Value;
            bool trustedOpen = preserveTrustedColorWrapper &&
                               string.Equals(tag, "<color=silver>", StringComparison.OrdinalIgnoreCase);
            bool trustedClose = preserveTrustedColorWrapper &&
                                string.Equals(tag, "</color>", StringComparison.OrdinalIgnoreCase) && trustedColorOpen;
            if (trustedOpen)
            {
                trustedColorOpen = true;
            }
            else if (trustedClose)
            {
                trustedColorOpen = false;
            }

            sb.Append(trustedOpen || trustedClose
                ? tag
                : "＜" + tag.Substring(1, tag.Length - 2) + "＞");
            last = match.Index + match.Length;
        }

        sb.Append(markdown, last, markdown.Length - last);
        return sb.ToString();
    }

    /// <summary>
    /// 跳脫供應商或其他不受信任輸入中的 Unity rich-text 標籤。
    /// 與 <see cref="ToRichText"/> 不同，這條路徑不允許任何預先存在的灰色 wrapper。
    /// </summary>
    internal static string EscapeUntrustedUnityTags(string text)
    {
        return SanitizeUnityTags(text, preserveTrustedColorWrapper: false);
    }

    /// <summary>
    /// 將 Markdown 文字轉換為 Unity IMGUI 支援的 rich text。
    /// </summary>
    public static string ToRichText(string markdown)
    {
        if (markdown == null) return null;
        if (markdown.Length == 0) return string.Empty;

        string preprocessed = PreprocessMarkdown(SanitizeUnityTags(markdown, preserveTrustedColorWrapper: true));
        MarkdownDocument document = Markdown.Parse(preprocessed, Pipeline);
        var sb = new StringBuilder(preprocessed.Length + 64);
        var renderer = new UnityRichTextRenderer(sb);
        renderer.Render(document);
        return sb.ToString()
            .Replace(BoldOpenMarker, "<b>")
            .Replace(BoldCloseMarker, "</b>")
            .TrimEnd('\r', '\n');
    }

    private sealed class UnityRichTextRenderer
    {
        private readonly StringBuilder _sb;
        private int _listDepth;
        private static readonly char[] NewlineSeparator = ['\n'];

        public UnityRichTextRenderer(StringBuilder sb)
        {
            _sb = sb;
        }

        public void Render(MarkdownDocument document)
        {
            bool first = true;
            foreach (Block block in document)
            {
                // 頂層區塊（段落、清單、標題、程式碼）之間留一行空白，與一般 Markdown 呈現一致；
                // 清單項目、引用行等區塊內部的換行由各自的 Render 方法處理。
                if (!first)
                {
                    _sb.Append("\n\n");
                }
                RenderBlock(block);
                first = false;
            }
        }

        private void RenderBlock(Block block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    RenderHeading(heading);
                    break;
                case ParagraphBlock paragraph:
                    RenderParagraph(paragraph);
                    break;
                case QuoteBlock quote:
                    RenderQuote(quote);
                    break;
                case ListBlock list:
                    RenderList(list);
                    break;
                case ListItemBlock listItem:
                    RenderListItem(listItem);
                    break;
                case FencedCodeBlock fenced:
                    RenderFencedCode(fenced);
                    break;
                case CodeBlock code:
                    RenderCodeBlock(code);
                    break;
                case ThematicBreakBlock _:
                    _sb.Append(ColorTagOpen).Append(MutedColor).Append(">------------------------------------------------").Append(ColorTagClose);
                    break;
                case HtmlBlock html:
                    RenderHtmlBlock(html);
                    break;
                default:
                    if (block is ContainerBlock container)
                    {
                        foreach (Block child in container)
                        {
                            RenderBlock(child);
                        }
                    }
                    break;
            }
        }

        private void RenderHeading(HeadingBlock heading)
        {
            int level = heading.Level;
            bool hasSize = level >= 1 && level <= HeadingSizes.Length;

            if (hasSize)
            {
                _sb.Append("<size=").Append(HeadingSizes[level - 1]).Append("><b>");
            }
            else
            {
                _sb.Append("<b>");
            }

            if (heading.Inline != null)
            {
                RenderInlines(heading.Inline);
            }

            if (hasSize)
            {
                _sb.Append("</b></size>");
            }
            else
            {
                _sb.Append("</b>");
            }
        }

        private void RenderParagraph(ParagraphBlock paragraph)
        {
            if (paragraph.Inline != null)
            {
                RenderInlines(paragraph.Inline);
            }
        }

        private void RenderQuote(QuoteBlock quote)
        {
            var subSb = new StringBuilder();
            var subRenderer = new UnityRichTextRenderer(subSb);
            bool firstChild = true;
            foreach (Block child in quote)
            {
                if (!firstChild) subSb.Append('\n');
                subRenderer.RenderBlock(child);
                firstChild = false;
            }

            string content = subSb.ToString().TrimEnd('\r', '\n');
            string[] lines = content.Replace("\r\n", "\n").Split(NewlineSeparator);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append(ColorTagOpen).Append(MutedColor).Append(">| ").Append(lines[i]).Append(ColorTagClose);
            }
        }

        private void RenderList(ListBlock list)
        {
            _listDepth++;
            bool isOrdered = list.IsOrdered;
            int itemIndex = 1;

            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                if (list[i] is ListItemBlock listItem)
                {
                    RenderListItemInternal(listItem, isOrdered, itemIndex++);
                }
            }
            _listDepth--;
        }

        private void RenderListItem(ListItemBlock listItem)
        {
            RenderListItemInternal(listItem, false, 1);
        }

        private void RenderListItemInternal(ListItemBlock listItem, bool isOrdered, int index)
        {
            string indent = new(' ', _listDepth * 2);
            _sb.Append(indent);
            if (isOrdered)
            {
                _sb.Append(index).Append(". ");
            }
            else
            {
                _sb.Append("• ");
            }

            bool firstChild = true;
            foreach (Block child in listItem)
            {
                if (!firstChild) _sb.Append('\n');
                if (child is ParagraphBlock p)
                {
                    if (p.Inline != null) RenderInlines(p.Inline);
                }
                else
                {
                    RenderBlock(child);
                }
                firstChild = false;
            }
        }

        private void RenderFencedCode(FencedCodeBlock fenced)
        {
            RenderCodeLines(fenced);
        }

        private void RenderCodeBlock(CodeBlock code)
        {
            RenderCodeLines(code);
        }

        // FencedCodeBlock 繼承 CodeBlock，共用同一份行繪製邏輯。
        private void RenderCodeLines(CodeBlock code)
        {
            _sb.Append(ColorTagOpen).Append(CodeColor).Append(">");
            for (int i = 0; i < code.Lines.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append("  ").Append(EscapeCode(code.Lines.Lines[i].Slice.ToString()));
            }
            _sb.Append(ColorTagClose);
        }

        private void RenderHtmlBlock(HtmlBlock html)
        {
            for (int i = 0; i < html.Lines.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append(SanitizeRawHtml(html.Lines.Lines[i].Slice.ToString()));
            }
        }

        private static string SanitizeRawHtml(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
            {
                return text;
            }

            return RawHtmlTagPattern.Replace(text, match =>
            {
                string tag = match.Value;
                if (IsTrustedColorTag(tag))
                {
                    return tag;
                }

                return "＜" + tag.Substring(1, tag.Length - 2) + "＞";
            });
        }

        private static bool IsTrustedColorTag(string tag)
        {
            return string.Equals(tag, "<color=silver>", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "</color>", StringComparison.OrdinalIgnoreCase);
        }

        private void RenderInlines(ContainerInline container)
        {
            foreach (Inline inline in container)
            {
                RenderInline(inline);
            }
        }

        private void RenderInline(Inline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    _sb.Append(literal.Content.ToString());
                    break;
                case EmphasisInline emphasis:
                    RenderEmphasis(emphasis);
                    break;
                case CodeInline code:
                    _sb.Append(ColorTagOpen).Append(CodeColor).Append(">")
                       .Append(EscapeCode(code.Content))
                       .Append(ColorTagClose);
                    break;
                case LinkInline link:
                    RenderLink(link);
                    break;
                case LineBreakInline _:
                    _sb.Append('\n');
                    break;
                case HtmlInline html:
                    _sb.Append(SanitizeRawHtml(html.Tag));
                    break;
                default:
                    if (inline is ContainerInline subContainer)
                    {
                        RenderInlines(subContainer);
                    }
                    break;
            }
        }

        private void RenderEmphasis(EmphasisInline emphasis)
        {
            if (emphasis.DelimiterChar == '_')
            {
                // 為了防止 snake_case（如 some_field_name）誤判，若是底線則原樣輸出其子內容
                foreach (Inline child in emphasis)
                {
                    RenderInline(child);
                }
                return;
            }

            bool isBold = emphasis.DelimiterCount >= 2;
            if (isBold)
            {
                _sb.Append("<b>");
            }
            else
            {
                _sb.Append("<i>");
            }

            foreach (Inline child in emphasis)
            {
                RenderInline(child);
            }

            if (isBold)
            {
                _sb.Append("</b>");
            }
            else
            {
                _sb.Append("</i>");
            }
        }

        private void RenderLink(LinkInline link)
        {
            _sb.Append(ColorTagOpen).Append(LinkColor).Append(">");
            if (link.FirstChild != null)
            {
                foreach (Inline child in link)
                {
                    RenderInline(child);
                }
            }
            else
            {
                _sb.Append(link.Url);
            }
            _sb.Append(ColorTagClose);
        }
    }
}
#pragma warning restore S101, S2342
