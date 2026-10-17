# RimTalk 編譯期參考組件

`RimTalk.dll` 取自 Steam 工作坊 RimTalk（`cj.rimtalk`，工作坊 ID 3551203752）1.6 版，Mod 版本 1.2.12。

**僅供編譯期參考，不隨 Mod 出貨**（csproj 中 `Private=false`）。執行期一律使用玩家實際安裝的 RimTalk；
`Compat/RimTalk/` 下的程式碼只在偵測到 RimTalk 啟用時才會被 JIT，RimTalk 缺席時不會觸發型別載入。
**RimTalk 型別只能出現在實例方法簽章與方法本體**，不得當任何類別的基底或介面——那是 `Assembly.GetTypes()`
就會解析的層級，RimTalk 缺席時整顆框架 DLL 會被 RimWorld 拒載（`FrameworkAssembly_HasNoTypeLevelRimTalkReferences` 測試鎖住）。另外**靜態方法**的簽章也不得含目標 Mod 型別：Debug 動作選單會對所有靜態方法做 `IsDefined`，執行期因此解析簽章，目標 Mod 缺席時選單會拋 `TypeLoadException`（改用 `object` 並於方法本體轉型，或改成實例方法；`FrameworkAssembly_HasNoCompatTargetTypesInMethodSignatures` 測試鎖住）。

RimTalk 更新且 `IAIClient` / `Payload` / `JsonStreamParser` 簽名變動時，換掉這顆 DLL 並修正轉接器。

此參考是第三方舊版的編譯契約，不隨 .NET 10 正式版出貨。本機未提供該模組的 1.7 版本；保留轉接器與型別隔離，但不代表已完成該目標的 1.7 遊戲內驗證。
