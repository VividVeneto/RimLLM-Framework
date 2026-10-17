using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using RimLLM_Framework.Mod;
using RimTalk;
using RimTalk.Client;
using RimTalk.Client.OpenAI;
using RimTalk.Data;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 純 Harmony 接管 RimTalk 的 LLM 流量，框架本身不實作任何 RimTalk 介面。
/// </summary>
/// <remarks>
/// <para>
/// 三個攔截點：
/// <list type="number">
/// <item><c>AIClientFactory.GetAIClientAsync</c>（唯一的 client 取得點）：接管生效時回傳一個由本類別持有的
/// RimTalk 原生 <see cref="OpenAIClient"/> 哨兵實例。RimTalk 的串流對話（ChatStreaming）與結構化查詢
/// （Query<T>）都經由 <c>AIService.ExecuteWithRetry</c> 取得 client，因此 OpenAI／Gemini／Custom／
/// Local／Player2 全部流量都會落到哨兵上。</item>
/// <item><see cref="OpenAIClient"/> 的兩個非泛型漏斗 <c>GetChatCompletionAsync(prefix, messages, imageBase64, onRequestPrepared)</c>
/// 與私有 <c>StreamAsync(…, onChunk, onRequestPrepared)</c>：<c>__instance</c> 是哨兵時改呼叫
/// <see cref="RimTalkCompatClient"/>，不是哨兵（玩家自己的原生 client）則原樣放行。RimTalk 的泛型
/// <c>GetStreamingChatCompletionAsync<T></c> 與三參數 <c>GetChatCompletionAsync</c> 都是這兩個方法的包裝，
/// 不必碰泛型方法的 Harmony 攔截。</item>
/// </list>
/// 為什麼不直接讓 <see cref="RimTalkCompatClient"/> 實作 <see cref="IAIClient"/>：那是型別定義層級的參考，
/// RimWorld 載入 DLL 時的 <c>Assembly.GetTypes()</c> 會在 RimTalk 缺席時整顆拒載框架。哨兵是 RimTalk 自己的型別，
/// 建構它只是方法本體裡的一次 <c>newobj</c>，只在 RimTalk 存在時才會被 JIT。
/// </para>
/// <para>
/// 第二個攔截點 <c>RimTalkSettings.GetActiveConfig</c>：RimTalk 在 <c>TalkService</c> 與
/// <c>TickManagerPatch</c> 裡以「回傳 null」判定未設定 API，會直接不發起對話並提示缺金鑰——
/// 這道門檻在 <c>GetAIClientAsync</c> 之前。接管生效時若玩家從未設定 RimTalk，就補一個合成的
/// 設定讓門檻通過；玩家自己有設定則原樣保留（模型名稱只用於 RimTalk 的提示訊息）。
/// </para>
/// </remarks>
internal static class RimTalkCompatPatch
{
    /// <summary>合成設定用的端點與模型名稱；只會出現在 RimTalk 自己的提示訊息與設定雜湊裡。</summary>
    private const string SyntheticEndpoint = "rimllm://" + RimTalkCompatTarget.PackageId;
    private const string SyntheticModel = "RimLLM";

    private static RimTalkCompatClient _client;

    /// <summary>目前的哨兵。setter 僅供測試不經 Harmony 掛載就驗證路由。</summary>
    internal static object Sentinel { get; set; }

    /// <summary>轉接器；測試可注入帶假 <c>IChatClient</c> 的實例。</summary>
    internal static RimTalkCompatClient Client
    {
        get => _client ?? (_client = new RimTalkCompatClient());
        set => _client = value;
    }
    private static readonly CompatTakeoverGate _gate = new(
        RimTalkCompatTarget.PackageId,
        "框架尚未就緒，RimTalk 接管本次退回原生路徑。原因：{0}",
        "RimLLM 目前沒有可用的供應商，RimTalk 接管本次退回原生路徑。原因：{0}");

    public static void Apply(Harmony harmony)
    {
        // 先把全部目標方法與哨兵都準備好，確認無誤才開始 Patch：
        // 任何一步失敗都不能留下「GetAIClientAsync 已被攔、哨兵卻是 null」的半掛載狀態，否則 RimTalk 會拿到 null client。
        MethodInfo target = AccessTools.Method(typeof(AIClientFactory), nameof(AIClientFactory.GetAIClientAsync)) ?? throw new MissingMethodException("RimTalk.Client.AIClientFactory.GetAIClientAsync 不存在（RimTalk 版本可能已變動）。");

        MethodInfo activeConfig = AccessTools.Method(typeof(RimTalkSettings), nameof(RimTalkSettings.GetActiveConfig)) ?? throw new MissingMethodException("RimTalk.RimTalkSettings.GetActiveConfig 不存在（RimTalk 版本可能已變動）。");
        Type messages = typeof(List<(Role role, string message)>);
        MethodInfo completion = AccessTools.Method(typeof(OpenAIClient), nameof(OpenAIClient.GetChatCompletionAsync),
            [messages, messages, typeof(string), typeof(Action<Payload>)]) ?? throw new MissingMethodException("RimTalk.Client.OpenAI.OpenAIClient.GetChatCompletionAsync(prefix, messages, imageBase64, onRequestPrepared) 不存在（RimTalk 版本可能已變動）。");

        MethodInfo stream = AccessTools.Method(typeof(OpenAIClient), "StreamAsync",
            [messages, messages, typeof(string), typeof(Action<string>), typeof(Action<Payload>)]) ?? throw new MissingMethodException("RimTalk.Client.OpenAI.OpenAIClient.StreamAsync 不存在（RimTalk 版本可能已變動）。");

        // 哨兵在掛載階段就建好：RimTalk 建構子簽章若漂移，MissingMethodException 會落在 TryApply 裡、顯示在設定頁，
        // 而不是等到第一次對話才炸。baseUrl 給 null 讓 OpenAIClient 跳過 Uri 解析。
        object sentinel = new OpenAIClient(null, SyntheticModel, null, null, null, AIProvider.Local);

        // 漏斗先掛、入口最後掛：GetAIClientAsync 的 Prefix 一旦生效就會交出哨兵，此時兩個漏斗必須已經就位。
        harmony.Patch(completion, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetChatCompletionAsyncPrefix)));
        harmony.Patch(stream, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(StreamAsyncPrefix)));
        harmony.Patch(activeConfig, postfix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetActiveConfigPostfix)));
        Sentinel = sentinel;
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetAIClientAsyncPrefix)));
    }

    /// <summary>
    /// 玩家沒有任何有效的 RimTalk API 設定時，接管生效期間補一個合成設定，讓 RimTalk 願意發起對話。
    /// 只在原生結果為 null 時介入；Provider 設為 Local 是因為它是唯一不要求金鑰的類型，
    /// 而 BaseUrl 永遠不會被真的連線——請求在 <see cref="GetAIClientAsyncPrefix"/> 就被接走了。
    /// </summary>
    public static void GetActiveConfigPostfix(ref object __result)
    {
        if (__result != null || !_gate.ShouldTakeOver()) return;

        __result = new ApiConfig
        {
            IsEnabled = true,
            Provider = AIProvider.Local,
            BaseUrl = SyntheticEndpoint,
            SelectedModel = SyntheticModel
        };
    }

    /// <summary>回傳 false 代表攔下並以 RimLLM 轉接器取代；true 放行 RimTalk 原生路徑。</summary>
    public static bool GetAIClientAsyncPrefix(ref object __result)
    {
        // TalkService 是用 GetActiveConfig()!=null（可能是 Postfix 補的合成設定）放行對話，
        // 走到這裡時若供應商剛好全部不可用而判定翻轉，原生路徑會因設定為 null 而回傳 null client，
        // RimTalk 接著對 null 呼叫方法。開關仍開著時這種情況交給轉接器，讓它以清楚的 ProviderOffline 錯誤結束；
        // 開關已被關掉則一律放行，尊重玩家的選擇。
        if (!_gate.ShouldTakeOver() && (!_gate.IsToggleEnabled() || Settings.Get()?.GetActiveConfig() != null)) return true;
        // 沒有哨兵（掛載未完成）時寧可放行原生路徑，也不能交出 null client。
        if (Sentinel == null) return true;

        __result = Task.FromResult((IAIClient)Sentinel);
        return false;
    }

    /// <summary>非串流漏斗：哨兵實例改走 RimLLM，其他實例放行。</summary>
    public static bool GetChatCompletionAsyncPrefix(
        object __instance,
        object prefixMessages,
        object messages,
        string imageBase64,
        object onRequestPrepared,
        ref object __result)
    {
        if (!ReferenceEquals(__instance, Sentinel)) return true;

        __result = Client.GetChatCompletionAsync(
            (List<(Role role, string message)>)prefixMessages,
            (List<(Role role, string message)>)messages,
            imageBase64,
            (Action<Payload>)onRequestPrepared);
        return false;
    }

    /// <summary>串流漏斗：哨兵實例改走 RimLLM，文字塊交回 RimTalk 自己的 JSONL parser。</summary>
    public static bool StreamAsyncPrefix(
        object __instance,
        object prefixMessages,
        object messages,
        string imageBase64,
        Action<string> onChunk,
        object onRequestPrepared,
        ref object __result)
    {
        if (!ReferenceEquals(__instance, Sentinel)) return true;

        __result = Client.StreamAsync(
            (List<(Role role, string message)>)prefixMessages,
            (List<(Role role, string message)>)messages,
            imageBase64,
            onChunk,
            (Action<Payload>)onRequestPrepared);
        return false;
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

    /// <summary>測試用：直接讀取當前判定結果（用於斷言驗證）。</summary>
    internal static bool ShouldTakeOver()
    {
        return _gate.ShouldTakeOver();
    }

    /// <summary>測試用：直接讀取開關狀態（用於斷言驗證）。</summary>
    internal static bool IsToggleEnabled()
    {
        return _gate.IsToggleEnabled();
    }
}
