# ModCompatChecker 編譯期參考

本目錄包含 **Mod 兼容性檢查器 (Mod Compatibility Checker)**（`modcompatchecker.main`，作者 秋羽雪绪）
的程式組件副本，僅供 RimLLM Framework 在編譯期間參考。

| 欄位         | 值                                         |
|-------------|---------------------------------------------|
| PackageId   | `modcompatchecker.main`                     |
| DLL 來源    | `Assemblies/ModCompatChecker.dll`           |
| 遊戲版本    | 1.6                                         |
| Steam Workshop ID | 3737125696                           |

## 注意事項

* **不隨包出貨**：`RimLLM Framework.csproj` 中以 `Private=false` 參考，Release 組建不會將此 DLL 複製到 `Assemblies/`。
* **執行期依賴**：遊戲啟動時繫結的是玩家實際安裝的 `ModCompatChecker.dll`；若玩家未安裝該 Mod，相容層攔截不會被套用。
* **型別隔離**：框架內部絕不在類別繼承、介面或欄位宣告中使用 ModCompatChecker 的型別，防止 `TypeLoadException`。

此參考是第三方舊版的編譯契約，不隨 .NET 10 正式版出貨。本機未提供該模組的 1.7 版本；保留轉接器與型別隔離，但不代表已完成該目標的 1.7 遊戲內驗證。
