using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// Markdown 轉 Unity 舊版 rich text 的行為測試。
/// 重點在於「只產生舊版 IMGUI 認得的六個標籤」，以及不要把模型輸出的內容誤判成語法。
/// </summary>
[TestFixture]
public class MarkdownTests
{
    [Test]
    public void BoldAndItalicBecomeRichTextTags()
    {
        ClassicAssert.AreEqual("<b>粗體</b>與<i>斜體</i>", RimLLMMarkdown.ToRichText("**粗體**與*斜體*"));
    }

    [Test]
    public void BoldIsNotBrokenIntoNestedItalic()
    {
        // 先處理 ** 再處理 *，否則 **text** 會被拆成 <i>*text</i>*
        ClassicAssert.AreEqual("<b>abc</b>", RimLLMMarkdown.ToRichText("**abc**"));
    }

    [Test]
    public void HeadingBecomesSizedBoldLine()
    {
        ClassicAssert.AreEqual("<size=20><b>標題</b></size>", RimLLMMarkdown.ToRichText("# 標題"));
        ClassicAssert.AreEqual("<size=15><b>第三層</b></size>", RimLLMMarkdown.ToRichText("### 第三層"));
    }

    [Test]
    public void DeepHeadingFallsBackToBoldOnly()
    {
        ClassicAssert.AreEqual("<b>第五層</b>", RimLLMMarkdown.ToRichText("##### 第五層"));
    }

    [Test]
    public void UnorderedListBecomesBulletWithIndent()
    {
        string result = RimLLMMarkdown.ToRichText("- 甲\n- 乙");
        ClassicAssert.AreEqual("  • 甲\n  • 乙", result);
    }

    [Test]
    public void NestedListGetsDeeperIndent()
    {
        string result = RimLLMMarkdown.ToRichText("- 外層\n  - 內層");
        ClassicAssert.AreEqual("  • 外層\n    • 內層", result);
    }

    [Test]
    public void OrderedListKeepsItsNumber()
    {
        ClassicAssert.AreEqual("  1. 第一項", RimLLMMarkdown.ToRichText("1. 第一項"));
    }

    [Test]
    public void InlineCodeContentIsNotReprocessed()
    {
        // 程式碼內的星號不是語法，必須原樣保留。
        string result = RimLLMMarkdown.ToRichText("看 `a * b * c` 這段");
        StringAssert.Contains("a * b * c", result);
        ClassicAssert.IsFalse(result.Contains("<i>"), "行內程式碼裡的星號不該被當成斜體。");
    }

    [Test]
    public void FencedCodeBlockDropsFencesAndKeepsContent()
    {
        string result = RimLLMMarkdown.ToRichText("```csharp\nint x = 1;\n```");
        StringAssert.Contains("int x = 1;", result);
        ClassicAssert.IsFalse(result.Contains("```"), "圍籬本身不該顯示出來。");
        ClassicAssert.IsFalse(result.Contains("csharp"), "語言標記不該顯示出來。");
    }

    [Test]
    public void UnclosedFenceStillRendersAsCode()
    {
        // 串流途中圍籬尚未閉合是常態，不能因此讓後續內容消失。
        string result = RimLLMMarkdown.ToRichText("```\nint x = 1;");
        StringAssert.Contains("int x = 1;", result);
    }

    [Test]
    public void MarkdownInsideFenceIsNotConverted()
    {
        string result = RimLLMMarkdown.ToRichText("```\n# 這不是標題\n```");
        StringAssert.Contains("# 這不是標題", result);
        ClassicAssert.IsFalse(result.Contains("<size="), "程式碼區塊內不該套用標題樣式。");
    }

    [Test]
    public void LinkKeepsLabelAndDropsUrlMarkup()
    {
        string result = RimLLMMarkdown.ToRichText("[說明](https://example.com)");
        StringAssert.Contains("說明", result);
        ClassicAssert.IsFalse(result.Contains("https://example.com"), "舊版 rich text 不能點擊，網址只是噪音。");
    }

    [Test]
    public void SnakeCaseIsNotTreatedAsItalic()
    {
        // 刻意不支援底線斜體，就是為了避免這種誤判。
        string result = RimLLMMarkdown.ToRichText("欄位 some_field_name 保持原樣");
        StringAssert.Contains("some_field_name", result);
        ClassicAssert.IsFalse(result.Contains("<i>"));
    }

    [Test]
    public void BlockQuoteBecomesMutedLine()
    {
        string result = RimLLMMarkdown.ToRichText("> 引用");
        StringAssert.Contains("引用", result);
        StringAssert.StartsWith("<color=", result);
    }

    [Test]
    public void ExistingRichTextTagsPassThrough()
    {
        // 思考過程的灰色包裝是在 Markdown 之前就加上去的，不能被破壞。
        const string input = "<color=silver>思考中</color>\n一般內容";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("<color=silver>思考中</color>", result);
    }

    [Test]
    public void UntrustedRawUnityTagsAreEscaped()
    {
        const string input = "<color=red>外部標記</color> <size=999>過大文字</size>";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("＜color=red＞外部標記＜/color＞", result);
        StringAssert.Contains("＜size=999＞過大文字＜/size＞", result);
        ClassicAssert.IsFalse(result.Contains("<color=red>"));
        ClassicAssert.IsFalse(result.Contains("<size=999>"));
    }

    [Test]
    public void UntrustedSilverWrapperIsEscapedBeforeChatTestWrapping()
    {
        const string input = "<color=silver>供應商內容</color>";
        string result = RimLLMMarkdown.EscapeUntrustedUnityTags(input);

        StringAssert.Contains("＜color=silver＞供應商內容＜/color＞", result);
        ClassicAssert.IsFalse(result.Contains("<color=silver>"));
    }

    [Test]
    public void RawHtmlSanitizerPreservesOrdinaryGreaterThanText()
    {
        string result = RimLLMMarkdown.ToRichText("<div>HTML</div> a > b");

        StringAssert.Contains("a > b", result);
        ClassicAssert.IsFalse(result.Contains("<div>"));
        ClassicAssert.IsFalse(result.Contains("</div>"));
    }

    [Test]
    public void EncodedUnityTagsDoNotBecomeRichText()
    {
        string result = RimLLMMarkdown.ToRichText(
            "&lt;color=red&gt;外部標記&lt;/color&gt;");

        ClassicAssert.IsFalse(result.Contains("<color=red>"));
        ClassicAssert.IsFalse(result.Contains("</color>"));
    }

    [Test]
    public void OnlyLegacySupportedTagsAreEmitted()
    {
        // 舊版 IMGUI 只認得 b/i/size/color/material/quad，其餘標籤會被原樣印出來。
        string result = RimLLMMarkdown.ToRichText(
            "# 標題\n- 項目 **粗** *斜* `碼`\n> 引用\n\n---\n\n[連結](https://example.com)");
        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(result, @"</?([a-zA-Z]+)"))
        {
            string tag = match.Groups[1].Value.ToLowerInvariant();
            ClassicAssert.IsTrue(
                tag == "b" || tag == "i" || tag == "size" || tag == "color" || tag == "material" || tag == "quad",
                "產生了舊版 rich text 不支援的標籤: " + tag);
        }
    }

    [Test]
    public void EmptyInputIsReturnedUnchanged()
    {
        ClassicAssert.AreEqual("", RimLLMMarkdown.ToRichText(""));
        ClassicAssert.IsNull(RimLLMMarkdown.ToRichText(null));
    }

    [Test]
    public void PlainTextIsUnchanged()
    {
        const string input = "這只是一段普通文字，沒有任何標記。";
        ClassicAssert.AreEqual(input, RimLLMMarkdown.ToRichText(input));
    }

    [Test]
    public void CppCodeAngleBracketsAreLeftAlone()
    {
        // Unity 舊版 IMGUI 只認得 b/i/size/color/material/quad，<iostream>、vector<int>、x < y 都會原樣顯示（實機驗證），
        // 不需要跳脫；改成全形角括號反而讓程式碼看起來多了空格。
        const string input = "```cpp\n#include <iostream>\nvector<int> arr;\nif (x < y) cout << x;\n```";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.StartsWith("<color=#4ec9b0>", result);
        StringAssert.EndsWith("</color>", result);
        StringAssert.Contains("#include <iostream>", result);
        StringAssert.Contains("vector<int> arr;", result);
        StringAssert.Contains("if (x < y) cout << x;", result);
    }

    [Test]
    public void UnityTagsInsideCodeAreEscaped()
    {
        // 程式碼裡的 HTML 標籤若剛好是 Unity 認得的標籤，會被當成格式套用、甚至提早關掉包住程式碼的 <color>。
        const string input = "```html\n<b>bold</b> <color=red>x</color> <span>ok</span>\n```\n\n行內 `<i>x</i>` 也一樣";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("＜b＞bold＜/b＞ ＜color=red＞x＜/color＞ <span>ok</span>", result);
        StringAssert.Contains("＜i＞x＜/i＞", result);
        // 只有程式碼區塊自己的 <color> 包裝與行內碼的包裝是真正的標籤
        ClassicAssert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(result, "<color=#4ec9b0>").Count);
    }

    [Test]
    public void TopLevelBlocksAreSeparatedByBlankLine()
    {
        string result = RimLLMMarkdown.ToRichText("第一段\n\n第二段\n\n- 項目");
        ClassicAssert.AreEqual("第一段\n\n第二段\n\n  • 項目", result);
    }

    [Test]
    public void QuoteWithMultipleParagraphsKeepsLineBreaks()
    {
        string result = RimLLMMarkdown.ToRichText("> 甲\n>\n> 乙");
        StringAssert.Contains("| 甲</color>\n<color=", result);
        StringAssert.Contains("| 乙</color>", result);
    }

    [Test]
    public void BoldWithTrailingWhitespaceInDelimiterIsRendered()
    {
        const string input = "這是一個使用 C++ 實作**快速排序法 (Quick Sort) **的完整範例。";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("<b>快速排序法 (Quick Sort)</b>", result);
        ClassicAssert.IsFalse(result.Contains("**"), "原始星號不應殘留。");
    }

    [Test]
    public void BoldWithCjkPunctuationIsRendered()
    {
        const string input = "以下是用 C++ 實作**快速排序法（Quick Sort）**的程式碼";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("<b>快速排序法（Quick Sort）</b>", result);
        ClassicAssert.IsFalse(result.Contains("**"), "原始星號不應殘留。");
    }

    [Test]
    public void DoubleStarInInlineCodeIndentedCodeAndTildeFenceIsNotBold()
    {
        // Python 的 **kwargs、a ** b 是程式碼，不是粗體；預轉換必須放過行內碼、縮排程式碼與 ~~~ 圍籬。
        string inlineCode = RimLLMMarkdown.ToRichText("使用 `**kwargs` 與 `**extra` 參數");
        StringAssert.Contains("<color=#4ec9b0>**kwargs</color>", inlineCode);
        StringAssert.Contains("<color=#4ec9b0>**extra</color>", inlineCode);

        string indented = RimLLMMarkdown.ToRichText("    x = a ** b ** c");
        StringAssert.Contains("x = a ** b ** c", indented);

        string tilde = RimLLMMarkdown.ToRichText("~~~\na ** b ** c\n~~~");
        StringAssert.Contains("a ** b ** c", tilde);

        string unclosed = RimLLMMarkdown.ToRichText("```\na ** b ** c");
        StringAssert.Contains("a ** b ** c", unclosed);

        // 一般文字裡的粗體仍要正常
        ClassicAssert.AreEqual("<b>粗</b>", RimLLMMarkdown.ToRichText("**粗**"));
    }

    [Test]
    public void PointersInCodeBlocksAreNotTouchedByBold()
    {
        const string input = "```cpp\nint** ptr = nullptr;\n```";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("int** ptr = nullptr;", result);
    }

    [Test]
    public void PipeTableBecomesHeaderLineAndBulletRows()
    {
        const string input = "| 姓名 | 年齡 |\n|------|------|\n| Fujita | 61 |\n| Kups | 19 |";
        string result = RimLLMMarkdown.ToRichText(input);

        ClassicAssert.AreEqual(
            "<color=#9aa0a6>姓名｜年齡</color>\n• Fujita｜61\n• Kups｜19",
            result);
    }

    [Test]
    public void TableDelimiterRowIsNeverRendered()
    {
        // 舊版管線讓 |----|----| 這一列原樣輸出，正是使用者看到的雜訊。
        string result = RimLLMMarkdown.ToRichText("| a | b |\n|:----|----:|\n| 1 | 2 |");
        ClassicAssert.IsFalse(result.Contains("-----"), "分隔線不應出現在輸出。");
        ClassicAssert.IsFalse(result.Contains("|:"), "對齊冒號不應殘留。");
        StringAssert.Contains("<color=#9aa0a6>a｜b</color>", result);
        StringAssert.Contains("• 1｜2", result);
    }

    [Test]
    public void StarsInsideTableCellsAreNotPairedAcrossColumns()
    {
        // 模型用 ** 表示「主熱情」層級。若表格被當成單一段落，粗體預轉換會把
        // 「醫療 10**；射擊 12**」配成一組，變成 醫療 10<b>；射擊 12</b>。
        const string input = "| 姓名 | 熱情 |\n|------|------|\n| Fujita | 醫療 10**；射擊 12**；智識 9* |";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("醫療 10**；射擊 12**；智識 9*", result);
        ClassicAssert.IsFalse(result.Contains("<b>"), "欄位內不應產生粗體標籤。");
    }

    [Test]
    public void BoldOutsideTableStillWorks()
    {
        const string input = "**標題**\n\n| a | b |\n|----|----|\n| 1 | 2 |";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("<b>標題</b>", result);
        StringAssert.Contains("• 1｜2", result);
    }

    [Test]
    public void MultipleTablesInOneReplyAreEachRendered()
    {
        const string input = "| a |\n|---|\n| 1 |\n\n文字\n\n| b |\n|---|\n| 2 |";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("<color=#9aa0a6>a</color>\n• 1", result);
        StringAssert.Contains("<color=#9aa0a6>b</color>\n• 2", result);
        StringAssert.Contains("文字", result);
    }

    [Test]
    public void PipeWithoutDelimiterRowIsNotTreatedAsTable()
    {
        // 只有欄位線、沒有 |---| 對齊列時不該被當表格吃掉。
        string result = RimLLMMarkdown.ToRichText("a | b\nc | d");
        StringAssert.Contains("a | b", result);
        StringAssert.Contains("c | d", result);
    }

    [Test]
    public void DelimiterRowWithoutHeaderIsNotTreatedAsTable()
    {
        string result = RimLLMMarkdown.ToRichText("前文\n----|----");
        StringAssert.Contains("前文", result);
        StringAssert.Contains("----|----", result);
    }

    [Test]
    public void EscapedPipeInsideCellDoesNotCreateExtraColumn()
    {
        const string input = "| 道具 | 備註 |\n|------|------|\n| 劍 | a \\| b |";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("a | b", result);
    }

    [Test]
    public void UnityTagsInsideTableCellsAreEscaped()
    {
        const string input = "| 名稱 | 備註 |\n|------|------|\n| 甲 | <b>粗體</b> |";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("＜b＞粗體＜/b＞", result);
    }

    [Test]
    public void SingleColumnTableIsRendered()
    {
        // GFM 允許單欄表格。
        string result = RimLLMMarkdown.ToRichText("| 注意 |\n|------|\n| 這是提示 |");
        ClassicAssert.AreEqual("<color=#9aa0a6>注意</color>\n• 這是提示", result);
    }

    [Test]
    public void TableInsideFencedCodeStaysCode()
    {
        // 迴歸：自行用正則掃 |---| 時，程式碼圍籬內的表格會被誤判成表格，
        // 而且產生的 <color> 未閉合會讓後續文字全部染色。
        const string input = "圍籬：\n```\n| a | b |\n|---|---|\n| 1 | 2 |\n```";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("<color=#4ec9b0>", result);
        StringAssert.Contains("| a | b |", result);
        StringAssert.Contains("|---|---|", result);
        ClassicAssert.IsFalse(result.Contains("<color=#9aa0a6>"), "圍籬內不應出現表格欄名。");
        ClassicAssert.AreEqual(result.Split("<color=").Length - 1, result.Split("</color>").Length - 1,
            "color 標籤必須成對，否則後續文字會被染色。");
    }

    [Test]
    public void IndentedTableInsideCodeBlockStaysCode()
    {
        const string input = "    | a | b |\n    |---|---|\n    | 1 | 2 |";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("<color=#4ec9b0>", result);
        ClassicAssert.IsFalse(result.Contains("<color=#9aa0a6>"), "縮排程式碼不應被當成表格。");
    }

    [Test]
    public void TableLikeIndentedLinesAfterParagraphStayCode()
    {
        // PipeTables 比 indented code 優先：段落後的 4 空格縮排行本應是段落延續，
        // Markdig 卻開成表格。渲染層要把這個語意還原。
        const string input = "縮排：\n    | a | b |\n    |---|---|\n    | 1 | 2 |";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("<color=#4ec9b0>", result);
        ClassicAssert.IsFalse(result.Contains("<color=#9aa0a6>"), "縮排行不應被當成表格。");
    }

    [Test]
    public void InlineCodeContainingPipesStaysCode()
    {
        const string input = "行內：`| a | b |` 與 `|---|---|`";
        string result = RimLLMMarkdown.ToRichText(input);

        StringAssert.Contains("<color=#4ec9b0>| a | b |</color>", result);
        StringAssert.Contains("<color=#4ec9b0>|---|---|</color>", result);
    }

    [Test]
    public void EscapedPipeInsideCellIsUnescaped()
    {
        const string input = "| 道具 | 備註 |\n|---|---|\n| 劍 | a \\| b |";
        string result = RimLLMMarkdown.ToRichText(input);
        StringAssert.Contains("a | b", result);
        ClassicAssert.IsFalse(result.Contains("a \\| b"), "跳脫反斜線不應殘留。");
    }
}
