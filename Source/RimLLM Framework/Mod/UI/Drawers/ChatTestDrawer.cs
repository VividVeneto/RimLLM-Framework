using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責對話測試（ChatTest）分頁的 UI 渲染與對話生命週期狀態管理。
/// </summary>
public static class ChatTestDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    // 對話測試專屬的 UI 暫存狀態
    private static string chatInput = "";
    private static readonly List<string> chatHistory = [];
    private static Vector2 chatScrollPosition = Vector2.zero;
    private static bool chatLoading;
    private static ReasoningEffort? chatReasoningEffort;
    private static bool chatReasoningInitialized;
    /// <summary>對話測試釘選的模型（"Provider:Model"），null 代表自動、沿用備援鏈順序。</summary>
    private static string chatModelId;

    /// <summary>聊天輸入框的控制項名稱，用於將 Enter 鍵綁定限縮在該欄位取得焦點時。</summary>
    private const string ChatInputControlName = "RimLLM_ChatInput";
    private const string ChatAiTag = "RimLLM_ChatAi";
    private static readonly char[] SpaceTrimChars = [' '];

    /// <summary>目前進行中請求的取消來源，供「清空」按鈕中止長時間回應。</summary>
    private static System.Threading.CancellationTokenSource chatCts;

    /// <summary>
    /// 串流中重新渲染畫面的最短間隔。50ms 對眼睛已是連續更新，
    /// 但把每秒上百次的全文 Markdown 重排壓到最多 20 次。
    /// </summary>
    private const int StreamRenderIntervalMs = 50;
    private static readonly long StreamRenderIntervalTicks =
        System.Diagnostics.Stopwatch.Frequency * StreamRenderIntervalMs / 1000L;

    /// <summary>
    /// 判斷聊天輸入是否應該送出。純空白視為未輸入。
    /// </summary>
    internal static bool ShouldSendChatInput(string rawInput)
    {
        return !string.IsNullOrEmpty(rawInput) && rawInput.Trim().Length > 0;
    }

    /// <summary>
    /// 中止進行中的聊天請求（若有）。
    /// </summary>
    private static void CancelActiveChatRequest()
    {
        var cts = chatCts;
        chatCts = null;
        if (cts == null) return;

#pragma warning disable S108, S2486 // reason: 取消/釋放已釋放的 CTS 在 Unity 主線程屬預期競爭，刻意忽略不影響主流程
        try { cts.Cancel(); }
        catch
        { // 刻意忽略：CTS 可能已取消或已釋放，不影響主流程
        }
        try { cts.Dispose(); }
        catch
        { // 刻意忽略：重複釋放屬預期競爭，不影響主流程
        }
#pragma warning restore S108, S2486
        chatLoading = false;
    }

    /// <summary>中繼資料標記的前後綴，用於在持久化歷史中保存應答模型、耗時與 Token 數。</summary>
    internal const string MetaPrefix = "\n<!--rimllm-meta:";
    internal const string MetaSuffix = "-->";

    /// <summary>
    /// AI 應答的微型中繼資料。
    /// </summary>
    internal sealed class ChatEntryMeta
    {
        public string ModelId { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public int TotalTokens { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public bool IsEstimatedTokens { get; set; }

        public string Serialize()
        {
            return $"model={ModelId ?? ""}|ms={ElapsedMs}|tok={TotalTokens}|p={PromptTokens}|c={CompletionTokens}|est={(IsEstimatedTokens ? 1 : 0)}";
        }

        private static readonly char[] PipeSeparator = ['|'];

        public static ChatEntryMeta Deserialize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var meta = new ChatEntryMeta();
            var parts = raw.Split(PipeSeparator);
            foreach (var part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                ParseMetaField(meta, key, val);
            }
            return meta;
        }

        private static void ParseMetaField(ChatEntryMeta meta, string key, string val)
        {
            switch (key)
            {
                case "model":
                    meta.ModelId = val;
                    break;
                case "ms":
                    if (long.TryParse(val, out long ms)) { meta.ElapsedMs = ms; }
                    break;
                case "tok":
                    if (int.TryParse(val, out int tok)) { meta.TotalTokens = tok; }
                    break;
                case "p":
                    if (int.TryParse(val, out int p)) { meta.PromptTokens = p; }
                    break;
                case "c":
                    if (int.TryParse(val, out int c)) { meta.CompletionTokens = c; }
                    break;
                case "est":
                    meta.IsEstimatedTokens = val == "1";
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 於主線程更新 AI 回覆佔位項目。索引失效（歷史已被清空）時只略過寫入，其餘收尾照常執行。
    /// </summary>
    /// <param name="persist">是否一併寫回遙測並解除載入狀態（串流結束時使用）。</param>
    /// <param name="scrollToBottom">是否把對話框捲到底。</param>
    private static void UpdateAiHistoryEntry(int index, string reply, ChatEntryMeta meta = null, bool persist = false, bool scrollToBottom = true)
    {
        RimLLMDispatcher.EnqueueOnMainThread(() =>
        {
            if (index < chatHistory.Count)
            {
                string entry = EntryPrefix(ChatAiTag) + reply;
                if (meta != null)
                {
                    entry += MetaPrefix + meta.Serialize() + MetaSuffix;
                }
                chatHistory[index] = entry;
            }
            if (persist)
            {
                PersistChatHistory();
                chatLoading = false;
            }
            if (scrollToBottom)
            {
                chatScrollPosition.y = 999999f;
            }
        });
    }

    /// <summary>
    /// 將目前的對話歷史複本交給遙測儲存，寫檔（AES + JSON + 磁碟）交由背景單寫者執行。
    /// 先前在主執行緒同步寫檔，背景寫入正持有檔案鎖時主執行緒還會被卡住。
    /// </summary>
    private static void PersistChatHistory()
    {
        Settings.ChatHistory = new List<string>(chatHistory);
        Settings.QueueTelemetrySave();
    }

    /// <summary>
    /// 初始化對話歷史紀錄。
    /// </summary>
    public static void Initialize(RimLLMFrameworkSettings settings)
    {
        if (settings.ChatHistory != null && settings.ChatHistory.Count > 0)
        {
            chatHistory.Clear();
            chatHistory.AddRange(settings.ChatHistory);
        }
    }

    /// <summary>
    /// 獲取對話分頁詳細內容的滾動高度。
    /// </summary>
    public static float GetHeight(float width)
    {
        return 686f;
    }

    /// <summary>
    /// 繪製對話測試介面。
    /// </summary>
#pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
    public static void DrawChatTestSettings(Listing_Standard listing)
    {
        if (!chatReasoningInitialized)
        {
            chatReasoningEffort = Settings.DefaultReasoningEffort;
            chatReasoningInitialized = true;
        }

        // 滾動顯示對話歷史
        Rect chatRect = listing.GetRect(480f);
        Widgets.DrawMenuSection(chatRect);

        float chatContentWidth = chatRect.width - 16f;

        GUIStyle richLabelStyle = ResolveRichLabelStyle();

        float bubbleWidth = chatContentWidth - 8f;
        float bubbleInnerWidth = bubbleWidth - 16f;
        List<BubbleLayout> bubbleLayouts = ResolveBubbleLayouts(richLabelStyle, bubbleInnerWidth);
        float totalBubblesHeight = 8f;
        for (int i = 0; i < bubbleLayouts.Count; i++)
        {
            totalBubblesHeight += bubbleLayouts[i].Height + 8f;
        }

        float chatViewHeight = Math.Max(480f, totalBubblesHeight);
        Rect chatViewRect = new Rect(0f, 0f, chatContentWidth, chatViewHeight);

        Widgets.BeginScrollView(chatRect, ref chatScrollPosition, chatViewRect);

        if (chatHistory.Count == 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(new Rect(0f, 0f, chatContentWidth, 480f), "<color=grey>（目前尚無對話記錄，請在下方輸入測試訊息）</color>");
            }
        }
        else
        {
            float curY = 8f;
            // 捲出可視範圍的氣泡完全跳過：長對話下每幀真正需要畫的只有幾張卡片。
            // 捲動位置自行夾住：串流時每個更新都把 y 設成 999999 捲到底，
            // Unity 要到下一幀才會夾回合法值，不夾的話那一幀會判定所有卡片都在視野外。
            float visibleTop = Mathf.Clamp(chatScrollPosition.y, 0f, Mathf.Max(0f, chatViewHeight - chatRect.height));
            float visibleBottom = visibleTop + chatRect.height;
            string copyLabel = "RimLLM_ChatCopyMessage".Translate();

            for (int i = 0; i < bubbleLayouts.Count; i++)
            {
                BubbleLayout layout = bubbleLayouts[i];
                float cardTop = curY;
                curY += layout.Height + 8f;
                if (cardTop > visibleBottom || cardTop + layout.Height < visibleTop)
                {
                    continue;
                }

                Rect bubbleRect = new Rect(4f, cardTop, bubbleWidth, layout.Height);

                Color bgColor = layout.IsUser ? RimLLMUIStyle.BubbleUserFill : RimLLMUIStyle.BubbleAiFill;

                Widgets.DrawBoxSolid(bubbleRect, bgColor);
                Widgets.DrawBox(bubbleRect, 1);

                // 標題列
                Rect headerRect = new Rect(bubbleRect.x + 8f, bubbleRect.y + 4f, bubbleRect.width - 16f, 20f);
                using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny))
                {
                    Widgets.Label(headerRect, layout.HeaderText);
                }

                // 內文
                Rect bodyRect = new Rect(bubbleRect.x + 8f, bubbleRect.y + 24f, bubbleInnerWidth, layout.TextHeight + 2f);
                GUI.Label(bodyRect, layout.Body, richLabelStyle);

                // AI 訊息底部工具列（微型標籤與一鍵複製）
                if (!layout.IsUser)
                {
                    float btnWidth = 80f;
                    float btnHeight = 22f;
                    float bottomY = bubbleRect.yMax - 27f;
                    Rect copyBtnRect = new Rect(bubbleRect.xMax - 8f - btnWidth, bottomY, btnWidth, btnHeight);

                    if (!chatLoading && Widgets.ButtonText(copyBtnRect, copyLabel))
                    {
                        GUIUtility.systemCopyBuffer = StripRichTextForClipboard(layout.Body);
                        Messages.Message("RimLLM_CopiedToClipboard".Translate("AI"), MessageTypeDefOf.TaskCompletion, false);
                    }

                    // 繪製應答模型、耗時與 Token 數微型標籤
                    if (layout.Meta != null)
                    {
                        Rect badgesArea = new Rect(bubbleRect.x + 8f, bottomY, copyBtnRect.x - 8f - (bubbleRect.x + 8f), btnHeight);
                        DrawAiMetaBadges(badgesArea, layout);
                    }
                }
            }
        }

        Widgets.EndScrollView();

        listing.Gap(6f);

        // 測試模型選擇列：釘選後本次對話走該模型優先（備援鏈首），失敗仍可沿鏈降級。
        DrawModelSelectorRow(listing);
        listing.Gap(6f);

        // 思考強度設定列
        Rect effortRowRect = listing.GetRect(30f);
        float effortLabelWidth = Text.CalcSize("RimLLM_ReasoningEffortLabel".Translate()).x;
        Rect effortLabelRect = new Rect(effortRowRect.x, effortRowRect.y, effortLabelWidth + 5f, effortRowRect.height);
        Rect effortBtnRect = new Rect(effortRowRect.x + effortLabelWidth + 15f, effortRowRect.y, 160f, effortRowRect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
#pragma warning disable S108, S1643, S2486, S8949 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
        {
            Widgets.Label(effortLabelRect, "RimLLM_ReasoningEffortLabel".Translate());
        }
        string chatEffortLabel = chatReasoningEffort == null ? "RimLLM_ReasoningEffort_Auto".Translate() : $"RimLLM_ReasoningEffort_{chatReasoningEffort}".Translate();
        if (Widgets.ButtonText(effortBtnRect, chatEffortLabel))
        {
            List<FloatMenuOption> options =
            [
                new("RimLLM_ReasoningEffort_Auto".Translate(), () => { chatReasoningEffort = null; }),
                new("RimLLM_ReasoningEffort_Low".Translate(), () => { chatReasoningEffort = ReasoningEffort.Low; }),
                new("RimLLM_ReasoningEffort_Medium".Translate(), () => { chatReasoningEffort = ReasoningEffort.Medium; }),
                new("RimLLM_ReasoningEffort_High".Translate(), () => { chatReasoningEffort = ReasoningEffort.High; })
            ];
            Find.WindowStack.Add(new FloatMenu(options));
        }
        listing.Gap(6f);

        // 輸入框、清空按鈕與發送/停止按鈕
        Rect inputRowRect = listing.GetRect(30f);
        Rect textInputRect = new Rect(inputRowRect.x, inputRowRect.y, inputRowRect.width - 180f, inputRowRect.height);
        Rect clearBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 170f, inputRowRect.y, 80f, inputRowRect.height);
        Rect sendBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 80f, inputRowRect.y, 80f, inputRowRect.height);

        if (chatLoading)
        {
            int dotCount = 1 + (int)(Time.realtimeSinceStartup * 2.5) % 3;
            string dots = new('.', dotCount);
            Widgets.Label(textInputRect, $"<color=silver>{"RimLLM_AiThinking".Translate()}{dots}</color>");
        }
        else
        {
            GUI.SetNextControlName(ChatInputControlName);
            chatInput = Widgets.TextField(textInputRect, chatInput);
        }

        // 點擊清空按鈕邏輯
        if (!chatLoading && Widgets.ButtonText(clearBtnRect, "RimLLM_ClearBtn".Translate()))
        {
            CancelActiveChatRequest();
            chatHistory.Clear();
            Settings.ClearChatHistory();
            // 清空是少見的明確操作，歷史可能含敏感內容：同步寫檔，不留給背景寫入的時間窗。
            Settings.SaveTelemetry();
            chatInput = "";
        }

        // Enter 僅在聊天輸入框取得焦點時才觸發送出。
        bool pressEnter = Event.current.type == EventType.KeyDown &&
                          Event.current.keyCode == KeyCode.Return &&
                          GUI.GetNameOfFocusedControl() == ChatInputControlName;

        if (chatLoading)
        {
            Color origColor = GUI.color;
            GUI.color = new Color(0.95f, 0.35f, 0.35f);
            if (Widgets.ButtonText(sendBtnRect, "RimLLM_ChatStop".Translate()))
            {
                CancelActiveChatRequest();
            }
            GUI.color = origColor;
        }
        else if (Widgets.ButtonText(sendBtnRect, "RimLLM_Send".Translate()) || pressEnter)
        {
            // 消耗事件，避免同一個 Enter 被 RimWorld 視窗系統重複處理（重複送出或誤關視窗）。
            if (pressEnter) Event.current.Use();

            if (ShouldSendChatInput(chatInput))
            {
                // 先中止前一輪尚未完成的請求（此呼叫會把 chatLoading 歸零），再開始新一輪。
                CancelActiveChatRequest();

                string userPrompt = chatInput.Trim();
                string pinnedModel = chatModelId;
                chatHistory.Add(EntryPrefix("RimLLM_ChatUser") + userPrompt);

                // 先新增一個 AI 回覆的佔位項目，以利後續串流更新
                chatHistory.Add(EntryPrefix(ChatAiTag));
                int aiHistoryIndex = chatHistory.Count - 1;

                PersistChatHistory();
                chatInput = "";
                chatLoading = true;

                object replyLock = new();
                var accumulatedReply = new System.Text.StringBuilder();

                // 串流渲染節流狀態（都在 replyLock 底下讀寫）：
                // 每個 chunk 都對累積全文重跑一次 Markdown 與四段 Regex 是 O(n²)，
                // 快供應商一秒上百個 chunk 時背景執行緒與主執行緒都在白忙。
                long lastRenderTimestamp = 0;
                bool trailingRenderScheduled = false;
                bool streamCompleted = false;

                var requestCts = new System.Threading.CancellationTokenSource();
                chatCts = requestCts;

                Task.Run(async () =>
                {
                    try
                    {
                        var client = RimLLMProvider.CreateChatClient(ServiceConsumerList.DebugChatModId);
                        var messages = new List<ChatMessage> { new(ChatRole.User, userPrompt) };
                        var options = new RimLLMChatOptions
                        {
                            MaxOutputTokens = 4096,
                            Temperature = 0.7f,
                            DisableReasoning = false,
                            Reasoning = chatReasoningEffort.HasValue ? new ReasoningOptions { Effort = chatReasoningEffort.Value } : null
                        };
                        // 釘選測試模型：路由層會把它移到備援鏈首優先嘗試；null 則沿用鏈順序。
                        options.ModelId = pinnedModel;
                        // 框架的串流只傳遞 MEAI 原生的 TextReasoningContent，
                        // <think> 標記由這裡自己組——顯示成灰色的邏輯需要它。
                        var thinkFormatter = new RimLLMThinkTagFormatter();
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        string actualModelId = null;
                        int promptTokens = 0;
                        int completionTokens = 0;
                        int totalTokens = 0;
                        bool hasExactTokens = false;

                        var enumerator = client.GetStreamingResponseAsync(messages, options, requestCts.Token).GetAsyncEnumerator();
                        try
                        {
                            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                var update = enumerator.Current;
                                if (update != null)
                                {
                                    if (string.IsNullOrEmpty(actualModelId) && !string.IsNullOrEmpty(update.ModelId))
                                    {
                                        actualModelId = update.ModelId;
                                    }
                                    if (update.Contents != null)
                                    {
                                        foreach (var part in update.Contents)
                                        {
                                            if (part is UsageContent usage && usage.Details != null)
                                            {
                                                if (usage.Details.TotalTokenCount.HasValue && usage.Details.TotalTokenCount.Value > 0)
                                                {
                                                    totalTokens = (int)usage.Details.TotalTokenCount.Value;
                                                    hasExactTokens = true;
                                                }
                                                if (usage.Details.InputTokenCount.HasValue) promptTokens = (int)usage.Details.InputTokenCount.Value;
                                                if (usage.Details.OutputTokenCount.HasValue) completionTokens = (int)usage.Details.OutputTokenCount.Value;
                                            }
                                        }
                                    }
                                }
                                AppendToReply(thinkFormatter.Append(update));
                            }
                        }
                        finally
                        {
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                        }

                        // 整段都是推理內容時，收尾標籤只能在串流結束後補。
                        // 直接附加不觸發即時渲染：最終渲染緊接在後，多一次只是浪費。
                        string tail = thinkFormatter.Complete();
                        string fullReply;
                        lock (replyLock)
                        {
                            if (!string.IsNullOrEmpty(tail)) accumulatedReply.Append(tail);
                            // 先標記完成，讓排程中的尾端渲染看到後直接放棄，不會覆蓋最終結果。
                            streamCompleted = true;
                            fullReply = accumulatedReply.ToString();
                        }

                        stopwatch.Stop();
                        long finalElapsed = stopwatch.ElapsedMilliseconds;
                        if (!hasExactTokens || totalTokens <= 0)
                        {
                            // 與用量帳本用同一套估算，顯示的數字才會和統計頁一致。
                            promptTokens = RimLLMChatClientExecutor.EstimateTokens(userPrompt);
                            completionTokens = RimLLMChatClientExecutor.EstimateTokens(fullReply);
                            totalTokens = promptTokens + completionTokens;
                            hasExactTokens = false;
                        }

                        var finalMeta = new ChatEntryMeta
                        {
                            ModelId = actualModelId,
                            ElapsedMs = finalElapsed,
                            TotalTokens = totalTokens,
                            PromptTokens = promptTokens,
                            CompletionTokens = completionTokens,
                            IsEstimatedTokens = !hasExactTokens
                        };

                        void AppendToReply(string text)
                        {
                            if (string.IsNullOrEmpty(text)) return;

                            bool renderNow;
                            bool scheduleTrailing = false;
                            lock (replyLock)
                            {
                                accumulatedReply.Append(text);
                                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                                renderNow = now - lastRenderTimestamp >= StreamRenderIntervalTicks;
                                if (renderNow)
                                {
                                    lastRenderTimestamp = now;
                                }
                                else if (!trailingRenderScheduled)
                                {
                                    // 節流窗內的 chunk 先累積；排一次尾端渲染，
                                    // 讓串流中途停頓時最後幾個 chunk 也不會卡在畫面外。
                                    trailingRenderScheduled = true;
                                    scheduleTrailing = true;
                                }
                            }

                            if (renderNow)
                            {
                                RenderLive();
                            }
                            else if (scheduleTrailing)
                            {
                                Task.Delay(StreamRenderIntervalMs).ContinueWith(_ =>
                                {
                                    lock (replyLock)
                                    {
                                        trailingRenderScheduled = false;
                                        if (streamCompleted) return;
                                        lastRenderTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                                    }
                                    RenderLive();
                                }, TaskScheduler.Default);
                            }
                        }

                        void RenderLive()
                        {
                            // 「檢查 completed → 渲染 → 入列」必須在同一個鎖內完成：
                            // 入列若放在鎖外，較舊的快照可能在最終渲染（或錯誤訊息）之後才入列，
                            // 派遣器依入列順序執行，畫面就會被截斷版蓋掉且不再被修正。
                            lock (replyLock)
                            {
                                if (streamCompleted) return;
                                string localReply = RenderReply(accumulatedReply.ToString());
                                var liveMeta = new ChatEntryMeta
                                {
                                    ModelId = actualModelId,
                                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                                    TotalTokens = totalTokens,
                                    PromptTokens = promptTokens,
                                    CompletionTokens = completionTokens,
                                    IsEstimatedTokens = !hasExactTokens
                                };
                                UpdateAiHistoryEntry(aiHistoryIndex, localReply, meta: liveMeta);
                            }
                        }

                        string formattedFinal = RenderReply(fullReply);
                        UpdateAiHistoryEntry(aiHistoryIndex, formattedFinal, meta: finalMeta, persist: true);
                    }
                    catch (Exception ex)
                    {
                        // 排程中的尾端渲染不得在錯誤訊息寫入後又把它蓋掉。
                        lock (replyLock) { streamCompleted = true; }

                        string safeError = EntryPrefix("RimLLM_ChatAiError") +
                            "<color=#ef4444>" + RimLLMLog.SanitizeForLog(ex.Message, 240) + "</color>";
                        RimLLMDispatcher.EnqueueOnMainThread(() =>
                        {
                            if (aiHistoryIndex < chatHistory.Count)
                            {
                                chatHistory[aiHistoryIndex] = safeError;
                            }
                            else
                            {
                                chatHistory.Add(safeError);
                            }
                            PersistChatHistory();
                            chatLoading = false;
                            chatScrollPosition.y = 999999f;
                        });
                    }
                });
            }
        }
    }
#pragma warning restore S3776

    /// <summary>
    /// 測試模型選擇列。選單列出備援鏈上的 "Provider:Model" 條目，選中後以
    /// <see cref="ChatOptions.ModelId"/> 釘選為鏈首；選自動則不指定、沿用備援鏈順序。
    /// </summary>
    private static void DrawModelSelectorRow(Listing_Standard listing)
    {
        // 與模型設置頁共用同一份去空去重邏輯，兩處列出的條目才不會漂移。
        List<string> entries = ModelSettingsDrawer.CollectEntries();
        // 備援鏈被改掉後，舊的釘選已無意義，退回自動。
        if (!string.IsNullOrEmpty(chatModelId) && !entries.Contains(chatModelId))
        {
            chatModelId = null;
        }

        Rect rowRect = listing.GetRect(30f);
        float labelWidth = Text.CalcSize("RimLLM_ChatTestModelLabel".Translate()).x;
        Rect labelRect = new Rect(rowRect.x, rowRect.y, labelWidth + 5f, rowRect.height);
        Rect btnRect = new Rect(rowRect.x + labelWidth + 15f, rowRect.y, 300f, rowRect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(labelRect, "RimLLM_ChatTestModelLabel".Translate());
        }

        string btnLabel = string.IsNullOrEmpty(chatModelId)
            ? "RimLLM_ChatTestModelAuto".Translate().ToString()
            : chatModelId;
        if (Widgets.ButtonText(btnRect, btnLabel))
        {
            var options = new List<FloatMenuOption>
            {
                new("RimLLM_ChatTestModelAuto".Translate(), () => { chatModelId = null; })
            };
            foreach (string entry in entries)
            {
                string captured = entry;
                options.Add(new FloatMenuOption(captured, () => { chatModelId = captured; }));
            }
            if (entries.Count == 0)
            {
                options.Add(new FloatMenuOption("RimLLM_ModelsEmptyWarning".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private static string ThinkStartLabel => "RimLLM_ThinkProcessLabel".Translate();
    private static string ThinkEndLabel => "RimLLM_ThinkProcessEndLabel".Translate();
    private static string ThinkingLabel => "RimLLM_ThinkingLabel".Translate();

    /// <summary>
    /// 把模型回覆整理成可顯示的 rich text：先抽出思考過程並標灰，再把剩下的 Markdown 轉成標籤。
    /// 順序不能顛倒 —— ```thought 圍籬必須先被思考處理吃掉，否則會被當成一般程式碼區塊。
    /// </summary>
    private static string RenderReply(string text)
    {
        return RimLLMMarkdown.ToRichText(FormatThinkProcess(text));
    }

    private static string FormatThinkProcess(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 先跳脫供應商內容中的 Unity 標籤，再加入框架自行產生的灰色 thinking wrapper；
        // 否則供應商可以用同名的 </color> 提早關閉框架標籤。
        text = RimLLMMarkdown.EscapeUntrustedUnityTags(text);

        // 註：此處原本在 DetailedLogging 下記錄完整的模型輸出。
        // 該日誌未經 SanitizeForLog、未截斷，且因為由 OnChunkReceived 觸發，
        // 會對每個 chunk 重複輸出整段累積內容，使 PII、prompt 或意外出現的金鑰
        // 長期留存於 Player.log。已移除（稽核 SEC-002）。

        // 1. 處理已閉合的 <think>...</think> 或 <thought>...</thought> -> 標記為灰色
        string result = Regex.Replace(text, @"<(think|thought)>([\s\S]*?)</\1>", m => WrapClosedThought(m.Groups[2].Value), RegexOptions.None, TimeSpan.FromSeconds(1));

        // 2. 處理未閉合的 <think> 或 <thought>（串流中常遇到） -> 將後續全部標記為灰色
        var matchUnclosedXml = Regex.Match(result, @"<(think|thought)>(?![\s\S]*</\1>)", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (matchUnclosedXml.Success)
        {
            result = WrapTrailingThought(result, matchUnclosedXml.Index, matchUnclosedXml.Length);
        }

        // 3. 處理已閉合的 markdown 思考區塊 ```thought...``` -> 標記為灰色
        result = Regex.Replace(result, @"```thought([\s\S]*?)```", m => WrapClosedThought(m.Groups[1].Value), RegexOptions.None, TimeSpan.FromSeconds(1));

        // 4. 處理未閉合的 markdown 思考區塊 ```thought （串流中常遇到）-> 將後續全部標記為灰色
        int markdownMarker = result.IndexOf(MarkdownThoughtMarker, StringComparison.Ordinal);
        if (markdownMarker >= 0)
        {
            result = WrapTrailingThought(result, markdownMarker, MarkdownThoughtMarker.Length);
        }

        return result.Trim();
    }

    private const string MarkdownThoughtMarker = "```thought";

    /// <summary>已閉合的思考區塊：內容為空則整段捨棄，否則以灰色包起來。</summary>
    private static string WrapClosedThought(string content)
    {
        string thinkContent = content.Trim();
        return string.IsNullOrEmpty(thinkContent)
            ? ""
            : $"\n<color=silver>{ThinkStartLabel}\n{thinkContent}\n{ThinkEndLabel}</color>\n\n";
    }

    /// <summary>未閉合的思考區塊：把標記之後的所有內容都視為思考中並標灰。</summary>
    private static string WrapTrailingThought(string text, int markerIndex, int markerLength)
    {
        string before = text.Substring(0, markerIndex);
        string after = text.Substring(markerIndex + markerLength);
        return before + $"\n<color=silver>{ThinkingLabel}\n{after} ...</color>";
    }

    /// <summary>
    /// 對話紀錄項目的前綴（例如 "&lt;b&gt;[AI]:&lt;/b&gt; "）。
    /// 一定要以 ToString() 取 RawText 再串接：TaggedString 隱含轉成 string 時會呼叫 StripTags()，
    /// 把整條訊息裡所有 &lt;...&gt; 標籤（包含 Markdown 轉出的 color/size/b）一併剝掉，
    /// 畫面上就只剩沒有任何格式的純文字。
    /// </summary>
    private static string EntryPrefix(string key)
    {
        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return key.Translate().ToString() + " ";
            }
        }
        catch
        {
            // 單元測試或環境未載入語系時安全退回
        }

        return key switch
        {
            "RimLLM_ChatUser" => "<b>[Me]:</b> ",
            ChatAiTag => "<b>[AI]:</b> ",
            "RimLLM_ChatAiError" => "<b>[AI Error]:</b> ",
            _ => key + " ",
        };
    }

    /// <summary>
    /// 嘗試剝掉 <paramref name="key"/> 對應的前綴。
    /// 修正前持久化的舊紀錄前綴已被 StripTags 剝成純文字（"[AI]: "），兩種形式都要認得。
    /// </summary>
    private static bool TryStripPrefix(string entry, string key, out string label, out string body)
    {
        string raw = EntryPrefix(key);
        string plain = raw.StripTags();
        label = plain.Trim();
        if (entry.StartsWith(raw, StringComparison.Ordinal))
        {
            body = entry.Substring(raw.Length);
            return true;
        }
        if (entry.StartsWith(plain, StringComparison.Ordinal))
        {
            body = entry.Substring(plain.Length);
            return true;
        }
        body = string.Empty;
        return false;
    }

    internal static bool ParseMessage(string entry, out string label, out string body, out ChatEntryMeta meta)
    {
        meta = null;
        if (string.IsNullOrEmpty(entry))
        {
            label = string.Empty;
            body = string.Empty;
            return false;
        }

        // 抽離並淨化中繼資料標記，避免污染本文顯示與剪貼簿
        int metaStart = entry.LastIndexOf(MetaPrefix, StringComparison.Ordinal);
        if (metaStart >= 0)
        {
            int metaEnd = entry.IndexOf(MetaSuffix, metaStart + MetaPrefix.Length, StringComparison.Ordinal);
            if (metaEnd >= 0)
            {
                string rawMeta = entry.Substring(metaStart + MetaPrefix.Length, metaEnd - (metaStart + MetaPrefix.Length));
                meta = ChatEntryMeta.Deserialize(rawMeta);
                entry = entry.Substring(0, metaStart);
            }
        }

        if (TryStripPrefix(entry, "RimLLM_ChatUser", out label, out body)) return true;
        if (TryStripPrefix(entry, "RimLLM_ChatAiError", out label, out body)) return false;
        if (TryStripPrefix(entry, ChatAiTag, out label, out body)) return false;

        // 舊格式或手動拼接的格式相容
        if (entry.StartsWith("<b>[我]:</b>", StringComparison.Ordinal) || entry.StartsWith("<b>[Me]:</b>", StringComparison.Ordinal))
        {
            int endTag = entry.IndexOf("</b>", StringComparison.Ordinal);
            label = entry.Substring(0, endTag + 4).Replace("<b>", "").Replace("</b>", "").Trim();
            body = entry.Substring(endTag + 4).TrimStart(SpaceTrimChars);
            return true;
        }

        if (entry.StartsWith("<b>[AI]:</b>", StringComparison.Ordinal))
        {
            int endTag = entry.IndexOf("</b>", StringComparison.Ordinal);
            label = entry.Substring(0, endTag + 4).Replace("<b>", "").Replace("</b>", "").Trim();
            body = entry.Substring(endTag + 4).TrimStart(SpaceTrimChars);
            return false;
        }

        label = EntryPrefix(ChatAiTag).StripTags().Trim();
        body = entry;
        return false;
    }

    internal static bool ParseMessage(string entry, out string label, out string body)
    {
        return ParseMessage(entry, out label, out body, out _);
    }

    /// <summary>
    /// 格式化模型名稱供微型標籤顯示。若為 "供應商:模型" 複合識別碼，轉為易讀的 "供應商 • 模型" 格式。
    /// </summary>
    internal static string FormatModelDisplayName(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return string.Empty;
        int colonIdx = modelId.IndexOf(':');
        if (colonIdx > 0 && colonIdx < modelId.Length - 1)
        {
            string provider = modelId.Substring(0, colonIdx);
            string model = modelId.Substring(colonIdx + 1);
            return $"{provider} • {model}";
        }
        return modelId;
    }

    /// <summary>
    /// 繪製 AI 訊息底部的微型中繼標籤（方案 A：極簡扁平資訊列）。
    /// 徹底拿掉刺眼的硬外框與高對比底色，以低調柔和的冷灰字階呈現，
    /// 滑鼠懸停時微顯高亮，既保留一鍵複製與詳細 Tooltip，又完全不搶正文焦點。
    /// </summary>
    private static void DrawAiMetaBadges(Rect area, BubbleLayout layout)
    {
        if (layout.Meta == null || area.width < 50f) return;

        float curX = area.x;
        float gap = 8f;

        // 1. 模型標籤（點擊可一鍵複製，懸停微高亮）
        DrawModelBadge(ref curX, area, layout, gap);

        // 2. 耗時與 Token 數（懸停顯示詳細分解 Tooltip）
        DrawMetricsBadge(curX, area, layout);
    }

    private static void DrawModelBadge(ref float curX, Rect area, BubbleLayout layout, float gap)
    {
        string modelDisplay = layout.ModelDisplay;
        if (string.IsNullOrEmpty(modelDisplay)) return;
        string modelId = layout.Meta.ModelId;

        float modelW = Mathf.Min(layout.ModelTextWidth + 6f, area.width * 0.6f);
        Rect modelRect = new Rect(curX, area.y, modelW, area.height);

        bool hovered = Mouse.IsOver(modelRect);
        if (hovered)
        {
            Widgets.DrawHighlight(modelRect);
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
        {
            Widgets.Label(modelRect, "<color=#94a3b8>" + RimLLMUIStyle.TruncateCached(modelDisplay, modelRect.width) + "</color>");
        }

        if (hovered)
        {
            TooltipHandler.TipRegion(modelRect, "RimLLM_ChatModelBadgeTooltip".Translate(modelId));
        }

        if (Widgets.ButtonInvisible(modelRect))
        {
            GUIUtility.systemCopyBuffer = modelId;
            Messages.Message("RimLLM_CopiedToClipboard".Translate(modelId), MessageTypeDefOf.TaskCompletion, false);
        }

        curX += modelW;

        // 分隔點
        if (area.xMax - curX > 50f)
        {
            Rect dotRect = new Rect(curX, area.y, gap, area.height);
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter, GameFont.Tiny, wordWrap: false))
            {
                Widgets.Label(dotRect, "<color=#475569>•</color>");
            }
            curX += gap;
        }
    }

    private static void DrawMetricsBadge(float curX, Rect area, BubbleLayout layout)
    {
        float remainW = area.xMax - curX;
        if (remainW <= 40f) return;

        float statW = Mathf.Min(layout.StatTextWidth + 8f, remainW);
        Rect statRect = new Rect(curX, area.y, statW, area.height);

        bool hovered = Mouse.IsOver(statRect);
        if (hovered)
        {
            Widgets.DrawHighlight(statRect);
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
        {
            Widgets.Label(statRect, layout.StatText);
        }

        if (hovered)
        {
            ChatEntryMeta meta = layout.Meta;
            float timeSec = meta.ElapsedMs / 1000f;
            string statTip = meta.IsEstimatedTokens
                ? "RimLLM_ChatStatBadgeTooltipEst".Translate(meta.ElapsedMs, timeSec.ToString("F2"), meta.TotalTokens.ToString("N0"))
                : "RimLLM_ChatStatBadgeTooltip".Translate(meta.ElapsedMs, timeSec.ToString("F2"), meta.TotalTokens.ToString("N0"), meta.PromptTokens.ToString("N0"), meta.CompletionTokens.ToString("N0"));
            TooltipHandler.TipRegion(statRect, statTip);
        }
    }

    /// <summary>
    /// 一張氣泡卡片繪製所需的全部預算結果：解析後的標籤與內文、量測過的高度、
    /// 徽章文字與寬度。<see cref="Entry"/> 記住來源字串，讓快取以參考相等判定是否過期。
    /// </summary>
    private sealed class BubbleLayout
    {
        public string Entry;
        public bool IsUser;
        public string Body;
        public ChatEntryMeta Meta;
        public float Height;
        public float TextHeight;
        public string HeaderText;
        public string ModelDisplay;
        public float ModelTextWidth;
        public string StatText;
        public float StatTextWidth;
    }

    private static readonly List<BubbleLayout> _layoutCache = [];
    private static float _layoutCacheWidth = -1f;
    private static GameFont _layoutCacheFont;

    private static GUIStyle _richLabelStyle;
    private static GameFont _richLabelStyleFont;

    /// <summary>
    /// 內文用的 rich text 樣式。GUIStyle 是 Unity 原生物件，先前每個 OnGUI pass 都 new 一個；
    /// 只有字型變了才需要重建。
    /// </summary>
    private static GUIStyle ResolveRichLabelStyle()
    {
        if (_richLabelStyle == null || _richLabelStyleFont != Text.Font)
        {
            _richLabelStyle = new GUIStyle(Text.CurFontStyle)
            {
                richText = true,
                wordWrap = true
            };
            _richLabelStyleFont = Text.Font;
        }
        return _richLabelStyle;
    }

    /// <summary>
    /// 逐筆比對歷史項目與快取的來源字串：沒變的直接沿用，變了的（串流中的那一筆、新增的、
    /// 清空後重建的）才重新解析與量測。先前每個 OnGUI pass 都對全部歷史重做
    /// Translate、StripTags（Regex）與 CalcHeight，長對話開著設定頁就是持續的每幀開銷。
    /// 寬度或字型變了會讓所有量測失效，此時整批重算。
    /// </summary>
    private static List<BubbleLayout> ResolveBubbleLayouts(GUIStyle richLabelStyle, float bubbleInnerWidth)
    {
        if (Math.Abs(_layoutCacheWidth - bubbleInnerWidth) > 0.001f || _layoutCacheFont != Text.Font)
        {
            _layoutCache.Clear();
            _layoutCacheWidth = bubbleInnerWidth;
            _layoutCacheFont = Text.Font;
        }

        if (_layoutCache.Count > chatHistory.Count)
        {
            _layoutCache.RemoveRange(chatHistory.Count, _layoutCache.Count - chatHistory.Count);
        }

        for (int i = 0; i < chatHistory.Count; i++)
        {
            string entry = chatHistory[i];
            if (i < _layoutCache.Count)
            {
                if (ReferenceEquals(_layoutCache[i].Entry, entry)) continue;
                _layoutCache[i] = BuildBubbleLayout(entry, richLabelStyle, bubbleInnerWidth);
            }
            else
            {
                _layoutCache.Add(BuildBubbleLayout(entry, richLabelStyle, bubbleInnerWidth));
            }
        }

        return _layoutCache;
    }

    private static BubbleLayout BuildBubbleLayout(string entry, GUIStyle richLabelStyle, float bubbleInnerWidth)
    {
        bool isUser = ParseMessage(entry, out string label, out string body, out ChatEntryMeta meta);
        float textHeight = richLabelStyle.CalcHeight(new GUIContent(body), bubbleInnerWidth);

        var layout = new BubbleLayout
        {
            Entry = entry,
            IsUser = isUser,
            Body = body,
            Meta = meta,
            TextHeight = textHeight,
            Height = 24f + textHeight + (isUser ? 10f : 34f),
            HeaderText = isUser
                ? "<color=#60a5fa><b>" + label + "</b></color>"
                : "<color=#4ade80><b>" + label + "</b></color>"
        };

        if (meta != null)
        {
            layout.ModelDisplay = FormatModelDisplayName(meta.ModelId);

            string timeStr = meta.ElapsedMs < 1000 ? $"{meta.ElapsedMs}ms" : $"{(meta.ElapsedMs / 1000f):F1}s";
            string tokenPrefix = meta.IsEstimatedTokens ? "~" : "";
            string tokenStr = meta.TotalTokens > 0 ? $"{tokenPrefix}{meta.TotalTokens:N0} tok" : "";
            layout.StatText = string.IsNullOrEmpty(tokenStr)
                ? $"<color=#64748b>{timeStr}</color>"
                : $"<color=#64748b>{timeStr}</color>  <color=#475569>•</color>  <color=#64748b>{tokenStr}</color>";

            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                layout.ModelTextWidth = string.IsNullOrEmpty(layout.ModelDisplay) ? 0f : Text.CalcSize(layout.ModelDisplay).x;
                layout.StatTextWidth = Text.CalcSize(layout.StatText.StripTags()).x;
            }
        }

        return layout;
    }

    /// <summary>
    /// 複製到剪貼簿前先移除真正的 rich text 標籤與可能存在的中繼標記，再把 <see cref="RimLLMMarkdown.EscapeCode"/> 為了不被 Unity 解讀
    /// 而改成全形的程式碼標籤（＜b＞、＜/color＞…）還原成 ASCII。只還原那六種標籤，
    /// 中文回覆裡本來就是全形的「＜注意＞」不能被改掉。順序不能顛倒，否則還原後的程式碼標籤會被當成格式一起剝掉。
    /// </summary>
    private static string StripRichTextForClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var timeout = TimeSpan.FromSeconds(1);
        string cleaned = Regex.Replace(text, @"<!--rimllm-meta:[\s\S]*?-->", "", RegexOptions.IgnoreCase, timeout);
        cleaned = Regex.Replace(cleaned, @"</?(?:b|i|size|color|material|quad)(?:=[^>]*)?>", "", RegexOptions.IgnoreCase, timeout);
        return Regex.Replace(cleaned, @"＜(/?(?:b|i|size|color|material|quad)(?:=[^＞]*)?)＞", "<$1>", RegexOptions.IgnoreCase, timeout);
    }
}
#pragma warning restore S108, S1643, S2486, S8949
