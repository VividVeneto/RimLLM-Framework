using System;
using RimLLM_Framework.Api;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 共用的接管判定閘門：TTL 快取、開關檢查、供應商能力查詢、節流警告。
/// 三套相容層各自實例化一個，參數化 PackageId 與離線警告訊息模板。
/// </summary>
internal sealed class CompatTakeoverGate
{
    private readonly string _packageId;
    private readonly string _notReadyTemplate;
    private readonly string _noProviderTemplate;

    private bool _cachedTakeOver;
    private DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan TakeOverCacheTtl = TimeSpan.FromSeconds(1);

    private DateTime _lastOfflineWarning = DateTime.MinValue;
    private static readonly TimeSpan OfflineWarningInterval = TimeSpan.FromMinutes(1);

    public CompatTakeoverGate(string packageId, string notReadyTemplate, string noProviderTemplate)
    {
        _packageId = packageId;
        _notReadyTemplate = notReadyTemplate;
        _noProviderTemplate = noProviderTemplate;
    }

    /// <summary>每次請求時判定是否接管：設定開關開啟且 RimLLM 當下有可用供應商。</summary>
    public bool ShouldTakeOver()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _cachedAt < TakeOverCacheTtl) return _cachedTakeOver;

        _cachedTakeOver = EvaluateTakeOver();
        _cachedAt = now;
        return _cachedTakeOver;
    }

    /// <summary>測試隔離用：還原判定快取與警告節流，避免測試間順序相依。</summary>
    public void ResetCacheForTests()
    {
        _cachedTakeOver = false;
        _cachedAt = DateTime.MinValue;
        _lastOfflineWarning = DateTime.MinValue;
    }

    /// <summary>測試用：直接指定接管判定結果（繞過開關＋供應商檢查）。</summary>
    public void SetTakeOverCacheForTests(bool value)
    {
        _cachedTakeOver = value;
        _cachedAt = DateTime.UtcNow;
    }

    /// <summary>玩家在設定分頁切換接管開關時呼叫，使快取失效。</summary>
    public void OnTakeoverToggled()
    {
        _cachedAt = DateTime.MinValue;
    }

    public bool IsToggleEnabled()
    {
        RimLLMFrameworkSettings settings = RimLLMFrameworkMod.Settings;
        return settings != null && settings.IsCompatTakeoverEnabled(_packageId);
    }

    private bool EvaluateTakeOver()
    {
        if (!IsToggleEnabled())
        {
            return false;
        }

        if (RimLLMProvider.TryGetEffectiveCapabilities(out _, out string failureReason))
        {
            return true;
        }

        if (RimLLMCompatTarget.IsNotReadyReason(failureReason))
        {
            WarnThrottled(string.Format(_notReadyTemplate, failureReason));
        }
        else
        {
            WarnThrottled(string.Format(_noProviderTemplate, failureReason));
        }
        return false;
    }

    public void WarnThrottled(string reason)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastOfflineWarning >= OfflineWarningInterval)
        {
            _lastOfflineWarning = now;
            try
            {
                Log.Warning($"[RimLLM] 相容層：{reason}");
            }
            catch (Exception)
            {
                // 警告屬 best-effort：單元測試環境沒有 Unity ECall，Verse.Log 會擲錯；
                // 不可讓記警告本身拖累退回原生的 fallback 路徑。
            }
        }
    }
}
