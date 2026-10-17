using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DevTools.Testing;
using HarmonyLib;
using RimLLM_Framework.Compat;
using Verse;

namespace RimLLM_Framework.InGameTests;

/// <summary>
/// 相容層的 Harmony 攔截是否真的掛在玩家實際安裝的目標 Mod 上。
/// headless 測試只能對編譯期參考（Source/Libs）驗證簽章，看不到版本漂移或掛載失敗。
/// 目標 Mod 未啟用時記錄 skipped；要涵蓋全部目標，需以 cj.rimtalk、seohyeon.autotranslation、
/// modcompatchecker.main、ow1r.scauto 為伴隨 Mod 再跑一次。
/// </summary>
[TestFixture(TestType.MainMenu)]
internal sealed class CompatPatchTests
{
    private enum PatchKind { Prefix, Postfix }

    private sealed class ExpectedPatch
    {
        public string ModId;
        public string TypeName;
        public string MethodName;
        public PatchKind Kind;
        // 相容層對「可選」攔截採 fail-soft：方法不存在時只記警告，不算掛載失敗。
        public bool Optional;
    }

    private static readonly ExpectedPatch[] Expected =
    [
        new ExpectedPatch { ModId = RimTalkCompatTarget.PackageId, TypeName = "AIClientFactory", MethodName = "GetAIClientAsync", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = RimTalkCompatTarget.PackageId, TypeName = "RimTalkSettings", MethodName = "GetActiveConfig", Kind = PatchKind.Postfix },
        new ExpectedPatch { ModId = RimTalkCompatTarget.PackageId, TypeName = "OpenAIClient", MethodName = "GetChatCompletionAsync", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = RimTalkCompatTarget.PackageId, TypeName = "OpenAIClient", MethodName = "StreamAsync", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = AutoTranslationCompatTarget.PackageId, TypeName = "Translator_OpenAICompatible", MethodName = "GetResponseUnsafe", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = AutoTranslationCompatTarget.PackageId, TypeName = "TranslatorManager", MethodName = "Translate", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = ModCompatCheckerCompatTarget.PackageId, TypeName = "AIService", MethodName = "CallAPIWithTimeout", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = ModCompatCheckerCompatTarget.PackageId, TypeName = "ModCompatSettings", MethodName = "IsAIConfigured", Kind = PatchKind.Postfix, Optional = true },
        new ExpectedPatch { ModId = ModCompatCheckerCompatTarget.PackageId, TypeName = "ApiBalanceChecker", MethodName = "CheckBalance", Kind = PatchKind.Prefix, Optional = true },
        new ExpectedPatch { ModId = ScAutoCompatTarget.PackageId, TypeName = "ResponsesClient", MethodName = "PostOnceCoreAsync", Kind = PatchKind.Prefix },
        new ExpectedPatch { ModId = ScAutoCompatTarget.PackageId, TypeName = "ApiConfigurationStore", MethodName = "IsLocalBaseUrl", Kind = PatchKind.Postfix, Optional = true },
        new ExpectedPatch { ModId = ScAutoCompatTarget.PackageId, TypeName = "ApiConnectionProbe", MethodName = "FetchModelsAsync", Kind = PatchKind.Prefix, Optional = true },
        new ExpectedPatch { ModId = ScAutoCompatTarget.PackageId, TypeName = "ThinkingParameterProbeService", MethodName = "RunAsync", Kind = PatchKind.Prefix, Optional = true },
    ];

    [Test]
    public void InstalledTargetsReportPatched()
    {
        if (!RimLLMCompatBootstrap.Targets.Any(t => t.IsInstalled))
        {
            Test.Skip("No compatibility target mods are active.");
            return;
        }
        var failures = new List<string>();
        foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
        {
            if (!target.IsInstalled)
            {
                TestHelpers.LogSkipped(nameof(InstalledTargetsReportPatched), $"{target.ModId} not active");
                continue;
            }
            if (!target.IsPatched || target.PatchError != null)
            {
                failures.Add($"{target.ModId}: IsPatched={target.IsPatched}, PatchError={target.PatchError}");
            }
        }
        TestHelpers.AssertNone(failures, "compat targets failed to hook");
    }

    [Test]
    public void EveryExpectedHarmonyPatchIsApplied()
    {
        if (!RimLLMCompatBootstrap.Targets.Any(t => t.IsInstalled))
        {
            Test.Skip("No compatibility target mods are active.");
            return;
        }
        var failures = new List<string>();
        foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
        {
            // 與框架同一套偵測（IsInstalled），兩個測試的 skip 判定才會一致。
            if (!target.IsInstalled)
            {
                TestHelpers.LogSkipped(nameof(EveryExpectedHarmonyPatchIsApplied), $"{target.ModId} not active");
                continue;
            }

            string packageId = ModLister.GetActiveModWithIdentifier(target.ModId, ignorePostfix: true).PackageId;
            ModContentPack mod = LoadedModManager.RunningModsListForReading.First(m => m.PackageId == packageId);
            List<Type> modTypes = [.. mod.assemblies.loadedAssemblies.SelectMany(AccessTools.GetTypesFromAssembly)];
            foreach (ExpectedPatch expected in Expected.Where(e => e.ModId == target.ModId))
            {
                CheckPatch(expected, modTypes, failures);
            }
        }
        TestHelpers.AssertNone(failures, "Harmony patches missing on compat targets");
    }

    private static void CheckPatch(ExpectedPatch expected, List<Type> modTypes, List<string> failures)
    {
        string label = $"{expected.ModId} {expected.TypeName}.{expected.MethodName} ({expected.Kind})";
        List<MethodInfo> overloads = [.. modTypes
            .Where(t => t.Name == expected.TypeName)
            .SelectMany(AccessTools.GetDeclaredMethods)
            .Where(m => m.Name == expected.MethodName)];

        if (overloads.Count == 0)
        {
            if (expected.Optional)
            {
                TestHelpers.LogSkipped(nameof(EveryExpectedHarmonyPatchIsApplied), $"{label} absent in installed version");
            }
            else
            {
                failures.Add($"{label}: method not found in installed mod");
            }
            return;
        }

        bool patched = overloads.Any(method =>
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            IEnumerable<Patch> patches = expected.Kind == PatchKind.Prefix ? info.Prefixes : info.Postfixes;
            return patches.Any(p => p.owner == RimLLMCompatBootstrap.HarmonyId);
        });
        if (!patched)
        {
            failures.Add($"{label}: no {RimLLMCompatBootstrap.HarmonyId} patch on any of {overloads.Count} overload(s)");
        }
    }
}
