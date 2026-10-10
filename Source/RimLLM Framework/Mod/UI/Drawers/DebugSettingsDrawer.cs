using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責偵錯面板（Debug）分頁的 UI 渲染與呼叫日誌、診斷匯出管理。
    /// </summary>
    public static class DebugSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 偵錯面板專屬的 UI 暫存狀態
        private static Vector2 debugScrollPosition = Vector2.zero;
        private static string schemaSelfTestStatus = string.Empty;
        private static bool schemaSelfTestRunning;

        /// <summary>
        /// 結構化輸出自我檢查用的型別。
        /// 刻意包含 <c>Nullable&lt;T&gt;</c>、清單與列舉：這三者正是各供應商 schema 方言差異最大的地方，
        /// 只用純量成員的話兩種方言會產出相同的 schema，測不出方言接線是否正確。
        /// </summary>
#pragma warning disable S1144, S3459 // reason: 供序列化/反射與 Schema 自我檢查使用，未賦值與未使用屬預期，維持現狀
        private sealed class SchemaSelfTestPayload
        {
            public string Summary { get; set; }
            public int Score { get; set; }
            public int? OptionalScore { get; set; }
            public List<string> Tags { get; set; }
        }
#pragma warning restore S1144, S3459

        /// <summary>
        /// 獲取偵錯分頁詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            // 自我檢查按鈕，加上狀態列 —— 失敗時會多帶一段展平後的例外鏈，需要更多空間。
            float statusHeight;
            if (string.IsNullOrEmpty(schemaSelfTestStatus))
            {
                statusHeight = 0f;
            }
            else if (schemaSelfTestStatus.Contains("\n"))
            {
                statusHeight = 150f;
            }
            else
            {
                statusHeight = 48f;
            }
            return 690f + 42f + statusHeight;
        }

        /// <summary>
        /// 繪製偵錯設定與日誌。
        /// </summary>
        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        public static void DrawDebugSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_TitleDebugExplanation".Translate());
            listing.Gap(8f);

            // Token 與費用累計統計看板
            listing.Label("<b>" + "RimLLM_UsageHeader".Translate() + "</b>");
            Rect usageRect = listing.GetRect(50f);
            Widgets.DrawMenuSection(usageRect);

            Rect usageInfoRect = new Rect(usageRect.x + 8f, usageRect.y + 4f, usageRect.width - 160f, usageRect.height - 8f);
            Rect resetUsageBtnRect = new Rect(usageRect.xMax - 150f, usageRect.y + 10f, 140f, 30f);

            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                string usageText = "RimLLM_UsageInfo".Translate(
                    Settings.TotalPromptTokens.ToString("N0"),
                    Settings.TotalCompletionTokens.ToString("N0"),
                    Settings.TotalEstimatedCost.ToString("F4")
                );
                Widgets.Label(usageInfoRect, usageText);
            }

            if (RimLLMProvider.TryGetManager(out var costManager) &&
                costManager.UsageTracker.UnpricedUsageCount > 0)
            {
                listing.Label("RimLLM_UnpricedUsageNotice".Translate(costManager.UsageTracker.UnpricedUsageCount));
            }

            if (Widgets.ButtonText(resetUsageBtnRect, "RimLLM_ResetUsageBtn".Translate()) &&
                RimLLMProvider.TryGetManager(out var usageManager))
            {
                usageManager.ResetUsage();
                Messages.Message("RimLLM_MsgUsageReset".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            listing.Gap(10f);

            // 詳細日誌
            bool detailedLogging = Settings.DetailedLogging;
            bool prevDetailedLogging = detailedLogging;
            listing.CheckboxLabeled("RimLLM_DetailedLogging".Translate(), ref detailedLogging, "RimLLM_DetailedLoggingExplanation".Translate());
            Settings.DetailedLogging = detailedLogging;
            RimLLMLog.Enabled = detailedLogging;
            if (prevDetailedLogging != detailedLogging)
            {
                Settings.Write();
            }
            listing.Gap(8f);

            // 匯出診斷按鈕
            Rect exportRect = listing.GetRect(30f);
            if (Widgets.ButtonText(exportRect, "RimLLM_ExportDiag".Translate()))
            {
                ExportDiagnostics();
            }
            listing.Gap(8f);

            // 結構化輸出自我檢查
            Rect schemaTestRect = listing.GetRect(30f);
            if (Widgets.ButtonText(schemaTestRect, "RimLLM_SchemaSelfTest".Translate()) && !schemaSelfTestRunning)
            {
                RunSchemaSelfTest();
            }
            if (!string.IsNullOrEmpty(schemaSelfTestStatus))
            {
                listing.Gap(4f);
                listing.Label(schemaSelfTestStatus);
            }
            listing.Gap(12f);

            // 最近請求紀錄日誌
            Rect headerRect = listing.GetRect(30f);
            Rect labelRect = new Rect(headerRect.x, headerRect.y, headerRect.width - 150f, headerRect.height);
            Rect clearBtnRect = new Rect(headerRect.x + headerRect.width - 140f, headerRect.y + 2f, 140f, headerRect.height - 4f);

            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
#pragma warning disable S1192, S3358 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
            {
                Widgets.Label(labelRect, "<b>" + "RimLLM_RecentRequests".Translate(30) + "</b>");
            }

            if (Widgets.ButtonText(clearBtnRect, "RimLLM_ClearRequestsBtn".Translate()) &&
                RimLLMProvider.TryGetManager(out var logManager))
            {
                logManager.ClearLogs();
                Messages.Message("RimLLM_MsgLogsCleared".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            listing.Gap(4f);

            // manager 尚未初始化時（載入順序異常、或框架自身載入失敗）不能整段不畫 ——
            // 那會讓面板變成一片空白，看起來像「請求沒有被記錄」。改為退回讀持久化的遙測資料。
            bool managerAvailable = RimLLMProvider.TryGetManager(out var manager);
            List<RimLLMManager.RequestLogEntry> logs = managerAvailable
                ? new List<RimLLMManager.RequestLogEntry>(manager.RequestLogs)
                : new List<RimLLMManager.RequestLogEntry>();

            // 面板為空時，光看畫面分不出是「沒有紀錄」還是「讀錯來源」，因此把兩邊的筆數直接標出來。
            int persistedCount = Settings.RequestLogs != null ? Settings.RequestLogs.Count : 0;
            Text.Font = GameFont.Tiny;
            listing.Label("RimLLM_RequestsSourceCounts".Translate(
                managerAvailable ? "OK" : "NULL", logs.Count, persistedCount));
            Text.Font = GameFont.Small;

            // 記憶體佇列是空的（核心未初始化，或這個 session 還沒發過請求）就退回已保存的紀錄，
            // 免得明明跑過請求、面板卻什麼都不顯示。
            if (logs.Count == 0 && Settings.RequestLogs != null && Settings.RequestLogs.Count > 0)
            {
                logs = new List<RimLLMManager.RequestLogEntry>(Settings.RequestLogs);
                listing.Label("RimLLM_RequestsFromTelemetry".Translate());
            }

            if (logs.Count == 0)
            {
                listing.Label("RimLLM_NoRequests".Translate());
                return;
            }

            List<string> logLines = ResolveLogLines(logs);

            Rect logScrollRect = listing.GetRect(420f);
            Widgets.DrawMenuSection(logScrollRect);

            float contentWidth = logScrollRect.width - 16f;
            float logHeight = 24f;
            float viewHeight = Math.Max(420f, logLines.Count * logHeight + 10f);
            Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

            // 清除歷史後內容會從 30 筆縮回幾筆，捲動位置若留在原處，僅存的那幾列會被畫到可視範圍之外，
            // 看起來就像「請求沒有被記錄」。內容變短時必須把捲動位置一起收回來。
            float maxScrollY = Math.Max(0f, viewHeight - logScrollRect.height);
            if (debugScrollPosition.y > maxScrollY)
            {
                debugScrollPosition.y = maxScrollY;
            }

            Widgets.BeginScrollView(logScrollRect, ref debugScrollPosition, viewRect);

            Text.Font = GameFont.Tiny;
            for (int i = 0; i < logLines.Count; i++)
            {
                Rect lineRect = new Rect(4f, i * logHeight + 4f, contentWidth - 8f, logHeight - 2f);
                Widgets.Label(lineRect, logLines[i]);
            }
            Text.Font = GameFont.Small;
            Widgets.EndScrollView();
        }
        #pragma warning restore S3776

        /// <summary>上一次格式化所依據的日誌快照，與格式化後的顯示列一一對應。</summary>
        private static readonly List<RimLLMManager.RequestLogEntry> _cachedLogSnapshot = new List<RimLLMManager.RequestLogEntry>();
        private static readonly List<string> _cachedLogLines = new List<string>();

        /// <summary>
        /// 把日誌轉成顯示列。每列要做翻譯、敏感字遮罩（Regex）與時間格式化，而日誌只在有請求完成時
        /// 才變動；設定視窗開著時每幀重算 30 列純屬浪費，因此以「同一組項目參考」判定命中，
        /// 只有佇列內容真的變了才重新格式化。
        /// </summary>
        private static List<string> ResolveLogLines(List<RimLLMManager.RequestLogEntry> logs)
        {
            logs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            bool same = logs.Count == _cachedLogSnapshot.Count;
            for (int i = 0; same && i < logs.Count; i++)
            {
                same = ReferenceEquals(logs[i], _cachedLogSnapshot[i]);
            }
            if (same) return _cachedLogLines;

            _cachedLogSnapshot.Clear();
            _cachedLogSnapshot.AddRange(logs);
            _cachedLogLines.Clear();
            foreach (var log in logs)
            {
                string timeStr = log.Timestamp.ToString("HH:mm:ss");
                // 色碼保留在程式碼中，只有文字部分抽成翻譯鍵，避免譯者需要處理富文字標記。
                string statusText = log.Success
                    ? $"<color=#22c55e>{"RimLLM_StatusRequestSuccess".Translate(log.LatencyMs)}</color>"
                    : $"<color=#ef4444>{"RimLLM_StatusRequestFailed".Translate(RimLLMLog.SanitizeForLog(log.ErrorMessage, 160))}</color>";
                // token 拿不到（失敗、或供應商未回報用量）時省略，避免 0 誤導。
                int totalTokens = log.PromptTokens + log.CompletionTokens;
                string tokenText = totalTokens > 0 ? $" | {totalTokens:N0} tok" : string.Empty;

                _cachedLogLines.Add($"[{timeStr}] Mod: {log.ModId} | {log.Provider} ({log.Model}) | {statusText}{tokenText}");
            }
            return _cachedLogLines;
        }

        /// <summary>
        /// 結構化輸出自我檢查。分兩段：
        ///
        /// 第一段在本機完成，不發網路請求也不花錢 —— 產生 schema，藉此確認
        /// <c>System.Text.Json</c> 的 <c>JsonSchemaExporter</c> 在 RimWorld 的 Mono 執行環境可用。
        /// 這件事單元測試驗不到（測試跑在真正的 .NET Framework 上）。
        ///
        /// 第二段才真的向目前的 fallback 鏈發一次結構化請求，驗證供應商確實接受這份 schema。
        /// </summary>
        private static void RunSchemaSelfTest()
        {
            schemaSelfTestRunning = true;
            schemaSelfTestStatus = "RimLLM_SchemaSelfTestRunning".Translate();

            RimLLMSchemaResult schema;
            try
            {
                schema = RimLLMSchemaBuilder.Build(typeof(SchemaSelfTestPayload));
            }
            catch (Exception exception)
            {
                schemaSelfTestRunning = false;
                schemaSelfTestStatus = "<color=#ef4444>" +
                    "RimLLM_SchemaSelfTestBuildFailed".Translate(RimLLMLog.SanitizeForLog(exception.Message, 200)) +
                    "</color>";
                return;
            }

            Log.Message("[RimLLM] Schema self-test: " + schema.Json);

            schemaSelfTestStatus = "RimLLM_SchemaSelfTestLocalOk".Translate();

            Task.Run(async () =>
            {
                string result;
                try
                {
                    IChatClient client = RimLLMProvider.CreateChatClient(ServiceConsumerList.SchemaSelfTestModId);
                    SchemaSelfTestPayload payload = await client.GetResponseObjectAsync<SchemaSelfTestPayload>(
                        new List<ChatMessage>
                        {
                            new ChatMessage(
                                ChatRole.User,
                                "Reply with a short structured self-test record. " +
                                "Summary: any single sentence. Score: any integer 0-100. " +
                                "OptionalScore: may be null. Tags: two or three short words.")
                        }).ConfigureAwait(false);

                    result = "<color=#22c55e>" + "RimLLM_SchemaSelfTestOk".Translate(
                        RimLLMLog.SanitizeForLog(payload?.Summary ?? string.Empty, 80),
                        (payload?.Score ?? 0).ToString()) + "</color>";
                }
                catch (Exception exception)
                {
                    result = "<color=#ef4444>" + "RimLLM_SchemaSelfTestRemoteFailed".Translate(
                        RimLLMLog.SanitizeForLog(exception.Message, 200)) + "</color>";
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    schemaSelfTestStatus = result;
                    schemaSelfTestRunning = false;
                });
            });
        }

        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        private static void ExportDiagnostics()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== RimLLM Framework Diagnostics ===");
                sb.AppendLine($"Export Time: {DateTime.Now}");
                sb.AppendLine($"OS: {SystemInfo.operatingSystem}");
                string maskedDeviceId;
                try
                {
                    string rawId = SystemInfo.deviceUniqueIdentifier;
                    if (!string.IsNullOrEmpty(rawId) && rawId.Length > 8)
                    {
                        maskedDeviceId = rawId.Substring(0, 4) + "****" + rawId.Substring(rawId.Length - 4);
                    }
                    else
                    {
                        maskedDeviceId = rawId;
                    }
                }
                catch
                {
                    maskedDeviceId = "Unknown";
                }
                sb.AppendLine($"Device ID: {maskedDeviceId}");
                sb.AppendLine($"Max Concurrent Requests Setting: {Settings.MaxConcurrentRequests}");
                sb.AppendLine($"Timeout Setting: {Settings.ApiTimeout}s");
                sb.AppendLine($"Max Retries Setting: {Settings.MaxRetries}");
                sb.AppendLine($"Retry Delay Setting: {Settings.RetryDelay}s");
                sb.AppendLine($"Detailed Logging: {Settings.DetailedLogging}");
                sb.AppendLine();

                sb.AppendLine("=== Fallback Chain ===");
                var fallbackChain = Settings.FallbackChain;
                for (int i = 0; i < fallbackChain.Count; i++)
                {
                    sb.AppendLine($"  {i + 1}. {fallbackChain[i]}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Provider Setup ===");
                List<string> providers = RimLLMProvider.TryGetManager(out var providerManager)
                    ? providerManager.GetRegisteredProviderIds()
                    : new List<string>(ProviderIds.BuiltIn);
                foreach (var prov in providers)
                {
                    bool enabled = Settings.IsProviderEnabled(prov);
                    bool hasKey = !string.IsNullOrEmpty(Settings.GetApiKey(prov));
                    string endpoint = Settings.GetEndpoint(prov, "default");
                    sb.AppendLine($"  {prov}: Enabled={enabled}, HasKey={hasKey}, Endpoint={MaskEndpoint(endpoint)}");
                    var models = Settings.GetModelList(prov);
                    string modelPreview = models.Count > 20
                        ? string.Join(", ", models.GetRange(0, 20)) + $", ... ({models.Count - 20} more)"
                        : string.Join(", ", models);
                    sb.AppendLine($"    Cached Models ({models.Count}): {RimLLMLog.SanitizeForLog(modelPreview, 1000)}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Recent Request Logs ===");
                if (RimLLMProvider.TryGetManager(out var logManager))
                {
                    var logs = logManager.RequestLogs.ToArray();
                    if (logs.Length == 0)
                    {
                        sb.AppendLine("  No requests recorded in this session.");
                    }
                    else
                    {
                        foreach (var log in logs)
                        {
                            string status = log.Success ? "SUCCESS" : $"FAILED ({RimLLMLog.SanitizeForLog(log.ErrorMessage, 200)})";
                            int totalTokens = log.PromptTokens + log.CompletionTokens;
                            string tokenInfo = totalTokens > 0 ? $" | Tokens: {totalTokens:N0} (prompt {log.PromptTokens:N0}, completion {log.CompletionTokens:N0})" : "";
                            sb.AppendLine($"  [{log.Timestamp:yyyy-MM-dd HH:mm:ss}] Mod: {log.ModId} | Provider: {log.Provider} ({log.Model}) | {status} | Latency: {log.LatencyMs}ms{tokenInfo}");
                        }
                    }
                }
                string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimLLM_Diagnostic.txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Messages.Message("RimLLM_ExportDiagSuccess".Translate(path), MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception ex)
            {
                Messages.Message("RimLLM_ExportDiagFailed".Translate(RimLLMLog.SanitizeForLog(ex.Message, 200)), MessageTypeDefOf.RejectInput, false);
            }
        }
        #pragma warning restore S3776

        private static string MaskEndpoint(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return endpoint;
            try
            {
                var uri = new Uri(endpoint);
                string port = uri.IsDefaultPort ? "" : $":{uri.Port}";
                return $"{uri.Scheme}://{uri.Host}{port}/...";
            }
            catch
            {
                return RimLLMLog.SanitizeForLog(endpoint, 120);
            }
        }
    }
#pragma warning restore S1192, S3358
}
