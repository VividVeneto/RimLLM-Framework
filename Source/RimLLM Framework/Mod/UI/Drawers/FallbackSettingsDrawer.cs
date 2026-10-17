using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責 Fallback 鏈配置面板的 UI 渲染與互動狀態管理。
/// </summary>
public static class FallbackSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    /// <summary>
    /// 獲取 Fallback 設定詳細內容的滾動高度。本頁只剩路由策略與鏈條順序，新增模型已搬至模型設置頁。
    /// </summary>
    public static float GetHeight(float width)
    {
        int chainCount = Settings.FallbackChain.Count;
        return 150f + (chainCount * 36f);
    }

    /// <summary>
    /// 繪製 Fallback 鏈設定。
    /// </summary>
#pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
    public static void DrawFallbackSettings(Listing_Standard listing)
    {
        // 3. 智慧路由設定
        Rect routingRect = listing.GetRect(30f);
        float routingLabelWidth = Text.CalcSize("RimLLM_RoutingStrategyLabel".Translate()).x;
        Rect strategyLabelRect = new Rect(routingRect.x, routingRect.y, routingLabelWidth + 5f, routingRect.height);
        Rect strategyBtnRect = new Rect(routingRect.x + routingLabelWidth + 15f, routingRect.y, 220f, routingRect.height);

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
#pragma warning disable S3267 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
        {
            Widgets.Label(strategyLabelRect, "RimLLM_RoutingStrategyLabel".Translate());
        }

        if (Widgets.ButtonText(strategyBtnRect, StrategyLabelKey(Settings.RoutingStrategy).Translate()))
        {
            var options = new List<FloatMenuOption>();
            for (int strategy = 0; strategy < StrategyNames.Length; strategy++)
            {
                int captured = strategy;
                options.Add(new FloatMenuOption(
                    StrategyLabelKey(captured).Translate(),
                    () => { Settings.RoutingStrategy = captured; Settings.Write(); }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        listing.GapLine(10f);

        listing.Label("RimLLM_FallbackExplanation".Translate());
        listing.Gap(8f);
        var chain = Settings.FallbackChain;
        int originalCount = chain.Count;
        chain.RemoveAll(entry => string.IsNullOrEmpty(entry));
        if (chain.Count != originalCount)
        {
            Settings.FallbackChain = chain;
            Settings.Write();
        }

        // 1. 繪製 Fallback 鏈列表（只管順序與增刪；新增模型改在模型設置頁）
        if (chain.Count == 0)
        {
            listing.Label("RimLLM_FallbackEmptyWarning".Translate());
        }
        else
        {
            for (int i = 0; i < chain.Count; i++)
            {
                string entry = chain[i];
                Rect itemRect = listing.GetRect(30f);

                // 左右劃分：本頁只管順序與增刪，模型級數值（分級、上下文上限）改在模型設置頁調整。
                Rect labelRect = new Rect(itemRect.x, itemRect.y, itemRect.width - 120f, itemRect.height);
                Rect upRect = new Rect(itemRect.x + itemRect.width - 110f, itemRect.y, 30f, itemRect.height);
                Rect downRect = new Rect(itemRect.x + itemRect.width - 75f, itemRect.y, 30f, itemRect.height);
                Rect deleteRect = new Rect(itemRect.x + itemRect.width - 40f, itemRect.y, 30f, itemRect.height);

                // 繪製順序標記與名稱
                Widgets.Label(labelRect, $" {i + 1}. <color=cyan>{entry}</color>");

                // 上移按鈕
                if (i > 0)
                {
                    if (Widgets.ButtonText(upRect, "▲"))
                    {
                        string temp = chain[i];
                        chain[i] = chain[i - 1];
                        chain[i - 1] = temp;
                        Settings.FallbackChain = chain;
                        Settings.Write();
                        break;
                    }
                }
                else
                {
                    GUI.color = Color.gray;
                    Widgets.ButtonText(upRect, "▲");
                    GUI.color = Color.white;
                }

                // 下移按鈕
                if (i < chain.Count - 1)
                {
                    if (Widgets.ButtonText(downRect, "▼"))
                    {
                        string temp = chain[i];
                        chain[i] = chain[i + 1];
                        chain[i + 1] = temp;
                        Settings.FallbackChain = chain;
                        Settings.Write();
                        break;
                    }
                }
                else
                {
                    GUI.color = Color.gray;
                    Widgets.ButtonText(downRect, "▼");
                    GUI.color = Color.white;
                }

                // 刪除按鈕
                if (Widgets.ButtonText(deleteRect, "X"))
                {
                    Settings.RemoveFallbackEntry(chain[i]);
                    Settings.Write();
                    break;
                }
            }
        }
        listing.GapLine(10f);

    }
#pragma warning restore S3776

    /// <summary>
    /// 路由策略名稱，索引即為 <see cref="RimLLMFrameworkSettings.RoutingStrategy"/> 的值。
    /// 目前選項的顯示與下拉選單共用這份清單，不需要另外維護一份 switch 對照。
    /// </summary>
    private static readonly string[] StrategyNames = ["PriorityFailover", "MinLatency", "RoundRobin", "LowestCost"];

    private static string StrategyLabelKey(int strategy)
    {
        string name = strategy >= 0 && strategy < StrategyNames.Length ? StrategyNames[strategy] : StrategyNames[0];
        return "RimLLM_RoutingStrategy_" + name;
    }
}
#pragma warning restore S3267
