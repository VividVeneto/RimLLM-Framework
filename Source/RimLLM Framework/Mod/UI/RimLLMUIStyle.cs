using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// 設定介面共用的樣式常數與繪製輔助。
///
/// 顏色集中在此，是因為同一種語意（選中、次要文字、成功、危險）先前以字面值散落在各個
/// drawer，任何調整都得逐檔搜尋，實際上也已經出現同語意不同值的情形。
/// 這裡的值直接沿用原本的硬編值，收攏本身不改變外觀。
/// </summary>
public static class RimLLMUIStyle
{
    /// <summary>選中項目的底色。</summary>
    public static readonly Color SelectionFill = new(1f, 1f, 1f, 0.08f);

    /// <summary>模型 chip 的底色。</summary>
    public static readonly Color ChipFill = new(1f, 1f, 1f, 0.05f);

    /// <summary>停用或次要狀態的文字色。</summary>
    public static readonly Color Muted = new(0.53f, 0.53f, 0.53f);

    /// <summary>啟用狀態的文字色。</summary>
    public static readonly Color Success = new(0.13f, 0.77f, 0.37f);

    /// <summary>進度條未填滿的底色。</summary>
    public static readonly Color BarTrack = new(0.2f, 0.2f, 0.2f, 0.6f);

    /// <summary>失敗比例的底色（成功率條的背景）。</summary>
    public static readonly Color BarTrackDanger = new(0.35f, 0.15f, 0.15f, 0.6f);

    /// <summary>成功率條的填色。</summary>
    public static readonly Color BarFillSuccess = new(0.18f, 0.48f, 0.18f, 0.8f);

    /// <summary>快取命中率條的填色。</summary>
    public static readonly Color BarFillCache = new(0.15f, 0.45f, 0.6f, 0.8f);

    /// <summary>警告狀態文字/圖示色。</summary>
    public static readonly Color Warning = new(0.96f, 0.62f, 0.04f);

    /// <summary>對話測試中使用者發言氣泡的底色。</summary>
    public static readonly Color BubbleUserFill = new(0.14f, 0.24f, 0.35f, 0.45f);

    /// <summary>對話測試中使用者發言氣泡的外框色。</summary>
    public static readonly Color BubbleUserBorder = new(0.28f, 0.48f, 0.70f, 0.65f);

    /// <summary>對話測試中 AI 回覆氣泡的底色。</summary>
    public static readonly Color BubbleAiFill = new(0.12f, 0.12f, 0.12f, 0.55f);

    /// <summary>對話測試中 AI 回覆氣泡的外框色。</summary>
    public static readonly Color BubbleAiBorder = new(0.32f, 0.32f, 0.32f, 0.65f);

    /// <summary>
    /// 暫時改變 <see cref="Text"/> 的全域繪製狀態，離開作用域自動還原。
    ///
    /// RimWorld 的 Text.Anchor / Font / WordWrap 是全域可變狀態，各處手動設值再手動還原；
    /// 只要中途拋出例外，還原那行就不會執行，之後整個遊戲的 UI 都會沿用被改壞的狀態。
    /// 以 using 包住可確保還原一定發生。
    /// </summary>
    public static Scope With(TextAnchor? anchor = null, GameFont? font = null, bool? wordWrap = null)
    {
        return new Scope(anchor, font, wordWrap);
    }

    /// <summary>
    /// <see cref="With"/> 回傳的作用域。以 struct 實作避免每幀配置。
    /// </summary>
    public struct Scope : IDisposable
    {
        private readonly TextAnchor _anchor;
        private readonly GameFont _font;
        private readonly bool _wordWrap;

        internal Scope(TextAnchor? anchor, GameFont? font, bool? wordWrap)
        {
            _anchor = Text.Anchor;
            _font = Text.Font;
            _wordWrap = Text.WordWrap;

            if (anchor.HasValue) Text.Anchor = anchor.Value;
            if (font.HasValue) Text.Font = font.Value;
            if (wordWrap.HasValue) Text.WordWrap = wordWrap.Value;
        }

        public void Dispose()
        {
            Text.Anchor = _anchor;
            Text.Font = _font;
            Text.WordWrap = _wordWrap;
        }
    }

    /// <summary>API 金鑰遮罩時保留的頭尾字元數。</summary>
    private const int MaskedPrefixLength = 8;
    private const int MaskedSuffixLength = 4;

    /// <summary>
    /// 把 API 金鑰轉成可安全顯示於畫面的遮罩形式。
    ///
    /// 保留頭尾是為了讓使用者能辨認自己設定了哪一把金鑰（多把金鑰輪替時特別需要），
    /// 中段一律隱藏。長度不足以安全保留頭尾時整串隱藏 —— 短金鑰露出頭尾等同露出大半內容。
    /// 空字串維持空，否則畫面上會看起來像已經設定過。
    /// </summary>
    public static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return string.Empty;

        if (apiKey.Length <= MaskedPrefixLength + MaskedSuffixLength)
        {
            return new string('•', apiKey.Length);
        }

        return apiKey.Substring(0, MaskedPrefixLength) +
               "…" +
               apiKey.Substring(apiKey.Length - MaskedSuffixLength);
    }

    /// <summary>
    /// 以關鍵字過濾模型名稱。空白關鍵字代表不過濾。
    /// 供應商設定頁與 <see cref="Dialog_SelectModel"/> 共用同一份實作，
    /// 否則兩處的比對規則會各自漂移（例如一邊分大小寫、一邊不分）。
    /// </summary>
    public static List<string> FilterModels(IEnumerable<string> models, string filter)
    {
        var result = new List<string>();
        if (models == null) return result;

        bool matchAll = string.IsNullOrEmpty(filter) || filter.Trim().Length == 0;
        string needle = matchAll ? null : filter.Trim();

        // 以不分大小寫的比對取代逐筆 ToLowerInvariant：這段每幀對數百筆模型名跑一次，
        // 每筆各配置一個小寫副本只是白白製造 GC 壓力。
        foreach (string model in models)
        {
            if (string.IsNullOrEmpty(model)) continue;
            if (matchAll || model.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Add(model);
            }
        }
        return result;
    }

    /// <summary>
    /// 過濾結果的快取。模型清單與過濾字串都只在使用者操作時變動，但 OnGUI 每幀跑好幾次，
    /// 沒有這層的話每個 pass 都要重新走一遍數百筆比對。
    /// 以呼叫端提供的 <paramref name="cacheKey"/>（通常是供應商 ID）與清單版本號區分項目。
    /// </summary>
    private sealed class FilteredModelsEntry
    {
        public int Version;
        public string Filter;
        public List<string> Result;
    }

    private static readonly Dictionary<string, FilteredModelsEntry> FilteredModelsCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 帶快取的 <see cref="FilterModels"/>。<paramref name="version"/> 或 <paramref name="filter"/>
    /// 與上次相同就直接回傳上次的結果（同一個 List 實例，呼叫端不得改動）。
    /// <paramref name="sourceProvider"/> 只在需要重算時才被呼叫，讓呼叫端連清單複製都能省下。
    /// </summary>
    public static List<string> FilterModelsCached(string cacheKey, int version, string filter, Func<IList<string>> sourceProvider)
    {
        if (sourceProvider == null) throw new ArgumentNullException(nameof(sourceProvider));
        filter = filter ?? string.Empty;

        if (FilteredModelsCache.TryGetValue(cacheKey ?? string.Empty, out FilteredModelsEntry entry) &&
            entry.Version == version &&
            string.Equals(entry.Filter, filter, StringComparison.Ordinal))
        {
            return entry.Result;
        }

        IList<string> source = sourceProvider();
        entry = new FilteredModelsEntry
        {
            Version = version,
            Filter = filter,
            Result = FilterModels(source, filter)
        };
        FilteredModelsCache[cacheKey ?? string.Empty] = entry;
        return entry.Result;
    }

    /// <summary>
    /// 算出捲動清單中真正落在可視範圍內的列區間 [firstRow, endRow)。
    /// 每列固定高度的清單只畫可視的那幾列：OpenRouter 的模型晶片動輒數百個，
    /// 全部畫出來等於每幀對捲出視野的內容做數百次文字量測與繪製呼叫。
    /// 前後各多算一列，捲動時不會露出空白。
    /// </summary>
    public static void GetVisibleRowRange(float scrollY, float viewportHeight, float rowStride, float topPadding, int rowCount, out int firstRow, out int endRow)
    {
        if (rowCount <= 0 || rowStride <= 0f)
        {
            firstRow = 0;
            endRow = 0;
            return;
        }

        firstRow = Mathf.Clamp(Mathf.FloorToInt((scrollY - topPadding) / rowStride) - 1, 0, rowCount);
        endRow = Mathf.Clamp(Mathf.CeilToInt((scrollY - topPadding + viewportHeight) / rowStride) + 1, firstRow, rowCount);
    }

    /// <summary>
    /// 依字型與寬度分開的截斷快取。RimWorld 的 <c>Truncate</c> 不帶快取時會逐字元呼叫
    /// <c>Text.CalcSize</c> 直到塞得下為止，長模型名在窄晶片裡一次要量十幾回，
    /// 每幀對每個晶片重做是設定頁最貴的單一開銷。原生快取只以字串為鍵，
    /// 寬度或字型一變就會拿到錯的結果，因此這裡再包一層以 (字型, 寬度) 分桶。
    /// </summary>
    private static readonly Dictionary<long, Dictionary<string, string>> TruncateCaches =
        [];

    public static string TruncateCached(string text, float width)
    {
        if (string.IsNullOrEmpty(text)) return text;

        long bucket = ((long)Text.Font << 32) | (uint)Mathf.RoundToInt(width);
        if (!TruncateCaches.TryGetValue(bucket, out Dictionary<string, string> cache))
        {
            cache = new Dictionary<string, string>(StringComparer.Ordinal);
            TruncateCaches[bucket] = cache;
        }
        return text.Truncate(width, cache);
    }

    /// <summary>
    /// 依可用寬度算出模型 chip 的欄數與實際寬度。
    ///
    /// 欄數由偏好寬度決定，但寬度改由欄數反推，讓 chip 填滿整列 ——
    /// 先前使用固定寬度，右緣總是留下一條用不到的空隙，長模型名稱卻同時被截斷。
    /// </summary>
    public static void ComputeChipLayout(
        float contentWidth, float gap, float preferredWidth, out int columns, out float chipWidth)
    {
        columns = Mathf.Max(1, Mathf.FloorToInt((contentWidth - gap) / (preferredWidth + gap)));
        chipWidth = Mathf.Max(1f, (contentWidth - gap * (columns + 1)) / columns);
    }

    /// <summary>
    /// 清單項目的選取／滑過外框。左欄一級分頁與中欄供應商清單共用同一套視覺。
    /// </summary>
    public static void DrawSelectableFrame(Rect rect, bool selected)
    {
        if (selected)
        {
            Widgets.DrawBoxSolid(rect, SelectionFill);
            Widgets.DrawBox(rect, 1);
            // 左側高亮指示條，強化層次感
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), Success);
        }
        else if (Mouse.IsOver(rect))
        {
            Widgets.DrawHighlight(rect);
        }
    }

    /// <summary>
    /// 帶「顯示／遮罩」切換鈕的 API 金鑰欄位，回傳欄位內的最新值。
    ///
    /// 遮罩時刻意不畫 TextField：TextField 會把畫面上的字串當成使用者輸入寫回，
    /// 那會讓遮罩字串直接覆蓋掉真正的金鑰。改畫唯讀外觀的標籤。
    /// 供應商分頁與 Embedding 分頁共用，否則兩處的遮罩規則會各自漂移。
    /// </summary>
    /// <param name="revealed">傳入目前是否明文顯示；使用者按下切換鈕時就地翻轉。</param>
    public static string DrawMaskableKeyField(Rect inputRect, Rect revealRect, string value, ref bool revealed)
    {
        string result = value;
        if (revealed)
        {
            result = Widgets.TextField(inputRect, value);
        }
        else
        {
            Widgets.DrawBoxSolid(inputRect, ChipFill);
            Widgets.DrawBox(inputRect, 1);
            using (With(TextAnchor.MiddleLeft, wordWrap: false))
            {
                Widgets.Label(inputRect.ContractedBy(4f), MaskApiKey(value));
            }
        }

        if (Widgets.ButtonText(revealRect, revealed ? "abc" : "•••"))
        {
            revealed = !revealed;
        }
        TooltipHandler.TipRegion(revealRect, (revealed ? "RimLLM_HideApiKey" : "RimLLM_RevealApiKey").Translate());
        return result;
    }

    /// <summary>狀態列（第三方整合、使用中的模組分頁）的列高與列距。</summary>
    public const float StatusRowHeight = 34f;
    public const float StatusRowGap = 4f;
    private const float StatusRowPadding = 10f;
    private const float StatusDotSize = 8f;
    private const float StatusBadgeWidth = 120f;

    /// <summary>狀態列色點：生效／已呼叫。</summary>
    public static readonly Color StatusGreen = new Color32(0x4a, 0xde, 0x80, 0xff);

    /// <summary>狀態列色點：可用但未生效／尚未呼叫。</summary>
    public static readonly Color StatusBlue = new Color32(0x60, 0xa5, 0xfa, 0xff);

    /// <summary>
    /// 緊湊狀態列：底色、色點、標題與右側狀態標籤，兩個分頁共用同一套版面以免各自漂移。
    /// <paramref name="tint"/> 套用在色點、標題與標籤上（例如未安裝時淡化），底色不受影響；結束時 GUI.color 還原為白色。
    /// <paramref name="rightInset"/> 為標籤右側預留給額外控制項（如勾選框）的寬度。
    /// 回傳滑鼠是否停在列上，提示由呼叫端掛，才能只在懸停時組字串。
    /// </summary>
    public static bool DrawStatusRow(Rect rowRect, Color statusColor, string title, string badge, Color tint, float rightInset = 0f)
    {
        Widgets.DrawBoxSolid(rowRect, ChipFill);
        bool hovered = Mouse.IsOver(rowRect);
        if (hovered) Widgets.DrawHighlight(rowRect);

        Rect badgeRect = new Rect(rowRect.xMax - StatusRowPadding - rightInset - StatusBadgeWidth, rowRect.y, StatusBadgeWidth, rowRect.height);
        Rect dotRect = new Rect(
            rowRect.x + StatusRowPadding,
            rowRect.y + (rowRect.height - StatusDotSize) / 2f,
            StatusDotSize, StatusDotSize);
        Rect titleRect = new Rect(dotRect.xMax + 8f, rowRect.y, badgeRect.x - dotRect.xMax - 16f, rowRect.height);

        GUI.color = tint;
        Widgets.DrawBoxSolid(dotRect, statusColor * tint);
        using (With(TextAnchor.MiddleLeft, GameFont.Small, wordWrap: false))
        {
            Widgets.Label(titleRect, title);
        }
        using (With(TextAnchor.MiddleRight, GameFont.Tiny, wordWrap: false))
        {
            GUI.color = statusColor * tint;
            Widgets.Label(badgeRect, badge);
        }
        GUI.color = Color.white;
        return hovered;
    }

    /// <summary>
    /// 比例條：底色 + 依比例填滿 + 外框 + 置中百分比標籤。
    /// </summary>
    public static void DrawRatioBar(Rect rect, float fillPercent, Color track, Color fill, string label)
    {
        Widgets.DrawBoxSolid(rect, track);
        if (fillPercent > 0f)
        {
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width * fillPercent, rect.height), fill);
        }
        Widgets.DrawBox(rect, 1);

        using (With(TextAnchor.MiddleCenter, GameFont.Tiny))
        {
            Widgets.Label(rect, label);
        }
    }

    /// <summary>
    /// 判斷當前 OnGUI 事件是否適合寫入設定至磁碟（僅在滑鼠放開或無事件時觸發，避免每幀 60 FPS 重複寫入）。
    /// </summary>
    public static bool ShouldSaveSettings(bool changed)
    {
        return changed && (Event.current == null || Event.current.type == EventType.MouseUp || Event.current.rawType == EventType.MouseUp);
    }
}
#pragma warning restore S101, S2342
