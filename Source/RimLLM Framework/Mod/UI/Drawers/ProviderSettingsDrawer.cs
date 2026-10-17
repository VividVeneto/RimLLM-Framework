using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責 API 供應商（Providers）分頁的頂層 SubTab 路由分發與選單繪製。
/// </summary>
public static class ProviderSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    /// <summary>
    /// 中欄選單項目：顯示名稱與供應商識別碼。
    /// 顯示名稱刻意保留品牌寫法（如 NVIDIA），因此無法直接沿用 ProviderIds 的值。
    /// </summary>
    private static readonly List<KeyValuePair<string, string>> MenuEntries =
    [
        new KeyValuePair<string, string>("Google Gemini", ProviderIds.Gemini),
        new KeyValuePair<string, string>("OpenAI", ProviderIds.OpenAI),
        new KeyValuePair<string, string>("DeepSeek", ProviderIds.DeepSeek),
        new KeyValuePair<string, string>("Groq", ProviderIds.Groq),
        new KeyValuePair<string, string>("Grok", ProviderIds.Grok),
        new KeyValuePair<string, string>("Z.ai", ProviderIds.Zai),
        new KeyValuePair<string, string>("OpenRouter", ProviderIds.OpenRouter),
        new KeyValuePair<string, string>("Kimi", ProviderIds.Kimi),
        new KeyValuePair<string, string>("MiniMax", ProviderIds.MiniMax),
        new KeyValuePair<string, string>("Qwen", ProviderIds.Qwen),
        new KeyValuePair<string, string>("NVIDIA", ProviderIds.Nvidia),
        new KeyValuePair<string, string>("Player2", ProviderIds.Player2),
        new KeyValuePair<string, string>("OpenAI Compatible", ProviderIds.OpenAICompatible)
    ];

    private const float SubButtonHeight = 46f;
    private const float SubButtonGap = 4f;

    // 供應商分頁專屬的 UI 暫存狀態
    public static string ActiveProviderSubTab { get; set; } = ProviderIds.Gemini;
    private static Vector2 _midScrollPosition = Vector2.zero;

    /// <summary>
    /// 獲取供應商設定詳細內容的滾動高度。
    /// </summary>
    public static float GetHeight(float width)
    {
        bool enabled = Settings.IsProviderEnabled(ActiveProviderSubTab);
        if (!enabled) return 120f;

        int modelCount = Settings.GetModelCount(ActiveProviderSubTab);
        float modelSectionHeight = modelCount > 0 ? 280f : 60f;

        // 動態計算 API 金鑰列表的高度
        float keysHeight = 0f;
        if (ActiveProviderSubTab != ProviderIds.OpenAICompatible)
        {
            string rawApiKey = Settings.GetApiKey(ActiveProviderSubTab);
            var keys = rawApiKey.Split(RimLLMFrameworkSettings.ApiKeySeparators, System.StringSplitOptions.None);
            int keyCount = Mathf.Max(1, keys.Length);
            keysHeight = 30f + (keyCount * 32f) + 36f;
        }

        float extraHeight = 0f;
        if (ProviderIds.HasChinaEndpoint(ActiveProviderSubTab))
        {
            extraHeight = 30f;
        }
        else if (ProviderIds.HasCustomEndpoint(ActiveProviderSubTab))
        {
            extraHeight = 60f;
        }

        float statsHeight = 120f;
        if (RimLLMProvider.TryGetManager(out var mgr) &&
            mgr.UsageTracker.ProviderStatistics.TryGetValue(ActiveProviderSubTab, out var stats) &&
            stats.TotalPromptTokens > 0)
        {
            statsHeight += 50f;
        }
        return 250f + keysHeight + modelSectionHeight + 100f + statsHeight + extraHeight;
    }

    /// <summary>
    /// 繪製中欄的供應商選單。
    /// </summary>
    public static void DrawMiddleProviderMenu(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(6f);

        Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 20f);
        Text.Font = GameFont.Small;
        Widgets.Label(titleRect, "RimLLM_ApiProviders".Translate());

        Rect listRect = new Rect(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
        float viewHeight = MenuEntries.Count * (SubButtonHeight + SubButtonGap) + 10f;
        Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, viewHeight);
        Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
        Listing_Standard listing = new Listing_Standard();
        listing.Begin(viewRect);

        foreach (var entry in MenuEntries)
        {
            DrawProviderSubButton(listing, entry.Key, entry.Value);
            listing.Gap(SubButtonGap);
        }
        listing.End();

        Widgets.EndScrollView();
    }

    private static void DrawProviderSubButton(Listing_Standard listing, string label, string providerId)
    {
        Rect btnRect = listing.GetRect(SubButtonHeight);
        RimLLMUIStyle.DrawSelectableFrame(btnRect, ActiveProviderSubTab == providerId);

        if (Widgets.ButtonInvisible(btnRect))
        {
            ActiveProviderSubTab = providerId;
        }

        Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
        Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
        Text.Font = GameFont.Small;
        string nameText = ActiveProviderSubTab == providerId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
        Widgets.Label(nameRect, nameText);

        bool enabled = Settings.IsProviderEnabled(providerId);
        string statusText;
        Color statusColor;

        if (!enabled)
        {
            statusText = "RimLLM_StatusDisabled".Translate();
            statusColor = RimLLMUIStyle.Muted;
        }
        // 端點相關是因為 Player2 有雙模式：判斷需代入實際端點（未設定時傳 null，視為本機模式）。
        else if (ProviderIds.RequiresApiKey(providerId, Settings.GetEndpoint(providerId, null)) && string.IsNullOrEmpty(Settings.GetApiKey(providerId)))
        {
            statusText = "RimLLM_StatusNoApiKey".Translate();
            statusColor = RimLLMUIStyle.Warning;
        }
        else
        {
            int modelCount = Settings.GetModelCount(providerId);
            statusText = "RimLLM_StatusEnabled".Translate() + " | " + "RimLLM_ModelsCount".Translate(modelCount);
            statusColor = RimLLMUIStyle.Success;
        }

        Color oldColor = GUI.color;
        GUI.color = statusColor;
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Widgets.Label(statusRect, statusText);
        }
        GUI.color = oldColor;
    }

    /// <summary>
    /// 根據目前的供應商，調度右側的詳細配置渲染。
    /// </summary>
    public static void DrawRightDetailContent(Listing_Standard listing)
    {
        GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, ActiveProviderSubTab);
    }
}
