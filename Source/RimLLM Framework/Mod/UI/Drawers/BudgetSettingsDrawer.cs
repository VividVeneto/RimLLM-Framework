using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責預算與防護（Budget &amp; Safety）分頁的 UI 渲染。
/// </summary>
public static class BudgetSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    public static float GetHeight(float width)
    {
        float height = 220f; // 基礎預算設定 + 遙測顯示
        if (Settings.EnableAntiAbuse)
        {
            height += 150f; // 展開的防爆細部滑桿
        }
        else
        {
            height += 40f; // 僅防爆開關
        }
        return height;
    }

    /// <summary>每日 token 預算滑桿上限。0 代表無限制。</summary>
    private const float MaxDailyTokenBudget = 10000000f;

    public static void DrawBudgetSettings(Listing_Standard listing)
    {
        long prevDailyLimit = Settings.DailyTokenBudgetLimit;
        int prevPolicy = Settings.BudgetPolicy;
        bool prevEnableAntiAbuse = Settings.EnableAntiAbuse;
        int prevMaxRequests = Settings.MaxRequestsPerWindow;
        int prevWindow = Settings.ThrottlingWindowSeconds;
        int prevCooldown = Settings.CoolDownDurationSeconds;

        // 1. 今日 token 預算上限（輸入＋輸出合計），0 代表無限制
        listing.Label("RimLLM_DailyBudgetLimitLabel".Translate(Settings.DailyTokenBudgetLimit.ToString("N0")));
        Settings.DailyTokenBudgetLimit = (long)Math.Round(listing.Slider((float)Settings.DailyTokenBudgetLimit, 0f, MaxDailyTokenBudget));

        // 2. 預算超限應對策略 (Budget Policy)
        Rect policyRect = listing.GetRect(30f);
        Rect policyLabelRect = new Rect(policyRect.x, policyRect.y, 250f, policyRect.height);
        Rect policyBtnRect = new Rect(policyRect.x + 260f, policyRect.y, 280f, policyRect.height);

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(policyLabelRect, "RimLLM_BudgetPolicyLabel".Translate());
        }

        string policyLabelKey = PolicyLabelKey(Settings.BudgetPolicy);
        if (Widgets.ButtonText(policyBtnRect, policyLabelKey.Translate()))
        {
            var options = new List<FloatMenuOption>();
            for (int policy = 0; policy < PolicyNames.Length; policy++)
            {
                int captured = policy;
                options.Add(new FloatMenuOption(
                    PolicyLabelKey(captured).Translate(),
                    () => { Settings.BudgetPolicy = captured; Settings.Write(); }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        listing.Gap(12f);

        // 3. 頻率防護 (Anti-Abuse Throttling)
        bool enableAntiAbuse = Settings.EnableAntiAbuse;
        listing.CheckboxLabeled("RimLLM_EnableAntiAbuseLabel".Translate(), ref enableAntiAbuse);
        Settings.EnableAntiAbuse = enableAntiAbuse;
        listing.Gap(6f);

        if (Settings.EnableAntiAbuse)
        {
            // 窗口內最大請求次數
            listing.Label("RimLLM_MaxRequestsPerWindowLabel".Translate(Settings.MaxRequestsPerWindow));
            float maxRequestsVal = listing.Slider((float)Settings.MaxRequestsPerWindow, 2f, 50f);
            Settings.MaxRequestsPerWindow = Mathf.RoundToInt(maxRequestsVal);

            // 監測窗口秒數
            listing.Label("RimLLM_ThrottlingWindowSecondsLabel".Translate(Settings.ThrottlingWindowSeconds));
            float windowVal = listing.Slider((float)Settings.ThrottlingWindowSeconds, 2f, 60f);
            Settings.ThrottlingWindowSeconds = Mathf.RoundToInt(windowVal);

            // 冷卻秒數
            listing.Label("RimLLM_CoolDownDurationSecondsLabel".Translate(Settings.CoolDownDurationSeconds));
            float cooldownVal = listing.Slider((float)Settings.CoolDownDurationSeconds, 5f, 300f);
            Settings.CoolDownDurationSeconds = Mathf.RoundToInt(cooldownVal);
            listing.Gap(12f);
        }

        // 4. 遙測統計與重置
        listing.GapLine(12f);
        listing.Label("RimLLM_DailyAccumulatedCostLabel".Translate(
            Settings.DailyAccumulatedTokens.ToString("N0"),
            string.IsNullOrEmpty(Settings.DailyBudgetResetDate) ? DateTime.Today.ToString("yyyy-MM-dd") : Settings.DailyBudgetResetDate
        ));
        listing.Gap(6f);

        Rect resetBtnRect = listing.GetRect(32f);
        resetBtnRect.width = 180f;
        if (Widgets.ButtonText(resetBtnRect, "RimLLM_ResetDailyCostBtn".Translate()))
        {
            Settings.DailyAccumulatedTokens = 0;
            Settings.DailyAccumulatedCost = 0f;
            Settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
            Settings.SaveTelemetry();
            Messages.Message("RimLLM_MsgDailyCostReset".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        // 檢查變更並寫入（僅在釋放滑鼠或非 GUI 呼叫時寫入，避免拖曳滑桿時每幀觸發同步磁碟 I/O）
        bool changed = prevDailyLimit != Settings.DailyTokenBudgetLimit ||
            prevPolicy != Settings.BudgetPolicy ||
            prevEnableAntiAbuse != Settings.EnableAntiAbuse ||
            prevMaxRequests != Settings.MaxRequestsPerWindow ||
            prevWindow != Settings.ThrottlingWindowSeconds ||
            prevCooldown != Settings.CoolDownDurationSeconds;

        if (RimLLMUIStyle.ShouldSaveSettings(changed))
        {
            Settings.Write();
        }
    }

    /// <summary>
    /// 預算政策的名稱，索引即為 <see cref="RimLLMFrameworkSettings.BudgetPolicy"/> 的值。
    /// 目前選項的顯示與下拉選單共用這份清單，不需要另外維護一份 switch 對照。
    /// </summary>
    private static readonly string[] PolicyNames = ["HardBlock", "WarnOnly"];

    private static string PolicyLabelKey(int policy)
    {
        string name = policy >= 0 && policy < PolicyNames.Length ? PolicyNames[policy] : PolicyNames[0];
        return "RimLLM_BudgetPolicy_" + name;
    }
}
