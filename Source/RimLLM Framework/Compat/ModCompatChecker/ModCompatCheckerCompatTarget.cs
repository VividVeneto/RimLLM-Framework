using HarmonyLib;

namespace RimLLM_Framework.Compat;

/// <summary>
/// Mod 兼容性檢查器相容層常數。實際 Target 實作已併入 <see cref="DelegateCompatTarget"/>。
/// 保留此檔僅為向後相容：測試與相容層程式碼仍引用 PackageId 常數。
/// </summary>
internal static class ModCompatCheckerCompatTarget
{
    public const string PackageId = "modcompatchecker.main";
}
