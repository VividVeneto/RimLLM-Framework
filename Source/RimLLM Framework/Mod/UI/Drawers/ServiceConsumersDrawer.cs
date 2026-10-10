using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 「使用中的模組」分頁：列出使用 RimLLM 服務的模組。來源有兩個：
    /// 已啟用且在 About.xml 宣告依賴本框架的模組（靜態），以及本次遊戲實際送出過請求的 modId（執行期）。
    /// 合併規則見 <see cref="ServiceConsumerList"/>。
    /// </summary>
    public static class ServiceConsumersDrawer
    {
        /// <summary>
        /// 宣告依賴的模組（packageId → 名稱）：Mod 清單在執行期不會變動，只算一次。
        /// 只存字串而不存 ModMetaData：該型別帶 Steamworks 欄位，單元測試以反射掃描本組件欄位型別時會載入失敗。
        /// </summary>
        private static List<KeyValuePair<string, string>> _declaredMods;

        private static List<ServiceConsumerRow> _rows;

        /// <summary>上次組列時的呼叫端 modId 數量；集合只增不減，數量變了才重組。</summary>
        private static int _rowsCallerCount = -1;

        public static float GetHeight(float width)
        {
            return 80f + Rows.Count * (RimLLMUIStyle.StatusRowHeight + RimLLMUIStyle.StatusRowGap);
        }

        public static void DrawServiceConsumers(Listing_Standard listing)
        {
            using (RimLLMUIStyle.With(font: GameFont.Small))
            {
                GUI.color = RimLLMUIStyle.Muted;
                listing.Label("RimLLM_ConsumersIntro".Translate());
                GUI.color = Color.white;
            }
            listing.Gap(12f);

            List<ServiceConsumerRow> rows = Rows;
            if (rows.Count == 0)
            {
                GUI.color = RimLLMUIStyle.Muted;
                listing.Label("RimLLM_ConsumersEmpty".Translate());
                GUI.color = Color.white;
                return;
            }

            foreach (ServiceConsumerRow row in rows)
            {
                DrawRow(listing.GetRect(RimLLMUIStyle.StatusRowHeight), row);
                listing.Gap(RimLLMUIStyle.StatusRowGap);
            }
        }

        private static List<ServiceConsumerRow> Rows
        {
            get
            {
                RimLLMUsageTracker tracker = RimLLMProvider.TryGetManager(out RimLLMManager manager) ? manager.UsageTracker : null;
                int callerCount = tracker != null ? tracker.CallerModIdCount : 0;
                if (_rows != null && _rowsCallerCount == callerCount) return _rows;

                IEnumerable<string> callerModIds = tracker != null ? tracker.CallerModIds : (IEnumerable<string>)Array.Empty<string>();
                _rows = ServiceConsumerList.Build(DeclaredMods, callerModIds, ResolveActiveMod);
                _rowsCallerCount = callerCount;
                return _rows;
            }
        }

        private static KeyValuePair<string, string>? ResolveActiveMod(string modId)
        {
            ModMetaData mod = ModLister.GetActiveModWithIdentifier(modId, ignorePostfix: true);
            return mod != null ? new KeyValuePair<string, string>(mod.PackageIdPlayerFacing, mod.Name) : (KeyValuePair<string, string>?)null;
        }

        private static List<KeyValuePair<string, string>> DeclaredMods
        {
            get
            {
                if (_declaredMods != null) return _declaredMods;

                var mods = new List<KeyValuePair<string, string>>();
                // PackageIdPlayerFacing 不帶 _steam 後綴；PackageId 在本機與工作坊副本並存時會帶，與依賴宣告永遠比對不上。
                string ownPackageId = LoadedModManager.GetMod<RimLLMFrameworkMod>()?.Content?.PackageIdPlayerFacing;
                if (!string.IsNullOrEmpty(ownPackageId))
                {
                    foreach (ModMetaData mod in ModsConfig.ActiveModsInLoadOrder)
                    {
                        if (mod.SamePackageId(ownPackageId, ignorePostfix: true)) continue;
                        foreach (ModDependency dependency in mod.Dependencies)
                        {
                            if (string.Equals(dependency.packageId, ownPackageId, StringComparison.OrdinalIgnoreCase))
                            {
                                mods.Add(new KeyValuePair<string, string>(mod.PackageIdPlayerFacing, mod.Name));
                                break;
                            }
                        }
                    }
                }
                _declaredMods = mods;
                return _declaredMods;
            }
        }

        private static void DrawRow(Rect rowRect, ServiceConsumerRow row)
        {
            bool called = row.CalledModIds.Count > 0;
            string title = row.PackageId != null
                ? $"<b>{row.Name}</b>  <color=#94a3b8>{row.PackageId}</color>"
                : $"<b>{row.Name}</b>";
            string badge = (called ? "RimLLM_ConsumersBadgeCalled" : "RimLLM_ConsumersBadgeIdle").Translate();

            if (RimLLMUIStyle.DrawStatusRow(rowRect, called ? RimLLMUIStyle.StatusGreen : RimLLMUIStyle.StatusBlue, title, badge, Color.white))
            {
                TooltipHandler.TipRegion(rowRect, BuildTooltip(row));
            }
        }

        /// <summary>只在滑鼠停在該列時才組字串。</summary>
        private static string BuildTooltip(ServiceConsumerRow row)
        {
            var lines = new List<string>
            {
                (row.Declared ? "RimLLM_ConsumersDeclared" : "RimLLM_ConsumersNotDeclared").Translate()
            };
            lines.Add(row.CalledModIds.Count > 0
                ? "RimLLM_ConsumersCalled".Translate(string.Join(", ", row.CalledModIds)).ToString()
                : "RimLLM_ConsumersNotCalled".Translate().ToString());
            if (row.PackageId == null) lines.Add("RimLLM_ConsumersUnresolved".Translate());
            return string.Join("\n", lines);
        }
    }

    /// <summary>「使用中的模組」的一列。</summary>
    internal sealed class ServiceConsumerRow
    {
        public string Name;

        /// <summary>對不上任何已啟用模組的 modId 為 null。</summary>
        public string PackageId;

        public bool Declared;

        /// <summary>對應到此列的呼叫端 modId，已排序；空清單代表本次遊戲尚未送出請求。</summary>
        public List<string> CalledModIds = new List<string>();
    }

    /// <summary>
    /// 「使用中的模組」的合併邏輯，不碰 Verse，方便單元測試。
    /// 宣告依賴與執行期 modId 以 packageId 合併成一列；modId 對不上任何已啟用模組時以原字串單獨成列。
    /// </summary>
    internal static class ServiceConsumerList
    {
        /// <summary>設定頁對話測試使用的 modId。</summary>
        internal const string DebugChatModId = "RimLLM.DebugChat";

        /// <summary>Debug 分頁 Schema 自檢使用的 modId。</summary>
        internal const string SchemaSelfTestModId = "RimLLM.SchemaSelfTest";

        /// <param name="declared">宣告依賴的模組（packageId → 名稱）。</param>
        /// <param name="callerModIds">本次遊戲送出過請求的 modId。</param>
        /// <param name="resolve">modId → 已啟用模組的（packageId, 名稱）；對不上回傳 null。</param>
        internal static List<ServiceConsumerRow> Build(
            IEnumerable<KeyValuePair<string, string>> declared,
            IEnumerable<string> callerModIds,
            Func<string, KeyValuePair<string, string>?> resolve)
        {
            var byPackageId = new Dictionary<string, ServiceConsumerRow>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ServiceConsumerRow>();

            foreach (KeyValuePair<string, string> mod in declared)
            {
                var row = new ServiceConsumerRow { Name = mod.Value, PackageId = mod.Key, Declared = true };
                byPackageId[mod.Key] = row;
                rows.Add(row);
            }

            foreach (string modId in callerModIds)
            {
                // 只排除框架自己的兩個設定頁工具；以前綴過濾會誤藏名稱剛好以 RimLLM. 開頭的第三方模組。
                if (string.Equals(modId, DebugChatModId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modId, SchemaSelfTestModId, StringComparison.OrdinalIgnoreCase)) continue;

                KeyValuePair<string, string>? mod = resolve(modId);
                string key = mod.HasValue ? mod.Value.Key : modId;
                if (!byPackageId.TryGetValue(key, out ServiceConsumerRow row))
                {
                    row = new ServiceConsumerRow { Name = mod.HasValue ? mod.Value.Value : modId, PackageId = mod?.Key };
                    byPackageId[key] = row;
                    rows.Add(row);
                }
                row.CalledModIds.Add(modId);
            }

            foreach (ServiceConsumerRow row in rows)
            {
                row.CalledModIds.Sort(StringComparer.OrdinalIgnoreCase);
            }

            // 已呼叫的排前面，其餘依名稱排序。
            rows.Sort((a, b) =>
            {
                int called = (b.CalledModIds.Count > 0).CompareTo(a.CalledModIds.Count > 0);
                return called != 0 ? called : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return rows;
        }
    }
}
