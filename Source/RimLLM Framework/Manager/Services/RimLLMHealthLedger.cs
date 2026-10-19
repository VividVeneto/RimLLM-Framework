using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 管理呼叫目標的健康帳本（Health Ledger），
/// 包含呼叫成果記錄、連續失敗退避熔斷、單次失敗暫時冷卻與延遲統計。
/// </summary>
/// <remarks>
/// 帳本的鍵由呼叫端決定粒度。<see cref="RimLLMFallbackPipeline"/> 傳入的是
/// 「供應商:模型」，因為同一個供應商底下常有多個模型同時掛在備用鏈上
/// （例如三個 OpenRouter 模型），若以供應商為單位，其中一個模型出錯就會
/// 把另外兩個健康的模型一起連坐冷卻。
/// </remarks>
public class RimLLMHealthLedger
{
    /// <summary>連續失敗達此門檻後改用指數退避熔斷。</summary>
    private const int CircuitFailureThreshold = 3;

    /// <summary>熔斷的基礎冷卻秒數，之後每多失敗一次翻倍。</summary>
    private const double CircuitBaseCooldownSeconds = 60.0;

    /// <summary>指數退避的最大倍率次方（60s → 960s 封頂）。</summary>
    private const int MaxCircuitBackoffSteps = 4;

    /// <summary>
    /// 未達熔斷門檻時的短暫冷卻秒數。
    /// 呼叫端已把「一次請求的所有重試」合併成一次失敗記錄，所以這裡的一次失敗
    /// 代表整個請求連重試都打不通；但那仍可能只是一次網路抖動，冷卻太久會讓
    /// 健康的目標平白消失數分鐘，因此只擋下緊接著的幾次呼叫。
    /// </summary>
    private const double TransientCooldownSeconds = 15.0;

    private sealed class ProviderHealthRecord
    {
        public int ContinuousFailures;
        public DateTime CooldownUntil = DateTime.MinValue;
        public readonly List<long> Latencies = [];
    }

    private readonly ConcurrentDictionary<string, ProviderHealthRecord> _ledger =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// 記錄一次成功的呼叫，重設連續失敗次數、解除冷卻並更新延遲。
    /// </summary>
    /// <param name="target">帳本鍵，粒度由呼叫端決定（見類別說明）。</param>
    /// <param name="elapsedMs">本次呼叫耗時（毫秒），0 表示不記錄延遲。</param>
    public void RecordSuccess(string target, long elapsedMs = 0)
    {
        if (string.IsNullOrEmpty(target)) return;

        var record = _ledger.GetOrAdd(target, _ => new ProviderHealthRecord());
        lock (_lock)
        {
            record.ContinuousFailures = 0;
            record.CooldownUntil = DateTime.MinValue;
            if (elapsedMs > 0)
            {
                record.Latencies.Add(elapsedMs);
                if (record.Latencies.Count > 5)
                {
                    record.Latencies.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>
    /// 記錄一次失敗的呼叫。
    /// 若為可重試故障，連續失敗達 3 次以上則觸發指數退避熔斷（60s, 120s, 240s, 480s, 960s）；
    /// 未達門檻則給予 15 秒暫時冷卻以利備援轉移。
    /// </summary>
    /// <param name="target">帳本鍵，粒度由呼叫端決定（見類別說明）。</param>
    /// <param name="isRetryable">非可重試故障（如金鑰無效、內容過濾）不代表目標不健康，不計入。</param>
    public void RecordFailure(string target, bool isRetryable = true)
    {
        if (string.IsNullOrEmpty(target) || !isRetryable) return;

        var record = _ledger.GetOrAdd(target, _ => new ProviderHealthRecord());
        lock (_lock)
        {
            record.ContinuousFailures++;
            double cooldownSeconds;
            if (record.ContinuousFailures >= CircuitFailureThreshold)
            {
                int power = record.ContinuousFailures - CircuitFailureThreshold;
                cooldownSeconds = CircuitBaseCooldownSeconds * Math.Pow(2, Math.Min(power, MaxCircuitBackoffSteps));
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning($"[RimLLM] Target {target} has failed {record.ContinuousFailures} times continuously. Circuit cooldown set for {cooldownSeconds} seconds.");
                }
            }
            else
            {
                cooldownSeconds = TransientCooldownSeconds;
            }

            DateTime newCooldown = DateTime.UtcNow.AddSeconds(cooldownSeconds);
            if (newCooldown > record.CooldownUntil)
            {
                record.CooldownUntil = newCooldown;
            }
        }
    }

    /// <summary>
    /// 檢查指定目標目前是否處於冷卻或熔斷狀態中。
    /// </summary>
    public bool IsInCooldown(string target, out DateTime cooldownUntil, out int continuousFailures)
    {
        return IsInCooldown(target, DateTime.UtcNow, out cooldownUntil, out continuousFailures);
    }

    /// <summary>
    /// 以呼叫端傳入的同一時間戳檢查冷卻，供候選過濾逐個比對時共用，
    /// 避免每個候選各取一次 <see cref="DateTime.UtcNow"/>。語意與無參版本一致。
    /// </summary>
    internal bool IsInCooldown(string target, DateTime now, out DateTime cooldownUntil, out int continuousFailures)
    {
        cooldownUntil = DateTime.MinValue;
        continuousFailures = 0;

        if (!string.IsNullOrEmpty(target) && _ledger.TryGetValue(target, out var record))
        {
            lock (_lock)
            {
                cooldownUntil = record.CooldownUntil;
                continuousFailures = record.ContinuousFailures;
                if (record.CooldownUntil > now)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 簡化版冷卻檢查。
    /// </summary>
    public bool IsInCooldown(string target)
    {
        return IsInCooldown(target, out _, out _);
    }

    /// <summary>
    /// 以共用時間戳的簡化版冷卻檢查。
    /// </summary>
    internal bool IsInCooldown(string target, DateTime now)
    {
        return IsInCooldown(target, now, out _, out _);
    }


    /// <summary>
    /// 取得目標的平均延遲（毫秒），無記錄時回傳 0。
    /// 只有成功的呼叫會記錄延遲，失敗不記錄。
    /// </summary>
    public float GetAverageLatency(string target)
    {
        if (!string.IsNullOrEmpty(target) && _ledger.TryGetValue(target, out var record))
        {
            lock (_lock)
            {
                if (record.Latencies.Count > 0)
                {
                    long sum = 0;
                    foreach (long lat in record.Latencies) sum += lat;
                    return (float)sum / record.Latencies.Count;
                }
            }
        }
        return 0f;
    }

    /// <summary>
    /// 清除所有健康與冷卻記錄。
    /// </summary>
    public void Clear()
    {
        _ledger.Clear();
    }
}
