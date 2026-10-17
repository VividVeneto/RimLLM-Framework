using System.Collections.Generic;
using DevTools.Testing;
using RimLLM_Framework.Api;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.InGameTests;

/// <summary>Mod 建構子的啟動接線：設定、Manager、主線程派遣器。</summary>
[TestFixture(TestType.MainMenu)]
internal sealed class StartupTests
{
    [Test]
    public void ModConstructorWiredSettingsAndManager()
    {
        var failures = new List<string>();
        if (LoadedModManager.GetMod<RimLLMFrameworkMod>() == null)
        {
            failures.Add("RimLLMFrameworkMod is not registered in LoadedModManager");
        }
        if (RimLLMFrameworkMod.Settings == null)
        {
            failures.Add("RimLLMFrameworkMod.Settings is null");
        }
        if (!RimLLMProvider.TryGetManager(out _))
        {
            failures.Add("RimLLMProvider has no manager");
        }
        TestHelpers.AssertNone(failures, "startup wiring incomplete");
    }

    /// <summary>
    /// 派遣器沒有 pump 時會退回「同步執行」，背景執行緒的回呼就會直接在背景執行緒碰 Unity API。
    /// headless 測試永遠是無 pump 的路徑，只有遊戲內能確認 pump 真的存在。
    /// </summary>
    [Test]
    public void DispatcherHasLiveMainThreadPump()
    {
        var failures = new List<string>();
        int dispatchers = Resources.FindObjectsOfTypeAll<RimLLMDispatcher>().Length;
        if (dispatchers != 1)
        {
            failures.Add($"expected exactly one RimLLMDispatcher component, found {dispatchers}");
        }

        if (!RimLLMDispatcher.HasPump)
        {
            failures.Add("RimLLMDispatcher reports no pump; main-thread dispatch falls back to synchronous execution");
        }
        TestHelpers.AssertNone(failures, "dispatcher not running");
    }
}
