using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 備援鏈條目移除（連帶清除覆寫）與第三方整合頁的狀態分類／排序。
/// </summary>
[TestFixture]
public class FallbackRemovalAndCompatStateTests
{
    // ---------- 備援鏈移除 ----------

    [Test]
    public void RemoveFallbackEntry_RemovesDuplicatesAndContextOverride()
    {
        var settings = new RimLLMFrameworkSettings();
        settings.FallbackChain = ["OpenRouter:foo", "Gemini:bar", "OpenRouter:foo"];
        settings.SetContextWindowOverride("OpenRouter:foo", 262144);

        settings.RemoveFallbackEntry("OpenRouter:foo");

        CollectionAssert.AreEqual(new[] { "Gemini:bar" }, settings.FallbackChain);
        ClassicAssert.AreEqual(0, settings.GetContextWindowOverride("OpenRouter:foo"));
    }

    [Test]
    public void RemoveFallbackEntry_ClearsLevelOverrideOnlyWhenNoOtherEntryUsesModel()
    {
        var settings = new RimLLMFrameworkSettings();
        settings.FallbackChain = ["OpenRouter:shared", "Groq:shared", "Gemini:solo"];
        settings.SetModelLevelOverride("shared", 3);
        settings.SetModelLevelOverride("solo", 2);

        settings.RemoveFallbackEntry("OpenRouter:shared");
        ClassicAssert.AreEqual(3, settings.GetModelLevelOverride("shared"), "Groq 仍用同名模型，分級要保留。");

        settings.RemoveFallbackEntry("Groq:shared");
        ClassicAssert.AreEqual(0, settings.GetModelLevelOverride("shared"));

        settings.RemoveFallbackEntry("Gemini:solo");
        ClassicAssert.AreEqual(0, settings.GetModelLevelOverride("solo"));
    }

    [Test]
    public void RemoveFallbackEntry_PureProviderEntryJustLeavesChain()
    {
        var settings = new RimLLMFrameworkSettings();
        settings.FallbackChain = ["OpenRouter", "Gemini:bar"];

        settings.RemoveFallbackEntry("OpenRouter");

        CollectionAssert.AreEqual(new[] { "Gemini:bar" }, settings.FallbackChain);
    }

    // ---------- 第三方整合狀態 ----------

    [TestCase(false, false, true, 0)]
    [TestCase(false, true, true, 1)]
    [TestCase(true, true, false, 2)]
    [TestCase(true, true, true, 3)]
    public void Classify_SeparatesNotInstalledFromInactive(
        bool installed, bool downloaded, bool patched, int expected)
    {
        ClassicAssert.AreEqual(
            (CompatSettingsDrawer.CompatRowState)expected,
            CompatSettingsDrawer.Classify(installed, downloaded, patched));
    }

    [Test]
    public void SortRank_OrdersReadyThenFailedThenInactiveThenNotInstalled()
    {
        int ready = CompatSettingsDrawer.SortRank(CompatSettingsDrawer.CompatRowState.Ready);
        int failed = CompatSettingsDrawer.SortRank(CompatSettingsDrawer.CompatRowState.PatchFailed);
        int inactive = CompatSettingsDrawer.SortRank(CompatSettingsDrawer.CompatRowState.Inactive);
        int missing = CompatSettingsDrawer.SortRank(CompatSettingsDrawer.CompatRowState.NotInstalled);

        ClassicAssert.Less(ready, failed);
        ClassicAssert.Less(failed, inactive);
        ClassicAssert.Less(inactive, missing);
    }
}
