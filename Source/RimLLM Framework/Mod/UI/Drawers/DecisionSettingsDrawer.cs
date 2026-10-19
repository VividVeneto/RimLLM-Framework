using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// Decision 模組設定頁。版面與 Embedding 模組一致：
/// 中欄是供應商清單，右欄是啟用開關、金鑰、端點、模型與連線測試。
/// </summary>
public static class DecisionSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const float SubButtonHeight = 46f;
    private const float SubButtonGap = 4f;
    private const string ColorTagOpen = "<color=";
    private const string ColorTagClose = "</color>";

    /// <summary>目前在中欄選取的供應商；與實際啟用的 DecisionProvider 分開，方便先編輯再啟用。</summary>
    public static string ActiveDecisionSubTab { get; set; } = RimLLMDecisionProviderCatalog.TypeSafe;

    private static Vector2 _midScrollPosition = Vector2.zero;

    private static readonly Dictionary<string, bool> Fetching = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FetchStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Testing = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TestStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Vector2> ModelScrollPositions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ModelFilters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> RevealedKeys = new(StringComparer.Ordinal);

    /// <summary>本機端點探測進行中；同一時間只跑一個，避免重複的逾時等待。</summary>
    private static bool _isDetectingLocal;

    /// <summary>本機端點探測的結果訊息。</summary>
    private static string _detectStatusMessage = "";

    /// <summary>
    /// 每一個本機決策服務的探測候選。
    /// 決策端點要走 System One 的 /v1/models；Ollama 另外也提供 /api/tags，
    /// 兩者都試一次，服務活著就抓得到。
    /// </summary>
    private static readonly Dictionary<string, LocalEndpointProbe.Target[]> LocalProbeTargets =
        new(StringComparer.Ordinal)
        {
            [RimLLMDecisionProviderCatalog.Ollama] =
            [
                new LocalEndpointProbe.Target
                {
                    Name = "Ollama",
                    BaseUrl = "http://localhost:11434",
                    TestUrl = "http://localhost:11434/v1/models"
                },
                new LocalEndpointProbe.Target
                {
                    Name = "Ollama",
                    BaseUrl = "http://localhost:11434",
                    TestUrl = "http://localhost:11434/api/tags"
                }
            ],
            [RimLLMDecisionProviderCatalog.Unsloth] =
            [
                new LocalEndpointProbe.Target
                {
                    Name = "Unsloth",
                    BaseUrl = "http://localhost:8888",
                    TestUrl = "http://localhost:8888/v1/models"
                }
            ],
            [RimLLMDecisionProviderCatalog.Local] =
            [
                new LocalEndpointProbe.Target
                {
                    Name = "Local",
                    BaseUrl = "http://localhost:8000",
                    TestUrl = "http://localhost:8000/v1/models"
                },
                new LocalEndpointProbe.Target
                {
                    Name = "Ollama",
                    BaseUrl = "http://localhost:11434",
                    TestUrl = "http://localhost:11434/v1/models"
                },
                new LocalEndpointProbe.Target
                {
                    Name = "Unsloth",
                    BaseUrl = "http://localhost:8888",
                    TestUrl = "http://localhost:8888/v1/models"
                }
            ]
        };

    /// <summary>該供應商目前的端點是否走明文 HTTP（玩家自架的本機服務）。</summary>
    private static bool IsPlaintextEndpoint(string providerId)
    {
        return DecisionEndpointRouting.IsPlaintext(Settings.GetDecisionEndpoint(providerId));
    }

    public static string GetProviderDisplayName(string providerId)
    {
        RimLLMDecisionProvider provider = RimLLMDecisionProviderCatalog.Get(providerId);
        string fallback = provider.DefaultModel;
        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return $"RimLLM_DecisionProvider_{provider.Id}".Translate();
            }
        }
        catch
        {
            // 單元測試或語言資料尚未載入時，退回可讀的預設值。
        }
        return fallback;
    }

    public static float GetHeight(float width)
    {
        string providerId = ActiveDecisionSubTab;
        int modelCount = Settings.GetModelCount(RimLLMDecisionService.GetModelListKey(providerId));
        if (modelCount == 0 && RimLLMDecisionProviderCatalog.TryGet(providerId, out RimLLMDecisionProvider provider))
        {
            modelCount = provider.PresetModels.Count;
        }
        float modelSectionHeight = modelCount > 0 ? 280f : 70f;
        return 460f + modelSectionHeight;
    }

    public static void DrawMiddleDecisionMenu(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(6f);

        Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 20f);
        Text.Font = GameFont.Small;
        Widgets.Label(titleRect, "RimLLM_DecisionProvidersTitle".Translate());

        Rect listRect = new Rect(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
        float viewHeight = (RimLLMDecisionProviderCatalog.All.Count * (SubButtonHeight + SubButtonGap)) + 10f;
        Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, viewHeight);

        Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
        Listing_Standard listing = new Listing_Standard();
        listing.Begin(viewRect);

        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            DrawDecisionSubButton(listing, GetProviderDisplayName(provider.Id), provider.Id);
            listing.Gap(SubButtonGap);
        }

        listing.End();
        Widgets.EndScrollView();
    }

    private static void DrawDecisionSubButton(Listing_Standard listing, string label, string providerId)
    {
        Rect btnRect = listing.GetRect(SubButtonHeight);
        RimLLMUIStyle.DrawSelectableFrame(btnRect, ActiveDecisionSubTab == providerId);

        if (Widgets.ButtonInvisible(btnRect))
        {
            ActiveDecisionSubTab = providerId;
        }

        Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
        Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
        Text.Font = GameFont.Small;
        string nameText = ActiveDecisionSubTab == providerId
            ? $"{ColorTagOpen}white><b>{label}</b>{ColorTagClose}"
            : $"{ColorTagOpen}silver>{label}{ColorTagClose}";
        Widgets.Label(nameRect, nameText);

        bool isActive = string.Equals(Settings.DecisionProvider, providerId, StringComparison.Ordinal);
        string statusText;
        Color statusColor;

        if (isActive)
        {
            if (string.IsNullOrEmpty(Settings.GetDecisionApiKey(providerId)) && !IsPlaintextEndpoint(providerId))
            {
                statusText = "RimLLM_StatusNoApiKey".Translate();
                statusColor = RimLLMUIStyle.Warning;
            }
            else
            {
                string model = Settings.GetDecisionModel(providerId);
                string modelDisplay = string.IsNullOrEmpty(model) ? "Default" : model.Truncate(14);
                statusText = "RimLLM_DecisionStatusActiveDetail".Translate(modelDisplay);
                statusColor = RimLLMUIStyle.Success;
            }
        }
        else
        {
            statusText = "RimLLM_DecisionStatusInactive".Translate();
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

    public static void DrawRightDetailContent(Listing_Standard listing)
    {
        RimLLMDecisionProvider provider = RimLLMDecisionProviderCatalog.Get(ActiveDecisionSubTab);
        string providerId = provider.Id;

        // 不在這裡把選取項目拉回「目前啟用」的供應商：玩家需要能先選另一個供應商、
        // 填好金鑰與模型，再勾選啟用。啟用狀態只由下面的核取方塊改變。
        bool isCurrentlyActive = string.Equals(Settings.DecisionProvider, providerId, StringComparison.Ordinal);
        bool oldActive = isCurrentlyActive;
        listing.CheckboxLabeled("RimLLM_EnableDecisionProvider".Translate(), ref isCurrentlyActive);
        if (isCurrentlyActive != oldActive)
        {
            Settings.DecisionProvider = isCurrentlyActive
                ? providerId
                : RimLLMDecisionProviderCatalog.Disabled;
            Settings.Write();
        }

        if (isCurrentlyActive)
        {
            listing.Label("RimLLM_DecisionActiveBadge".Translate());
        }
        listing.Gap(4f);

        Rect expRect = listing.GetRect(44f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldC = GUI.color;
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(expRect, "RimLLM_DecisionExplanation".Translate());
            GUI.color = oldC;
        }
        listing.Gap(6f);

        DrawApiKeySection(listing, providerId);
        DrawEndpointSection(listing, providerId, provider);
        DrawModelSection(listing, providerId, provider);

        listing.Gap(8f);
        DrawConnectionTest(listing, providerId);
        listing.GapLine(4f);
    }

    private static void DrawApiKeySection(Listing_Standard listing, string providerId)
    {
        bool isPlaintext = IsPlaintextEndpoint(providerId);
        listing.Label(isPlaintext
            ? "RimLLM_DecisionLocalKeyHint".Translate()
            : "RimLLM_DecisionKeyHint".Translate());

        string currentKey = Settings.GetDecisionApiKey(providerId);
        Rect rowRect = listing.GetRect(30f);
        Rect inputRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 48f, rowRect.height);
        Rect revealRect = new Rect(inputRect.xMax + 8f, rowRect.y, 40f, rowRect.height);

        bool revealed = RevealedKeys.TryGetValue(providerId, out bool r) && r;
        string newKey = RimLLMUIStyle.DrawMaskableKeyField(inputRect, revealRect, currentKey, ref revealed);
        RevealedKeys[providerId] = revealed;

        if (newKey != currentKey)
        {
            Settings.SetDecisionApiKey(providerId, newKey);
            Settings.Write();
        }

        // 本機端點通常不驗證金鑰，留空是正常狀態，不必提醒玩家補上。
        if (string.IsNullOrEmpty(newKey) && !isPlaintext)
        {
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                listing.Label("RimLLM_DecisionNoKeyWarning".Translate());
            }
        }
        listing.Gap(8f);
    }

    private static void DrawEndpointSection(
        Listing_Standard listing,
        string providerId,
        RimLLMDecisionProvider provider)
    {
        listing.Label(
            "RimLLM_DecisionEndpointLabel".Translate() +
            $" {ColorTagOpen}grey>({provider.DefaultEndpoint}){ColorTagClose}");

        // 欄位顯示原始輸入；空白代表使用預設端點，玩家可以清空後回到預設值。
        string currentEndpoint = Settings.GetDecisionEndpointRaw(providerId);
        string newEndpoint = listing.TextEntry(currentEndpoint);
        if (newEndpoint != currentEndpoint)
        {
            Settings.SetDecisionEndpoint(providerId, newEndpoint?.Trim());
            Settings.Write();
        }
        listing.Gap(4f);

        if (IsPlaintextEndpoint(providerId))
        {
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                listing.Label("RimLLM_DecisionLocalEndpointHint".Translate());
            }
            listing.Gap(4f);
        }

        if (LocalProbeTargets.ContainsKey(providerId))
        {
            DrawLocalDetectionControls(listing, providerId);
        }

        listing.Gap(8f);
    }

    /// <summary>本機決策服務的自動偵測（Ollama／Unsloth 等）：填入端點並回報服務是否在線。</summary>
    private static void DrawLocalDetectionControls(Listing_Standard listing, string providerId)
    {
        Rect detectRect = listing.GetRect(30f);
        Rect detectBtnRect = new Rect(detectRect.x, detectRect.y, 250f, detectRect.height);
        Rect detectStatusRect = new Rect(detectRect.x + 260f, detectRect.y, detectRect.width - 260f, detectRect.height);

        if (_isDetectingLocal)
        {
            GUI.color = Color.gray;
            Widgets.ButtonText(detectBtnRect, "RimLLM_DetectingLocal".Translate());
            GUI.color = Color.white;
        }
        else if (Widgets.ButtonText(detectBtnRect, "RimLLM_DecisionDetectLocalBtn".Translate()))
        {
            StartDetectLocalEndpoint(providerId);
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(detectStatusRect, _detectStatusMessage);
        }

        listing.Gap(4f);
    }

    private static void StartDetectLocalEndpoint(string providerId)
    {
        if (_isDetectingLocal || !LocalProbeTargets.TryGetValue(providerId, out LocalEndpointProbe.Target[] targets))
        {
            return;
        }

        _isDetectingLocal = true;
        _detectStatusMessage = "RimLLM_DetectingLocal".Translate();

        Task.Run(async () =>
        {
            // 決策服務常要求金鑰，探測不帶金鑰，因此 401 也算服務活著。
            LocalEndpointProbe.Target hit = await LocalEndpointProbe
                .FindReachableAsync(targets, acceptUnauthorized: true)
                .ConfigureAwait(false);

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                _isDetectingLocal = false;
                if (hit == null)
                {
                    _detectStatusMessage = "RimLLM_DetectFailed".Translate();
                    Messages.Message("RimLLM_MsgDetectFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }

                Settings.SetDecisionEndpoint(providerId, hit.BaseUrl);
                Settings.Write();
                _detectStatusMessage = "RimLLM_DetectSuccess".Translate(hit.Name, hit.BaseUrl);
                Messages.Message(
                    "RimLLM_MsgDetectSuccess".Translate(hit.Name),
                    MessageTypeDefOf.PositiveEvent,
                    false);
            });
        });
    }

    private static void DrawModelSection(
        Listing_Standard listing,
        string providerId,
        RimLLMDecisionProvider provider)
    {
        listing.Label(
            "RimLLM_DecisionModelLabel".Translate() +
            $" {ColorTagOpen}grey>({provider.DefaultModel}){ColorTagClose}");

        string currentModel = Settings.GetDecisionModelRaw(providerId);
        string newModel = listing.TextEntry(currentModel);
        if (newModel != currentModel)
        {
            Settings.SetDecisionModel(providerId, newModel);
            Settings.Write();
        }
        listing.Gap(4f);

        listing.Label("RimLLM_AvailableModelsTitle".Translate());
        string modelListKey = RimLLMDecisionService.GetModelListKey(providerId);
        int cachedCount = Settings.GetModelCount(modelListKey);
        bool isPreset = cachedCount == 0 && provider.PresetModels.Count > 0;
        bool hasModels = cachedCount > 0 || isPreset;

        if (!hasModels)
        {
            listing.Label("RimLLM_DecisionNoModelList".Translate());
        }
        else
        {
            DrawModelFilterBar(listing, providerId, isPreset);

            List<string> visibleModels = RimLLMUIStyle.FilterModelsCached(
                "decision:" + providerId,
                Settings.ModelListVersion,
                ModelFilters.TryGetValue(providerId, out string filter) ? filter : "",
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
        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Fetching.TryGetValue(providerId, out bool fetching) && fetching,
            "RimLLM_Fetching".Translate(),
            "RimLLM_FetchModelsBtn".Translate(),
            FetchStatus.TryGetValue(providerId, out string fetchStatus)
                ? fetchStatus
                : "RimLLM_FetchStatusNotRun".Translate().ToString(),
            () => StartFetchDecisionModels(providerId),
            buttonWidth: 200f);
    }

    private static IList<string> ResolveAvailableModels(string providerId)
    {
        List<string> cached = Settings.GetModelList(RimLLMDecisionService.GetModelListKey(providerId));
        if (cached.Count == 0 &&
            RimLLMDecisionProviderCatalog.TryGet(providerId, out RimLLMDecisionProvider provider))
        {
            return new List<string>(provider.PresetModels);
        }
        return cached;
    }

    private static void DrawModelFilterBar(Listing_Standard listing, string providerId, bool isPreset)
    {
        Rect hintRect = listing.GetRect(18f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldC = GUI.color;
            GUI.color = isPreset ? RimLLMUIStyle.Muted : RimLLMUIStyle.Success;
            Widgets.Label(
                hintRect,
                isPreset
                    ? "RimLLM_DecisionPresetModelsHint".Translate()
                    : "RimLLM_DecisionFetchedModelsHint".Translate());
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
        float viewHeight = Mathf.Max(200f, (rows * (chipHeight + gap)) + gap);
        Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

        if (!ModelScrollPositions.ContainsKey(providerId))
        {
            ModelScrollPositions[providerId] = Vector2.zero;
        }
        Vector2 scrollPos = ModelScrollPositions[providerId];

        Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
        ModelScrollPositions[providerId] = scrollPos;

        RimLLMUIStyle.GetVisibleRowRange(
            scrollPos.y,
            scrollRect.height,
            chipHeight + gap,
            gap,
            rows,
            out int firstRow,
            out int endRow);
        string copyHint = "\n\n" + "RimLLM_ClickToCopy".Translate();

        for (int i = firstRow * cols; i < visibleModels.Count && i < endRow * cols; i++)
        {
            string model = visibleModels[i];
            int col = i % cols;
            int row = i / cols;

            Rect chipRect = new Rect(
                (col * (chipWidth + gap)) + gap,
                (row * (chipHeight + gap)) + gap,
                chipWidth,
                chipHeight);

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
                Widgets.Label(
                    textRect,
                    ColorTagOpen + "silver>" + RimLLMUIStyle.TruncateCached(model, textRect.width) + ColorTagClose);
            }
        }

        Widgets.EndScrollView();
    }

    private static void ShowModelMenu(string providerId, string model)
    {
        string capturedModel = model;
        var options = new List<FloatMenuOption>
        {
            new("RimLLM_DecisionSetAsModel".Translate(), () =>
            {
                Settings.SetDecisionModel(providerId, capturedModel);
                Settings.Write();
                Messages.Message(
                    "RimLLM_DecisionModelLabel".Translate() + capturedModel,
                    MessageTypeDefOf.TaskCompletion,
                    false);
            }),
            new("RimLLM_ClickToCopyMenu".Translate(), () =>
            {
                GUIUtility.systemCopyBuffer = capturedModel;
                Messages.Message("RimLLM_CopiedToClipboard".Translate(capturedModel), MessageTypeDefOf.TaskCompletion, false);
            })
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static void StartFetchDecisionModels(string providerId)
    {
        Fetching[providerId] = true;
        FetchStatus[providerId] = "RimLLM_Fetching".Translate();

        string endpoint = Settings.GetDecisionEndpoint(providerId);
        string model = Settings.GetDecisionModel(providerId);
        string apiKey = Settings.GetDecisionApiKey(providerId);

        Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                IReadOnlyList<Kkdev92.Jev.JevModel> models = await new RimLLMDecisionService(Settings)
                    .FetchModelsAsync(endpoint, model, apiKey)
                    .ConfigureAwait(false);

                applyResult = () =>
                {
                    List<string> names = models
                        .Select(entry => entry.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (names.Count == 0)
                    {
                        return "RimLLM_FetchSuccessEmpty".Translate();
                    }

                    Settings.SetModelList(RimLLMDecisionService.GetModelListKey(providerId), names);
                    Settings.Write();
                    return "RimLLM_FetchSuccessCount".Translate(names.Count);
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

    private static void DrawConnectionTest(Listing_Standard listing, string providerId)
    {
        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Testing.TryGetValue(providerId, out bool testing) && testing,
            "RimLLM_DecisionTesting".Translate(),
            "RimLLM_DecisionTestBtn".Translate(),
            TestStatus.TryGetValue(providerId, out string testStatus)
                ? testStatus
                : "RimLLM_TestStatusNotRun".Translate().ToString(),
            () => StartTestDecision(providerId),
            buttonWidth: 200f);
    }

    private static void StartTestDecision(string providerId)
    {
        Testing[providerId] = true;
        TestStatus[providerId] = "RimLLM_DecisionTesting".Translate();

        string endpoint = Settings.GetDecisionEndpoint(providerId);
        string model = Settings.GetDecisionModel(providerId);
        string apiKey = Settings.GetDecisionApiKey(providerId);

        Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                await new RimLLMDecisionService(Settings)
                    .TestConnectionAsync(endpoint, model, apiKey)
                    .ConfigureAwait(false);
                applyResult = () => "RimLLM_DecisionTestSuccess".Translate();
            }
            catch (Exception ex)
            {
                string message = "RimLLM_DecisionTestFailed".Translate(
                    RimLLMLog.SanitizeForLog(ex.Message, 200));
                applyResult = () => message;
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Testing[providerId] = false;
                TestStatus[providerId] = applyResult();
            });
        });
    }
}
