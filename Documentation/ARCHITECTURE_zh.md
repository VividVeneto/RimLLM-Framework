# RimLLM Framework —— 架構、安全性與測試

[← 回到 README](../README_zh.md) · [English](ARCHITECTURE.md)

## 🛠️ 架構設計

### 1. 統一介面與調度核心（`IChatClient` / `IEmbeddingGenerator` 與 `RimLLMProvider`）

* 框架對外暴露標準的 Microsoft.Extensions.AI 介面。呼叫端只面對 `IChatClient` 或 `IEmbeddingGenerator`，完全不需要知道實際由哪個供應商或模型處理 —— 調度與 Fallback 輪替由 `RimLLMManager` 負責。
* `CreateChatClient` 回傳的是一疊 MEAI `DelegatingChatClient` 中介層——思考強度正規化、防濫用節流、預算保護、優先權佇列——最內層是沿 Fallback 鏈路由的 `FailoverChatClient`。每一層都是 `internal`。使用端會碰到的框架專屬型別只有 `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException` 與 `LLMError`，其餘跨越邊界的全是 MEAI 型別。
* `modId` 是純標籤，不是憑證。它是每個 Mod 防濫用節流與遙測歸屬的鍵，不需要任何註冊呼叫。
* 對話與 Embedding 門面在送出計費請求前檢查同一份用量帳本，HardBlock、WarnOnly 與每日重置在兩條路徑上的意義相同。對話訊息在守衛入口只物化一次，判斷工具續輪時不會先耗盡只能列舉一次的序列。

### 2. Unity 主執行緒派送器（`RimLLMDispatcher`）

* 網路請求在背景執行緒池上非同步執行，但多數 Unity API 與 RimWorld 邏輯並非執行緒安全 —— 從背景執行緒呼叫會造成崩潰或 TPS 掉幀。
* `RimLLMDispatcher` 是 MonoBehaviour 單例，以 `ConcurrentQueue` 收集背景執行緒的回呼，並在 Unity 每幀的 `Update` 中派送回主執行緒。
* 排隊中的工具會在叫用前檢查取消，等待端可在工具尚未出列時取消等待；工具開始後則由工具本身配合取消。對話測試回呼攜帶請求世代，並在執行回呼時檢查；停止或清空會先使舊更新失效，避免歷史索引被新請求重用後遭到覆寫。

### 3. 串流橋接（`Channel<T>`）

* Executor 的串流 API 是回呼形式（`Func<ChatResponseUpdate, Task> onUpdateReceived`），而 MEAI 要的是 `IAsyncEnumerable<ChatResponseUpdate>`。兩者之間以**有界**的 `System.Threading.Channels.Channel<T>`（64 筆 update）橋接，生產端 await `WriteAsync`，因此消費端慢時會把背壓回推到網路讀取，而不是把整段串流緩衝在記憶體。消費端是 `ChannelReader.ReadAllAsync()`。
* update 以**原樣**穿過這座橋——你列舉到的就是供應商產生的那個物件，只有 `ModelId` 被改寫。框架不再自行合成一個收尾 update，因此 `UsageContent`、`FinishReason` 與 `ResponseId` 只在供應商真的送出時才存在。
* `IAsyncEnumerable` 與 `ValueTask` 直接使用 .NET 10 的標準型別，不再使用 BCL 相容套件或 extern alias。
* 一層薄包裝會解開 `ChannelClosedException`，讓生產端的失敗以原始的 `RimLLMException` 呈現給呼叫端。
* Executor 的 fallback token 估算逐塊累加成計數，不暫存串流文字，異常大的串流不會讓記憶體無限成長。
* 串流已有輸出或回報用量時，正常與異常結束都只結算一次。取消、閒置逾時及斷線會保留原本錯誤語意，同時保留已觀察到的消耗；尚未輸出或回報用量就失敗的串流，不會捏造一筆成功用量。

### 4. 統一的 HTTP 錯誤對照（`LLMErrorMapper`）

* HTTP 狀態碼轉換為 `LLMError` 的規則集中在 `LLMErrorMapper` 一處，由官方 SDK 路徑（`ClientResultException`）與 embedding 服務共用。
* `Retry-After` 的解析也在其中，基於 `RetryConditionHeaderValue`，因此延遲秒數與 HTTP 日期兩種格式在各處行為一致。
* 這使得「哪些狀態碼可重試」與「哪些代表 Schema 遭拒應降級」在各處行為完全相同。第三方自訂供應商也能引用同一份對照。

### 5. 容錯的結構化輸出（structured output 與 JSON repair）

* 開發者經常需要模型回傳特定的 JSON 結構。
* 內建供應商優先使用官方 SDK 的原生結構化輸出，經 `IChatClient` 的 JSON Schema response format 送出（Gemini 經其 OpenAI 相容端點同樣適用）。框架依序列化契約產生的 Schema 形狀檢查 JSON 必填成員是否存在與可空性，包含字典的值，不以反序列化後的 CLR 預設值推斷完整性；忽略或未參與契約的成員不視為必要輸出。
* schema 本身只有一種 OpenAI 相容方言 —— 所有成員都列入 `required`，選填性以 `["integer","null"]` 聯集表達。詳見[架構設計 §6](#6-官方-sdk-與供應商職責)。
* `RepairJson` 回退機制僅在供應商不支援原生 Schema、服務拒絕 Schema，或模型仍回傳格式錯誤內容時啟用。它處理 Markdown 圍籬（如 ` ```json `）、未閉合括號、尾隨逗號與 JSON 區塊擷取。修復時區分 JSON 語法與引號內的字串，保留字串中的逗號、括號及 `<think>…</think>` 原文。

### 6. 官方 SDK 與供應商職責

* **所有供應商共用一套 SDK。** 內建供應商全都走 `OpenAI` SDK `2.14.0` 與 Microsoft.Extensions.AI `10.10.0` 的 `IChatClient`，沒有直接打 HTTP 的對話路徑，也沒有第二套 SDK。Gemini 走 Google 官方的 OpenAI 相容端點。共用層只依賴 `IChatClient`、`LLMProviderCapabilities` 與 `ILLMProvider`。
* **僅支援 RimWorld 1.7／.NET 10。** 三個專案統一使用 `net10.0`，SDK 與 MEAI 透過正常 NuGet 資產選擇載入。
* **MEAI.Abstractions 使用 .NET 10 資產。** 不再強制 netstandard 資產或隨包附帶舊 BCL；`ShippedAssembliesTargetNet10` 驗證正式產物。
* **結構化輸出的 schema** 由 `System.Text.Json` 的 `JsonSchemaExporter` 產生，再由 `RimLLMSchemaBuilder` 正規化成每家供應商都接受的子集：`$ref` 展開成內嵌（循環在 CLR 型別層級截斷）、巢狀最多 5 層（OpenAI strict 模式的上限；總共 100 個屬性的上限尚未檢查）、所有成員都列入 `required`，選填性以 `["type","null"]` 聯集表達，並保留 `[Description]`。Nullable 值型別作為集合元素或字典值時，也會保留相同的可空聯集。
* **結構化輸出型別的規則**：schema 產生與反序列化共用同一份 System.Text.Json 契約，因此不要在這些型別上掛自訂 `JsonConverter`。從 Newtonsoft 遷移的 Mod 必須把 `[JsonProperty("x")]` 改成 `[JsonPropertyName("x")]`，並改用 System.Text.Json 的 `[JsonIgnore]`；舊屬性會被無聲忽略。

---

## 🔐 安全性說明

為避免誤解，以下誠實說明每項安全機制實際防護的範圍：

* **API 金鑰加密使用 OS 每使用者保護金鑰。** 新資料使用 AES-256 與隨機儲存金鑰，該金鑰由目前使用者的 OS 保護機制包裝。僅支援 `v3:` 密文，不再保留固定種子或裝置衍生的舊格式解密路徑。這能防止其他使用者或其他機器直接解密複製的設定，但**無法**防禦以相同使用者身分或在同一 RimWorld 行程內執行的程式碼（包含其他 Mod），因為框架必須使用金鑰才能提供請求服務。
* **`modId` 是歸屬標籤，不是身份驗證。** 每 Mod 節流仍用來維持合作型 Mod 之間的公平性；另外加上「每 Mod 視窗上限十倍」的共享安全上限，每個實際供應商呼叫（包含工具迴圈續輪）都會計入，避免輪換標籤建立無限供應商請求。同一個 RimWorld 行程內的 Mod 仍沒有隔離，請勿把 SDK 當成惡意 Mod 沙盒。
* **金鑰不會進入請求 URL。** 所有供應商都以 HTTP Header 傳遞金鑰。診斷預覽經過 `SanitizeForLog` 並限制長度，涵蓋已知金鑰前綴，以及 Header、URL 和帶引號 JSON 欄位中的金鑰／授權賦值；這是文字遮罩，不能保證辨識第三方文字裡所有任意形式的秘密。診斷匯出中的裝置識別碼也會遮罩。
* **重新整理模型清單時也會連到 models.dev。** 除了供應商本身，重新整理時還會下載公開的 `https://models.dev/api.json` 來補齊上下文上限。這是一般的 GET 請求，不帶 API 金鑰或任何玩家資料；下載失敗時重新整理照常成功，只記一筆警告。
* **供應商輸出視為不受信任的 UI 輸入。** ChatTest 只保留框架自行產生的灰色思考色彩標籤；供應商回應中的 raw HTML 標籤會以文字顯示，再交給 Markdown/Unity IMGUI。

---


儲存保護依平台選擇：Windows 以目前使用者的 DPAPI 包裝隨機 256-bit 金鑰；macOS 經 Security.framework 的 SecItem API 存入 generic-password 項目；Linux 經 libsecret 使用工作階段 Secret Service。macOS／Linux 的 `RimLLM_EncryptionKey.dat` 只保存隨機 `keyring:` 項目識別碼，不包含儲存金鑰。相同使用者的鑰匙圈內移動引用檔仍可讀取；只將設定複製給其他使用者或機器，不會一併取得秘密。

Linux 需要 `libsecret-1.so.0`、工作階段 D-Bus 與已解鎖的 Secret Service 提供者，例如 GNOME Keyring 或相容的 KWallet 設定；若遊戲受沙箱限制，必須允許存取該服務。libsecret 操作超過十秒會取消。macOS 需要可用的登入鑰匙圈，並允許 RimWorld 存取對應項目。服務缺席、鎖定或拒絕存取時會明確失敗，不退回明文金鑰檔。

每個設定目錄的獨占鎖負責協調多個程序；新的 DPAPI 資料或鑰匙圈引用經過 flush 的暫存檔與原子重新命名後才發佈。已存在的引用若找不到秘密，會拒絕讀取，不會偷偷重建成不同金鑰。備份加密設定時，必須保留引用檔及其 OS 保護的秘密。`SecureKeyStoreTests` 涵蓋僅含引用的檔案、重新載入、損毀、服務不可用、秘密遺失及並行建立；另以 Linux 整合程式驗證真正的 libsecret／D-Bus／GNOME Keyring 工作階段與兩個程序。macOS 原生呼叫仍需在實機驗證。

僅支援 `v3:` 密文。無版本前綴／v1／v2 格式、硬體 salt 注入、固定種子與遷移 helper 均已移除，舊的 CustomSalt 與 InitializeKeyAndIv API 也已刪除。未知前綴會在存取金鑰儲存前回傳 `null`；目前格式解密失敗時，設定與遙測呼叫端仍保留原始密文。這些保護不能隔離以相同 OS 使用者身分或在 RimWorld 行程內執行的程式碼。

## 🧪 單元測試與驗證

專案在 `Source/RimLLM Framework.Tests`（與主專案並列的獨立專案）附有完整的單元測試套件，涵蓋 AES 加解密、模型 Fallback、JSON Schema 產生（正規化）與修復、HTTP 錯誤對照、`Retry-After` 解析、`ChatOptions` 複製、串流重試與預算控制。

審查回歸測試另涵蓋 Embedding 超額阻擋與呼叫端取消、部分串流結算、較長的 `Retry-After`、能力查詢不影響 RoundRobin 輪替、一次性訊息序列、工具排隊取消、過期聊天回呼、帶引號的日誌秘密、JSON 必填成員、字典值及 Nullable 集合 Schema。HTTP 行為以假傳輸測試，不需呼叫付費供應商。

### 建置與執行環境

使用 .NET 10 SDK。`global.json` 選擇相容的 10.0 SDK，`Source/Directory.Build.props` 統一三個專案的 `net10.0` 與 C# 14 設定。編譯參考來自**本機 RimWorld 1.7 遊戲根目錄**的 `Assembly-CSharp.dll`、`UnityStubs.dll` 與 `Waylith.dll`，不再使用舊的 `Krafs.Rimworld.Ref`。遊戲組件不隨框架散布。

預設路徑適用於放在 `RimWorld/Mods` 裡的工作樹。其他位置請設定 `-p:RimWorldDir="<遊戲根目錄>"` 或 `RIMWORLD_DIR`。Harmony 預設取自工作坊 2009463077 的 `1.7/Assemblies/0Harmony.dll`；可覆寫 `HarmonyDll`，或 `WorkshopDir`／`WORKSHOP_DIR`。參考組件缺少時，建置會提供明確錯誤。

```powershell
dotnet build "Source/RimLLM Framework.slnx"
dotnet test "Source/RimLLM Framework.Tests/RimLLM Framework.Tests.csproj"
dotnet build "Source/RimLLM Framework/RimLLM Framework.csproj" -c Release
dotnet build "Source/RimLLM Framework.InGameTests/RimLLM Framework.InGameTests.csproj"
```

只有 Release 會寫入玩家使用的 `Assemblies/`；Debug 與 NUnit 建置輸出至專案的 `bin/`。NUnit 會驗證出貨組件使用 .NET 10 資產，且不含舊 polyfill。System.Text.Json、非同步串流、ValueTask 與 Channels 等 BCL 由遊戲提供。SDK 套件改由 NuGet 正常選擇相容資產；未提供 `net10.0` 資產的套件可使用相容的較早 .NET 資產。

### 原生鑰匙圈整合驗證

有 Docker 的環境可執行 pwsh -File Tests/NativeKeyring/run-linux.ps1。啟動器將真正的安全性原始碼複製到已忽略的 TestResults，再以隔離的 .NET 10／libsecret／D-Bus／GNOME Keyring 容器執行；測試時沒有主機秘密掛載或外部網路。涵蓋 v3 加解密與重新載入、服務缺席、秘密遺失、僅保存引用及跨程序並行。在已解鎖登入鑰匙圈的 macOS 上，可執行 dotnet run --project Tests/NativeKeyring/Probe.csproj，驗證真正的 SecItem 後端並於結束後清除暫存項目。只有遊戲／日誌邊界以 stub 隔離，加解密與原生儲存都使用實作。

### C# 撰寫慣例

原始碼採用 C# 14 支援的穩定寫法：檔案範圍 namespace、目標型別建構、精確型別匹配的集合運算式、using 宣告與模式比對，並由 .editorconfig 保存慣例，供後續開發沿用。既有公開 SDK 簽章與序列化資料形狀維持相容；可取消的 Dispatcher 以新增多載提供，保留原簽章。串流佇列中介層使用帶 EnumeratorCancellation 的 async iterator 與 await foreach，由編譯器管理取消 token 合併及列舉器釋放；並行名額橫跨整段列舉，包含兩次 update 間的等待。QueuedStreamingTests 驗證延遲啟動、yield 期間持有名額、提早停止後釋放資源，以及方法與列舉端 token 不同時任一端均可取消。這些修改用於改善維護性，未量測前不宣稱效能提升。

### 遊戲內整合測試（SmashPhil DevTools）

`Source/RimLLM Framework.InGameTests` 是開發用伴隨模組，不在方案內，也不隨正式版出貨。非靜態的 `MainMenu` fixtures 驗證啟動設定／Manager 接線、Dispatcher pump、出貨 DLL 的型別載入與 Debug 動作反射、已安裝模組的相容攔截、原生工具／Schema 建立，以及語系注入。獨立單元測試維持使用 NUnit。

DevTools 嵌入伴隨模組，`DevTools.dll` 與 `RimLLM Framework.InGameTests.dll` 都從其 `Mod/Assemblies/` 載入。相依只有 Core、Harmony 與 RimLLM Framework；沒有獨立的 DevTools packageId，也不需要 Laboratory。SDK 參考與 MIT 授權放在 `Source/Libs/DevTools`；可用 `DevToolsDll` 指定其他相容的本機組建。

先儲存進度並關閉 RimWorld，再使用 PowerShell 7 執行：

```powershell
pwsh -File "Source/RimLLM Framework.InGameTests/launch-tests.ps1"
pwsh -File "Source/RimLLM Framework.InGameTests/launch-tests.ps1" -Where "class == StartupTests"
```

啟動器會建置 Release 與測試 DLL、在 `Mods/` 建立開發用 junction，再使用新的 `TestResults/devtools/<時間戳>/Config/ModsConfig.xml` 與 `-savedatafolder` 啟動遊戲，保留玩家設定與存檔。執行鎖避免多次啟動器共用伴隨模組的日誌設定。`-AdditionalMods` 可加入相容目標的 packageId，但目標必須支援 1.7，且要一併加入它自己的相依。`-SkipBuild` 僅適用於已重新建置的組件。

底層參數是 `--pid GreenMushroom.RimLLMFramework.InGameTests --test --where "namespace == RimLLM_Framework.InGameTests" --exit`。執行資料夾內包含 `Test.log`、`test-report.md`、`stdout.log` 與 `stderr.log`；DevTools 日誌位置由伴隨模組自動產生的 `Configs/UnitTestConfig.xml` 明確指定。其他啟動診斷見 Player.log。啟動器要求有案例結果，且本輪含 `Test runner finished. Result: Passed`，不能只以退出碼 0 判定通過。逾時、中斷與零案例執行都不算驗證成功。

沒有啟用任何相容目標時，兩個相容案例標記為 **Skipped**。部分缺席的目標／方法與未載入語言會記錄為 `[RimLLM.InGameTests] skipped:` 前置條件，不算額外執行案例；需要另以對應模組／語言組合驗證。玩家發行包不得包含測試伴隨模組、DevTools DLL 或自動產生的開發設定。
