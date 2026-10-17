using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// 負責 RimLLM Framework 的設定畫面進入點與框架布局。
/// 將具體的分頁渲染與交互狀態委託給各個 Drawer 類別。
/// </summary>
public static class RimLLMSettingsUI
{
    private const string ProvidersCategoryId = "Providers";

    // 全域選單狀態
    private static string activeMainCategory = ProvidersCategoryId;
    private static Vector2 _detailScrollPosition = Vector2.zero;

    /// <summary>RimWorld 捲軸的固定寬度。</summary>
    private const float ScrollBarWidth = 16f;

    /// <summary>捲到底時在最後一個控制項下方保留的呼吸空間。</summary>
    private const float DetailBottomPadding = 12f;

    /// <summary>各分頁上一幀實際畫出的內容高度，見 <see cref="ResolveDetailViewHeight"/>。</summary>
    private static readonly Dictionary<string, float> _measuredHeights = [];

    /// <summary>
    /// 初始化 UI 狀態，例如還原對話歷史。
    /// </summary>
    public static void Initialize(RimLLMFrameworkSettings settings)
    {
        ChatTestDrawer.Initialize(settings);
    }

    /// <summary>
    /// 繪製 Mod 設定視窗的主入口。
    /// </summary>
    public static void DoSettingsWindowContents(Rect inRect)
    {
        // 動態尋找並調整 RimWorld 預設的 Mod 設定視窗大小
        var modSettingsWindow = Find.WindowStack.WindowOfType<Dialog_ModSettings>();
        if (modSettingsWindow != null)
        {
            float targetWidth = 1150f;
            float targetHeight = 780f;
            if (Math.Abs(modSettingsWindow.windowRect.width - targetWidth) > 1f || Math.Abs(modSettingsWindow.windowRect.height - targetHeight) > 1f)
            {
                modSettingsWindow.windowRect.width = targetWidth;
                modSettingsWindow.windowRect.height = targetHeight;
                modSettingsWindow.windowRect.x = (UI.screenWidth - targetWidth) / 2f;
                modSettingsWindow.windowRect.y = (UI.screenHeight - targetHeight) / 2f;
            }
        }

        // 直接在原地繪製完整設定內容
        DrawSettingsWindowBody(inRect);
    }

    private static void DrawSettingsWindowBody(Rect inRect)
    {
        float leftWidth = 135f;
        float midWidth = 190f;
        float gap = 8f;
        float height = inRect.height - 10f;

        // 1. 左側一級分類欄
        Rect leftColRect = new Rect(inRect.x, inRect.y, leftWidth, height);
        Widgets.DrawMenuSection(leftColRect);
        DrawLeftCategoryMenu(leftColRect);

        // 2. 根據選中項目決定中欄與右欄佈局
        if (activeMainCategory == ProvidersCategoryId)
        {
            Rect midColRect = new Rect(leftColRect.xMax + gap, inRect.y, midWidth, height);
            Widgets.DrawMenuSection(midColRect);
            ProviderSettingsDrawer.DrawMiddleProviderMenu(midColRect);

            Rect rightColRect = new Rect(midColRect.xMax + gap, inRect.y, inRect.width - leftWidth - midWidth - gap * 2f, height);
            Widgets.DrawMenuSection(rightColRect);
            DrawRightDetailContent(rightColRect);
        }
        else if (activeMainCategory == "Embedding")
        {
            Rect midColRect = new Rect(leftColRect.xMax + gap, inRect.y, midWidth, height);
            Widgets.DrawMenuSection(midColRect);
            EmbeddingSettingsDrawer.DrawMiddleEmbeddingMenu(midColRect);

            Rect rightColRect = new Rect(midColRect.xMax + gap, inRect.y, inRect.width - leftWidth - midWidth - gap * 2f, height);
            Widgets.DrawMenuSection(rightColRect);
            DrawRightDetailContent(rightColRect);
        }
        else
        {
            Rect rightColRect = new Rect(leftColRect.xMax + gap, inRect.y, inRect.width - leftWidth - gap, height);
            Widgets.DrawMenuSection(rightColRect);
            DrawRightDetailContent(rightColRect);
        }
    }

    private static void DrawLeftCategoryMenu(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(6f);
        Listing_Standard listing = new Listing_Standard();
        listing.Begin(contentRect);
        Text.Font = GameFont.Small;
        listing.Label("RimLLM_MainMenu".Translate());
        listing.Gap(6f);

        for (int i = 0; i < Pages.Length; i++)
        {
            if (i > 0) listing.Gap(4f);
            DrawCategoryButton(listing, Pages[i].MenuLabelKey.Translate(), Pages[i].Id);
        }
        listing.End();
    }

    private static void DrawCategoryButton(Listing_Standard listing, string label, string categoryId)
    {
        Rect btnRect = listing.GetRect(32f);
        RimLLMUIStyle.DrawSelectableFrame(btnRect, activeMainCategory == categoryId);

        if (Widgets.ButtonInvisible(btnRect))
        {
            activeMainCategory = categoryId;
            _detailScrollPosition = Vector2.zero;
        }

        string text = activeMainCategory == categoryId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
        using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
#pragma warning disable S1192 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
        {
            Widgets.Label(btnRect, text);
        }
    }

    private static void DrawRightDetailContent(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(8f);
        Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 24f);
        DetailPage page = FindPage(activeMainCategory);
        string titleText = page != null ? page.Title() : string.Empty;

        Widgets.Label(titleRect, $"<size=14><b>{titleText}</b></size>");
        Widgets.DrawLineHorizontal(contentRect.x, titleRect.yMax + 4f, contentRect.width);

        Rect detailRect = new Rect(contentRect.x, titleRect.yMax + 8f, contentRect.width, contentRect.height - 36f);

        // 扣掉捲軸寬度，否則垂直捲軸出現時內容被擠出右緣，還會多冒出一條水平捲軸。
        float viewWidth = detailRect.width - ScrollBarWidth;
        Rect viewRect = new Rect(0f, 0f, viewWidth, ResolveDetailViewHeight(viewWidth, detailRect.height));
        Widgets.BeginScrollView(detailRect, ref _detailScrollPosition, viewRect);
        Listing_Standard listing = new Listing_Standard();
        listing.Begin(viewRect);

        page?.Draw(listing);

        // CurHeight 必須在 End() 之前讀取；End() 之後該值不再代表本次繪製的內容高度。
        _measuredHeights[GetDetailPageKey()] = listing.CurHeight;

        listing.End();
        Widgets.EndScrollView();
    }

    /// <summary>
    /// 決定捲動內容的高度。優先採用上一幀實際畫出來的高度，只有該分頁第一次繪製時
    /// 才退回各 Drawer 的估計值。
    ///
    /// 先前一律使用估計值，而那些是手調的魔術數字，會與實際繪製內容漂移 ——
    /// 供應商分頁曾回報 848px 但實際只畫了約 662px，底部因此多出一大塊幽靈空白。
    /// 改為量測後，新增或調整任何控制項都不必再同步維護一份高度公式。
    /// </summary>
    private static float ResolveDetailViewHeight(float width, float visibleHeight)
    {
        float height = _measuredHeights.TryGetValue(GetDetailPageKey(), out float measured)
            ? measured + DetailBottomPadding
            : GetDetailViewHeight(width);

        // 內容比可視區短時仍讓 viewRect 至少等高，避免捲動範圍為負值。
        return Mathf.Max(height, visibleHeight);
    }

    /// <summary>
    /// 量測值的鍵。供應商分頁各家內容長度不同，因此要連子分頁一起入鍵。
    /// </summary>
    private static string GetDetailPageKey()
    {
        if (activeMainCategory == ProvidersCategoryId)
            return ProvidersCategoryId + "/" + ProviderSettingsDrawer.ActiveProviderSubTab;
        if (activeMainCategory == "Embedding")
            return "Embedding/" + EmbeddingSettingsDrawer.ActiveEmbeddingSubTab;
        return activeMainCategory;
    }

    private static float GetDetailViewHeight(float width)
    {
        DetailPage page = FindPage(activeMainCategory);
        return page != null ? page.EstimateHeight(width) : 280f;
    }

    /// <summary>
    /// 一個一級分頁的完整定義：選單標籤、標題、繪製與首幀高度估計。
    ///
    /// 這四件事先前分散在四段各自的 if/else-if 串接中，新增或改名一個分頁得同步改四處，
    /// 漏改任何一處都只會在執行期才顯現（標題空白、內容不繪製、捲動高度錯誤）。
    /// </summary>
    private sealed class DetailPage
    {
        public string Id;
        public string MenuLabelKey;
        public Func<string> Title;
        public Action<Listing_Standard> Draw;
        public Func<float, float> EstimateHeight;
    }

    private static readonly DetailPage[] Pages =
    [
        new() {
            Id = ProvidersCategoryId,
            MenuLabelKey = "RimLLM_TabProviders",
            Title = () =>
            {
                string tabName = ProviderSettingsDrawer.ActiveProviderSubTab;
                if (tabName == ProviderIds.OpenAICompatible)
                {
                    tabName += "RimLLM_LocalCompatibleMode".Translate();
                }
                return "RimLLM_TitleProviderSettings".Translate(tabName);
            },
            Draw = ProviderSettingsDrawer.DrawRightDetailContent,
            EstimateHeight = ProviderSettingsDrawer.GetHeight
        },
        new() {
            Id = "Models",
            MenuLabelKey = "RimLLM_TabModels",
            Title = () => "RimLLM_TitleModels".Translate(),
            Draw = ModelSettingsDrawer.DrawModelSettings,
            EstimateHeight = ModelSettingsDrawer.GetHeight
        },
        new() {
            Id = "Fallback",
            MenuLabelKey = "RimLLM_TabFallback",
            Title = () => "RimLLM_TitleFallback".Translate(),
            Draw = FallbackSettingsDrawer.DrawFallbackSettings,
            EstimateHeight = FallbackSettingsDrawer.GetHeight
        },
        new() {
            Id = "GlobalConfig",
            MenuLabelKey = "RimLLM_TabGlobalConfig",
            Title = () => "RimLLM_TitleGlobalConfig".Translate(),
            Draw = GlobalConfigDrawer.DrawGlobalConfigSettings,
            EstimateHeight = GlobalConfigDrawer.GetHeight
        },
        new() {
            Id = "Embedding",
            MenuLabelKey = "RimLLM_TabEmbedding",
            Title = () =>
            {
                string tabName = EmbeddingSettingsDrawer.GetProviderDisplayName(EmbeddingSettingsDrawer.ActiveEmbeddingSubTab);
                return "RimLLM_TitleEmbeddingSettings".Translate(tabName);
            },
            Draw = EmbeddingSettingsDrawer.DrawRightDetailContent,
            EstimateHeight = EmbeddingSettingsDrawer.GetHeight
        },
        new() {
            Id = "Budget",
            MenuLabelKey = "RimLLM_TabBudget",
            Title = () => "RimLLM_TitleBudget".Translate(),
            Draw = BudgetSettingsDrawer.DrawBudgetSettings,
            EstimateHeight = BudgetSettingsDrawer.GetHeight
        },
        new() {
            Id = "Compat",
            MenuLabelKey = "RimLLM_TabCompat",
            Title = () => "RimLLM_TitleCompat".Translate(),
            Draw = CompatSettingsDrawer.DrawCompatSettings,
            EstimateHeight = CompatSettingsDrawer.GetHeight
        },
        new() {
            Id = "Consumers",
            MenuLabelKey = "RimLLM_TabConsumers",
            Title = () => "RimLLM_TitleConsumers".Translate(),
            Draw = ServiceConsumersDrawer.DrawServiceConsumers,
            EstimateHeight = ServiceConsumersDrawer.GetHeight
        },
        new() {
            Id = "ChatTest",
            MenuLabelKey = "RimLLM_TabChatTest",
            Title = () => "RimLLM_ChatTitle".Translate(),
            Draw = ChatTestDrawer.DrawChatTestSettings,
            EstimateHeight = ChatTestDrawer.GetHeight
        },
        new() {
            Id = "Debug",
            MenuLabelKey = "RimLLM_TabDebug",
            Title = () => "RimLLM_TitleDebug".Translate(),
            Draw = DebugSettingsDrawer.DrawDebugSettings,
            EstimateHeight = DebugSettingsDrawer.GetHeight
        }
    ];

    private static DetailPage FindPage(string categoryId)
    {
        foreach (DetailPage page in Pages)
        {
            if (page.Id == categoryId) return page;
        }
        return null;
    }
}
#pragma warning restore S101, S2342
#pragma warning restore S1192
