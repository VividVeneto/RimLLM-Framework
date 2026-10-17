using System.Collections.Generic;
using RimLLM_Framework.Compat;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責第三方整合（Compat）分頁的 UI 渲染：每個可接管的 Mod 一列緊湊卡片，
/// 狀態以色點＋短標籤呈現，細節與警語收進滑鼠懸停的提示。
///
/// 先前每個 Mod 各佔一大塊（名稱、狀態、開關、兩段警語全部重複），
/// 目標一多整頁就得不停捲動；共用的警語改為只在頁首顯示一次。
/// </summary>
public static class CompatSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const float RowPadding = 10f;
    private const float CheckboxSize = 24f;

    private static readonly Color StatusActive = RimLLMUIStyle.StatusGreen;
    private static readonly Color StatusInstalled = RimLLMUIStyle.StatusBlue;
    private static readonly Color StatusPatchFailed = new Color32(0xef, 0x44, 0x44, 0xff);
    private static readonly Color StatusNotInstalled = new Color32(0x94, 0xa3, 0xb8, 0xff);

    /// <summary>未安裝的列整體淡化，讓可操作的列在清單中一眼可辨。</summary>
    private static readonly Color DimmedTint = new(1f, 1f, 1f, 0.55f);

    /// <summary>
    /// 一列的靜態資料。安裝與掛載狀態在啟動後就固定（Mod 清單不會在執行期變動、
    /// Harmony 攔截只在啟動時套用一次），因此只算一次；接管開關每幀從設定讀。
    /// </summary>
    private sealed class Row
    {
        public RimLLMCompatTarget Target;
        public CompatRowState State;
        public string Title;
    }

    private static List<Row> _rows;

    /// <summary>
    /// 排序：可用（已啟用且掛載成功）→ 掛載失敗 → 已安裝未啟用 → 未安裝。
    /// 玩家真正能操作的列排最前，清單變長時不必在一堆未安裝項目裡找。
    /// </summary>
    private static List<Row> Rows
    {
        get
        {
            if (_rows != null) return _rows;

            List<Row> rows = new List<Row>(RimLLMCompatBootstrap.Targets.Count);
            foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
            {
                rows.Add(new Row
                {
                    Target = target,
                    State = Classify(target.IsInstalled, target.IsDownloaded, target.IsPatched),
                    Title = $"<b>{target.DisplayName}</b>  <color=#94a3b8>{target.ModId}</color>"
                });
            }
            rows.Sort((a, b) => SortRank(a.State).CompareTo(SortRank(b.State)));
            _rows = rows;
            return _rows;
        }
    }

    /// <summary>目標 Mod 在設定頁的狀態；未安裝與未啟用分開，掛載失敗只可能發生在已啟用時。</summary>
    internal enum CompatRowState
    {
        NotInstalled,
        Inactive,
        PatchFailed,
        Ready
    }

    internal static CompatRowState Classify(bool installed, bool downloaded, bool patched)
    {
        if (!installed) return downloaded ? CompatRowState.Inactive : CompatRowState.NotInstalled;
        return patched ? CompatRowState.Ready : CompatRowState.PatchFailed;
    }

    internal static int SortRank(CompatRowState state)
    {
        return state switch
        {
            CompatRowState.Ready => 0,
            CompatRowState.PatchFailed => 1,
            CompatRowState.Inactive => 2,
            _ => 3,
        };
    }

    public static float GetHeight(float width)
    {
        // 說明段落 + 共用警語 + 每個目標一列
        return 150f + RimLLMCompatBootstrap.Targets.Count * (RimLLMUIStyle.StatusRowHeight + RimLLMUIStyle.StatusRowGap);
    }

    public static void DrawCompatSettings(Listing_Standard listing)
    {
        using (RimLLMUIStyle.With(font: GameFont.Small))
        {
            GUI.color = RimLLMUIStyle.Muted;
            listing.Label("RimLLM_CompatIntro".Translate());
            GUI.color = Color.white;
        }
        listing.Gap(6f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            GUI.color = RimLLMUIStyle.Warning;
            listing.Label("RimLLM_CompatTakeoverWarningShared".Translate());
            GUI.color = RimLLMUIStyle.Muted;
            listing.Label("RimLLM_CompatOfflineFallbackNote".Translate());
            GUI.color = Color.white;
        }
        listing.Gap(12f);

        foreach (Row row in Rows)
        {
            DrawRow(listing.GetRect(RimLLMUIStyle.StatusRowHeight), row);
            listing.Gap(RimLLMUIStyle.StatusRowGap);
        }
    }

    private static void DrawRow(Rect rowRect, Row row)
    {
        bool enabled = Settings.IsCompatTakeoverEnabled(row.Target.ModId);
        Color statusColor = StatusColor(row, enabled);

        Color tint = row.State >= CompatRowState.PatchFailed ? Color.white : DimmedTint;
        if (RimLLMUIStyle.DrawStatusRow(rowRect, statusColor, row.Title, StatusBadge(row, enabled), tint, CheckboxSize + 8f))
        {
            TooltipHandler.TipRegion(rowRect, BuildTooltip(row, enabled));
        }

        Rect checkboxRect = new Rect(
            rowRect.xMax - RowPadding - CheckboxSize,
            rowRect.y + (rowRect.height - CheckboxSize) / 2f,
            CheckboxSize, CheckboxSize);

        GUI.color = tint;
        bool toggled = enabled;
        Widgets.Checkbox(checkboxRect.position, ref toggled, CheckboxSize);
        GUI.color = Color.white;

        if (toggled != enabled)
        {
            Settings.SetCompatTakeoverEnabled(row.Target.ModId, toggled);
            Settings.Write();
            row.Target.OnTakeoverToggled(toggled);
        }
    }

    /// <summary>
    /// 狀態優先序：未安裝 → 掛載失敗 → 接管生效中 → 已安裝（開關未開）。
    /// 「接管生效中」代表開關開啟且 Harmony 攔截已就緒；RimLLM 當下有沒有可用供應商是每次請求才判定，這裡不重複查。
    /// </summary>
    private static Color StatusColor(Row row, bool enabled)
    {
        return row.State switch
        {
            CompatRowState.NotInstalled or CompatRowState.Inactive => StatusNotInstalled,
            CompatRowState.PatchFailed => StatusPatchFailed,
            _ => enabled ? StatusActive : StatusInstalled,
        };
    }

    private static string StatusBadge(Row row, bool enabled)
    {
        return row.State switch
        {
            CompatRowState.NotInstalled => (string)"RimLLM_CompatBadgeNotInstalled".Translate(),
            CompatRowState.Inactive => (string)"RimLLM_CompatBadgeInactive".Translate(),
            CompatRowState.PatchFailed => (string)"RimLLM_CompatBadgePatchFailed".Translate(),
            _ => (string)(enabled ? "RimLLM_CompatBadgeActive" : "RimLLM_CompatBadgeInstalled").Translate(),
        };
    }

    private static string StatusDescription(Row row, bool enabled)
    {
        return row.State switch
        {
            CompatRowState.NotInstalled => (string)"RimLLM_CompatStatusNotInstalled".Translate(),
            CompatRowState.Inactive => (string)"RimLLM_CompatStatusInactive".Translate(),
            CompatRowState.PatchFailed => (string)"RimLLM_CompatStatusPatchFailed".Translate(row.Target.PatchError ?? string.Empty),
            _ => (string)(enabled ? "RimLLM_CompatStatusActive" : "RimLLM_CompatStatusInstalled").Translate(),
        };
    }

    /// <summary>只在滑鼠停在該列時才組字串，避免每幀對每列做 Translate 與串接。</summary>
    private static string BuildTooltip(Row row, bool enabled)
    {
        string name = row.Target.DisplayName;
        return $"<b>{name}</b>  <color=#94a3b8>{row.Target.ModId}</color>\n"
            + $"{StatusDescription(row, enabled)}\n\n"
            + $"{"RimLLM_CompatTakeoverLabel".Translate(name)}\n"
            + $"{"RimLLM_CompatTakeoverWarning".Translate(name)}";
    }
}
