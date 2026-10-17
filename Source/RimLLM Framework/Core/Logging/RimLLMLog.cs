using System;
using System.Text.RegularExpressions;
using Verse;

namespace RimLLM_Framework.Core;

/// <summary>
/// 日誌包裝器。提供安全且跨環境的日誌輸出。
/// 在 RimWorld 遊戲環境中，呼叫 Verse.Log 輸出至遊戲日誌與控制台；
/// 在無 Unity 引擎的單元測試環境中，自動 Fallback 使用 System.Console 輸出，防範 ECall 崩潰。
/// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
public static class RimLLMLog
{
    public static bool Enabled { get; set; } = true;
    /// <summary>
    /// 敏感字串遮罩樣式。除了通用的 api_key／authorization 賦值形式外，
    /// 另涵蓋本框架 12 家供應商實際使用的金鑰前綴，避免漏遮。
    /// </summary>
    private static readonly Regex SecretPattern = new(
        @"(?i)(" +
        // 賦值形式（含 x-api-key、x-goog-api-key 等 header 名稱）
        @"(?:x-goog-)?api[_\- ]?key\s*[:=]\s*[""']?[^""'\s,;}]+" +
        @"|x-api-key\s*[:=]\s*[""']?[^""'\s,;}]+" +
        // 需一併吃掉 "Bearer " 前綴，否則只會遮到 scheme 而讓後面的 token 露出。
        @"|authorization\s*[:=]\s*[""']?(?:Bearer\s+)?[^""'\s,;}]+" +
        @"|key=[^&\s]+" +
        // Bearer token
        @"|Bearer\s+[A-Za-z0-9\-._~+/]{16,}={0,2}" +
        // 各供應商的金鑰前綴
        @"|sk-ant-[A-Za-z0-9\-_]{16,}" +
        @"|sk-[a-z0-9_\-]{8,}" +
        @"|AIza[0-9A-Za-z\-_]{20,}" +
        @"|gsk_[A-Za-z0-9]{20,}" +
        @"|xai-[A-Za-z0-9]{20,}" +
        @"|nvapi-[A-Za-z0-9\-_]{20,}" +
        @")",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// 正則前快徑：訊息若不含任何金鑰指標子字串，正則必然無匹配，直接跳過。
    /// 此處列出的每個指標都是對應正則分支的必要子字串（大小寫不敏感），
    /// 因此跳過不會改變任何輸出，僅省下每請求一次的正則掃描。
    /// </summary>
    private static bool HasPotentialSecret(string value)
    {
        return value.IndexOf("api", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("key=", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("sk-", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("AIza", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("gsk_", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("xai-", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("nvapi-", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string SanitizeForLog(string value, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(value)) return value;

        string sanitized = HasPotentialSecret(value)
            ? SecretPattern.Replace(value, match =>
        {
            string text = match.Value;
            int idx = text.IndexOf('=');
            if (idx < 0) idx = text.IndexOf(':');
            if (idx > 0)
            {
                return text.Substring(0, idx + 1) + "[redacted]";
            }
            return "[redacted-secret]";
        })
            : value;

        sanitized = sanitized.Replace("\r", "\\r").Replace("\n", "\\n");
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength) + "...";
        }
        return sanitized;
    }

    public static void Message(string msg)
    {
        if (!Enabled) return;
        try
        {
            Log.Message(msg);
        }
        catch
        {
            Console.WriteLine($"[INFO] {msg}");
        }
    }

    /// <summary>
    /// 輸出警告。不檢查 Enabled 旗標：警告多半是一次性的診斷線索（金鑰解不開、遙測寫檔失敗、
    /// schema exporter 降級），詳細日誌預設關閉時仍必須看得到。每次請求都會觸發的警告
    /// 由呼叫端自行以 <see cref="Enabled"/> 包住。
    /// </summary>
    public static void Warning(string msg)
    {
        try
        {
            Log.Warning(msg);
        }
        catch
        {
            WriteConsole(ConsoleColor.Yellow, "WARN", msg);
        }
    }

    /// <summary>
    /// 輸出錯誤日誌。故意不檢查 Enabled 旗標，因為嚴重錯誤與異常必須始終輸出並觸發遊戲控制台彈出，以利開發者與玩家發現問題。
    /// </summary>
    public static void Error(string msg)
    {
        try
        {
            Log.Error(msg);
        }
        catch
        {
            WriteConsole(ConsoleColor.Red, "ERROR", msg);
        }
    }

    /// <summary>
    /// 無 Unity 引擎時的控制台輸出。還原前景色的責任集中在此，
    /// 避免每個等級各自 set / restore 而漏掉還原。
    /// </summary>
    private static void WriteConsole(ConsoleColor color, string level, string msg)
    {
        ConsoleColor original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        try
        {
            Console.WriteLine($"[{level}] {msg}");
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }
#pragma warning restore S101
}
