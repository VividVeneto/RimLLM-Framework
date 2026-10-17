using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class SettingsAndTelemetryTests
{
    private string _encryptionKeyDirectory;

    [SetUp]
    public void SetUpEncryptionKeyStore()
    {
        _encryptionKeyDirectory = Path.Combine(
            Path.GetTempPath(),
            "RimLLMSettingsEncryptionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_encryptionKeyDirectory);
        EncryptionUtility.ResetSecureKeyForTests();
        EncryptionUtility.SecureKeyPathResolver = () =>
            Path.Combine(_encryptionKeyDirectory, "RimLLM_EncryptionKey.dat");
    }

    [TearDown]
    public void TearDownEncryptionKeyStore()
    {
        EncryptionUtility.ResetSecureKeyForTests();
        try
        {
            Directory.Delete(_encryptionKeyDirectory, true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 預設思考強度的存檔往返。先前載入端把「&gt; 3 即無效」寫死，
    /// 而 MEAI 10.10 的列舉是 None=0…ExtraHigh=4：High 存成 4 後每次載入都變回 Auto。
    /// </summary>
    [Test]
    public void DefaultReasoningEffort_RoundTripsThroughSettingsEncoding()
    {
        foreach (ReasoningEffort effort in Enum.GetValues(typeof(ReasoningEffort)))
        {
            int encoded = RimLLMFrameworkSettings.EncodeReasoningEffort(effort);
            ClassicAssert.AreEqual(effort, RimLLMFrameworkSettings.DecodeReasoningEffort(encoded),
                $"{effort} 存檔後必須原樣讀回");
        }

        ClassicAssert.AreEqual(0, RimLLMFrameworkSettings.EncodeReasoningEffort(null));
        ClassicAssert.IsNull(RimLLMFrameworkSettings.DecodeReasoningEffort(0), "0 代表 Auto");
        ClassicAssert.IsNull(RimLLMFrameworkSettings.DecodeReasoningEffort(-1));
        ClassicAssert.IsNull(RimLLMFrameworkSettings.DecodeReasoningEffort(99), "未知值退回 Auto 而非擲例外");

        // 與既有設定檔相容：舊版以同一套 +1 編碼寫入的 Low／Medium 讀回不變。
        ClassicAssert.AreEqual(ReasoningEffort.Low, RimLLMFrameworkSettings.DecodeReasoningEffort(2));
        ClassicAssert.AreEqual(ReasoningEffort.Medium, RimLLMFrameworkSettings.DecodeReasoningEffort(3));
        ClassicAssert.AreEqual(ReasoningEffort.High, RimLLMFrameworkSettings.DecodeReasoningEffort(4));
    }

    [Test]
    public void BudgetPolicy_StoredValuesOutsideTheSupportedRangeFallBackToHardBlock()
    {
        // 現存策略必須原樣讀回
        for (int i = 0; i <= RimLLMFrameworkSettings.MaxBudgetPolicy; i++)
        {
            ClassicAssert.AreEqual(i, RimLLMFrameworkSettings.SanitizeBudgetPolicy(i),
                $"策略 {i} 是現存選項，不得被改寫");
        }

        // 已移除的 FallbackToFree(2) / DialogPrompt(3) 與負值、未知值一律退回 HardBlock(0)：
        // 要不要繼續燒 token 是使用者自己的決定，升級時不得替他決定。
        foreach (int stale in new[] { 2, 3, 99, -1 })
        {
            ClassicAssert.AreEqual(0, RimLLMFrameworkSettings.SanitizeBudgetPolicy(stale),
                $"已移除或未知的策略 {stale} 必須退回 HardBlock");
        }

        // 舊值 1 (SilentMocking) 仍在範圍內，讀回後等同 WarnOnly：同樣是預期中的行為變更。
    }

    /// <summary>
    /// 存檔路徑的加密失敗必須被吞掉：ExposeData 在 Scribe 存檔中途執行，
    /// 例外一旦冒出去，備援鏈、端點與所有開關都會跟著存不下來。
    /// </summary>
    [Test]
    public void TryEncryptForSave_SwallowsEncryptionFailure_InsteadOfThrowingThroughScribe()
    {
        ClassicAssert.IsTrue(RimLLMFrameworkSettings.TryEncryptForSave("sk-secret", out string cipher));
        ClassicAssert.AreEqual("sk-secret", EncryptionUtility.Decrypt(cipher));

        // 讓安全金鑰無法載入：Encrypt 會擲 RimLLMException，存檔路徑要回 false 而不是往外丟。
        EncryptionUtility.ResetSecureKeyForTests();
        EncryptionUtility.SecureKeyPathResolver = () => null;
        try
        {
            ClassicAssert.IsFalse(RimLLMFrameworkSettings.TryEncryptForSave("sk-secret", out string failed));
            ClassicAssert.IsNull(failed);
        }
        finally
        {
            EncryptionUtility.ResetSecureKeyForTests();
            EncryptionUtility.SecureKeyPathResolver = () =>
                Path.Combine(_encryptionKeyDirectory, "RimLLM_EncryptionKey.dat");
        }
    }

    [Test]
    public void TestClearLogs()
    {
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        var entry = new RimLLMManager.RequestLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ModId = "test-mod",
            Provider = "OpenAI",
            Model = "gpt-4",
            Success = true,
            LatencyMs = 150
        };
        manager.RequestLogs.Enqueue(entry);
        ClassicAssert.AreEqual(1, manager.RequestLogs.Count);

        manager.ClearLogs();
        ClassicAssert.AreEqual(0, manager.RequestLogs.Count);
    }

    [Test]
    public void TestMultipleApiKeysRoundRobin()
    {
        var settings = new RimLLMFrameworkSettings();
        settings.SetApiKey("TestProvider", "key-a, key-b; key-c");

        // 驗證輪詢邏輯 (多個以逗號或分號分隔的 key 會循環回傳)
        ClassicAssert.AreEqual("key-a", settings.GetActiveApiKey("TestProvider"));
        ClassicAssert.AreEqual("key-b", settings.GetActiveApiKey("TestProvider"));
        ClassicAssert.AreEqual("key-c", settings.GetActiveApiKey("TestProvider"));
        ClassicAssert.AreEqual("key-a", settings.GetActiveApiKey("TestProvider")); // 繞回第一個金鑰
    }

    [Test]
    public void ModelCountAndDefaultModelReadWithoutCopyingList()
    {
        var settings = new RimLLMFrameworkSettings();
        int versionBefore = settings.ModelListVersion;

        ClassicAssert.AreEqual(0, settings.GetModelCount("OpenRouter"));
        ClassicAssert.AreEqual("fallback", settings.GetDefaultModel("OpenRouter", "fallback"));

        settings.SetModelList("OpenRouter", ["first", "second"]);

        ClassicAssert.AreEqual(2, settings.GetModelCount("OpenRouter"));
        ClassicAssert.AreEqual("first", settings.GetDefaultModel("OpenRouter", "fallback"));
        ClassicAssert.Greater(settings.ModelListVersion, versionBefore, "清單變動必須推進版本號，設定頁的過濾快取靠它失效。");
    }

    [Test]
    public void TestSettingsDefaultRoutingStrategy()
    {
        var settings = new RimLLMFrameworkSettings();
        ClassicAssert.AreEqual(2, settings.RoutingStrategy);
    }

    [Test]
    public void TestTokenUsageAndCostRecording()
    {
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        // 1. 初始狀態應該是 0
        ClassicAssert.AreEqual(0, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(0, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(0f, mockSettings.TotalEstimatedCost);

        // 2. 未知或未維護精確費率的模型只累計 tokens，金額估算為 0。
        manager.RecordUsage("CustomProvider", "custom-unlisted-model", 100000, 50000);

        ClassicAssert.AreEqual(100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(50000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(0f, mockSettings.TotalEstimatedCost);
        // 查無費率的請求費用未知，但 token 照計入每日預算。
        ClassicAssert.AreEqual(150000, mockSettings.DailyAccumulatedTokens);

        // 3. 已知精確費率模型累計估算金額。
        manager.RecordUsage("Gemini", "gemini-2.5-flash", 1000000, 1000000);

        ClassicAssert.AreEqual(1100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(1050000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(2.80f, mockSettings.TotalEstimatedCost, 0.0001f);
        ClassicAssert.AreEqual(2150000, mockSettings.DailyAccumulatedTokens);

        // 4. Gemini 模型若帶官方 models/ 前綴也能正規化。
        manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 1000000);

        ClassicAssert.AreEqual(2100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(2050000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(5.60f, mockSettings.TotalEstimatedCost, 0.0001f);

        manager.RecordUsage("Gemini", "gemini-3.5-flash", 1000000, 1000000);

        ClassicAssert.AreEqual(3100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(3050000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(16.10f, mockSettings.TotalEstimatedCost, 0.0001f);

        manager.RecordUsage("DeepSeek", "deepseek-v4-flash", 1000000, 1000000);
        ClassicAssert.AreEqual(4100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(4050000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(16.52f, mockSettings.TotalEstimatedCost, 0.0001f);

        manager.RecordUsage("Groq", "llama-3.3-70b-versatile", 1000000, 1000000);
        ClassicAssert.AreEqual(5100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(5050000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(17.90f, mockSettings.TotalEstimatedCost, 0.0001f);

        manager.RecordUsage("MiniMax", "MiniMax-M3", 1000000, 1000000);
        ClassicAssert.AreEqual(6100000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(6050000, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(19.40f, mockSettings.TotalEstimatedCost, 0.0001f);
    }

    [Test]
    public void TestCachedTokenDiscountReducesCost()
    {
        // 快取命中的輸入 Token 應以折扣費率計價，藉此反映 Context Caching 的節省。
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        // Gemini：輸入價 $0.3/M，cache read 折扣 0.25x。
        // 1,000,000 輸入中有 800,000 為快取命中 → 200,000*0.3 + 800,000*0.3*0.25 = $0.06 + $0.06 = $0.12
        manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 800000);
        ClassicAssert.AreEqual(1000000, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(0.12f, mockSettings.TotalEstimatedCost, 0.0001f);

        // 對照組：相同輸入但完全無快取應為 $0.30。
        mockSettings.TotalEstimatedCost = 0f;
        manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 0);
        ClassicAssert.AreEqual(0.30f, mockSettings.TotalEstimatedCost, 0.0001f);

        // 防呆：cachedPromptTokens 超過 promptTokens 時應被夾到上限，不會出現負值或溢出。
        mockSettings.TotalEstimatedCost = 0f;
        manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 5000000);
        // 全部視為快取命中：1,000,000 * 0.30/M * 0.25 = $0.075
        ClassicAssert.AreEqual(0.075f, mockSettings.TotalEstimatedCost, 0.0001f);

        mockSettings.TotalEstimatedCost = 0f;
        manager.RecordUsage("DeepSeek", "deepseek-v4-flash", 1000000, 0, 1000000);
        ClassicAssert.AreEqual(0.0028f, mockSettings.TotalEstimatedCost, 0.0001f);
    }

    [Test]
    public void TestResetUsage()
    {
        var mockSettings = new MockSettings();
        var manager = new RimLLMManager(mockSettings);

        // 1. 設置一些初始使用量
        mockSettings.TotalPromptTokens = 5000;
        mockSettings.TotalCompletionTokens = 3000;
        mockSettings.TotalEstimatedCost = 0.05f;

        // 2. 執行重置
        manager.ResetUsage();

        // 3. 應該歸零
        ClassicAssert.AreEqual(0, mockSettings.TotalPromptTokens);
        ClassicAssert.AreEqual(0, mockSettings.TotalCompletionTokens);
        ClassicAssert.AreEqual(0f, mockSettings.TotalEstimatedCost);
    }

    [Test]
    public void TestBudgetReset()
    {
        var mockSettings = new MockSettings();
        var tracker = new RimLLMUsageTracker(mockSettings);

        // 設置非今日重置日期
        mockSettings.DailyBudgetResetDate = "2026-01-01";
        mockSettings.DailyAccumulatedCost = 5.5f;
        mockSettings.DailyAccumulatedTokens = 5500;

        // 觸發重置
        tracker.CheckDailyReset();

        string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        ClassicAssert.AreEqual(todayStr, mockSettings.DailyBudgetResetDate);
        ClassicAssert.AreEqual(0f, mockSettings.DailyAccumulatedCost);
        ClassicAssert.AreEqual(0, mockSettings.DailyAccumulatedTokens);
    }

    [Test]
    public void TestThrottlingAntiAbuse()
    {
        var mockSettings = new MockSettings
        {
            EnableAntiAbuse = true,
            MaxRequestsPerWindow = 3,
            ThrottlingWindowSeconds = 5,
            CoolDownDurationSeconds = 10,
            FallbackChain = ["MockSuccess:model-z"]
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.abuse.mod";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        // 呼叫 3 次應該都成功
        ClassicAssert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
        ClassicAssert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
        ClassicAssert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);

        // 第 4 次呼叫超出頻率限制，預期觸發 RateLimit 錯誤
        var ex = Assert.Throws<RimLLMException>(() =>
        {
            client.GetResponseAsync(messages).GetAwaiter().GetResult();
        });
        ClassicAssert.AreEqual(LLMError.RateLimit, ex.Error);
    }

    [Test]
    public void TestBudgetPolicyHardBlock()
    {
        var mockSettings = new MockSettings
        {
            DailyTokenBudgetLimit = 100000,
            DailyAccumulatedTokens = 120000,
            DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
            BudgetPolicy = 0, // HardBlock
            FallbackChain = ["MockSuccess:model-z"]
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.budget.block.mod";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };

        var ex = Assert.Throws<RimLLMException>(() =>
        {
            client.GetResponseAsync(messages).GetAwaiter().GetResult();
        });
        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex.Error);
    }

    [Test]
    public void TestBudgetPolicyWarnOnly()
    {
        var mockSettings = new MockSettings
        {
            DailyTokenBudgetLimit = 100000,
            DailyAccumulatedTokens = 120000,
            DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
            BudgetPolicy = 1, // WarnOnly
            FallbackChain = ["MockSuccess:model-z"]
        };
        mockSettings.EnabledProviders["MockSuccess"] = true;
        mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

        var manager = new RimLLMManager(mockSettings);
        var mockSuccess = new MockTestProvider
        {
            ProviderId = "MockSuccess",
            GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
        };
        manager.RegisterProvider(mockSuccess);

        const string modId = "test.budget.warnonly.mod";
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient(modId);

        // 超限只警告，請求照樣送到供應商
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        string resText = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;
        ClassicAssert.AreEqual("ok", resText);
    }

    [Test]
    public void TestTelemetrySaveIsAtomicAndEncryptsChatHistory()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "telemetry.json");

        var previousResolver = RimLLMTelemetryStore.FilePathResolver;
        RimLLMTelemetryStore.FilePathResolver = () => path;
        try
        {
            var store = new RimLLMTelemetryStore();
            store.ChatHistory.Add("SENTINEL-CONVERSATION-CONTENT");
            store.Save();

            ClassicAssert.IsFalse(System.IO.File.Exists(path + ".tmp"),
                "遙測寫入必須先寫暫存檔再原子替換，不得留下 .tmp");

            string raw = System.IO.File.ReadAllText(path);
            ClassicAssert.IsFalse(raw.Contains("SENTINEL-CONVERSATION-CONTENT"),
                "對話歷史不得以明文寫入遙測檔");

            var reloaded = new RimLLMTelemetryStore();
            reloaded.Load();
            CollectionAssert.Contains(reloaded.ChatHistory, "SENTINEL-CONVERSATION-CONTENT");
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = previousResolver;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public void TestTelemetryKeepsEncryptedHistoryWhenProtectedKeyIsUnavailable()
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "RimLLMTelemetryKeyMigrationTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "telemetry.json");
        string originalKeyPath = System.IO.Path.Combine(dir, "original-key.dat");
        string replacementKeyPath = System.IO.Path.Combine(dir, "replacement-key.dat");

        var previousResolver = RimLLMTelemetryStore.FilePathResolver;
        var previousKeyResolver = EncryptionUtility.SecureKeyPathResolver;
        try
        {
            EncryptionUtility.ResetSecureKeyForTests();
            EncryptionUtility.SecureKeyPathResolver = () => originalKeyPath;
            RimLLMTelemetryStore.FilePathResolver = () => path;

            var store = new RimLLMTelemetryStore();
            store.ChatHistory.Add("PROTECTED-HISTORY");
            store.Save();

            string originalCipher =
                System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path))
                    .RootElement.GetProperty("EncryptedChatHistory").GetString();
            ClassicAssert.IsNotEmpty(originalCipher);

            EncryptionUtility.ResetSecureKeyForTests();
            EncryptionUtility.SecureKeyPathResolver = () => replacementKeyPath;
            var reloaded = new RimLLMTelemetryStore();
            reloaded.Load();
            ClassicAssert.IsEmpty(reloaded.ChatHistory,
                "沒有原本的 per-user key 時不能把無法解密的歷史當成可用明文");

            reloaded.TotalPromptTokens = 42;
            reloaded.Save();

            string preservedCipher =
                System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path))
                    .RootElement.GetProperty("EncryptedChatHistory").GetString();
            ClassicAssert.AreEqual(originalCipher, preservedCipher,
                "更新其他遙測欄位時必須保留無法解密的原始密文");

            reloaded.ClearChatHistory();
            reloaded.Save();
            EncryptionUtility.ResetSecureKeyForTests();
            EncryptionUtility.SecureKeyPathResolver = () => originalKeyPath;
            var cleared = new RimLLMTelemetryStore();
            cleared.Load();
            ClassicAssert.IsEmpty(cleared.ChatHistory,
                "使用者明確清空歷史時，不能再恢復先前無法解密的密文");
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = previousResolver;
            EncryptionUtility.ResetSecureKeyForTests();
            EncryptionUtility.SecureKeyPathResolver = previousKeyResolver;
            try
            {
                System.IO.Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

    [Test]
    public void TestTelemetryLoadRecoversFromCorruptFileUsingBackup()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "telemetry.json");

        var previousResolver = RimLLMTelemetryStore.FilePathResolver;
        RimLLMTelemetryStore.FilePathResolver = () => path;
        try
        {
            // 先寫一份有效檔，再寫第二次讓第一份成為 .bak，最後把主檔弄壞。
            var store = new RimLLMTelemetryStore { TotalPromptTokens = 1234 };
            store.Save();
            store.TotalPromptTokens = 5678;
            store.Save();
            System.IO.File.WriteAllText(path, "{ this is not valid json");

            var reloaded = new RimLLMTelemetryStore();
            reloaded.Load();

            ClassicAssert.AreEqual(1234, reloaded.TotalPromptTokens,
                "主檔損毀時應改由備份檔還原");
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = previousResolver;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public void TestLegacyPlaintextChatHistoryIsMigratedOnSave()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "telemetry.json");

        var previousResolver = RimLLMTelemetryStore.FilePathResolver;
        RimLLMTelemetryStore.FilePathResolver = () => path;
        try
        {
            // 模擬舊版明文格式
            System.IO.File.WriteAllText(path,
                "{\"ChatHistory\":[\"LEGACY-PLAINTEXT-LINE\"],\"TotalPromptTokens\":7}");

            var store = new RimLLMTelemetryStore();
            store.Load();
            CollectionAssert.Contains(store.ChatHistory, "LEGACY-PLAINTEXT-LINE");
            ClassicAssert.IsTrue(store.IsDirty, "讀到舊版明文歷史後應標記為待重寫以完成加密遷移");

            store.Save();
            string raw = System.IO.File.ReadAllText(path);
            ClassicAssert.IsFalse(raw.Contains("LEGACY-PLAINTEXT-LINE"),
                "遷移後舊版明文歷史必須從磁碟消失");
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = previousResolver;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public void TestProviderStatisticsTracking()
    {
        var mockSettings = new MockSettings();
        var tracker = new RimLLMUsageTracker(mockSettings);

        // 1. 記錄 2 次成功與 1 次失敗
        tracker.RecordLog(new RimLLMManager.RequestLogEntry { Timestamp = DateTime.UtcNow, ModId = "mod", Provider = "Gemini", Model = "gemini-model", Success = true, ErrorMessage = "", LatencyMs = 100 });
        tracker.RecordLog(new RimLLMManager.RequestLogEntry { Timestamp = DateTime.UtcNow, ModId = "mod", Provider = "Gemini", Model = "gemini-model", Success = true, ErrorMessage = "", LatencyMs = 100 });
        tracker.RecordLog(new RimLLMManager.RequestLogEntry { Timestamp = DateTime.UtcNow, ModId = "mod", Provider = "Gemini", Model = "gemini-model", Success = false, ErrorMessage = "Error", LatencyMs = 100 });

        ClassicAssert.IsTrue(tracker.ProviderStatistics.TryGetValue("Gemini", out var stats));
        ClassicAssert.AreEqual(3, stats.TotalCount);
        ClassicAssert.AreEqual(2, stats.SuccessCount);
        ClassicAssert.AreEqual(1, stats.FailureCount);
        ClassicAssert.AreEqual(2.0f / 3.0f, stats.SuccessRate, 0.001f);

        // 2. 清空日誌後應清空統計
        tracker.ClearLogs();
        ClassicAssert.AreEqual(0, tracker.ProviderStatistics.Count);
    }

    [Test]
    public void TestRecordLogStoresTokenCounts()
    {
        var mockSettings = new MockSettings();
        var tracker = new RimLLMUsageTracker(mockSettings);

        tracker.RecordLog(new RimLLMManager.RequestLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ModId = "mod",
            Provider = "Gemini",
            Model = "gemini-model",
            Success = true,
            ErrorMessage = "",
            LatencyMs = 100,
            PromptTokens = 1200,
            CompletionTokens = 300
        });

        ClassicAssert.AreEqual(1, tracker.RequestLogs.Count);
        Assert.IsTrue(tracker.RequestLogs.TryPeek(out var entry));
        ClassicAssert.AreEqual(1200, entry.PromptTokens);
        ClassicAssert.AreEqual(300, entry.CompletionTokens);

        // 舊呼叫（不帶 token）預設為 0，顯示層會省略。
        tracker.RecordLog(new RimLLMManager.RequestLogEntry
        {
            Timestamp = DateTime.UtcNow,
            ModId = "mod",
            Provider = "Gemini",
            Model = "gemini-model",
            Success = false,
            ErrorMessage = "Error",
            LatencyMs = 100
        });
        Assert.IsTrue(tracker.RequestLogs.TryPeek(out _));
        var all = tracker.RequestLogs.ToArray();
        ClassicAssert.AreEqual(0, all[all.Length - 1].PromptTokens);
        ClassicAssert.AreEqual(0, all[all.Length - 1].CompletionTokens);
    }

    /// <summary>
    /// 上一個 session 的請求歷史必須在重啟後回到記憶體佇列裡。
    /// 沒有這條，Debug 面板重開遊戲就一片空白，而且第一次 RecordLog 會把舊紀錄整份蓋掉。
    /// </summary>
    [Test]
    public void TestUsageTrackerRestoresPersistedRequestLogs()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "telemetry.json");

        var previousResolver = RimLLMTelemetryStore.FilePathResolver;
        RimLLMTelemetryStore.FilePathResolver = () => path;
        try
        {
            var seed = new RimLLMTelemetryStore();
            seed.RequestLogs.Add(new RimLLMManager.RequestLogEntry
            {
                Timestamp = DateTime.Now,
                ModId = "seed.mod",
                Provider = "OpenRouter",
                Model = "model-a",
                Success = true,
                LatencyMs = 1234
            });
            seed.Save();

            var settings = new RimLLMFrameworkSettings();
            ClassicAssert.AreEqual(1, settings.RequestLogs.Count, "設定應從遙測檔載回既有請求歷史");

            var tracker = new RimLLMUsageTracker(settings);
            ClassicAssert.AreEqual(1, tracker.RequestLogs.Count, "UsageTracker 應把已保存的請求歷史放回記憶體佇列");
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = previousResolver;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Newtonsoft 時代寫入磁碟的遙測檔，STJ 必須原樣讀回。
    /// 下方字串是手刻的 Newtonsoft 13 輸出特徵（PascalCase 欄名、ISO 帶時區日期、
    /// 明文 ChatHistory 舊欄位、顯式 null），不是本引擎產生的 —— 拿來釘住讀取相容性。
    /// </summary>
    [Test]
    public void TestTelemetryLoadReadsNewtonsoftWrittenFile()
    {
        const string NewtonsoftGolden =
            "{\"EncryptedChatHistory\":null," +
            "\"ChatHistory\":[\"hello\"]," +
            "\"RequestLogs\":[{" +
            "\"Timestamp\":\"2026-01-02T03:04:05+08:00\"," +
            "\"ModId\":\"golden.mod\"," +
            "\"Provider\":\"Gemini\"," +
            "\"Model\":\"gemini-2.5-flash\"," +
            "\"Success\":true," +
            "\"ErrorMessage\":null," +
            "\"LatencyMs\":42}]," +
            "\"TotalPromptTokens\":100," +
            "\"TotalCompletionTokens\":20," +
            "\"TotalEstimatedCost\":12.5," +
            "\"DailyAccumulatedCost\":1.25," +
            "\"DailyBudgetResetDate\":\"2026-09-09\"}";

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "telemetry.json");

        var previousResolver = RimLLMTelemetryStore.FilePathResolver;
        RimLLMTelemetryStore.FilePathResolver = () => path;
        try
        {
            System.IO.File.WriteAllText(path, NewtonsoftGolden);

            var reloaded = new RimLLMTelemetryStore();
            reloaded.Load();

            ClassicAssert.IsTrue(reloaded.LoadedFromDisk, "舊格式檔案應成功載入");
            CollectionAssert.AreEqual(new[] { "hello" }, reloaded.ChatHistory, "舊版明文歷史應讀回");
            ClassicAssert.IsTrue(reloaded.IsDirty, "讀到明文歷史應標記待重寫以完成加密遷移");
            ClassicAssert.AreEqual(100, reloaded.TotalPromptTokens);
            ClassicAssert.AreEqual(20, reloaded.TotalCompletionTokens);
            ClassicAssert.AreEqual(12.5f, reloaded.TotalEstimatedCost);
            ClassicAssert.AreEqual(1.25f, reloaded.DailyAccumulatedCost);
            ClassicAssert.AreEqual("2026-09-09", reloaded.DailyBudgetResetDate);

            ClassicAssert.AreEqual(1, reloaded.RequestLogs.Count);
            var entry = reloaded.RequestLogs[0];
            ClassicAssert.AreEqual("golden.mod", entry.ModId);
            ClassicAssert.AreEqual("gemini-2.5-flash", entry.Model);
            ClassicAssert.IsTrue(entry.Success);
            ClassicAssert.AreEqual(42, entry.LatencyMs);
            // 兩引擎對時區的 Kind 處理不同，比 UTC 瞬間才是語意一致的斷言。
            ClassicAssert.AreEqual(
                new DateTime(2026, 1, 1, 19, 4, 5, DateTimeKind.Utc),
                entry.Timestamp.ToUniversalTime(),
                "ISO 帶時區日期的瞬間必須一致");
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = previousResolver;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// SettingsDto 的形狀集合（public field、string 鍵字典、數字、ISO 日期），
    /// 以手刻 Newtonsoft 輸出驗證 RimLLMJson 的讀取寬容度。
    /// </summary>
    [Test]
    public void TestRimLLMJsonReadsNewtonsoftFieldShapes()
    {
        const string NewtonsoftGolden =
            "{\"FallbackChain\":[\"Gemini:gemini-2.5-flash\"]," +
            "\"ModelLevelOverrides\":{\"Gemini:gemini-2.5-flash\":2}," +
            "\"ApiTimeout\":30.0," +
            "\"MaxRetries\":3," +
            "\"DetailedLogging\":true," +
            "\"EmbeddingProvider\":\"Google\"," +
            "\"Stamp\":\"2026-01-02T03:04:05+08:00\"," +
            "\"Count\":\"7\"}";

        SettingsLikeDto dto = RimLLMJsonHelper.Deserialize<SettingsLikeDto>(NewtonsoftGolden);

        ClassicAssert.IsNotNull(dto);
        CollectionAssert.AreEqual(new[] { "Gemini:gemini-2.5-flash" }, dto.FallbackChain);
        ClassicAssert.AreEqual(2, dto.ModelLevelOverrides["Gemini:gemini-2.5-flash"]);
        ClassicAssert.AreEqual(30f, dto.ApiTimeout);
        ClassicAssert.AreEqual(3, dto.MaxRetries);
        ClassicAssert.IsTrue(dto.DetailedLogging);
        ClassicAssert.AreEqual("Google", dto.EmbeddingProvider);
        ClassicAssert.AreEqual(
            new DateTime(2026, 1, 1, 19, 4, 5, DateTimeKind.Utc),
            dto.Stamp.ToUniversalTime());
        // Newtonsoft 會把字串 "7" 轉成數字，寬容度必須保留。
        ClassicAssert.AreEqual(7, dto.Count);
    }

#pragma warning disable 0649 // 欄位僅由 JSON 反序列化賦值
    private class SettingsLikeDto
    {
        public List<string> FallbackChain;
        public Dictionary<string, int> ModelLevelOverrides;
        public float ApiTimeout;
        public int MaxRetries;
        public bool DetailedLogging;
        public string EmbeddingProvider;
        public DateTime Stamp;
        public int Count;
    }
#pragma warning restore 0649

}
