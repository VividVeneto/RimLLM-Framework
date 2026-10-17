# SmashPhil DevTools 編譯期參考組件

`DevTools.dll` 取自本機 RimWorld 1.7 SmashPhil DevTools 模組組建（1.7 Assemblies，組件版本 1.0.0.0）。

**僅供遊戲內測試專案（InGameTests）編譯與測試伴隨模組使用，不隨正式 Mod 出貨**。
在 `RimLLM Framework.InGameTests.csproj` 中引用，建置時封裝至開發伴隨模組的 `Mod/Assemblies/` 供遊戲內 DevTools 執行器載入。
正式發行之 `Assemblies/` 絕不包含此組件。

來源：本機 FasterGameLoading---Continued/.test-temp/DevTools/_mod/1.7/Assemblies/DevTools.dll；本機原始碼 checkout 為 0b33dfc222de214c45af979c8a45a74e1fb06f3。上游為 https://github.com/SmashPhil/DevTools ，MIT 授權見本目錄 LICENSE。

參考 DLL SHA-256：c8cfea26a48209631e84b34c6844e835b0fb846fc571dc54493fa858bddaeabe。
