using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// 自訂模型選擇視窗，支援關鍵字過濾搜尋與滾動列表，以解決模型選項過多導致 UI 混亂的問題。
/// </summary>
public class Dialog_SelectModel : Window
{
    private readonly List<string> _allModels;
    private readonly Action<string> _onSelected;
    private string _filter = "";
    private Vector2 _scrollPosition = Vector2.zero;

    /// <summary>過濾結果快取：清單在視窗生命週期內固定，只有過濾字串變了才需要重算。</summary>
    private string _cachedFilter;
    private List<string> _cachedFiltered;

    public override Vector2 InitialSize => new(550f, 650f);

    public Dialog_SelectModel(List<string> models, Action<string> onSelected)
    {
        this._allModels = models ?? [];
        this._onSelected = onSelected;
        this.doCloseButton = true;
        this.doCloseX = true;
        this.closeOnClickedOutside = true;
        this.absorbInputAroundWindow = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        // 1. 標題
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "RimLLM_SelectModelTitle".Translate());
        Text.Font = GameFont.Small;

        // 2. 搜尋框與清空按鈕
        DrawSearchBar(inRect);

        // 快捷過濾標籤 Chip
        DrawQuickFilters(inRect);

        // 3. 過濾模型清單（與供應商設定頁共用同一份比對規則，避免兩處各自漂移）
        if (_cachedFiltered == null || !string.Equals(_cachedFilter, _filter, StringComparison.Ordinal))
        {
            _cachedFilter = _filter;
            _cachedFiltered = RimLLMUIStyle.FilterModels(_allModels, _filter);
        }
        List<string> filteredModels = _cachedFiltered;

        // 4. 滾動清單區
        DrawModelList(inRect, filteredModels);
    }

    private void DrawSearchBar(Rect inRect)
    {
        Rect searchLabelRect = new Rect(0f, 40f, 70f, 30f);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(searchLabelRect, "RimLLM_Search".Translate() + ": ");
        }

        bool hasFilter = !string.IsNullOrEmpty(_filter);
        float clearBtnWidth = hasFilter ? 30f : 0f;
        Rect filterRect = new Rect(75f, 40f, inRect.width - 75f - clearBtnWidth, 30f);
        _filter = Widgets.TextField(filterRect, _filter);

        if (hasFilter)
        {
            Rect clearRect = new Rect(inRect.width - 28f, 41f, 28f, 28f);
            if (Widgets.ButtonText(clearRect, "×"))
            {
                _filter = "";
            }
            TooltipHandler.TipRegion(clearRect, "RimLLM_ClearFilter".Translate());
        }
    }

    private void DrawQuickFilters(Rect inRect)
    {
        Rect quickFilterRow = new Rect(0f, 75f, inRect.width, 26f);
        string[] presetFilters = ["", "gemini", "gpt", "claude", "deepseek", "qwen", "flash", "free"];
        float chipX = quickFilterRow.x;
        for (int p = 0; p < presetFilters.Length; p++)
        {
            string tag = presetFilters[p];
            string label = string.IsNullOrEmpty(tag) ? (string)"RimLLM_FilterAll".Translate() : tag;
            float chipWidth = Text.CalcSize(label).x + 16f;
            if (chipX + chipWidth > inRect.width) break;

            Rect chipRect = new Rect(chipX, quickFilterRow.y, chipWidth, 24f);
            bool isActive = (string.IsNullOrEmpty(tag) && string.IsNullOrEmpty(_filter)) ||
                            (!string.IsNullOrEmpty(tag) && _filter.Equals(tag, StringComparison.OrdinalIgnoreCase));

            RimLLMUIStyle.DrawSelectableFrame(chipRect, isActive);
            if (Widgets.ButtonInvisible(chipRect))
            {
                _filter = tag;
            }
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter, GameFont.Tiny))
            {
                Widgets.Label(chipRect, isActive ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>");
            }

            chipX += chipWidth + 6f;
        }
    }

    private void DrawModelList(Rect inRect, List<string> filteredModels)
    {
        float topOffset = 108f;
        float bottomOffset = 55f; // 為關閉按鈕留空間
        Rect listRect = new Rect(0f, topOffset, inRect.width, inRect.height - topOffset - bottomOffset);
        Widgets.DrawMenuSection(listRect);

        if (_allModels.Count == 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(listRect, "<color=gray>" + "RimLLM_NoCachedModels".Translate() + "</color>");
            }
        }
        else if (filteredModels.Count == 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(listRect, "<color=gray>" + "RimLLM_NoMatchingModels".Translate() + "</color>");
            }
        }
        else
        {
            float contentWidth = listRect.width - 16f;
            float rowHeight = 36f;
            float viewHeight = Math.Max(listRect.height, filteredModels.Count * rowHeight + 10f);
            Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

            Widgets.BeginScrollView(listRect, ref _scrollPosition, viewRect);

            // 只畫可視範圍內的列，數百筆模型不必每幀全部畫一遍。
            RimLLMUIStyle.GetVisibleRowRange(_scrollPosition.y, listRect.height, rowHeight, 4f, filteredModels.Count, out int firstRow, out int endRow);
            for (int i = firstRow; i < endRow; i++)
            {
                string model = filteredModels[i];
                Rect rowRect = new Rect(4f, i * rowHeight + 4f, contentWidth - 8f, rowHeight - 2f);

                if (Widgets.ButtonText(rowRect, model, true, true, true))
                {
                    _onSelected?.Invoke(model);
                    Close();
                    break;
                }
            }

            Widgets.EndScrollView();
        }
    }
}
#pragma warning restore S101, S2342
