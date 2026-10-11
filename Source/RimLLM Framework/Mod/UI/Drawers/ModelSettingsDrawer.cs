using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責逐模型設置頁的 UI 渲染：備援鏈上每個模型的分級覆寫與上下文上限手寫值，
    /// 以及新增模型至備援鏈。備援鏈頁（<see cref="FallbackSettingsDrawer"/>）只保留
    /// 順序、增刪與路由策略。
    /// 存檔結構不變（沿用既有的分級覆寫與上下文上限覆寫字典），因此舊存檔無需遷移。
    /// </summary>
    public static class ModelSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 新增模型專屬的 UI 暫存狀態
        private static string addProviderId = ProviderIds.Gemini;
        private static string addModelName = "";

        /// <summary>
        /// 獲取模型設置詳細內容的滾動高度。
        /// </summary>
        public static int RowCount()
        {
            return CollectEntries().Count;
        }

        public static float GetHeight(float width)
        {
            return 280f + (RowCount() * 36f);
        }

        /// <summary>
        /// 備援鏈去空去重後的有序模型條目，供本頁逐列顯示。鏈內保證唯一，
        /// 但舊存檔可能殘留重複，去重後顯示才不會出現調了兩次的同一列。
        /// </summary>
        internal static List<string> CollectEntries()
        {
            return Settings.FallbackChain.Where(entry => !string.IsNullOrEmpty(entry)).Distinct().ToList();
        }

        /// <summary>
        /// 繪製模型設置。
        /// </summary>
        public static void DrawModelSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_ModelsExplanation".Translate());
            listing.Gap(8f);

            List<string> entries = CollectEntries();
            if (entries.Count == 0)
            {
                listing.Label("RimLLM_ModelsEmptyWarning".Translate());
            }
            else
            {
                string entryToRemove = null;
                for (int i = 0; i < entries.Count; i++)
                {
                    string entry = entries[i];
                    Rect itemRect = listing.GetRect(30f);

                    // 最右側為刪除按鈕，其餘按鈕依序往左排
                    if (Widgets.ButtonText(new Rect(itemRect.x + itemRect.width - 40f, itemRect.y, 30f, itemRect.height), "X"))
                    {
                        entryToRemove = entry;
                    }

                    int colonIndex = entry.IndexOf(':');
                    bool hasModelName = colonIndex >= 0;
                    float labelWidth = hasModelName ? (itemRect.width - 200f) : (itemRect.width - 50f);
                    Rect labelRect = new Rect(itemRect.x, itemRect.y, labelWidth, itemRect.height);
                    Widgets.Label(labelRect, $" <color=cyan>{entry}</color>");

                    // 純供應商條目沒有可調的模型級數值，只顯示名稱。
                    if (!hasModelName) continue;

                    string modelName = entry.Substring(colonIndex + 1);
                    DrawLevelButton(
                        new Rect(itemRect.x + itemRect.width - 115f, itemRect.y, 70f, itemRect.height),
                        modelName);
                    DrawContextWindowButton(
                        new Rect(itemRect.x + itemRect.width - 190f, itemRect.y, 70f, itemRect.height),
                        entry, entry.Substring(0, colonIndex), modelName);
                }

                if (entryToRemove != null)
                {
                    // 刪除會連同該模型調過的分級／上下文上限一起清掉，先確認避免誤觸
                    string removed = entryToRemove;
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "RimLLM_ConfirmRemoveModel".Translate(removed),
                        () =>
                        {
                            Settings.RemoveFallbackEntry(removed);
                            Settings.Write();
                        },
                        destructive: true));
                }
            }
            listing.GapLine(10f);

            DrawAddModelSection(listing);
        }

        /// <summary>
        /// 取得所有已註冊供應商識別碼（含第三方 Mod 註冊的外部供應商）。
        /// </summary>
        private static List<string> GetRegisteredProviderIds()
        {
            // SDK 尚未初始化時退回內建清單
            return RimLLMProvider.TryGetManager(out var manager)
                ? manager.GetRegisteredProviderIds()
                : new List<string>(ProviderIds.BuiltIn);
        }

        /// <summary>
        /// 判斷供應商在 UI 中是否可選（內建依設定啟用狀態；外部供應商註冊即啟用）。
        /// </summary>
        private static bool IsProviderSelectable(string providerId)
        {
            return RimLLMProvider.TryGetManager(out var manager)
                ? manager.IsProviderEnabled(providerId)
                : Settings.IsProviderEnabled(providerId);
        }

        private static void SetDefaultAddModelName(string providerId)
        {
            addProviderId = providerId;
            // GetDefaultModel 已是「有快取清單就取第一筆，否則用預設值」的語意。
            addModelName = Settings.GetDefaultModel(providerId, "default");
        }

        /// <summary>
        /// 新增模型至備援鏈：選供應商、選該供應商快取模型、加入。加入後本頁列表即出現該列。
        /// </summary>
        private static void EnsureActiveProvider()
        {
            if (!IsProviderSelectable(addProviderId))
            {
                string firstEnabled = GetRegisteredProviderIds().FirstOrDefault(IsProviderSelectable);
                if (firstEnabled != null)
                {
                    addProviderId = firstEnabled;
                    addModelName = ""; // 重設模型名稱以重新加載預設值
                }
            }

            if (string.IsNullOrEmpty(addModelName))
            {
                SetDefaultAddModelName(addProviderId);
            }
        }

        private static void ShowProviderSelectionMenu()
        {
            List<FloatMenuOption> options = GetRegisteredProviderIds()
                .Where(IsProviderSelectable)
                .Select(prov => new FloatMenuOption(prov, () => SetDefaultAddModelName(prov)))
                .ToList();

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("RimLLM_NoEnabledProviders".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void TryAddModelToChain()
        {
            string entry = $"{addProviderId}:{addModelName}";
            var chain = Settings.FallbackChain;
            if (chain.Contains(entry))
            {
                Messages.Message("RimLLM_MsgModelExists".Translate(), MessageTypeDefOf.RejectInput, false);
            }
            else
            {
                chain.Add(entry);
                Settings.FallbackChain = chain;
                Settings.Write();
                Messages.Message("RimLLM_MsgModelAdded".Translate(entry), MessageTypeDefOf.PositiveEvent, false);
            }
        }

        /// <summary>
        /// 新增模型至備援鏈：選供應商、選該供應商快取模型、加入。加入後本頁列表即出現該列。
        /// </summary>
        private static void DrawAddModelSection(Listing_Standard listing)
        {
            EnsureActiveProvider();

            listing.Label("RimLLM_AddToFallbackTitle".Translate());

            // 選擇供應商
            Rect addRect = listing.GetRect(30f);
            Rect addProvBtn = new Rect(addRect.x, addRect.y, 150f, addRect.height);
            Rect addModBtn = new Rect(addRect.x + 160f, addRect.y, 250f, addRect.height);
            Rect addSubmitBtn = new Rect(addRect.x + 420f, addRect.y, 100f, addRect.height);
            if (Widgets.ButtonText(addProvBtn, "RimLLM_SelectProviderBtn".Translate(addProviderId)))
            {
                ShowProviderSelectionMenu();
            }

            // 選擇該供應商底下的快取模型
            var models = Settings.GetModelList(addProviderId);
            string modelBtnLabel = string.IsNullOrEmpty(addModelName) ? "default" : addModelName;

            if (Widgets.ButtonText(addModBtn, "RimLLM_SelectModelBtn".Translate(modelBtnLabel)))
            {
                Find.WindowStack.Add(new Dialog_SelectModel(models, (selectedM) => addModelName = selectedM));
            }

            // 點擊新增
            if (Widgets.ButtonText(addSubmitBtn, "RimLLM_AddBtn".Translate()))
            {
                TryAddModelToChain();
            }
            listing.GapLine(10f);
        }

        /// <summary>分級標籤鍵，索引即為覆寫值（0＝自動）。</summary>
        private static readonly string[] LevelLabelKeys =
        {
            "RimLLM_FallbackLevelAuto",
            "RimLLM_FallbackLevelLow",
            "RimLLM_FallbackLevelMedium",
            "RimLLM_FallbackLevelHigh"
        };

        private static void DrawLevelButton(Rect rect, string modelName)
        {
            // 分級只有自動／低／中／高四檔，以陣列對照取代 switch，新增等級時只改一處。
            int currentLevel = Settings.GetModelLevelOverride(modelName);
            int index = currentLevel >= 1 && currentLevel <= 3 ? currentLevel : 0;
            string levelLabel = LevelLabelKeys[index].Translate();

            if (Widgets.ButtonText(rect, levelLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_FallbackLevelAuto".Translate(), () => { Settings.SetModelLevelOverride(modelName, 0); Settings.Write(); }),
                    new FloatMenuOption("RimLLM_FallbackLevelLow".Translate(), () => { Settings.SetModelLevelOverride(modelName, 1); Settings.Write(); }),
                    new FloatMenuOption("RimLLM_FallbackLevelMedium".Translate(), () => { Settings.SetModelLevelOverride(modelName, 2); Settings.Write(); }),
                    new FloatMenuOption("RimLLM_FallbackLevelHigh".Translate(), () => { Settings.SetModelLevelOverride(modelName, 3); Settings.Write(); })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>常用的上下文上限，讓玩家一鍵選取；其他數值走自訂視窗。</summary>
        private static readonly int[] ContextWindowPresets = { 131072, 200000, 262144, 1048576 };

        /// <summary>
        /// 繪製上下文上限按鈕：顯示目前生效的值（手動值優先於 API 回報值），點擊可手動設定或改回自動。
        /// </summary>
        private static void DrawContextWindowButton(Rect rect, string entry, string providerId, string modelName)
        {
            int manual = Settings.GetContextWindowOverride(entry);
            int? fetched = Settings.GetFetchedContextWindow(providerId, modelName);
            int? effective = manual > 0 ? manual : fetched;

            string label = effective.HasValue ? FormatTokens(effective.Value) : "?";
            if (manual > 0) label += "*";

            // 提示文字只在滑鼠停留時才需要，以延遲委派產生，避免每幀每列都翻譯與格式化
            TooltipHandler.TipRegion(rect, () =>
            {
                string source;
                if (manual > 0)
                {
                    source = "RimLLM_ContextWindowSourceManual".Translate();
                }
                else if (fetched.HasValue)
                {
                    source = "RimLLM_ContextWindowSourceApi".Translate();
                }
                else
                {
                    source = "RimLLM_ContextWindowSourceNone".Translate();
                }

                return "RimLLM_ContextWindowTooltip".Translate(
                    effective.HasValue ? effective.Value.ToString("N0") : "?", source);
            }, entry.GetHashCode());

            if (Widgets.ButtonText(rect, label))
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(
                        "RimLLM_ContextWindowAuto".Translate(fetched.HasValue ? fetched.Value.ToString("N0") : "?"),
                        () => { Settings.SetContextWindowOverride(entry, 0); Settings.Write(); })
                };
                foreach (int preset in ContextWindowPresets)
                {
                    int captured = preset;
                    options.Add(new FloatMenuOption(
                        FormatTokens(captured) + " (" + captured.ToString("N0") + ")",
                        () => { Settings.SetContextWindowOverride(entry, captured); Settings.Write(); }));
                }
                options.Add(new FloatMenuOption("RimLLM_ContextWindowCustom".Translate(), () =>
                    Find.WindowStack.Add(new Dialog_SetContextWindow(entry, manual, tokens =>
                    {
                        Settings.SetContextWindowOverride(entry, tokens);
                        Settings.Write();
                    }))));
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>以 K / M 縮寫 token 數，例如 131072 → 131K、1048576 → 1M。</summary>
        private static string FormatTokens(int tokens)
        {
            if (tokens >= 1000000)
            {
                return (tokens / 1000000.0).ToString("0.#") + "M";
            }
            if (tokens >= 1000)
            {
                return (tokens / 1000.0).ToString("0") + "K";
            }
            return tokens.ToString();
        }
    }
}
