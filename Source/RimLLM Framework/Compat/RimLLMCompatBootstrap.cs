using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RimLLM_Framework.Compat
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 第三方相容層登錄表與啟動掛載點。
    /// </summary>
    /// <remarks>
    /// 以 <see cref="StaticConstructorOnStartup"/> 觸發而非在 Mod 建構子裡做，因為 Mod 建構子執行時
    /// 其他 Mod 的組件不一定已載入；StaticConstructorOnStartup 保證所有 Mod 組件都已就緒，
    /// 載入順序因此不影響攔截是否成功。
    ///
    /// 新增一個目標 Mod 只需：在 Targets 清單加入一個 DelegateCompatTarget，指定 PackageId、顯示名稱與 ApplyPatch 委派。
    /// </remarks>
    [StaticConstructorOnStartup]
    internal static class RimLLMCompatBootstrap
    {
        internal const string HarmonyId = "GreenMushroom.RimLLMFramework";

        /// <summary>所有已知的接管目標（依設定頁顯示順序）。</summary>
        public static readonly IReadOnlyList<RimLLMCompatTarget> Targets = new RimLLMCompatTarget[]
        {
            new DelegateCompatTarget(
                RimTalkCompatTarget.PackageId,
                "RimTalk",
                h => RimTalkCompatPatch.Apply(h)),
            new DelegateCompatTarget(
                AutoTranslationCompatTarget.PackageId,
                "Auto Translation",
                h => AutoTranslationCompatPatch.Apply(h),
                enabled => AutoTranslationCompatPatch.OnTakeoverToggled(enabled)),
            new DelegateCompatTarget(
                ScAutoCompatTarget.PackageId,
                "One 簡中自動化 (Simplified Chinese Automation: One)",
                h => ScAutoCompatPatch.Apply(h),
                enabled => ScAutoCompatPatch.OnTakeoverToggled(enabled)),
            new DelegateCompatTarget(
                ModCompatCheckerCompatTarget.PackageId,
                "Mod 兼容性檢查器 (Mod Compatibility Checker)",
                h => ModCompatCheckerCompatPatch.Apply(h))
        };

        static RimLLMCompatBootstrap()
        {
            try
            {
                ApplyAll();
            }
            catch (Exception ex)
            {
                // Harmony 本身缺席（brrainz.harmony 未啟用）或其他啟動期意外：相容層整體停用，主功能不受影響。
                Log.Warning($"[RimLLM] 相容層初始化失敗，第三方接管功能停用：{ex.Message}");
            }
        }

        /// <summary>
        /// 與靜態建構子分開，讓 HarmonyLib 型別的載入延後到這個方法被 JIT 時——
        /// 0Harmony.dll 缺席時例外才會落在上面的 try/catch 裡，而不是在型別初始化階段炸掉。
        /// </summary>
        private static void ApplyAll()
        {
            var harmony = new Harmony(HarmonyId);
            foreach (RimLLMCompatTarget target in Targets)
            {
                if (target.IsInstalled)
                {
                    target.TryApply(harmony);
                }
            }
        }
    }
#pragma warning restore S101
}
