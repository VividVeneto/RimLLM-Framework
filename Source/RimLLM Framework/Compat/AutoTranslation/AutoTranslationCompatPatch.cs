using System;
using System.Reflection;
using AutoTranslation;
using AutoTranslation.Services;
using AutoTranslation.Translators;
using HarmonyLib;
using RimLLM_Framework.Api;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 純 Harmony 接管 Auto Translation 的翻譯流量，框架本身不實作任何 Auto Translation 介面。
/// </summary>
/// <remarks>
/// <para>
/// 核心設計：
/// <list type="number">
/// <item>以 Auto Translation 原生具備批次與佔位符防護的 <see cref="Translator_OpenAICompatible"/> 建構哨兵實例（<c>_sentinel</c>）。</item>
/// <item>攔截 <c>Translator_OpenAICompatible.GetResponseUnsafe</c>：當 <c>__instance</c> 為哨兵時改由
/// <see cref="AutoTranslationCompatClient"/> 呼叫 RimLLM，其餘原生實例一律放行。</item>
/// <item>攔截 <c>TranslatorManager.Translate</c>：在任何文本入列前以 <see cref="SyncCurrentTranslator"/>
/// 確保當前翻譯器與接管開關狀態完全同步；若接管開啟且線程尚未啟動，則自動喚醒。</item>
/// </list>
/// </para>
/// <para>
/// 型別載入防線：Auto Translation 型別不得出現在類別繼承、介面或欄位型別（欄位刻意使用 <see cref="object"/>），
/// 僅允許於方法本體與實例方法簽章中出現（靜態方法簽章也不行：Debug 動作選單會解析），保證在未安裝 Auto Translation 時框架 DLL 仍能安全載入。
/// </para>
/// </remarks>
internal static class AutoTranslationCompatPatch
{
    private static AutoTranslationCompatClient _client;

    /// <summary>哨兵 translator 實例，欄位型別刻意用 object 隔絕型別載入依賴。</summary>
    internal static object Sentinel { get; set; }

    /// <summary>在接管前或接管停用時的原生 translator 實例。</summary>
    internal static object NativeTranslator { get; set; }

    private static readonly CompatTakeoverGate _gate = new(
        AutoTranslationCompatTarget.PackageId,
        "Auto Translation 接管已開啟，但框架尚未就緒，本次退回 Auto Translation 原生路徑。原因：{0}",
        "Auto Translation 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回 Auto Translation 原生路徑。原因：{0}");

    internal static AutoTranslationCompatClient Client
    {
        get => _client ?? (_client = new AutoTranslationCompatClient());
        set => _client = value;
    }

    public static void Apply(Harmony harmony)
    {
        MethodInfo getResponse = AccessTools.Method(
            typeof(Translator_OpenAICompatible),
            "GetResponseUnsafe",
            [typeof(string), typeof(string)]) ?? throw new MissingMethodException("AutoTranslation.Translators.Translator_OpenAICompatible.GetResponseUnsafe 不存在（Auto Translation 版本可能已變動）。");

        MethodInfo translate = AccessTools.Method(
            typeof(TranslatorManager),
            nameof(TranslatorManager.Translate),
            [typeof(string), typeof(string), typeof(string), typeof(Action<string>)]) ?? throw new MissingMethodException("AutoTranslation.Services.TranslatorManager.Translate 不存在（Auto Translation 版本可能已變動）。");

        // 建構哨兵實例：啟用批次翻譯與 RimLLM 模型標識。
        // 注意：RequestTimeoutSeconds 刻意不設——哨兵的 GetResponseUnsafe 全數被 Prefix 攔走，
        // 從不走原生網路，RimLLM 側實際走框架自己的超時；在此設值只會誤導。
        var sentinel = new Translator_OpenAICompatible
        {
            Settings = new TranslatorSettings_AIModel
            {
                UserSelectedModel = "RimLLM",
                EnableBatchTranslation = true,
                BatchSizeTokens = 2000
            }
        };
        sentinel.Prepare();
        sentinel.Ready = true;

        Sentinel = sentinel;

        harmony.Patch(getResponse, prefix: new HarmonyMethod(typeof(AutoTranslationCompatPatch), nameof(GetResponseUnsafePrefix)));
        harmony.Patch(translate, prefix: new HarmonyMethod(typeof(AutoTranslationCompatPatch), nameof(TranslatePrefix)));

        // 啟動階段初始同步：fail-soft，避免 Manager 尚未初始化時把半掛載留給 TryApply。
        // Harmony Prefix 此時已掛上，Sync 失敗不應讓 Apply 跟著炸。
        try
        {
            SyncCurrentTranslator();
        }
        catch (Exception ex)
        {
            Log.Warning($"[RimLLM] 相容層：Auto Translation 啟動同步略過（稍後第一次 Translate 會重試）。原因：{ex.Message}");
        }
    }

    /// <summary>
    /// 文本入列翻譯隊列時觸發，確保 CurrentTranslator 狀態最新。
    /// fail-soft：啟動期 Manager 可能尚未初始化，不可把例外丟回遊戲執行緒。
    /// </summary>
    public static void TranslatePrefix()
    {
        try
        {
            SyncCurrentTranslator();
        }
        catch (Exception)
        {
            // EvaluateTakeOver 已把供應商缺席轉為 false 並節流警告；
            // 這裡只吞掉啟動順序異常等非預期路徑，避免每次入列都拋。
        }
    }

    /// <summary>
    /// 攔截哨兵實例的底層連線請求，改走 RimLLM 管道。
    /// </summary>
    /// <remarks>
    /// 離線退回注意：此時已在哨兵的 <c>TryTranslate</c> 內部（背景執行緒），無法把「當下這筆」
    /// 改派給原生翻譯器——原生可能是 DeepL／Google 等異構型別，其 <c>GetResponseUnsafe</c>
    /// 是受保護方法且回傳格式不同。因此退回策略是：後續入列經 <see cref="SyncCurrentTranslator"/>
    /// 切回原生；當下這筆回傳原文 echo JSON，讓哨兵的佔位符還原直接通過、快速回原文，
    /// 而非放行哨兵原生網路（預設打 localhost，超時可達數十秒）。
    /// Client 拋出（瞬斷、限流）時則原樣上拋，交給哨兵 <c>TryTranslate</c> 自帶的重試／回原文機制。
    /// </remarks>
    public static bool GetResponseUnsafePrefix(
        object __instance,
        string text,
        string prompt,
        ref string __result)
    {
        if (!ReferenceEquals(__instance, Sentinel))
        {
            return true;
        }

        if (!_gate.ShouldTakeOver())
        {
            try
            {
                SyncCurrentTranslator();
            }
            catch (Exception)
            {
                // 忽略同步例外，避免中斷調用流程
            }
            __result = BuildEchoResponse(text);
            return false;
        }

        try
        {
            __result = Client.GetResponseUnsafe(text, prompt);
            return false;
        }
        catch (Exception)
        {
            // 下一筆起有機會切回原生；當下這筆由 Auto Translation 重試／回原文。
            try
            {
                SyncCurrentTranslator();
            }
            catch (Exception)
            {
                // 忽略同步例外，避免中斷調用流程
            }
            throw;
        }
    }

    /// <summary>
    /// 玩家在設定分頁切換接管開關時即時呼叫。
    /// 只做快取失效再同步，讓 <see cref="ShouldTakeOver"/> 重走開關＋供應商判定，
    /// 避免無供應商時開啟開關仍有約 1 秒誤接管空窗。
    /// </summary>
    public static void OnTakeoverToggled(bool enabled)
    {
        _ = enabled;
        _gate.OnTakeoverToggled();
        try
        {
            SyncCurrentTranslator();
        }
        catch (Exception)
        {
            // 忽略同步例外，避免中斷調用流程
        }
    }

    /// <summary>
    /// 構造原文 echo 回應：content 取原文，讓哨兵的佔位符還原通過並回原文。
    /// 單條與批次皆適用——批次時 text 即 batch XML，ParseBatchXml 會把它還原為原文清單。
    /// </summary>
    private static string BuildEchoResponse(string text)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            content = text ?? string.Empty,
            prompt_tokens = 0,
            completion_tokens = 0
        }, AutoTranslationCompatClient.SharedJsonOptions);
    }

    /// <summary>
    /// 同步 Auto Translation 的 CurrentTranslator 實例與線程狀態。
    /// </summary>
    internal static void SyncCurrentTranslator()
    {
        if (Sentinel == null) return;

        if (_gate.ShouldTakeOver())
        {
            ApplySentinelTranslator();
        }
        else
        {
            RestoreNativeTranslator();
        }
    }

    private static void ApplySentinelTranslator()
    {
        if (!ReferenceEquals(TranslatorManager.CurrentTranslator, Sentinel))
        {
            NativeTranslator = TranslatorManager.CurrentTranslator;
            TranslatorManager.CurrentTranslator = (ITranslator)Sentinel;
            TranslatorManager.Ready = true;

            if (!TranslatorManager.Working)
            {
                TranslatorManager.StartThread();
            }
        }
    }

    private static void RestoreNativeTranslator()
    {
        if (!ReferenceEquals(TranslatorManager.CurrentTranslator, Sentinel)) return;

        // 還原優先用即時查詢：接管期間玩家可能在 Auto Translation 自家設定改了翻譯器，
        // 快照此時已陳舊。快照僅作後備；兩者皆 null 則不寫入，避免 CurrentTranslator=null 停擺。
        ITranslator live = null;
        try
        {
            live = TranslatorManager.GetTranslator(global::AutoTranslation.Settings.TranslatorName);
        }
        catch (Exception)
        {
            // 忽略設定讀取錯誤，後續嘗試使用快照還原
        }
        ITranslator snap = NativeTranslator as ITranslator;
        ITranslator native = (ITranslator)PickRestoreTranslator(live, snap);
        if (native == null)
        {
            Log.Warning("[RimLLM] 相容層：Auto Translation 還原原生翻譯器失敗（即時查詢與快照皆為 null），保留哨兵避免停擺。");
            return;
        }
        TranslatorManager.CurrentTranslator = native;
        TranslatorManager.Ready = native.Ready;
    }

    /// <summary>還原用翻譯器挑選：就緒者優先，其次即時值，最後快照。</summary>
    private static object PickRestoreTranslator(object live, object snap)
    {
        if (live is ITranslator l && l.Ready) return live;
        if (snap is ITranslator s && s.Ready) return snap;
        return live ?? snap;
    }

    /// <summary>測試隔離用：還原判定快取與警告節流，避免測試間順序相依。</summary>
    internal static void ResetCacheForTests()
    {
        _gate.ResetCacheForTests();
    }

    /// <summary>
    /// 測試用：直接指定接管判定結果（繞過開關＋供應商檢查）。
    /// 生產程式不得呼叫；離線降級分支請用 <c>false</c> 覆蓋。
    /// </summary>
    internal static void SetTakeOverCacheForTests(bool value)
    {
        _gate.SetTakeOverCacheForTests(value);
    }

    /// <summary>測試用：直接讀取當前判定結果（用於斷言驗證）。</summary>
    internal static bool ShouldTakeOver()
    {
        return _gate.ShouldTakeOver();
    }
}
