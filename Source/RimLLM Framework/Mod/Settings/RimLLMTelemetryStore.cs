using System;
using System.Collections.Generic;
using System.IO;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 遙測資料儲存（對話測試歷史、請求日誌、Token 用量統計）。
/// 與 ModSettings 設定本體分離，獨立存放於 Config 資料夾的 JSON 檔案，
/// 避免高頻變動的遙測資料讓設定 XML 膨脹並拖慢設定讀寫。
/// 對話歷史含使用者輸入與模型完整回覆，因此以 AES 加密後才落地。
/// </summary>
public class RimLLMTelemetryStore
{
    private const string FileName = "RimLLM_Telemetry.json";
    private const int MaxChatHistoryEntries = 100;

    private readonly object _ioLock = new();
    private string _undecryptableEncryptedChatHistory;
    private bool _preserveBackupOnSave;

    /// <summary>
    /// 檔案路徑解析器。抽為可替換的委派，讓單元測試能指向暫存目錄，
    /// 不必依賴 Verse 的 GenFilePaths。
    /// </summary>
    internal static Func<string> FilePathResolver =
        () => Path.Combine(GenFilePaths.ConfigFolderPath, FileName);

    public List<string> ChatHistory { get; set; } = [];
    public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = [];
    public long TotalPromptTokens { get; set; }
    public long TotalCompletionTokens { get; set; }
    public float TotalEstimatedCost { get; set; }
    public float DailyAccumulatedCost { get; set; }
    public long DailyAccumulatedTokens { get; set; }
    public string DailyBudgetResetDate { get; set; } = "";

    /// <summary>
    /// 磁碟上是否已存在遙測檔案。用於判斷是否需要從舊版設定 XML 遷移。
    /// </summary>
    public bool LoadedFromDisk { get; private set; }

    /// <summary>
    /// 是否有尚未寫入磁碟的變更。用量統計採節流寫入，
    /// 關閉遊戲時需依此判斷是否強制 flush，避免遺失最後一段用量。
    /// </summary>
    public bool IsDirty { get; private set; }

    private class TelemetryDto
    {
        /// <summary>加密後的對話歷史（JSON 陣列序列化後再以 AES 加密）。</summary>
        public string EncryptedChatHistory;

        /// <summary>舊版的明文對話歷史，僅供一次性遷移讀取，不再寫入。</summary>
        public List<string> ChatHistory;

        public List<RimLLMManager.RequestLogEntry> RequestLogs;
        public long TotalPromptTokens;
        public long TotalCompletionTokens;
        public float TotalEstimatedCost;
        public float DailyAccumulatedCost;
        public long DailyAccumulatedTokens;
        public string DailyBudgetResetDate;
    }

    private static string GetFilePath()
    {
        return FilePathResolver();
    }

    /// <summary>
    /// 標記有未寫入的變更（供節流寫入路徑呼叫）。
    /// </summary>
    public void MarkDirty()
    {
        IsDirty = true;
    }

    /// <summary>
    /// 清空記憶體中的對話歷史，並同時捨棄解密失敗時保留的原始密文。
    /// </summary>
    public void ClearChatHistory()
    {
        lock (_ioLock)
        {
            if (ChatHistory != null)
            {
                ChatHistory.Clear();
            }

            _undecryptableEncryptedChatHistory = null;
            _preserveBackupOnSave = false;
            IsDirty = true;
        }
    }

    /// <summary>
    /// 從磁碟載入遙測資料。檔案不存在或格式錯誤時保留預設空值；
    /// 主檔格式錯誤或加密歷史無法解密時會嘗試從 .bak 備份還原。
    /// </summary>
    public void Load()
    {
        lock (_ioLock)
        {
            string path;
            try
            {
                path = GetFilePath();
            }
            catch (Exception ex)
            {
                // 非 Unity 環境（單元測試、headless 反射執行）取不到 Config 路徑。
                RimLLMLog.Warning($"[RimLLM] Telemetry file path unavailable; skipping load: {ex.Message}");
                return;
            }

            string backupPath = path + ".bak";
            var primary = new RimLLMTelemetryStore();
            if (primary.TryLoadFrom(path, out bool needsRewrite))
            {
                if (primary._undecryptableEncryptedChatHistory != null)
                {
                    var backup = new RimLLMTelemetryStore();
                    if (backup.TryLoadFrom(backupPath, out _) &&
                        backup._undecryptableEncryptedChatHistory == null)
                    {
                        bool restored = TryRestoreFromBackup(backupPath, path);
                        CopyDataFrom(backup);
                        _preserveBackupOnSave = !restored;
                        LoadedFromDisk = true;
                        IsDirty = true;
                        RimLLMLog.Warning(restored
                            ? "[RimLLM] Telemetry chat history could not be decrypted; restored from the .bak backup."
                            : "[RimLLM] Telemetry chat history could not be decrypted; loaded the .bak backup in memory while preserving it on disk.");
                        return;
                    }

                    // 若主檔與備份的密文都無法解開，後續保存不可用主檔覆寫唯一備份。
                    if (backup._undecryptableEncryptedChatHistory != null)
                    {
                        primary._preserveBackupOnSave = true;
                    }
                }

                CopyDataFrom(primary);
                LoadedFromDisk = true;
                // 讀到的是舊版明文歷史，標記為待重寫以完成加密遷移。
                if (needsRewrite) IsDirty = true;
                return;
            }

            var recovered = new RimLLMTelemetryStore();
            if (recovered.TryLoadFrom(backupPath, out _))
            {
                bool restored = TryRestoreFromBackup(backupPath, path);
                CopyDataFrom(recovered);
                _preserveBackupOnSave = !restored;
                LoadedFromDisk = true;
                IsDirty = true;
                RimLLMLog.Warning(restored
                    ? "[RimLLM] Telemetry file could not be parsed; restored from the .bak backup."
                    : "[RimLLM] Telemetry file could not be parsed; loaded the .bak backup in memory while preserving it on disk.");
            }
        }
    }

    private void CopyDataFrom(RimLLMTelemetryStore source)
    {
        ChatHistory = source.ChatHistory;
        RequestLogs = source.RequestLogs;
        TotalPromptTokens = source.TotalPromptTokens;
        TotalCompletionTokens = source.TotalCompletionTokens;
        TotalEstimatedCost = source.TotalEstimatedCost;
        DailyAccumulatedCost = source.DailyAccumulatedCost;
        DailyAccumulatedTokens = source.DailyAccumulatedTokens;
        DailyBudgetResetDate = source.DailyBudgetResetDate;
        _undecryptableEncryptedChatHistory = source._undecryptableEncryptedChatHistory;
        _preserveBackupOnSave = source._preserveBackupOnSave;
    }

    private bool TryLoadFrom(string path, out bool needsRewrite)
    {
        needsRewrite = false;
        try
        {
            if (!File.Exists(path)) return false;

            var dto = RimLLMJsonHelper.Deserialize<TelemetryDto>(File.ReadAllText(path));
            if (dto == null) return false;

            ChatHistory = ReadChatHistory(
                dto,
                out needsRewrite,
                out _undecryptableEncryptedChatHistory);
            TrimChatHistory();
            RequestLogs = dto.RequestLogs ?? [];
            TotalPromptTokens = dto.TotalPromptTokens;
            TotalCompletionTokens = dto.TotalCompletionTokens;
            TotalEstimatedCost = dto.TotalEstimatedCost;
            DailyAccumulatedCost = dto.DailyAccumulatedCost;
            DailyAccumulatedTokens = dto.DailyAccumulatedTokens;
            DailyBudgetResetDate = dto.DailyBudgetResetDate ?? "";
            return true;
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Failed to load telemetry ({Path.GetFileName(path)}): {ex.Message}");
            return false;
        }
    }

    private static List<string> ReadChatHistory(
        TelemetryDto dto,
        out bool needsRewrite,
        out string undecryptableEncryptedChatHistory)
    {
        needsRewrite = false;
        undecryptableEncryptedChatHistory = null;

        if (!string.IsNullOrEmpty(dto.EncryptedChatHistory))
        {
            string plain = EncryptionUtility.Decrypt(dto.EncryptedChatHistory);
            if (plain == null)
            {
                // 無法取得原使用者的受保護 key 時，以空歷史起始，但保留密文，
                // 避免本次程序的其他遙測變更將歷史永久覆寫掉。
                undecryptableEncryptedChatHistory = dto.EncryptedChatHistory;
                RimLLMLog.Warning("[RimLLM] Chat history could not be decrypted (device or user may have changed); starting with an empty history and keeping the original ciphertext.");
                return [];
            }

            try
            {
                return RimLLMJsonHelper.Deserialize<List<string>>(plain) ?? [];
            }
            catch
            {
                // 明文格式損毀時同樣保留原始密文，避免把未知資料靜默改成空值。
                undecryptableEncryptedChatHistory = dto.EncryptedChatHistory;
                return [];
            }
        }

        // 舊版明文欄位：讀入後標記待重寫，下次 Save 即完成加密遷移。
        if (dto.ChatHistory != null && dto.ChatHistory.Count > 0)
        {
            needsRewrite = true;
            return dto.ChatHistory;
        }

        return [];
    }

    /// <summary>
    /// 將遙測資料寫入磁碟。
    /// 採「暫存檔 → 原子替換」避免程序中斷造成檔案截斷，並保留上一份有效檔為 .bak。
    /// </summary>
    public void Save()
    {
        lock (_ioLock)
        {
            string path;
            try
            {
                path = GetFilePath();
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Telemetry file path unavailable; skipping save: {ex.Message}");
                return;
            }

            string tempPath = path + ".tmp";
            string backupPath = path + ".bak";

            try
            {
                TrimChatHistory();

                string encryptedHistory = null;
                if (ChatHistory != null && ChatHistory.Count > 0)
                {
                    encryptedHistory = EncryptionUtility.Encrypt(
                        RimLLMJsonHelper.Serialize(ChatHistory));
                }
                else
                {
                    // 解密失敗時不能以 null 覆寫原始歷史；若使用者新增內容，上方的新密文則優先。
                    encryptedHistory = _undecryptableEncryptedChatHistory;
                }

                var dto = new TelemetryDto
                {
                    EncryptedChatHistory = encryptedHistory,
                    // 明文欄位明確寫 null，讓舊版殘留的明文歷史在首次存檔後即被清除。
                    ChatHistory = null,
                    RequestLogs = RequestLogs,
                    TotalPromptTokens = TotalPromptTokens,
                    TotalCompletionTokens = TotalCompletionTokens,
                    TotalEstimatedCost = TotalEstimatedCost,
                    DailyAccumulatedCost = DailyAccumulatedCost,
                    DailyAccumulatedTokens = DailyAccumulatedTokens,
                    DailyBudgetResetDate = DailyBudgetResetDate
                };

                File.WriteAllText(tempPath, RimLLMJsonHelper.Serialize(dto));
                ReplaceAtomically(tempPath, path, _preserveBackupOnSave ? null : backupPath);

                LoadedFromDisk = true;
                IsDirty = false;
                if (ChatHistory != null && ChatHistory.Count > 0)
                {
                    _undecryptableEncryptedChatHistory = null;
                }
                if (_undecryptableEncryptedChatHistory == null)
                {
                    _preserveBackupOnSave = false;
                }
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Failed to write telemetry: {ex.Message}");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void ReplaceAtomically(string tempPath, string path, string backupPath)
    {
        if (!File.Exists(path))
        {
            File.Move(tempPath, path);
            return;
        }

        try
        {
            File.Replace(tempPath, path, backupPath);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException)
        {
            if (backupPath == null)
            {
                // 檔案系統不支援原子替換時，復原流程仍必須保留已知有效的 .bak。
                string displacedPath = path + ".replace.old";
                try { if (File.Exists(displacedPath)) File.Delete(displacedPath); } catch { }
                File.Move(path, displacedPath);
                try
                {
                    File.Move(tempPath, path);
                }
                catch
                {
                    try
                    {
                        if (!File.Exists(path) && File.Exists(displacedPath)) File.Move(displacedPath, path);
                    }
                    catch { }
                    throw;
                }
                try { File.Delete(displacedPath); } catch { }
                return;
            }

            // 部分檔案系統不支援 File.Replace，退回「先備份再覆寫」。
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
            try { File.Move(path, backupPath); } catch { }
            File.Move(tempPath, path);
        }
    }

    private static bool TryRestoreFromBackup(string backupPath, string path)
    {
        string restorePath = path + ".restore.tmp";
        try
        {
            File.Copy(backupPath, restorePath, true);
            ReplaceAtomically(restorePath, path, null);
            return true;
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Failed to restore telemetry from .bak; preserving the backup: {ex.Message}");
            try { if (File.Exists(restorePath)) File.Delete(restorePath); } catch { }
            return false;
        }
    }

    private void TrimChatHistory()
    {
        // 限制大小在 100 條內，防遙測 JSON 無限膨脹
        if (ChatHistory != null && ChatHistory.Count > MaxChatHistoryEntries)
        {
            ChatHistory.RemoveRange(0, ChatHistory.Count - MaxChatHistoryEntries);
        }
    }
}
