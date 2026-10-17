using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using ModCompatChecker.AI;
using ModCompatChecker.Core;
using RimLLM_Framework.Api;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 純 Harmony 接管 Mod 兼容性檢查器的 AI 診斷流量，框架本身不實作任何 ModCompatChecker 介面。
/// </summary>
/// <remarks>
/// <para>
/// 核心設計：
/// <list type="number">
/// <item>攔截 <c>AIService.CallAPIWithTimeout</c>（唯一的傳輸出口）：接管開啟且 RimLLM 有可用供應商時，
/// 改由 <see cref="ModCompatCheckerCompatClient"/> 呼叫 RimLLM 並將結果填入 <c>__result</c>。</item>
/// <item>攔截 <c>ModCompatSettings.IsAIConfigured</c>：接管開啟時強制回傳 <c>true</c>，
/// 讓 UI 上的 AI 分析按鈕即使未填原生金鑰也能啟用。</item>
/// <item>攔截 <c>ApiBalanceChecker.CheckBalance</c>：接管開啟時短路跳過，
/// 避免未填原生金鑰時發出 401 錯誤。</item>
/// </list>
/// </para>
/// <para>
/// 型別載入防線：ModCompatChecker 型別不得出現在類別繼承、介面或欄位型別（欄位刻意使用 <see cref="object"/>），
/// 僅允許於方法本體與實例方法簽章中出現（靜態方法簽章也不行：Debug 動作選單會解析），保證在未安裝 ModCompatChecker 時框架 DLL 仍能安全載入。
/// </para>
/// </remarks>
internal static class ModCompatCheckerCompatPatch
{
    private static ModCompatCheckerCompatClient _client;

    private static readonly CompatTakeoverGate _gate = new(
        ModCompatCheckerCompatTarget.PackageId,
        "ModCompatChecker 接管已開啟，但框架尚未就緒，本次退回 ModCompatChecker 原生路徑。原因：{0}",
        "ModCompatChecker 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回 ModCompatChecker 原生路徑。原因：{0}");

    internal static ModCompatCheckerCompatClient Client
    {
        get => _client ?? (_client = new ModCompatCheckerCompatClient());
        set => _client = value;
    }

    /// <summary>取消／逾時時回給 UI 的提示文字（不經原生，避免依賴原生 endpoint 設定）。</summary>
    private const string CancelledText = "分析已取消。(Analysis cancelled.)";
    private const string TimeoutTextFormat = "[RimLLM] 分析逾時（{0} 秒），未再嘗試原生路徑。(Timed out after {0}s via RimLLM; native path skipped.)";

    public static void Apply(Harmony harmony)
    {
        // 1. 攔截 AIService.CallAPIWithTimeout——所有 AI 請求的唯一出口
        MethodInfo callAPI = AccessTools.Method(
            typeof(AIService),
            nameof(AIService.CallAPIWithTimeout)) ?? throw new MissingMethodException("ModCompatChecker.AI.AIService.CallAPIWithTimeout 不存在（ModCompatChecker 版本可能已變動）。");

        // 2. 攔截 ModCompatSettings.IsAIConfigured——讓接管開啟時無需填原生金鑰
        Type settingsType = AccessTools.TypeByName("ModCompatChecker.ModCompatSettings");
        MethodInfo isConfigured = settingsType != null
            ? AccessTools.Method(settingsType, "IsAIConfigured")
            : null;

        // 3. 攔截 ApiBalanceChecker.CheckBalance——接管開啟時短路跳過
        MethodInfo checkBalance = AccessTools.Method(
            typeof(ApiBalanceChecker),
            nameof(ApiBalanceChecker.CheckBalance));

        harmony.Patch(callAPI,
            prefix: new HarmonyMethod(typeof(ModCompatCheckerCompatPatch), nameof(CallAPIWithTimeoutPrefix)));

        if (isConfigured != null)
        {
            harmony.Patch(isConfigured,
                postfix: new HarmonyMethod(typeof(ModCompatCheckerCompatPatch), nameof(IsAIConfiguredPostfix)));
        }
        else
        {
            Log.Warning("[RimLLM] 相容層：找不到 ModCompatSettings.IsAIConfigured（ModCompatChecker 版本可能已變動），AI 按鈕啟用覆寫未掛載；接管開啟時仍需填原生金鑰才能按鈕分析。");
        }

        if (checkBalance != null)
        {
            harmony.Patch(checkBalance,
                prefix: new HarmonyMethod(typeof(ModCompatCheckerCompatPatch), nameof(CheckBalancePrefix)));
        }
        else
        {
            Log.Warning("[RimLLM] 相容層：找不到 ApiBalanceChecker.CheckBalance（ModCompatChecker 版本可能已變動），餘額檢查短路未掛載；接管開啟且無原生金鑰時可能出現 401 提示。");
        }
    }

    /// <summary>
    /// 攔截 AIService.CallAPIWithTimeout：接管開啟時由 RimLLM 處理並填入結果。
    /// </summary>
    /// <remarks>
    /// 例外分流：取消／逾時直接回提示文字並跳過原生——原生會再用同樣的
    /// <c>timeoutSeconds</c> 跑一次，總等待時間翻倍；離線等其他失敗才退回原生。
    /// </remarks>
    public static bool CallAPIWithTimeoutPrefix(
        string userMessage,
        int timeoutSeconds,
        ref bool cancelFlag,
        ref string __result)
    {
        if (!_gate.ShouldTakeOver())
        {
            return true; // 放行原生
        }

        try
        {
            __result = Client.CallAPI(userMessage, timeoutSeconds, ref cancelFlag);
            return false; // 跳過原生
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is TimeoutException)
        {
            __result = cancelFlag
                ? CancelledText
                : string.Format(CultureInfo.InvariantCulture, TimeoutTextFormat, timeoutSeconds);
            return false;
        }
        catch (Exception ex)
        {
            // 呼叫失敗時退回原生路徑
            _gate.WarnThrottled($"ModCompatChecker 接管請求失敗，退回原生路徑。原因：{ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 接管開啟且 RimLLM 有可用供應商時，強制 IsAIConfigured 回傳 true。
    /// </summary>
    public static void IsAIConfiguredPostfix(ref bool __result)
    {
        if (!__result && _gate.ShouldTakeOver())
        {
            __result = true;
        }
    }

    /// <summary>
    /// 接管開啟時短路 ApiBalanceChecker.CheckBalance，避免無原生金鑰時 401 錯誤。
    /// </summary>
    public static bool CheckBalancePrefix()
    {
        return !_gate.ShouldTakeOver();
    }

    /// <summary>測試隔離用：還原判定快取與警告節流，避免測試間順序相依。</summary>
    internal static void ResetCacheForTests()
    {
        _gate.ResetCacheForTests();
    }

    /// <summary>
    /// 測試用：直接指定接管判定結果（繞過開關＋供應商檢查）。
    /// 生產程式不得呼叫。
    /// </summary>
    internal static void SetTakeOverCacheForTests(bool value)
    {
        _gate.SetTakeOverCacheForTests(value);
    }
}
