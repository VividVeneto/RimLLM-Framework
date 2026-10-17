using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 設定介面的純函式：金鑰遮罩、模型過濾、chip 版面計算。
/// IMGUI 的繪製本身無法單元測試，因此把可判定的邏輯抽成純函式並在此釘住。
/// </summary>
[TestFixture]
public class UIStyleTests
{
    // ---------- 金鑰遮罩 ----------

    [Test]
    public void LongKeyKeepsHeadAndTailOnly()
    {
        string masked = RimLLMUIStyle.MaskApiKey("sk-or-v1-TESTKEY-TESTKEY-TESTKEY-TESTKEY-TESTKEY-TESTKEY-2e8a");

        ClassicAssert.AreEqual(13, masked.Length, "頭 8 + 省略號 1 + 尾 4。");
        StringAssert.StartsWith("sk-or-v1", masked);
        StringAssert.EndsWith("2e8a", masked);
        ClassicAssert.IsFalse(masked.Contains("TESTKEY"), "中段必須完全隱藏。");
    }

    [Test]
    public void ShortKeyIsFullyHidden()
    {
        // 短金鑰若也保留頭尾，等於露出大半內容。
        string masked = RimLLMUIStyle.MaskApiKey("abcd1234");

        ClassicAssert.AreEqual("••••••••", masked);
    }

    [Test]
    public void BoundaryLengthKeyIsFullyHidden()
    {
        // 剛好 12 字（8 + 4）時保留頭尾就等於全部露出，因此仍應全遮。
        string masked = RimLLMUIStyle.MaskApiKey("123456789012");

        ClassicAssert.AreEqual(12, masked.Length);
        ClassicAssert.AreEqual("••••••••••••", masked);
    }

    [Test]
    public void EmptyKeyStaysEmpty()
    {
        // 空金鑰若顯示成圓點，畫面上會看起來像已經設定過。
        ClassicAssert.AreEqual("", RimLLMUIStyle.MaskApiKey(""));
        ClassicAssert.AreEqual("", RimLLMUIStyle.MaskApiKey(null));
    }

    // ---------- 模型過濾 ----------

    [Test]
    public void EmptyFilterReturnsEverything()
    {
        var models = new List<string> { "gpt-4o", "o5-preview" };

        ClassicAssert.AreEqual(2, RimLLMUIStyle.FilterModels(models, "").Count);
        ClassicAssert.AreEqual(2, RimLLMUIStyle.FilterModels(models, null).Count);
        ClassicAssert.AreEqual(2, RimLLMUIStyle.FilterModels(models, "   ").Count);
    }

    [Test]
    public void FilterIsCaseInsensitiveAndMatchesSubstrings()
    {
        var models = new List<string> { "google/gemini-3.5-flash-lite", "anthropic/claude-opus-5", "qwen/qwen3.8-max" };

        var result = RimLLMUIStyle.FilterModels(models, "GEMINI");

        ClassicAssert.AreEqual(1, result.Count);
        ClassicAssert.AreEqual("google/gemini-3.5-flash-lite", result[0]);
    }

    [Test]
    public void NoMatchReturnsEmptyList()
    {
        var models = new List<string> { "gpt-4o" };

        ClassicAssert.AreEqual(0, RimLLMUIStyle.FilterModels(models, "llama").Count);
    }

    [Test]
    public void NullAndEmptyEntriesAreSkipped()
    {
        var models = new List<string> { null, "", "bge-m3" };

        var result = RimLLMUIStyle.FilterModels(models, "");

        ClassicAssert.AreEqual(1, result.Count);
        ClassicAssert.AreEqual("bge-m3", result[0]);
    }

    [Test]
    public void NullSourceIsSafe()
    {
        ClassicAssert.AreEqual(0, RimLLMUIStyle.FilterModels(null, "anything").Count);
    }

    // ---------- chip 版面 ----------

    [Test]
    public void ChipsFillTheAvailableWidth()
    {
        // 先前使用固定寬度，右緣總是留下一條用不到的空隙。
        RimLLMUIStyle.ComputeChipLayout(700f, 8f, 220f, out int cols, out float chipWidth);

        float consumed = cols * chipWidth + (cols + 1) * 8f;
        ClassicAssert.AreEqual(700f, consumed, 0.01f);
    }

    [Test]
    public void ColumnCountFollowsPreferredWidth()
    {
        RimLLMUIStyle.ComputeChipLayout(700f, 8f, 220f, out int cols, out float chipWidth);

        ClassicAssert.AreEqual(3, cols);
        ClassicAssert.GreaterOrEqual(chipWidth, 220f, "反推後的寬度不該小於偏好寬度，否則欄數就算多了。");
    }

    [Test]
    public void VeryNarrowPanelStillGetsOneColumn()
    {
        // 欄數為 0 會導致取餘數時除以零。
        RimLLMUIStyle.ComputeChipLayout(60f, 8f, 220f, out int cols, out float chipWidth);

        ClassicAssert.AreEqual(1, cols);
        ClassicAssert.Greater(chipWidth, 0f);
    }

    [Test]
    public void DegenerateWidthDoesNotProduceNegativeChip()
    {
        RimLLMUIStyle.ComputeChipLayout(4f, 8f, 220f, out int cols, out float chipWidth);

        ClassicAssert.AreEqual(1, cols);
        ClassicAssert.Greater(chipWidth, 0f);
    }

    // ---------- 過濾快取 ----------

    [Test]
    public void FilterModelsCachedReusesResultUntilVersionOrFilterChanges()
    {
        int sourceCalls = 0;
        System.Func<IList<string>> source = () =>
        {
            sourceCalls++;
            return ["gpt-4o", "gemini-2.5-flash", "deepseek-chat"];
        };

        var first = RimLLMUIStyle.FilterModelsCached("ui-test", 1, "g", source);
        var second = RimLLMUIStyle.FilterModelsCached("ui-test", 1, "g", source);

        ClassicAssert.AreSame(first, second, "版本與過濾字串都沒變時必須回傳同一份結果。");
        ClassicAssert.AreEqual(1, sourceCalls, "命中快取時不得再向來源要清單。");
        ClassicAssert.AreEqual(2, first.Count);

        var filtered = RimLLMUIStyle.FilterModelsCached("ui-test", 1, "deep", source);
        ClassicAssert.AreEqual(2, sourceCalls, "過濾字串變了必須重算。");
        ClassicAssert.AreEqual(1, filtered.Count);

        RimLLMUIStyle.FilterModelsCached("ui-test", 2, "deep", source);
        ClassicAssert.AreEqual(3, sourceCalls, "清單版本變了必須重算。");
    }

    [Test]
    public void FilterModelsCachedKeepsKeysIndependent()
    {
        var a = RimLLMUIStyle.FilterModelsCached("key-a", 1, "", () => ["a"]);
        var b = RimLLMUIStyle.FilterModelsCached("key-b", 1, "", () => ["b"]);

        ClassicAssert.AreEqual("a", a[0]);
        ClassicAssert.AreEqual("b", b[0]);
    }

    // ---------- 可視列區間 ----------

    [Test]
    public void VisibleRowRangeCoversViewportWithOneRowPadding()
    {
        // 列高 36、頂端留白 4、捲到第 10 列頂端、可視 3 列高。
        RimLLMUIStyle.GetVisibleRowRange(4f + 36f * 10f, 36f * 3f, 36f, 4f, 100, out int first, out int end);

        ClassicAssert.AreEqual(9, first, "前面多留一列，避免捲動時露出空白。");
        ClassicAssert.AreEqual(14, end, "後面多留一列。");
    }

    [Test]
    public void VisibleRowRangeClampsToRowCount()
    {
        RimLLMUIStyle.GetVisibleRowRange(0f, 1000f, 36f, 4f, 5, out int first, out int end);
        ClassicAssert.AreEqual(0, first);
        ClassicAssert.AreEqual(5, end);

        RimLLMUIStyle.GetVisibleRowRange(99999f, 200f, 36f, 4f, 5, out first, out end);
        ClassicAssert.AreEqual(5, first);
        ClassicAssert.AreEqual(5, end, "捲動超出範圍時不得產生負數或越界區間。");
    }

    [Test]
    public void VisibleRowRangeIsEmptyForNoRows()
    {
        RimLLMUIStyle.GetVisibleRowRange(50f, 200f, 36f, 4f, 0, out int first, out int end);
        ClassicAssert.AreEqual(0, first);
        ClassicAssert.AreEqual(0, end);
    }
}
