using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RimLLM_Framework.Mod;

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
    /// <summary>欄位分隔符。用全形直線而非 ASCII <c>|</c>，讓渲染結果一眼看出是欄位分隔而非殘留的 markdown。</summary>
    private const string CellSeparator = "｜";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly int[] HeadingSizes = [20, 17, 15];

    // 表格解析交給 Markdig 的 PipeTables 擴充：只有套件才認得圍籬、行內碼與縮排程式碼。
    // 自己用正則掃 |---| 會把程式碼區塊裡的表格誤判成表格，還會破壞 <color> 配對。
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

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
    /// 本方法在排除 <see cref="ProtectedRegion"/>（圍籬、行內碼、縮排程式碼）與
    /// <paramref name="extraSpans"/>（表格）後，將一般文本中的 **內容** 預先轉換為
    /// Unity 原生支援的 &lt;b&gt;內容&lt;/b&gt;。
    /// </summary>
    private static string PreprocessMarkdown(string markdown, List<(int Start, int End)> extraSpans = null)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var spans = new List<(int Start, int End)>();
        foreach (System.Text.RegularExpressions.Match region in ProtectedRegion.Matches(markdown))
        {
            spans.Add((region.Index, region.Index + region.Length));
        }
        if (extraSpans != null) spans.AddRange(extraSpans);
        // 兩種來源可能重疊（表格寫在程式碼圍籬內），排序後靠 last 擋掉重疊部分。
        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var sb = new StringBuilder(markdown.Length + 32);
        int last = 0;
        foreach ((int start, int end) in spans)
        {
            if (start < last) continue;
            sb.Append(BoldPattern.Replace(markdown.Substring(last, start - last), BoldOpenMarker + "$1" + BoldCloseMarker));
            sb.Append(markdown, start, end - start);
            last = end;
        }
        sb.Append(BoldPattern.Replace(markdown.Substring(last), BoldOpenMarker + "$1" + BoldCloseMarker));
        return sb.ToString();
    }

    /// <summary>
    /// 找出所有表格在原始文字中的位置。
    /// 表格欄位裡的 <c>**</c> 常是語意標記（熱情等級、版本號），不是粗體，
    /// 因此整張表都不做粗體預轉換，欄位文字直接取原始切片保留。
    /// </summary>
    private static List<(int Start, int End)> CollectTableSpans(string markdown)
    {
        var spans = new List<(int, int)>();
        if (markdown.IndexOf('|') < 0) return spans;

        CollectTableSpans(Markdown.Parse(markdown, Pipeline), spans);
        return spans;
    }

    private static void CollectTableSpans(ContainerBlock container, List<(int Start, int End)> spans)
    {
        foreach (Block block in container)
        {
            if (block is Table table) spans.Add((table.Span.Start, table.Span.End));
            if (block is ContainerBlock child) CollectTableSpans(child, spans);
        }
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

        string sanitized = SanitizeUnityTags(markdown, preserveTrustedColorWrapper: true);
        // 表格由 Markdig 辨識後，把整張表的範圍一併排除在粗體預轉換之外：
        // 欄位裡的 ** 常是語意標記（熱情等級、版本號），不是粗體。
        string preprocessed = PreprocessMarkdown(sanitized, CollectTableSpans(sanitized));
        MarkdownDocument document = Markdown.Parse(preprocessed, Pipeline);
        var sb = new StringBuilder(preprocessed.Length + 64);
        var renderer = new UnityRichTextRenderer(sb, preprocessed);
        renderer.Render(document);

        return sb.ToString()
            .Replace(BoldOpenMarker, "<b>")
            .Replace(BoldCloseMarker, "</b>")
            .TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 欄位文字取自原始 markdown 切片，而非解析後的 inline 樹。
    /// 表格是資料：欄位裡的 <c>*</c> / <c>**</c> 是語意標記而非強調，
    /// 讓 Markdig 解析只會把相鄰的星號配成一組並套上粗體。
    /// </summary>
    private static string CellText(TableCell cell, string source)
    {
        string raw = source.Substring(cell.Span.Start, cell.Span.Length).Trim();
        // 取的是原始切片，因此 \| 要自己還原；跳脫直線若不還原會變成多餘的反斜線。
        raw = raw.Replace("\\|", "|");
        return raw.Replace("<", "＜").Replace(">", "＞");
    }

    private sealed class UnityRichTextRenderer
    {
        private readonly StringBuilder _sb;
        private readonly string _source;
        private int _listDepth;
        private static readonly char[] NewlineSeparator = ['\n'];

        public UnityRichTextRenderer(StringBuilder sb, string source)
        {
            _sb = sb;
            _source = source;
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
                case Table table:
                    RenderTable(table);
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
            var subRenderer = new UnityRichTextRenderer(subSb, _source);
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

        /// <summary>表格第一行在原始文字中的行首位置；Span.Start 指向第一個非空白字元。</summary>
        private int TableLineStart(Table table)
        {
            int i = table.Span.Start;
            int lineStart = i > 0 ? _source.LastIndexOf('\n', i - 1) : -1;
            return lineStart < 0 ? 0 : lineStart + 1;
        }

        /// <summary>表格第一行是否以 4 空格或 tab 縮排（GFM 中 indented code 優先於表格）。</summary>
        private bool StartsIndented(Table table)
        {
            int i = table.Span.Start;
            if (i >= _source.Length) return false;
            if (_source[i] == '\t') return true;

            int lineStart = TableLineStart(table);
            int spaces = 0;
            for (int k = lineStart; k < i && k < _source.Length && _source[k] == ' '; k++) spaces++;
            return spaces >= 4;
        }

        private void RenderTableAsCode(Table table)
        {
            int lineStart = TableLineStart(table);
            string raw = _source.Substring(lineStart, table.Span.End - lineStart).Replace("\r\n", "\n");
            string[] lines = raw.Split('\n');

            _sb.Append(ColorTagOpen).Append(CodeColor).Append('>');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append("  ").Append(EscapeCode(lines[i]));
            }
            _sb.Append(ColorTagClose);
        }

        /// <summary>
        /// 畫成「欄名行 + 每列一行的項目符號列」。
        ///
        /// 舊版 IMGUI 的 rich text 沒有欄寬對齊（<c>GUIStyle.CalcSize</c> 要 Unity 端才拿得到，
        /// 此類是純邏輯層），因此不畫表格框線，改用全形 <c>｜</c> 分隔欄位：
        /// 視覺上足以分辨欄位，又不會產生因字型不等寬而全部錯位的假框線。
        /// </summary>
        private void RenderTable(Table table)
        {
            // PipeTables 比縮排程式碼優先：段落後接的 4 空格縮排行本應是段落延續，
            // 擴充卻把它開成表格（純 CommonMark 管線解析為單一段落）。這裡還原成程式碼外觀。
            if (StartsIndented(table))
            {
                RenderTableAsCode(table);
                return;
            }

            bool wroteAny = false;
            foreach (TableRow row in table)
            {
                var cells = new List<string>();
                foreach (TableCell cell in row)
                {
                    cells.Add(CellText(cell, _source));
                }
                if (cells.Count == 0) continue;

                if (wroteAny) _sb.Append('\n');
                if (row.IsHeader)
                {
                    _sb.Append(ColorTagOpen).Append(MutedColor).Append('>')
                       .Append(string.Join(CellSeparator, cells))
                       .Append(ColorTagClose);
                }
                else
                {
                    _sb.Append("• ").Append(string.Join(CellSeparator, cells));
                }
                wroteAny = true;
            }
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
