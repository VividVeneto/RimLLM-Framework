using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責「Embedding 向量」設定分頁的 UI 渲染。
/// 採用與「API 供應商」完全一致的 3 欄式設計（中欄供應商選單 + 右欄各供應商詳細設定與模型管理）。
/// </summary>
public static class EmbeddingSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const string ProviderGoogle = "Google";
    private const string ProviderOpenAI = "OpenAI";
    private const string ProviderOllama = "LocalAPI_Ollama";
    private const string ProviderLocalOpenAI = "LocalAPI_OpenAI";
    private const string ColorTagOpen = "<color=";
    private const string ColorTagClose = "</color>";

    /// <summary>
    /// 中欄選單項目：後備顯示名稱與供應商代號。
    /// 實際顯示走 <c>RimLLM_EmbeddingProvider_{id}</c> 語系鍵，後備名稱只在語系未載入（單元測試）時使用。
    /// </summary>
    private static readonly List<KeyValuePair<string, string>> MenuEntries =
    [
        new KeyValuePair<string, string>("Google Gemini", ProviderGoogle),
        new KeyValuePair<string, string>("OpenAI", ProviderOpenAI),
        new KeyValuePair<string, string>("Ollama (本地)", ProviderOllama),
        new KeyValuePair<string, string>("OpenAI 相容 (本地/自訂)", ProviderLocalOpenAI)
    ];

    private const float SubButtonHeight = 46f;
    private const float SubButtonGap = 4f;

    /// <summary>目前選中的 Embedding 供應商子分頁。</summary>
    public static string ActiveEmbeddingSubTab { get; set; } = ProviderGoogle;

    private static Vector2 _midScrollPosition = Vector2.zero;

    // 狀態與快取字典（各供應商獨立，避免切換子分頁時互相干擾）
    private static readonly Dictionary<string, bool> Fetching = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FetchStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Testing = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TestStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Vector2> ModelScrollPositions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ModelFilters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> RevealedKeys = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, bool> Detecting = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> DetectStatus = new(StringComparer.Ordinal);

    /// <summary>
    /// 取得供應商的友善顯示名稱。
    /// </summary>
    public static string GetProviderDisplayName(string providerId)
    {
        string fallback = null;
        foreach (var entry in MenuEntries)
        {
            if (entry.Value == providerId)
            {
                fallback = entry.Key;
                break;
            }
        }
        if (fallback == null) return providerId ?? "";

        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return $"RimLLM_EmbeddingProvider_{providerId}".Translate();
            }
        }
        catch
        {
            // 單元測試或語系尚未載入時退回後備名稱
        }
        return fallback;
    }

    /// <summary>
    /// 各供應商預先定義的真實存在推薦模型清單。
    /// 當尚未從遠端 API 抓取模型清單時，作為預設選項展示，供玩家即點即用。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultPresetModels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        {
            ProviderGoogle,
            new List<string> { "gemini-embedding-2", "gemini-embedding-001", "gemini-embedding-2-preview" }
        },
        {
            ProviderOpenAI,
            new List<string> { "text-embedding-3-small", "text-embedding-3-large", "text-embedding-ada-002" }
        },
        {
            ProviderOllama,
            new List<string> { "nomic-embed-text", "bge-m3", "all-minilm", "mxbai-embed-large", "snowflake-arctic-embed" }
        },
        {
            ProviderLocalOpenAI,
            new List<string> { "text-embedding-3-small", "text-embedding-3-large", "bge-m3", "nomic-embed-text" }
        }
    };

    public static float GetHeight(float width)
    {
        string providerId = ActiveEmbeddingSubTab;
        int modelCount = Settings.GetModelCount(RimLLMEmbeddingService.GetModelListKey(providerId));
        if (modelCount == 0 && DefaultPresetModels.TryGetValue(providerId, out var presets))
        {
            modelCount = presets.Count;
        }
        float modelSectionHeight = modelCount > 0 ? 280f : 70f;
        float localDetectHeight = (providerId == ProviderOllama || providerId == ProviderLocalOpenAI) ? 40f : 0f;
        return 460f + modelSectionHeight + localDetectHeight;
    }

    /// <summary>
    /// 繪製中欄的 Embedding 供應商選單。
    /// </summary>
    public static void DrawMiddleEmbeddingMenu(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(6f);

        Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 20f);
        Text.Font = GameFont.Small;
        Widgets.Label(titleRect, "RimLLM_EmbeddingProvidersTitle".Translate());

        Rect listRect = new Rect(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
        float viewHeight = MenuEntries.Count * (SubButtonHeight + SubButtonGap) + 10f;
        Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, viewHeight);

        Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
        Listing_Standard listing = new Listing_Standard();
        listing.Begin(viewRect);

        foreach (var providerId in MenuEntries.Select(entry => entry.Value))
        {
            DrawEmbeddingSubButton(listing, GetProviderDisplayName(providerId), providerId);
            listing.Gap(SubButtonGap);
        }
        listing.End();

        Widgets.EndScrollView();
    }

    private static void DrawEmbeddingSubButton(Listing_Standard listing, string label, string providerId)
    {
        Rect btnRect = listing.GetRect(SubButtonHeight);
        RimLLMUIStyle.DrawSelectableFrame(btnRect, ActiveEmbeddingSubTab == providerId);

        if (Widgets.ButtonInvisible(btnRect))
        {
            ActiveEmbeddingSubTab = providerId;
        }

        Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
        Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
        Text.Font = GameFont.Small;
        string nameText = ActiveEmbeddingSubTab == providerId ? $"{ColorTagOpen}white><b>{label}</b>{ColorTagClose}" : $"{ColorTagOpen}silver>{label}{ColorTagClose}";
        Widgets.Label(nameRect, nameText);

        bool isActive = Settings.EmbeddingProvider == providerId;
        string statusText;
        Color statusColor;

        if (isActive)
        {
            if (ProviderNeedsApiKey(providerId) && !HasEffectiveApiKey(providerId))
            {
                statusText = "RimLLM_StatusNoApiKey".Translate();
                statusColor = RimLLMUIStyle.Warning;
            }
            else
            {
                string model = Settings.GetEmbeddingModel(providerId);
                string modelDisplay = string.IsNullOrEmpty(model) ? "Default" : model.Truncate(12);
                statusText = "RimLLM_EmbeddingStatusActiveDetail".Translate(modelDisplay);
                statusColor = RimLLMUIStyle.Success;
            }
        }
        else
        {
            statusText = "RimLLM_EmbeddingStatusInactive".Translate();
            statusColor = RimLLMUIStyle.Muted;
        }

        Color oldColor = GUI.color;
        GUI.color = statusColor;
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Widgets.Label(statusRect, statusText);
        }
        GUI.color = oldColor;
    }

    private static bool ProviderNeedsApiKey(string providerId)
    {
        return providerId == "Google" || providerId == "OpenAI";
    }

    private static bool HasEffectiveApiKey(string providerId)
    {
        string customKey = Settings.GetEmbeddingApiKey(providerId);
        if (!string.IsNullOrEmpty(customKey)) return true;

        string mainProvider = RimLLMEmbeddingService.GetMainProviderIdForEmbedding(providerId);
        return !string.IsNullOrEmpty(Settings.GetApiKey(mainProvider));
    }

    /// <summary>
    /// 繪製右欄的 Embedding 供應商詳細設定內容。
    /// </summary>
    public static void DrawRightDetailContent(Listing_Standard listing)
    {
        string providerId = ActiveEmbeddingSubTab;

        // 1. 啟用核取方塊
        bool isCurrentlyActive = Settings.EmbeddingProvider == providerId;
        bool oldActive = isCurrentlyActive;
        listing.CheckboxLabeled("RimLLM_EnableEmbeddingProvider".Translate(), ref isCurrentlyActive);
        if (isCurrentlyActive != oldActive)
        {
            Settings.EmbeddingProvider = isCurrentlyActive ? providerId : "Disabled";
            Settings.Write();
        }

        if (isCurrentlyActive)
        {
            listing.Label("RimLLM_EmbeddingActiveBadge".Translate());
        }
        listing.Gap(4f);

        // 2. 簡介說明
        Rect expRect = listing.GetRect(32f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldC = GUI.color;
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(expRect, "RimLLM_EmbeddingExplanation".Translate());
            GUI.color = oldC;
        }
        listing.Gap(6f);

        // 3. API 金鑰設定
        DrawApiKeySection(listing, providerId);

        // 4. 端點設定
        DrawEndpointSection(listing, providerId);

        // 5. 模型名稱與可用模型清單
        DrawModelSection(listing, providerId);

        listing.Gap(8f);

        // 6. 連線與向量生成測試
        DrawConnectionTest(listing, providerId);

        listing.GapLine(4f);
    }

    /// <summary>
    /// 繪製 API 金鑰欄位與繼承提示。
    /// </summary>
    private static void DrawApiKeySection(Listing_Standard listing, string providerId)
    {
        if (ProviderNeedsApiKey(providerId))
        {
            listing.Label("RimLLM_EmbeddingKeyHintDefault".Translate());
        }
        else
        {
            listing.Label("RimLLM_EmbeddingKeyHintLocal".Translate());
        }

        string currentKey = Settings.GetEmbeddingApiKey(providerId);
        Rect rowRect = listing.GetRect(30f);
        Rect inputRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 48f, rowRect.height);
        Rect revealRect = new Rect(inputRect.xMax + 8f, rowRect.y, 40f, rowRect.height);

        bool revealed = RevealedKeys.TryGetValue(providerId, out bool r) && r;
        string newKey = RimLLMUIStyle.DrawMaskableKeyField(inputRect, revealRect, currentKey, ref revealed);
        RevealedKeys[providerId] = revealed;

        if (newKey != currentKey)
        {
            Settings.SetEmbeddingApiKey(providerId, newKey);
            Settings.Write();
        }

        if (string.IsNullOrEmpty(newKey) && ProviderNeedsApiKey(providerId))
        {
            string mainProvider = RimLLMEmbeddingService.GetMainProviderIdForEmbedding(providerId);
            string mainKey = Settings.GetApiKey(mainProvider);
            bool isCurrentlyActive = Settings.EmbeddingProvider == providerId;

            string hint;
            if (!string.IsNullOrEmpty(mainKey))
            {
                hint = "RimLLM_EmbeddingInheritedKeyHint".Translate();
            }
            else if (isCurrentlyActive)
            {
                hint = "RimLLM_EmbeddingNoKeyWarning".Translate();
            }
            else
            {
                hint = "RimLLM_EmbeddingInheritKeyNotice".Translate();
            }

            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                listing.Label(hint);
            }
        }
        listing.Gap(8f);
    }

    /// <summary>
    /// 繪製端點輸入框（本地服務提供自動探測按鈕）。
    /// </summary>
    private static void DrawEndpointSection(Listing_Standard listing, string providerId)
    {
        bool isLocal = providerId == ProviderOllama || providerId == ProviderLocalOpenAI;
        string defaultEndpoint = RimLLMFrameworkSettings.GetDefaultEmbeddingEndpoint(providerId);

        listing.Label("RimLLM_EmbeddingEndpointLabel".Translate() + $" {ColorTagOpen}grey>({defaultEndpoint}){ColorTagClose}");

        // 欄位顯示原始輸入值；空白代表使用預設端點，不可回填預設值，否則玩家無法清空重打。
        string currentEndpoint = Settings.GetEmbeddingEndpointRaw(providerId);
        string newEndpoint = listing.TextEntry(currentEndpoint);
        if (newEndpoint != currentEndpoint)
        {
            Settings.SetEmbeddingEndpoint(providerId, newEndpoint?.Trim());
            Settings.Write();
        }

        if (isLocal)
        {
            DrawLocalDetectionControls(listing, providerId);
        }
        listing.Gap(8f);
    }

    private static void DrawLocalDetectionControls(Listing_Standard listing, string providerId)
    {
        Rect detectRect = listing.GetRect(30f);
        Rect detectBtnRect = new Rect(detectRect.x, detectRect.y, 200f, detectRect.height);
        Rect detectStatusRect = new Rect(detectRect.x + 210f, detectRect.y, detectRect.width - 210f, detectRect.height);

        if (Detecting.TryGetValue(providerId, out bool isD) && isD)
        {
            GUI.color = Color.gray;
            Widgets.ButtonText(detectBtnRect, "RimLLM_DetectingLocal".Translate());
            GUI.color = Color.white;
        }
        else
        {
            if (Widgets.ButtonText(detectBtnRect, "RimLLM_DetectLocalBtn".Translate()))
            {
                StartDetectLocalEndpoint(providerId);
            }
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(detectStatusRect, DetectStatus.TryGetValue(providerId, out string ds) ? ds : "");
        }
    }

    /// <summary>
    /// 依供應商決定探測順序：Ollama 分頁只找 Ollama；OpenAI 相容分頁先找 LM Studio 等通用伺服器，
    /// 最後才退回 Ollama 的 /v1。否則 Ollama 分頁會被機器上恰好在跑的 LM Studio 搶走端點。
    /// </summary>
    private static LocalEndpointProbe.Target[] GetDetectTargets(string providerId)
    {
        var ollama = new LocalEndpointProbe.Target { Name = "Ollama", BaseUrl = "http://localhost:11434/v1", TestUrl = "http://localhost:11434/v1/models" };
        var ollamaRaw = new LocalEndpointProbe.Target { Name = "Ollama (Raw)", BaseUrl = "http://localhost:11434/v1", TestUrl = "http://localhost:11434/api/tags" };
        if (providerId == ProviderOllama)
        {
            return [ollama, ollamaRaw];
        }
        return
        [
            new LocalEndpointProbe.Target { Name = "LM Studio", BaseUrl = "http://localhost:1234/v1", TestUrl = "http://localhost:1234/v1/models" },
            new LocalEndpointProbe.Target { Name = "LocalAI/vLLM (8080)", BaseUrl = "http://localhost:8080/v1", TestUrl = "http://localhost:8080/v1/models" },
            new LocalEndpointProbe.Target { Name = "LocalAI/vLLM (8000)", BaseUrl = "http://localhost:8000/v1", TestUrl = "http://localhost:8000/v1/models" },
            ollama,
            ollamaRaw
        ];
    }

    private static void StartDetectLocalEndpoint(string providerId)
    {
        Detecting[providerId] = true;
        DetectStatus[providerId] = "RimLLM_DetectingLocal".Translate();

        var targets = GetDetectTargets(providerId);

        Task.Run(async () =>
        {
            LocalEndpointProbe.Target hit = await LocalEndpointProbe.FindReachableAsync(targets).ConfigureAwait(false);
            if (hit != null)
            {
                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    Settings.SetEmbeddingEndpoint(providerId, hit.BaseUrl);
                    Settings.Write();
                    Detecting[providerId] = false;
                    DetectStatus[providerId] = "RimLLM_DetectSuccess".Translate(hit.Name, hit.BaseUrl);
                    Messages.Message("RimLLM_MsgDetectSuccess".Translate(hit.Name), MessageTypeDefOf.PositiveEvent, false);
                });
                return;
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Detecting[providerId] = false;
                DetectStatus[providerId] = "RimLLM_DetectFailed".Translate();
                Messages.Message("RimLLM_MsgDetectFailed".Translate(), MessageTypeDefOf.RejectInput, false);
            });
        });
    }

    /// <summary>
    /// 繪製模型名稱輸入框與可用模型清單晶片網格。
    /// </summary>
    private static void DrawModelSection(Listing_Standard listing, string providerId)
    {
        DrawModelInputField(listing, providerId);

        listing.Label("RimLLM_AvailableModelsTitle".Translate());
        string modelListKey = RimLLMEmbeddingService.GetModelListKey(providerId);
        int cachedCount = Settings.GetModelCount(modelListKey);
        bool isPreset = cachedCount == 0 && DefaultPresetModels.ContainsKey(providerId);
        bool hasModels = cachedCount > 0 || isPreset;

        if (!hasModels)
        {
            listing.Label("RimLLM_EmbeddingNoModelList".Translate());
        }
        else
        {
            DrawModelFilterBar(listing, providerId, isPreset);

            // 過濾結果與清單複製都走快取：只有模型清單版本或過濾字串變了才重算。
            List<string> visibleModels = RimLLMUIStyle.FilterModelsCached(
                "embedding:" + providerId,
                Settings.ModelListVersion,
                ModelFilters[providerId],
                () => ResolveAvailableModels(providerId));
            Rect scrollRect = listing.GetRect(200f);
            Widgets.DrawMenuSection(scrollRect);

            if (visibleModels.Count == 0)
            {
                using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
                {
                    Widgets.Label(scrollRect, $"{ColorTagOpen}grey>" + "RimLLM_NoMatchingModels".Translate() + ColorTagClose);
                }
            }
            else
            {
                DrawModelGrid(scrollRect, providerId, visibleModels);
            }
        }

        listing.Gap(6f);

        // 抓取模型按鈕列
        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Fetching.TryGetValue(providerId, out bool isF) && isF,
            "RimLLM_Fetching".Translate(),
            "RimLLM_FetchModelsBtn".Translate(),
            FetchStatus.TryGetValue(providerId, out string fs) ? fs : "RimLLM_FetchStatusNotRun".Translate().ToString(),
            () => StartFetchEmbeddingModels(providerId),
            buttonWidth: 200f);
    }

    private static void DrawModelInputField(Listing_Standard listing, string providerId)
    {
        listing.Label("RimLLM_EmbeddingModelLabel".Translate() + $" {ColorTagOpen}grey>({RimLLMFrameworkSettings.GetDefaultEmbeddingModel(providerId)}){ColorTagClose}");
        string currentModel = Settings.GetEmbeddingModelRaw(providerId);
        string newModel = listing.TextEntry(currentModel);
        if (newModel != currentModel)
        {
            Settings.SetEmbeddingModel(providerId, newModel);
            Settings.Write();
        }
        listing.Gap(4f);
    }

    /// <summary>抓取過的模型清單優先，沒有才退回內建預設清單。只在過濾快取失效時被呼叫。</summary>
    private static IList<string> ResolveAvailableModels(string providerId)
    {
        string cacheKey = RimLLMEmbeddingService.GetModelListKey(providerId);
        List<string> cachedModels = Settings.GetModelList(cacheKey);
        if (cachedModels.Count == 0 && DefaultPresetModels.TryGetValue(providerId, out var presets))
        {
            return new List<string>(presets);
        }
        return cachedModels;
    }

    private static void DrawModelFilterBar(Listing_Standard listing, string providerId, bool isPreset)
    {
        Rect hintRect = listing.GetRect(18f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldC = GUI.color;
            GUI.color = isPreset ? RimLLMUIStyle.Muted : RimLLMUIStyle.Success;
            Widgets.Label(hintRect, isPreset ? "RimLLM_EmbeddingPresetModelsHint".Translate() : "RimLLM_EmbeddingFetchedModelsHint".Translate());
            GUI.color = oldC;
        }
        listing.Gap(2f);

        string filter = ModelFilters.TryGetValue(providerId, out string f) ? f : "";
        Rect searchRowRect = listing.GetRect(28f);
        float searchLabelWidth = Text.CalcSize("RimLLM_Search".Translate() + ": ").x;
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(
                new Rect(searchRowRect.x, searchRowRect.y, searchLabelWidth, searchRowRect.height),
                "RimLLM_Search".Translate() + ": ");
        }
        Rect searchFieldRect = new Rect(
            searchRowRect.x + searchLabelWidth + 4f,
            searchRowRect.y,
            searchRowRect.width - searchLabelWidth - 4f,
            searchRowRect.height);
        ModelFilters[providerId] = Widgets.TextField(searchFieldRect, filter);
        listing.Gap(4f);
    }

    private static void DrawModelGrid(Rect scrollRect, string providerId, List<string> visibleModels)
    {
        float contentWidth = scrollRect.width - 16f;
        float chipHeight = 28f;
        float gap = 8f;
        RimLLMUIStyle.ComputeChipLayout(contentWidth, gap, 220f, out int cols, out float chipWidth);

        int rows = Mathf.CeilToInt((float)visibleModels.Count / cols);
        float viewHeight = Mathf.Max(200f, rows * (chipHeight + gap) + gap);
        Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

        if (!ModelScrollPositions.ContainsKey(providerId))
        {
            ModelScrollPositions[providerId] = Vector2.zero;
        }
        Vector2 scrollPos = ModelScrollPositions[providerId];

        Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
        ModelScrollPositions[providerId] = scrollPos;

        // 只畫可視範圍內的列；tooltip 後綴的翻譯提到迴圈外，不必每個晶片各查一次。
        RimLLMUIStyle.GetVisibleRowRange(scrollPos.y, scrollRect.height, chipHeight + gap, gap, rows, out int firstRow, out int endRow);
        string copyHint = "\n\n" + "RimLLM_ClickToCopy".Translate();

        for (int i = firstRow * cols; i < visibleModels.Count && i < endRow * cols; i++)
        {
            string model = visibleModels[i];
            int col = i % cols;
            int row = i / cols;

            Rect chipRect = new Rect(
                col * (chipWidth + gap) + gap,
                row * (chipHeight + gap) + gap,
                chipWidth,
                chipHeight
            );

            Widgets.DrawBoxSolid(chipRect, RimLLMUIStyle.ChipFill);
            Widgets.DrawBox(chipRect, 1);

            if (Mouse.IsOver(chipRect))
            {
                Widgets.DrawHighlight(chipRect);
                TooltipHandler.TipRegion(chipRect, model + copyHint);
            }

            if (Widgets.ButtonInvisible(chipRect))
            {
                ShowModelMenu(providerId, model);
            }

            Rect textRect = chipRect.ContractedBy(4f);
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
            {
                Widgets.Label(textRect, ColorTagOpen + "silver>" + RimLLMUIStyle.TruncateCached(model, textRect.width) + ColorTagClose);
            }
        }

        Widgets.EndScrollView();
    }

    private static void ShowModelMenu(string providerId, string model)
    {
        string capturedModel = model;
        var options = new List<FloatMenuOption>
        {
            new("RimLLM_EmbeddingSetAsModel".Translate(), () =>
            {
                Settings.SetEmbeddingModel(providerId, capturedModel);
                Settings.Write();
                Messages.Message("RimLLM_EmbeddingModelLabel".Translate() + capturedModel, MessageTypeDefOf.TaskCompletion, false);
            }),
            new("RimLLM_ClickToCopyMenu".Translate(), () =>
            {
                GUIUtility.systemCopyBuffer = capturedModel;
                Messages.Message("RimLLM_CopiedToClipboard".Translate(capturedModel), MessageTypeDefOf.TaskCompletion, false);
            })
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static void StartFetchEmbeddingModels(string providerId)
    {
        Fetching[providerId] = true;
        FetchStatus[providerId] = "RimLLM_Fetching".Translate();

        string endpoint = Settings.GetEmbeddingEndpoint(providerId);
        string apiKey = Settings.GetEmbeddingApiKey(providerId);

        Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                RimLLMEmbeddingModelList result = await new RimLLMEmbeddingService(Settings)
                    .FetchAvailableModelsAsync(providerId, endpoint, apiKey)
                    .ConfigureAwait(false);

                applyResult = () =>
                {
                    List<string> models = result.Models;
                    if (models.Count == 0)
                    {
                        return result.Filtered
                            ? "RimLLM_EmbeddingFetchSuccessEmpty".Translate()
                            : "RimLLM_FetchSuccessEmpty".Translate();
                    }
                    Settings.SetModelList(RimLLMEmbeddingService.GetModelListKey(providerId), models);
                    Settings.Write();
                    // 沒過濾的清單可能混有對話模型，狀態列要講清楚，玩家才知道不能照單全收。
                    return result.Filtered
                        ? "RimLLM_EmbeddingFetchSuccessCount".Translate(models.Count)
                        : "RimLLM_EmbeddingFetchSuccessUnfiltered".Translate(models.Count);
                };
            }
            catch (Exception ex)
            {
                string message = "RimLLM_FetchFailed".Translate() +
                    " (" + RimLLMLog.SanitizeForLog(ex.Message, 220) + ")";
                applyResult = () => message;
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Fetching[providerId] = false;
                FetchStatus[providerId] = applyResult();
            });
        });
    }

    /// <summary>
    /// 繪製連線與向量生成測試區塊。
    /// </summary>
    private static void DrawConnectionTest(Listing_Standard listing, string providerId)
    {
        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Testing.TryGetValue(providerId, out bool isT) && isT,
            "RimLLM_EmbeddingTesting".Translate(),
            "RimLLM_EmbeddingTestBtn".Translate(),
            TestStatus.TryGetValue(providerId, out string ts) ? ts : "RimLLM_TestStatusNotRun".Translate().ToString(),
            () => StartTestEmbedding(providerId),
            buttonWidth: 200f);
    }

    private static void StartTestEmbedding(string providerId)
    {
        Testing[providerId] = true;
        TestStatus[providerId] = "RimLLM_EmbeddingTesting".Translate();

        string model = Settings.GetEmbeddingModel(providerId);
        string endpoint = Settings.GetEmbeddingEndpoint(providerId);
        string apiKey = Settings.GetEmbeddingApiKey(providerId);
        var sw = Stopwatch.StartNew();

        Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                var service = new RimLLMEmbeddingService(Settings);
                var result = await service.TestEmbeddingAsync(providerId, model, endpoint, apiKey).ConfigureAwait(false);
                sw.Stop();
                int dims = result.Vector != null ? result.Vector.Length : 0;
                long elapsed = sw.ElapsedMilliseconds;
                applyResult = () => "RimLLM_EmbeddingTestSuccess".Translate(dims, elapsed);
            }
            catch (Exception ex)
            {
                sw.Stop();
                string msg = "RimLLM_EmbeddingTestFailed".Translate(RimLLMLog.SanitizeForLog(ex.Message, 200));
                applyResult = () => msg;
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Testing[providerId] = false;
                TestStatus[providerId] = applyResult();
            });
        });
    }

    /// <summary>
    /// 向下相容既有呼叫。
    /// </summary>
    public static void DrawEmbeddingSettings(Listing_Standard listing)
    {
        DrawRightDetailContent(listing);
    }
}
