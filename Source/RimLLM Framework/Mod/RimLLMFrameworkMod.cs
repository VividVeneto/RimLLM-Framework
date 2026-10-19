using System;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// RimLLM Framework Mod 本體進入點。
/// 初始化 SDK、掛載 Dispatcher 並委託設定 GUI 的渲染。
/// </summary>
public class RimLLMFrameworkMod : Verse.Mod
{
    /// <summary>
    /// 全域設定檔實例。
    /// </summary>
    internal static RimLLMFrameworkSettings Settings { get; private set; }

    public RimLLMFrameworkMod(ModContentPack content) : base(content)
    {
        // 1. 載入並儲存 Settings 實體
        Settings = GetSettings<RimLLMFrameworkSettings>();

        // 2. 註冊 SDK 服務管理器到 Provider 入口 (依賴注入 Settings)
        RimLLMProvider.Initialize(new RimLLMManager(Settings));

        // 3. 於主線程建立 Unity 主線程派遣器 (RimLLMDispatcher)。
        //    背景執行緒不得建立 Unity GameObject，因此建立時機必須固定在此。
        RimLLMDispatcher.EnsureInitialized();

        // 4. 初始化 UI 狀態
        RimLLMSettingsUI.Initialize(Settings);

        // 5. 註冊關閉時的遙測強制寫入。
        //    用量統計採 15 秒節流，若沒有這步，session 最後一段用量與費用會遺失。
        //    RimWorld 沒有保證的關閉回呼，因此兩個掛勾互補註冊。
        RegisterShutdownFlush();

        Log.Message("[RimLLM] RimLLM Framework 載入成功。");
    }

    private static void RegisterShutdownFlush()
    {
        Application.quitting += FlushTelemetryQuietly;
        AppDomain.CurrentDomain.ProcessExit += (sender, args) => FlushTelemetryQuietly();
    }

    private static void FlushTelemetryQuietly()
    {
        try
        {
            Settings?.FlushTelemetryIfDirty();
        }
        catch (Exception ex)
        {
            // 關閉期間 GenFilePaths 等 Verse 服務可能已不可用，不得讓例外中斷關機流程。
            try { Log.Warning($"[RimLLM] 關閉時寫入遙測失敗: {ex.Message}"); } catch { }
        }
    }

    public override string SettingsCategory()
    {
        return "RimLLM Framework";
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        base.DoSettingsWindowContents(inRect);

        // 委託給獨立的 UI 渲染類別
        RimLLMSettingsUI.DoSettingsWindowContents(inRect);
    }
}
