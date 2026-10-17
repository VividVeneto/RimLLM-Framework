using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoTranslation.Translators;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// Auto Translation 相容層單元測試：型別載入防線、訊息封裝、參數設定、路由與開關。
/// </summary>
[TestFixture]
public class CompatAutoTranslationTests
{
    [Test]
    public void AutoTranslationTarget_DeclaresStablePackageId()
    {
        ClassicAssert.AreEqual("seohyeon.autotranslation", AutoTranslationCompatTarget.PackageId);
    }

    [Test]
    public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
    {
        var settings = new RimLLMFrameworkSettings();

        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId));
        settings.SetCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId, true);
        ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId));
        settings.SetCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId, false);
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId));
    }

    [Test]
    public void Client_GetResponseUnsafe_FormatsValidJsonWithTokens()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "翻譯成功文本"))
            {
                ModelId = "test-model",
                Usage = new UsageDetails
                {
                    InputTokenCount = 12,
                    OutputTokenCount = 8
                }
            }
        };
        var client = new AutoTranslationCompatClient(fake);

        string json = client.GetResponseUnsafe("原文", "系統提示詞");

        ClassicAssert.IsNotNull(json);
        StringAssert.Contains("\"content\":\"翻譯成功文本\"", json.Replace(" ", string.Empty));
        StringAssert.Contains("\"prompt_tokens\":12", json.Replace(" ", string.Empty));
        StringAssert.Contains("\"completion_tokens\":8", json.Replace(" ", string.Empty));
    }

    [Test]
    public void Client_BuildsMessagesCorrectlyAndDisablesReasoning()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))
        };
        var client = new AutoTranslationCompatClient(fake);

        client.GetResponseUnsafe("測試內容", "翻譯指示");

        ChatMessage[] messages = [.. fake.ReceivedMessages.Single()];
        ClassicAssert.AreEqual(2, messages.Length);
        ClassicAssert.AreEqual(ChatRole.System, messages[0].Role);
        ClassicAssert.AreEqual("翻譯指示", messages[0].Text);
        ClassicAssert.AreEqual(ChatRole.User, messages[1].Role);
        ClassicAssert.AreEqual("測試內容", messages[1].Text);

        ChatOptions options = fake.ReceivedOptions.Single();
        ClassicAssert.IsInstanceOf<RimLLMChatOptions>(options);
        var rimOptions = (RimLLMChatOptions)options;
        ClassicAssert.IsTrue(rimOptions.DisableReasoning);
        ClassicAssert.AreEqual(0.3f, rimOptions.Temperature);
        // 原生翻譯器不設 max_tokens；不明確給值會被框架預設的 1024 截斷批次譯文。
        ClassicAssert.AreEqual(AutoTranslationCompatClient.MaxOutputTokens, rimOptions.MaxOutputTokens);
        ClassicAssert.GreaterOrEqual(rimOptions.MaxOutputTokens, 2048);
    }

    [Test]
    public void Prefixes_RouteOnlySentinelInstance()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "攔截成功"))
            {
                Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5 }
            }
        };

        AutoTranslationCompatClient previousClient = AutoTranslationCompatPatch.Client;
        object previousSentinel = AutoTranslationCompatPatch.Sentinel;

        try
        {
            AutoTranslationCompatPatch.Client = new AutoTranslationCompatClient(fake);

            var sentinel = new Translator_OpenAICompatible();
            var native = new Translator_OpenAICompatible();
            AutoTranslationCompatPatch.Sentinel = sentinel;

            // 模擬接管生效：經測試接縫直接指定判定結果，不經開關＋供應商檢查
            AutoTranslationCompatPatch.SetTakeOverCacheForTests(true);

            // 原生實例：應放行（回傳 true）且 __result 為空
            string nativeResult = null;
            bool nativeHandled = AutoTranslationCompatPatch.GetResponseUnsafePrefix(native, "text", "prompt", ref nativeResult);
            ClassicAssert.IsTrue(nativeHandled);
            ClassicAssert.IsNull(nativeResult);

            // 哨兵實例：應攔截（回傳 false）且 __result 有值
            string sentinelResult = null;
            bool sentinelHandled = AutoTranslationCompatPatch.GetResponseUnsafePrefix(sentinel, "text", "prompt", ref sentinelResult);
            ClassicAssert.IsFalse(sentinelHandled);
            ClassicAssert.IsNotNull(sentinelResult);
            StringAssert.Contains("攔截成功", sentinelResult);
        }
        finally
        {
            AutoTranslationCompatPatch.Client = previousClient;
            AutoTranslationCompatPatch.Sentinel = previousSentinel;
            AutoTranslationCompatPatch.ResetCacheForTests();
        }
    }

    [Test]
    public void Prefix_WhenOffline_ReturnsEchoInsteadOfHittingNativeNetwork()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "不應被呼叫"))
        };

        AutoTranslationCompatClient previousClient = AutoTranslationCompatPatch.Client;
        object previousSentinel = AutoTranslationCompatPatch.Sentinel;

        try
        {
            AutoTranslationCompatPatch.Client = new AutoTranslationCompatClient(fake);

            var sentinel = new Translator_OpenAICompatible();
            AutoTranslationCompatPatch.Sentinel = sentinel;

            // 模擬離線：判定為 false 時當下這筆應回原文 echo，而非放行哨兵原生網路
            AutoTranslationCompatPatch.SetTakeOverCacheForTests(false);

            string result = null;
            bool handled = AutoTranslationCompatPatch.GetResponseUnsafePrefix(sentinel, "原文內容", "prompt", ref result);
            ClassicAssert.IsFalse(handled);
            ClassicAssert.IsNotNull(result);
            StringAssert.Contains("原文內容", result);
            ClassicAssert.IsEmpty(fake.ReceivedMessages);
        }
        finally
        {
            AutoTranslationCompatPatch.Client = previousClient;
            AutoTranslationCompatPatch.Sentinel = previousSentinel;
            AutoTranslationCompatPatch.ResetCacheForTests();
        }
    }

    [Test]
    public void Prefix_ClientThrows_PropagatesForRetry()
    {
        var fake = new CapturingChatClient
        {
            ResponseException = new InvalidOperationException("瞬斷")
        };

        AutoTranslationCompatClient previousClient = AutoTranslationCompatPatch.Client;
        object previousSentinel = AutoTranslationCompatPatch.Sentinel;

        try
        {
            AutoTranslationCompatPatch.Client = new AutoTranslationCompatClient(fake);

            var sentinel = new Translator_OpenAICompatible();
            AutoTranslationCompatPatch.Sentinel = sentinel;
            AutoTranslationCompatPatch.SetTakeOverCacheForTests(true);

            string result = null;
            ClassicAssert.Throws<InvalidOperationException>(() =>
                AutoTranslationCompatPatch.GetResponseUnsafePrefix(sentinel, "text", "prompt", ref result));
        }
        finally
        {
            AutoTranslationCompatPatch.Client = previousClient;
            AutoTranslationCompatPatch.Sentinel = previousSentinel;
            AutoTranslationCompatPatch.ResetCacheForTests();
        }
    }

    [Test]
    public void OnTakeoverToggled_OnlyInvalidatesCache()
    {
        object previousSentinel = AutoTranslationCompatPatch.Sentinel;
        ITranslator previousCurrent = AutoTranslation.Services.TranslatorManager.CurrentTranslator;
        bool previousReady = AutoTranslation.Services.TranslatorManager.Ready;

        try
        {
            // CurrentTranslator 不放哨兵，避免 Sync 觸發置換副作用，只觀察快取行為
            var sentinel = new Translator_OpenAICompatible();
            var other = new Translator_OpenAICompatible();
            AutoTranslationCompatPatch.Sentinel = sentinel;
            AutoTranslation.Services.TranslatorManager.CurrentTranslator = other;

            // 先強制判定為 true，再切換開關：若實作仍直寫快取，判定會維持 true；
            // 正確行為是失效重算（測試環境開關關閉 → false）
            AutoTranslationCompatPatch.SetTakeOverCacheForTests(true);
            AutoTranslationCompatPatch.OnTakeoverToggled(true);
            ClassicAssert.IsFalse(AutoTranslationCompatPatch.ShouldTakeOver());
        }
        finally
        {
            AutoTranslationCompatPatch.Sentinel = previousSentinel;
            AutoTranslation.Services.TranslatorManager.CurrentTranslator = previousCurrent;
            AutoTranslation.Services.TranslatorManager.Ready = previousReady;
            AutoTranslationCompatPatch.ResetCacheForTests();
        }
    }

    [Test]
    public void Client_LargeUsage_SaturatesToIntMax()
    {
        var fake = new CapturingChatClient
        {
            ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = (long)int.MaxValue + 100,
                    OutputTokenCount = long.MaxValue
                }
            }
        };
        var client = new AutoTranslationCompatClient(fake);

        string json = client.GetResponseUnsafe("t", "p");

        StringAssert.Contains($"\"prompt_tokens\":{int.MaxValue}", json.Replace(" ", string.Empty));
        StringAssert.Contains($"\"completion_tokens\":{int.MaxValue}", json.Replace(" ", string.Empty));
    }

    [Test]
    public void SyncCurrentTranslator_SwapsSentinelAndNativeCorrectly()
    {
        object previousSentinel = AutoTranslationCompatPatch.Sentinel;
        object previousNative = AutoTranslationCompatPatch.NativeTranslator;
        ITranslator previousCurrent = AutoTranslation.Services.TranslatorManager.CurrentTranslator;
        bool previousReady = AutoTranslation.Services.TranslatorManager.Ready;

        try
        {
            var sentinel = new Translator_OpenAICompatible();
            var native = new Translator_OpenAICompatible();
            native.Ready = true;
            AutoTranslationCompatPatch.Sentinel = sentinel;
            AutoTranslationCompatPatch.NativeTranslator = native;
            AutoTranslation.Services.TranslatorManager.CurrentTranslator = native;

            // 開啟接管：應置換為哨兵
            AutoTranslationCompatPatch.SetTakeOverCacheForTests(true);
            AutoTranslationCompatPatch.SyncCurrentTranslator();
            ClassicAssert.AreSame(sentinel, AutoTranslation.Services.TranslatorManager.CurrentTranslator);
            ClassicAssert.IsTrue(AutoTranslation.Services.TranslatorManager.Ready);

            // 關閉接管：應還原為原生
            AutoTranslationCompatPatch.SetTakeOverCacheForTests(false);
            AutoTranslationCompatPatch.SyncCurrentTranslator();
            ClassicAssert.AreSame(native, AutoTranslation.Services.TranslatorManager.CurrentTranslator);
        }
        finally
        {
            AutoTranslationCompatPatch.Sentinel = previousSentinel;
            AutoTranslationCompatPatch.NativeTranslator = previousNative;
            AutoTranslation.Services.TranslatorManager.CurrentTranslator = previousCurrent;
            AutoTranslation.Services.TranslatorManager.Ready = previousReady;
            AutoTranslationCompatPatch.ResetCacheForTests();
        }
    }

    /// <summary>
    /// 鎖住相容層的型別載入防線：框架 DLL 裡任何型別（含編譯器生成的 async 狀態機、closure、快取的 lambda
    /// 委派）都不得以 AutoTranslation 型別當基底類別、介面或欄位型別。
    /// </summary>
    [Test]
    public void FrameworkAssembly_HasNoAutoTranslationTypesInBaseInterfacesOrFields()
    {
        var offenders = new List<string>();
        foreach (Type type in typeof(AutoTranslationCompatPatch).Assembly.GetTypes())
        {
            foreach ((string where, Type dependency) in TypeLoadDependencies(type))
            {
                if (dependency.Assembly.GetName().Name == "AutoTranslation")
                {
                    offenders.Add(type.FullName + " " + where + " -> " + dependency.FullName);
                }
            }
        }

        ClassicAssert.IsEmpty(offenders,
            "這些型別在基底／介面／欄位層級參考了 AutoTranslation，Auto Translation 未安裝時框架會被 RimWorld 拒載或在 DevMode 啟動時報錯。");
    }

    /// <summary>
    /// 鎖住靜態方法簽章：Debug 動作選單（<c>DebugTabMenu_Actions.InitActions</c>）會對所有型別的
    /// <c>Static | Public | NonPublic</c> 方法做 <c>IsDefined</c>（實例方法不在掃描範圍），Mono 因此會解析參數與回傳型別；簽章含任一相容目標 Mod 的型別時，該 Mod 缺席
    /// 玩家一開 Debug 選單就會拋 TypeLoadException。目標 Mod 型別只能出現在方法本體。
    /// 涵蓋全部相容目標（AutoTranslation、ModCompatChecker、RimTalk、RimTranslation）。
    /// </summary>
    [Test]
    public void FrameworkAssembly_HasNoCompatTargetTypesInMethodSignatures()
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var targetAssemblies = new HashSet<string> { "AutoTranslation", "ModCompatChecker", "RimTalk", "RimTranslation" };
        var offenders = new List<string>();
        foreach (Type type in typeof(AutoTranslationCompatPatch).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(all))
            {
                var signature = method.GetParameters().Select(p => p.ParameterType).ToList();
                signature.Add(method.ReturnType);
                foreach (Type t in signature.SelectMany(Expand))
                {
                    if (targetAssemblies.Contains(t.Assembly.GetName().Name))
                    {
                        offenders.Add(type.FullName + "." + method.Name + " -> " + t.FullName);
                    }
                }
            }
        }

        ClassicAssert.IsEmpty(offenders,
            "這些方法簽章參考了相容目標 Mod 的型別，目標 Mod 未安裝時開啟 Debug 動作選單會拋 TypeLoadException。");
    }

    private static IEnumerable<(string where, Type type)> TypeLoadDependencies(Type type)
    {
        const BindingFlags allFields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        if (type.BaseType != null)
        {
            foreach (Type t in Expand(type.BaseType)) yield return ("base", t);
        }
        foreach (Type iface in type.GetInterfaces())
        {
            foreach (Type t in Expand(iface)) yield return ("interface", t);
        }
        foreach (FieldInfo field in type.GetFields(allFields))
        {
            foreach (Type t in Expand(field.FieldType)) yield return ("field " + field.Name, t);
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (Type t in Expand(type.GetElementType())) yield return t;
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type t in Expand(argument)) yield return t;
            }
        }
    }
}
