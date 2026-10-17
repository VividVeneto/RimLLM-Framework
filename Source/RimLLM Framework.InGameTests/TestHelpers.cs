using System.Collections.Generic;
using DevTools.Testing;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.InGameTests;

internal static class TestHelpers
{
    private const string LogPrefix = "[RimLLM.InGameTests]";

    /// <summary>收集完所有失敗再一次回報給 DevTools，一次執行就能看到全部問題。</summary>
    public static void AssertNone(List<string> failures, string message)
    {
        if (failures.Count > 0)
        {
            string details = $"{message} ({failures.Count}):\n{string.Join("\n", failures)}";
            Test.Fail(details);
        }
    }

    /// <summary>
    /// 記錄跳過日誌。
    /// </summary>
    public static void LogSkipped(string test, string reason)
    {
        Log.Message($"{LogPrefix} skipped: {test}: {reason}");
    }

    /// <summary>RimLLM Framework 自己的 ModContentPack。</summary>
    public static ModContentPack FrameworkContent =>
        LoadedModManager.GetMod<RimLLMFrameworkMod>()?.Content
        ?? throw new System.InvalidOperationException("RimLLM Framework mod not loaded (no RimLLMFrameworkMod instance)");
}
