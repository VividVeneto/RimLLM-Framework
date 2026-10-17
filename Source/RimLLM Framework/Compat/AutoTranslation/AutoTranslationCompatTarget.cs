using HarmonyLib;

namespace RimLLM_Framework.Compat;

/// <summary>
/// Auto Translation 相容層常數。實際 Target 實作已併入 <see cref="DelegateCompatTarget"/>。
/// 保留此檔僅為向後相容：測試與相容層程式碼仍引用 PackageId 常數。
/// </summary>
internal static class AutoTranslationCompatTarget
{
    public const string PackageId = "seohyeon.autotranslation";
}
