using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 各個 Mod 的請求時間視窗與冷卻狀態。
/// </summary>
/// <remarks>
/// 由 manager 持有單一實例，注入每個 per-mod 的防濫用中介層。
/// 這個共用是必要的：若改成每個 client 各持一份，同一個 Mod 只要多呼叫幾次
/// CreateChatClient 就能繞過節流，而 ClearCooldowns 也只會清掉其中一份。
/// </remarks>
internal sealed class RimLLMThrottleStore
{
    // modId 是公開 SDK 的相容性標籤，不能視為不可偽造的身份。
    // 共享上限防止呼叫端輪換 modId 無限消耗共用的 API 金鑰，
    // 同時保留原本每個 Mod 的獨立視窗，避免改變正常模組間的公平性。
    private const int GlobalRequestLimitMultiplier = 10;
    private readonly IRimLLMSettings _settings;

    private readonly ConcurrentDictionary<string, List<DateTime>> _requestTimestamps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _coolDownUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _globalLock = new();
    private readonly List<DateTime> _globalRequestTimestamps = [];
    private DateTime _globalCoolDownUntil = DateTime.MinValue;

    public RimLLMThrottleStore(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 檢查並記錄一次請求。超出視窗上限時讓該 Mod 進入冷卻並擲出 <see cref="LLMError.RateLimit"/>。
    /// </summary>
    /// <param name="modId">呼叫端 Mod 識別。</param>
    /// <param name="countTowardWindow">
    /// 是否把這次呼叫計入 per-Mod 時間視窗。工具迴圈的續輪傳 false：它們是同一個外層請求的
    /// 延續，逐輪計數會讓一個 10 輪的 Agent 迴圈在幾秒內就被判成濫用；但每個實際供應商呼叫
    /// 仍會計入共享 API safety ceiling，避免呼叫端偽造工具結果來繞過全域保護。
    /// 冷卻檢查不受此參數影響——已在冷卻中的 Mod 連續輪也一併擋下。
    /// </param>
    public void CheckAntiAbuse(string modId, bool countTowardWindow = true)
    {
        if (string.IsNullOrEmpty(modId)) return;

        DateTime now = DateTime.UtcNow;
        if (_coolDownUntil.TryGetValue(modId, out DateTime cdTime) && now < cdTime)
        {
            throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' is in anti-abuse cooldown until {cdTime.ToLocalTime()}.");
        }

        lock (_globalLock)
        {
            if (now < _globalCoolDownUntil)
            {
                throw new RimLLMException(
                    LLMError.RateLimit,
                    $"[RimLLM] Shared API anti-abuse cooldown is active until {_globalCoolDownUntil.ToLocalTime()}.");
            }

            DateTime globalLimit = now.AddSeconds(-_settings.ThrottlingWindowSeconds);
            _globalRequestTimestamps.RemoveAll(t => t < globalLimit);

            int globalMaxRequests = GetGlobalMaxRequests();
            if (_globalRequestTimestamps.Count >= globalMaxRequests)
            {
                _globalCoolDownUntil = now.AddSeconds(_settings.CoolDownDurationSeconds);
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning(
                        $"[RimLLM] Shared API anti-abuse limit reached ({globalMaxRequests} requests). " +
                        $"Cooling down until {_globalCoolDownUntil.ToLocalTime()}.");
                }
                throw new RimLLMException(
                    LLMError.RateLimit,
                    $"[RimLLM] Shared API anti-abuse limit reached. " +
                    $"Cooling down until {_globalCoolDownUntil.ToLocalTime()}.");
            }

            _globalRequestTimestamps.Add(now);
        }

        if (!countTowardWindow) return;

        var list = _requestTimestamps.GetOrAdd(modId, _ => []);
        lock (list)
        {
            DateTime limit = now.AddSeconds(-_settings.ThrottlingWindowSeconds);
            list.RemoveAll(t => t < limit);
            list.Add(now);

            if (list.Count > _settings.MaxRequestsPerWindow)
            {
                DateTime cdUntil = now.AddSeconds(_settings.CoolDownDurationSeconds);
                _coolDownUntil[modId] = cdUntil;
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning($"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                }
                throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
            }
        }
    }

    private int GetGlobalMaxRequests()
    {
        long perModLimit = Math.Max(1, _settings.MaxRequestsPerWindow);
        long globalLimit = perModLimit * GlobalRequestLimitMultiplier;
        return globalLimit > int.MaxValue ? int.MaxValue : (int)globalLimit;
    }

    public void ClearCooldowns()
    {
        _requestTimestamps.Clear();
        _coolDownUntil.Clear();
        lock (_globalLock)
        {
            _globalRequestTimestamps.Clear();
            _globalCoolDownUntil = DateTime.MinValue;
        }
    }
}
