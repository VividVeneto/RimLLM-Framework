using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責全域配置（GlobalConfig）分頁的 UI 渲染。
/// </summary>
public static class GlobalConfigDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    /// <summary>
    /// 獲取全域配置分頁詳細內容的滾動高度。
    /// </summary>
    public static float GetHeight(float width)
    {
        return 375f;
    }

    /// <summary>
    /// 繪製全域配置設定。
    /// </summary>
    public static void DrawGlobalConfigSettings(Listing_Standard listing)
    {
        float prevTimeout = Settings.ApiTimeout;
        int prevRetries = Settings.MaxRetries;
        float prevDelay = Settings.RetryDelay;
        int prevMaxConcurrent = Settings.MaxConcurrentRequests;
        bool prevNativeSchema = Settings.EnableNativeSchema;
        bool prevJsonRepair = Settings.EnableJsonRepair;

        // 1. API 逾時時間
        listing.Label("RimLLM_ApiTimeoutLabel".Translate(Mathf.RoundToInt(Settings.ApiTimeout)));
        Settings.ApiTimeout = listing.Slider(Settings.ApiTimeout, 5f, 120f);

        // 2. 單模型最多重試次數
        listing.Label("RimLLM_MaxRetriesLabel".Translate(Settings.MaxRetries));
        float retriesVal = listing.Slider((float)Settings.MaxRetries, 0f, 10f);
        Settings.MaxRetries = Mathf.RoundToInt(retriesVal);

        // 3. 重試間隔
        listing.Label("RimLLM_RetryDelayLabel".Translate(Mathf.RoundToInt(Settings.RetryDelay)));
        Settings.RetryDelay = listing.Slider(Settings.RetryDelay, 0f, 10f);

        // 4. 最大並行數
        listing.Label("RimLLM_MaxConcurrentRequestsLabel".Translate(Settings.MaxConcurrentRequests));
        float maxConcurrentVal = listing.Slider((float)Settings.MaxConcurrentRequests, 1f, 10f);
        Settings.MaxConcurrentRequests = Mathf.RoundToInt(maxConcurrentVal);
        listing.Gap(6f);

        // 4.5. 原生 Schema & JSON 修復開關
        bool enableNativeSchema = Settings.EnableNativeSchema;
        listing.CheckboxLabeled("RimLLM_EnableNativeSchemaLabel".Translate(), ref enableNativeSchema);
        Settings.EnableNativeSchema = enableNativeSchema;

        bool enableJsonRepair = Settings.EnableJsonRepair;
        listing.CheckboxLabeled("RimLLM_EnableJsonRepairLabel".Translate(), ref enableJsonRepair);
        Settings.EnableJsonRepair = enableJsonRepair;
        listing.Gap(6f);

        bool changed = Math.Abs(prevTimeout - Settings.ApiTimeout) > 0.001f ||
            prevRetries != Settings.MaxRetries ||
            Math.Abs(prevDelay - Settings.RetryDelay) > 0.001f ||
            prevMaxConcurrent != Settings.MaxConcurrentRequests ||
            prevNativeSchema != Settings.EnableNativeSchema ||
            prevJsonRepair != Settings.EnableJsonRepair;

        if (RimLLMUIStyle.ShouldSaveSettings(changed))
        {
            Settings.Write();
        }

        // 5. 預設思考強度
        Rect effortRect = listing.GetRect(30f);
        Rect labelRect = new Rect(effortRect.x, effortRect.y, 250f, effortRect.height);
        Rect btnRect = new Rect(effortRect.x + 260f, effortRect.y, 200f, effortRect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(labelRect, "RimLLM_ReasoningEffortLabel".Translate());
        }

        string currentEffortLabel = Settings.DefaultReasoningEffort == null ? "RimLLM_ReasoningEffort_Auto".Translate() : $"RimLLM_ReasoningEffort_{Settings.DefaultReasoningEffort}".Translate();
        if (Widgets.ButtonText(btnRect, currentEffortLabel))
        {
            List<FloatMenuOption> options =
            [
                new("RimLLM_ReasoningEffort_Auto".Translate(), () => { Settings.DefaultReasoningEffort = null; Settings.Write(); }),
                new("RimLLM_ReasoningEffort_Low".Translate(), () => { Settings.DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Low; Settings.Write(); }),
                new("RimLLM_ReasoningEffort_Medium".Translate(), () => { Settings.DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Medium; Settings.Write(); }),
                new("RimLLM_ReasoningEffort_High".Translate(), () => { Settings.DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.High; Settings.Write(); })
            ];
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
