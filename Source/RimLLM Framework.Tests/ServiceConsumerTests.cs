using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 設定頁「使用中的模組」：usage tracker 記錄實際送出請求的 modId，ServiceConsumerList 把它與依賴宣告合併。
/// </summary>
[TestFixture]
public class ServiceConsumerTests
{
    /// <summary>只建立 client 不算呼叫；真正送出請求（即使失敗）才記錄。</summary>
    [Test]
    public void TestCallerRecordedOnRequestNotOnClientCreation()
    {
        var manager = new RimLLMManager(new MockSettings());
        IChatClient client = manager.CreateChatClient("mod.a");
        manager.CreateEmbeddingGenerator("mod.b");
        ClassicAssert.AreEqual(0, manager.UsageTracker.CallerModIdCount);

        try
        {
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 測試設定沒有可用供應商，請求本身會失敗；這裡只關心呼叫端是否被記錄。
        }

        CollectionAssert.AreEquivalent(new[] { "mod.a" }, manager.UsageTracker.CallerModIds.ToArray());
    }

    [Test]
    public void TestRecordCaller_DeduplicatesIgnoringCase()
    {
        var tracker = new RimLLMUsageTracker(new MockSettings());

        tracker.RecordCaller("Mod.A");
        tracker.RecordCaller("mod.a");

        ClassicAssert.AreEqual(1, tracker.CallerModIdCount);
    }

    /// <summary>modId 可任意輪換，收錄數量必須有上限。</summary>
    [Test]
    public void TestRecordCaller_IsCapped()
    {
        var tracker = new RimLLMUsageTracker(new MockSettings());

        for (int i = 0; i < RimLLMUsageTracker.MaxCallerModIds + 50; i++)
        {
            tracker.RecordCaller("rotating." + i);
        }

        ClassicAssert.AreEqual(RimLLMUsageTracker.MaxCallerModIds, tracker.CallerModIdCount);
    }

    private static KeyValuePair<string, string>? Resolve(string modId)
    {
        return modId.ToLowerInvariant() switch
        {
            "foo.bar" or "foo.bar_steam" => (KeyValuePair<string, string>?)new KeyValuePair<string, string>("Foo.Bar", "Foo Bar"),
            "taken.over" => (KeyValuePair<string, string>?)new KeyValuePair<string, string>("Taken.Over", "Taken Over"),
            "rimllm.voiceaddon" => (KeyValuePair<string, string>?)new KeyValuePair<string, string>("RimLLM.VoiceAddon", "Voice Addon"),
            _ => null,
        };
    }

    [Test]
    public void TestBuild_MergesDeclaredAndCalled()
    {
        var declared = new[]
        {
            new KeyValuePair<string, string>("Foo.Bar", "Foo Bar"),
            new KeyValuePair<string, string>("Idle.Mod", "Idle Mod")
        };

        List<ServiceConsumerRow> rows = ServiceConsumerList.Build(
            declared, new[] { "foo.bar_steam", "foo.bar", "taken.over", "custom-id" }, Resolve);

        ClassicAssert.AreEqual(4, rows.Count);

        ServiceConsumerRow foo = rows.Single(r => r.PackageId == "Foo.Bar");
        ClassicAssert.IsTrue(foo.Declared);
        CollectionAssert.AreEqual(new[] { "foo.bar", "foo.bar_steam" }, foo.CalledModIds);

        ServiceConsumerRow takenOver = rows.Single(r => r.PackageId == "Taken.Over");
        ClassicAssert.IsFalse(takenOver.Declared);
        CollectionAssert.AreEqual(new[] { "taken.over" }, takenOver.CalledModIds);

        ServiceConsumerRow unresolved = rows.Single(r => r.Name == "custom-id");
        ClassicAssert.IsNull(unresolved.PackageId);

        // 尚未呼叫的排最後。
        ClassicAssert.AreEqual("Idle Mod", rows.Last().Name);
        CollectionAssert.IsEmpty(rows.Last().CalledModIds);
    }

    /// <summary>只排除框架自己的兩個工具 modId，以 RimLLM. 開頭的第三方模組仍要列出。</summary>
    [Test]
    public void TestBuild_ExcludesOnlyInternalModIds()
    {
        List<ServiceConsumerRow> rows = ServiceConsumerList.Build(
            new KeyValuePair<string, string>[0],
            new[] { ServiceConsumerList.DebugChatModId, ServiceConsumerList.SchemaSelfTestModId, "RimLLM.VoiceAddon" },
            Resolve);

        ClassicAssert.AreEqual(1, rows.Count);
        ClassicAssert.AreEqual("RimLLM.VoiceAddon", rows[0].PackageId);
    }
}
