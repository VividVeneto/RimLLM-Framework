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
}
