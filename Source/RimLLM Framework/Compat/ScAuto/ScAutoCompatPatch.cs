using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 純 Harmony 接管簡中自動化 One（RimTranslation）的 AI 翻譯流量，框架本身不實作它的任何介面。
/// </summary>
/// <remarks>
/// <para>
/// 該 Mod 的 <c>ResponsesClient</c>、<c>ApiConfigurationStore</c> 等類別都是 <c>internal</c>，無法在編譯期引用，
/// 因此整個相容層<b>只靠反射與 Harmony</b>：型別與方法以名稱解析，補丁方法的簽章只用 <see cref="object"/>、
/// <see cref="Delegate"/> 與 BCL 型別。這同時滿足型別載入防線（見 <see cref="RimLLMCompatTarget"/>）——
/// 目標 Mod 缺席或版本漂移時，框架 DLL 與 Debug 動作選單都不會因型別解析失敗而出事。
/// </para>
/// <para>
/// 攔截點：
/// <list type="number">
/// <item><c>ResponsesClient.PostOnceCoreAsync</c>：翻譯、評審、健康探測等一切請求的唯一傳輸出口，Chat 與 Responses
/// 兩種協議共用。接管開啟時把請求 JSON 交給 <see cref="ScAutoCompatClient"/>，再把合成的回應 JSON 餵給該 Mod 傳進來的
/// 解析委派，由它自己產出內部結果。</item>
/// <item><c>ApiConfigurationStore.IsLocalBaseUrl(string)</c>：該 Mod 在「無金鑰且非本機位址」時拒絕啟動隊列與儲存設定；
/// 接管開啟時視為本機，玩家不必填假金鑰。（可選，缺席時只記警告。）</item>
/// <item><c>ApiConnectionProbe.FetchModelsAsync</c> 與 <c>ThinkingParameterProbeService.RunAsync</c>：設定頁的「測試連線」與
/// 思考參數探測原本直連端點，接管開啟時短路為成功。（可選。）攔的是它們的 <c>Task.Run</c> 薄殼而不是裡面的
/// <c>ProbeCore</c>：後者含 <c>catch ... when</c> 例外過濾器，Harmony 在 Mono 上無法為它產生程式碼
/// （實機報 <c>Incorrect code generation for exception block</c>）。</item>
/// </list>
/// 不處理的：OpenAI Batch API 通道（<c>BatchClient</c>，非同步批次檔案上傳，無法對應 RimLLM 的請求模型）。
/// </para>
/// </remarks>
internal static class ScAutoCompatPatch
{
    private const string Namespace = "RimTranslation.Api.";

    /// <summary>接管開啟時，連線測試回報的模型清單。</summary>
    private const string ProbeModelLabel = "RimLLM";

    private static ScAutoCompatClient _client;

    private static readonly CompatTakeoverGate _gate = new(
        ScAutoCompatTarget.PackageId,
        "簡中自動化 One 接管已開啟，但框架尚未就緒，本次退回其原生路徑。原因：{0}",
        "簡中自動化 One 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回其原生路徑。原因：{0}");

    /// <summary>以該 Mod 的 <c>ResponsesResult</c> 型別封閉過的 <see cref="ParseAsync{T}"/>；Apply 時建好。</summary>
    private static MethodInfo _parseAsync;

    private static Type _connectionResultType;
    private static Type _thinkingResultType;

    internal static ScAutoCompatClient Client
    {
        get => _client ?? (_client = new ScAutoCompatClient());
        set => _client = value;
    }

    public static void Apply(Harmony harmony)
    {
        Apply(harmony, AccessTools.TypeByName);
    }

    /// <summary>型別解析器可注入，供測試以假型別驗證補丁綁定而不需要真的安裝目標 Mod。</summary>
    internal static void Apply(Harmony harmony, Func<string, Type> resolveType)
    {
        Type responsesClient = resolveType(Namespace + "ResponsesClient");
        MethodInfo postOnce = (responsesClient == null ? null : AccessTools.Method(responsesClient, "PostOnceCoreAsync")) ?? throw new MissingMethodException("RimTranslation.Api.ResponsesClient.PostOnceCoreAsync 不存在（簡中自動化 One 版本可能已變動）。");

        Type resultType = (postOnce.ReturnType.IsGenericType ? postOnce.ReturnType.GetGenericArguments()[0] : null) ?? throw new MissingMethodException("ResponsesClient.PostOnceCoreAsync 的回傳型別不是 Task<ResponsesResult>（簡中自動化 One 版本可能已變動）。");
        Type store = resolveType(Namespace + "ApiConfigurationStore");
        MethodInfo isLocal = store == null ? null : AccessTools.Method(store, "IsLocalBaseUrl", [typeof(string)]);

        Type connectionProbe = resolveType(Namespace + "ApiConnectionProbe");
        MethodInfo probeConnection = connectionProbe == null ? null : AccessTools.Method(connectionProbe, "FetchModelsAsync");

        Type thinkingProbe = resolveType(Namespace + "ThinkingParameterProbeService");
        MethodInfo probeThinking = thinkingProbe == null ? null : AccessTools.Method(thinkingProbe, "RunAsync");

        _parseAsync = AccessTools.Method(typeof(ScAutoCompatPatch), nameof(ParseAsync)).MakeGenericMethod(resultType);
        _connectionResultType = TaskResultType(probeConnection);
        _thinkingResultType = TaskResultType(probeThinking);

        harmony.Patch(postOnce, prefix: new HarmonyMethod(typeof(ScAutoCompatPatch), nameof(PostOnceCoreAsyncPrefix)));

        PatchOptional(harmony, isLocal, "ApiConfigurationStore.IsLocalBaseUrl", nameof(IsLocalBaseUrlPostfix), postfix: true,
            "接管開啟時仍需填原生金鑰才能啟動翻譯隊列。");
        PatchOptional(harmony, probeConnection, "ApiConnectionProbe.FetchModelsAsync", nameof(ConnectionProbePrefix), postfix: false,
            "設定頁的「測試連線」會直連原生端點。");
        PatchOptional(harmony, probeThinking, "ThinkingParameterProbeService.RunAsync", nameof(ThinkingProbePrefix), postfix: false,
            "思考參數探測會直連原生端點。");
    }

    /// <summary>方法回傳 <c>Task&lt;T&gt;</c> 時取出 T；否則回傳 null（該攔截視為不適用）。</summary>
    private static Type TaskResultType(MethodInfo method)
    {
        return method != null && method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : null;
    }

    private static void PatchOptional(Harmony harmony, MethodInfo target, string label, string patchName, bool postfix, string consequence)
    {
        if (target == null)
        {
            Log.Warning($"[RimLLM] 相容層：找不到 {label}（簡中自動化 One 版本可能已變動），該攔截未掛載；{consequence}");
            return;
        }

        var patch = new HarmonyMethod(typeof(ScAutoCompatPatch), patchName);
        try
        {
            harmony.Patch(target, prefix: postfix ? null : patch, postfix: postfix ? patch : null);
        }
        catch (Exception ex)
        {
            // 可選攔截失敗不能中止 Apply：此時必要的傳輸補丁已生效，中止只會留下「已接管卻顯示未掛載」的半掛載狀態。
            Log.Warning($"[RimLLM] 相容層：{label} 攔截掛載失敗，已略過；{consequence} 原因：{ex.Message}");
        }
    }

    /// <summary>
    /// 攔截傳輸出口：接管開啟時改由 RimLLM 處理。回傳 false 跳過原生；<paramref name="__result"/> 是以該 Mod 的
    /// <c>ResponsesResult</c> 封閉過的 <c>Task</c>，所以宣告為 <see cref="object"/>。
    /// </summary>
    /// <remarks>
    /// 請求在這一層才轉成 RimLLM 呼叫，原生的 <c>PostWithRetriesAsync</c> 等重試、降級、解析邏輯都留在外面照常運作；
    /// 取消則由 token 觸發 <see cref="OperationCanceledException"/> 往上傳，與原生行為一致。
    /// </remarks>
    public static bool PostOnceCoreAsyncPrefix(
        string relativeUrl,
        string payload,
        Delegate parseJson,
        CancellationToken cancellationToken,
        ref object __result)
    {
        if (_parseAsync == null || !_gate.ShouldTakeOver())
        {
            return true;
        }

        bool chatProtocol = relativeUrl != null && relativeUrl.IndexOf("chat/completions", StringComparison.OrdinalIgnoreCase) >= 0;
        Task<string> response = Client.CompleteAsync(payload, chatProtocol, cancellationToken);
        __result = _parseAsync.Invoke(null, [response, parseJson, chatProtocol]);
        return false;
    }

    /// <summary>
    /// 把合成的回應 JSON 交給該 Mod 的解析委派。泛型參數是它的 <c>ResponsesResult</c>：以泛型而不是反射建構結果，
    /// 型別不必出現在任何簽章，委派的回傳值再轉型即可。
    /// </summary>
    private static async Task<T> ParseAsync<T>(Task<string> response, Delegate parseJson, bool chatProtocol)
    {
        string json = await response.ConfigureAwait(false);
        try
        {
            return (T)parseJson.DynamicInvoke(json);
        }
        catch (TargetInvocationException ex)
        {
            // 解析器對格式錯誤擲 FormatException；原生在同一位置把它轉成 parse 失敗結果，這裡比照辦理。
            string failure = ScAutoCompatClient.BuildErrorJson("parse", ex.InnerException?.Message ?? ex.Message, chatProtocol);
            return (T)parseJson.DynamicInvoke(failure);
        }
    }

    /// <summary>接管開啟時，無金鑰也視為可啟動：該 Mod 只在憑證閘門上用這個判斷。</summary>
    public static void IsLocalBaseUrlPostfix(ref bool __result)
    {
        if (!__result && _gate.ShouldTakeOver())
        {
            __result = true;
        }
    }

    /// <summary>接管開啟時，「測試連線」回報成功並列出 RimLLM 作為唯一模型。</summary>
    public static bool ConnectionProbePrefix(ref object __result)
    {
        if (_connectionResultType == null || !_gate.ShouldTakeOver())
        {
            return true;
        }

        object result = Activator.CreateInstance(_connectionResultType, nonPublic: true);
        AccessTools.Property(_connectionResultType, "Ok").SetValue(result, true, null);
        AccessTools.Property(_connectionResultType, "Models").SetValue(result, new List<string> { ProbeModelLabel }, null);
        __result = CompletedTask(_connectionResultType, result);
        return false;
    }

    /// <summary>接管開啟時，思考參數探測直接判為通過：思考模式由 RimLLM 處理，探測原生端點沒有意義。</summary>
    public static bool ThinkingProbePrefix(ref object __result)
    {
        if (_thinkingResultType == null || !_gate.ShouldTakeOver())
        {
            return true;
        }

        object result = Activator.CreateInstance(_thinkingResultType, nonPublic: true);
        AccessTools.Field(_thinkingResultType, "Success").SetValue(result, true);
        AccessTools.Field(_thinkingResultType, "Attempts").SetValue(result, new List<string>());
        AccessTools.Field(_thinkingResultType, "Message").SetValue(result,
            "已由 RimLLM 接管：思考模式由 RimLLM 處理，略過探測。(RimLLM takeover active: probe skipped.)");
        __result = CompletedTask(_thinkingResultType, result);
        return false;
    }

    /// <summary>建立以該 Mod 的結果型別封閉的已完成 <c>Task</c>。</summary>
    private static object CompletedTask(Type resultType, object value)
    {
        return AccessTools.Method(typeof(ScAutoCompatPatch), nameof(FromResult)).MakeGenericMethod(resultType).Invoke(null, [value]);
    }

    private static Task<T> FromResult<T>(object value)
    {
        return Task.FromResult((T)value);
    }

    /// <summary>玩家在設定分頁切換接管開關時即時呼叫，讓判定快取失效。</summary>
    public static void OnTakeoverToggled(bool enabled)
    {
        _ = enabled;
        _gate.OnTakeoverToggled();
    }

    /// <summary>測試隔離用：還原判定快取、封閉過的方法與結果型別，避免測試間順序相依。</summary>
    internal static void ResetForTests()
    {
        _gate.ResetCacheForTests();
        _parseAsync = null;
        _connectionResultType = null;
        _thinkingResultType = null;
    }

    /// <summary>測試用：直接指定接管判定結果（繞過開關＋供應商檢查）。生產程式不得呼叫。</summary>
    internal static void SetTakeOverCacheForTests(bool value)
    {
        _gate.SetTakeOverCacheForTests(value);
    }
}
