using System;
using System.Collections.Generic;
using System.Linq;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 一個決策模型供應商的預設值。所有欄位都可在設定中覆寫，這裡只提供第一次使用時的起點。
/// </summary>
public sealed class RimLLMDecisionProvider
{
    public RimLLMDecisionProvider(string id, string defaultEndpoint, string defaultModel, params string[] presetModels)
    {
        Id = id;
        DefaultEndpoint = defaultEndpoint;
        DefaultModel = defaultModel;
        PresetModels = presetModels ?? [];
    }

    /// <summary>穩定的內部識別碼，也是設定檔與語系鍵的一部分。</summary>
    public string Id { get; }

    /// <summary>JevClient 的 BaseAddress；必須是沒有路徑的 HTTPS origin。</summary>
    public string DefaultEndpoint { get; }

    /// <summary>尚未設定模型時送出的模型名稱。</summary>
    public string DefaultModel { get; }

    /// <summary>在抓取伺服器清單之前，先顯示的已知模型。</summary>
    public IReadOnlyList<string> PresetModels { get; }
}

/// <summary>
/// 決策模型供應商清單。這些服務都接受 System One 的 state/questions 結構；
/// 其中 TypeSafe 是原生 Jev，其餘是提供相同 wire format 的託管服務或聚合閘道。
/// 端點與模型都可以在 UI 內修改，因此清單只是預設值，不是硬性限制。
/// </summary>
public static class RimLLMDecisionProviderCatalog
{
    public const string Disabled = "Disabled";
    public const string TypeSafe = "TypeSafe";
    public const string Clef = "Clef";
    public const string Liquid = "Liquid";
    public const string Solar = "Solar";
    public const string Kev = "Kev";
    public const string Tev = "Tev";
    public const string Laya = "Laya";
    public const string Span = "Span";
    public const string Microsoft = "Microsoft";
    public const string OpenAI = "OpenAI";

    /// <summary>
    /// decisions-api.dev 把多個供應商統一在 /v1/systemone 之下，因此非 TypeSafe 的原生服務
    /// 也能用同一個 JevClient。玩家仍可在 UI 改成供應商自己的端點。
    /// </summary>
    private const string AggregatorEndpoint = "https://decisions-api.dev";

    public static readonly IReadOnlyList<RimLLMDecisionProvider> All =
    [
        new RimLLMDecisionProvider(
            TypeSafe,
            "https://api.typesafe.ai",
            "jev-latest",
            "jev-latest", "jev-1.14", "jev-1.13", "jev-1.13.0"),
        new RimLLMDecisionProvider(
            Clef,
            AggregatorEndpoint,
            "cloudflare/clef",
            "cloudflare/clef", "cloudflare/clef-flash", "cloudflare/clef-omni"),
        new RimLLMDecisionProvider(
            Liquid,
            AggregatorEndpoint,
            "liquid/d1",
            "liquid/d1"),
        new RimLLMDecisionProvider(
            Solar,
            AggregatorEndpoint,
            "upstage/solar-decide",
            "upstage/solar-decide", "upstage/solar-decide-flash"),
        new RimLLMDecisionProvider(
            Kev,
            AggregatorEndpoint,
            "jaredpalmer/kev-4b",
            "jaredpalmer/kev-4b"),
        new RimLLMDecisionProvider(
            Tev,
            AggregatorEndpoint,
            "togethercomputer/tev1-4b-experimental",
            "togethercomputer/tev1-4b-experimental"),
        new RimLLMDecisionProvider(
            Laya,
            AggregatorEndpoint,
            "convaiinnovations/laya",
            "convaiinnovations/laya", "laya-auto", "laya-english", "laya-multilingual"),
        new RimLLMDecisionProvider(
            Span,
            AggregatorEndpoint,
            "respan/span-01",
            "respan/span-01", "respan/span-01-lite"),
        new RimLLMDecisionProvider(
            Microsoft,
            AggregatorEndpoint,
            "microsoft/microsoft-decision-1",
            "microsoft/microsoft-decision-1"),
        new RimLLMDecisionProvider(
            OpenAI,
            AggregatorEndpoint,
            "openai/gpt-6-luna-decisions",
            "openai/gpt-6-luna-decisions")
    ];

    private static readonly Dictionary<string, RimLLMDecisionProvider> ById =
        All.ToDictionary(provider => provider.Id, StringComparer.Ordinal);

    public static bool TryGet(string providerId, out RimLLMDecisionProvider provider)
    {
        if (!string.IsNullOrEmpty(providerId) && ById.TryGetValue(providerId, out provider))
        {
            return true;
        }
        provider = null;
        return false;
    }

    public static RimLLMDecisionProvider Get(string providerId)
    {
        return TryGet(providerId, out RimLLMDecisionProvider provider) ? provider : ById[TypeSafe];
    }

    public static string GetDefaultModel(string providerId)
    {
        return Get(providerId).DefaultModel;
    }

    public static string GetDefaultEndpoint(string providerId)
    {
        return Get(providerId).DefaultEndpoint;
    }
}
