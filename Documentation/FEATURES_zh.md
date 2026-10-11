# RimLLM Framework —— 功能特色

[← 回到 README](../README_zh.md) · [English](FEATURES.md)

各項功能的詳細行為與設計理由。SDK 用法請見 README。

1. **多供應商支援**
   * 原生支援 Google **Gemini**、**OpenAI**、**DeepSeek**、**Groq**、**Grok (xAI)**、**Z.ai**、**OpenRouter**、**Kimi**、**MiniMax**、**Qwen**、**NVIDIA** 與 **Player2**。
   * **Player2**（https://player2.game/）預設連向本機 Player2 App（`http://127.0.0.1:4315/v1`）：開著 App 就能用，本機模式不需 API 金鑰，實際模型由 App 內的 AI Selection 決定。它沒有 `/models` 端點，快取清單固定只有一個 `player2`。改用雲端 API 時，把端點改為 `https://api.player2.game/v1` 並填入 p2Key —— 雲端模式必須填金鑰，未填時設定頁會警告。它的美元費率記為已知的 $0（本地免費；雲端以 joules 計費，joules 不列入費用估算）。
   * 支援 **OpenAI 相容 API**，可設定任何本地或第三方相容端點（LM Studio、Ollama、LocalAI、vLLM 等）。預設端點為 `http://localhost:1234/v1`，並支援 API 金鑰。
   * **Kimi**、**MiniMax**、**Qwen** 提供一鍵切換「使用中國專用端點」（預設關閉），以改善連線品質。
2. **容錯與模型 Fallback**
   * **客戶端 Fallback 鏈**：可設定由主要模型與多個精確備援模型組成的鏈。目前模型遇到逾時、速率限制（HTTP 429）或連線錯誤時，框架會無縫往下切換。UI 產生的項目為 `Provider:Model` 形式；框架仍相容只填供應商的舊項目，並使用該供應商的預設模型。
     * 「模型設置」分頁逐列列出備援鏈條目的分級、上下文上限與 **X** 刪除鈕。刪除前會先確認，並一併清除該條目的手動上下文上限（分級覆寫則在鏈上已無同名模型時才清）。
   * **OpenRouter 服務端 Fallback**：OpenRouter 的項目可以用逗號列出多個模型 —— 把 `ChatOptions.ModelId` 設為 `"OpenRouter:model-a, model-b, model-c"`（Fallback 鏈項目也接受同樣的格式）。此時供應商會改送 OpenRouter 的 `models` 陣列而非單一 `model` 欄位，把「要用哪一個」的決定交給 OpenRouter 服務端；只填一個模型名時仍送出一般的 `model`。由 `TestOpenRouterFallbackPayload` 驗證。注意設定介面是從快取模型清單一次挑一個模型來組出項目，因此這種多模型寫法來自呼叫端程式碼，而不是 Fallback 鏈編輯器。
   * `Retry-After` 在所有路徑上都支援 RFC 7231 允許的兩種格式 —— 延遲秒數與 HTTP 日期。
   * **HTTP 402 不重試**：402 代表帳戶餘額已經用完，餘額不會在退避的幾十秒內變出來，因此直接換下一個候選，不先耗光重試額度。訊息只是提到「quota」的 429（Gemini 每分鐘限流）仍照常退避重試。
   * **退避期間不占併發名額**：全域 `MaxConcurrentRequests` 的名額以每一次嘗試為單位、只在真正打 API 時持有，等待指數退避（最長 60 秒）的請求不會讓其他 Mod 的請求排在後面。
   * **重試間的指數退避**：等待時間每次翻倍（`RetryDelay × 2ⁿ`）並加上 ±20% 抖動，上限 60 秒。遇到限流還用固定間隔連打，只會再一次撞上同一面牆，把重試額度白白耗光；伺服器透過 `Retry-After` 要求更長的等待時仍以其為準。
   * **冷卻以「供應商 + 模型」為單位。**健康帳本以 `Provider:Model` 為鍵。備用鏈上常同時掛著同一個供應商的多個模型（例如三個 OpenRouter 模型），只以供應商為鍵會讓其中一個模型限流就把另外兩個健康的模型一起連坐。
   * **一次請求只記一次失敗。**同一次請求的所有重試合計只計一次失敗。逐次記錄的話，單一次網路抖動（預設設定下共 4 次嘗試）就能把健康的目標推過熔斷門檻，冤枉凍結數分鐘。
   * **路由策略**：`PriorityFailover`（依鏈順序）、`MinLatency`、`RoundRobin` 與 `LowestCost`。`LowestCost` 直接沿用框架已經依 API 費率自動判定的模型分級排序，不需要另外維護一份價格表。所有排序都是穩定的，同級的候選會保留鏈本身的順序。透過 `ChatOptions.ModelId` 指定的模型不參與排序，固定排在第一位。
   * **上下文上限**：重新整理供應商的模型清單時，會一併記下 API 回報的上下文上限 —— OpenRouter 的 `context_length`、Groq 的 `context_window`、vLLM 的 `max_model_len`，以及 Gemini 原生 `/models` 端點的 `inputTokenLimit`（它的 OpenAI 相容端點不回報）。API 沒涵蓋的模型，會在同一次重新整理時下載 [models.dev](https://models.dev) 資料庫補齊（有輸入上限就用輸入上限，否則用整個視窗）；OpenRouter 的 API 已涵蓋全部模型所以略過，OpenAI 相容端點的模型名稱由使用者自訂也略過。會選 models.dev 而不是 LiteLLM 的 `model_prices_and_context_window.json`，是因為拿兩者對照官方文件後，互相矛盾的項目中 models.dev 較常正確。「模型設置」分頁的每一列都會顯示上限，玩家也能手動填寫（標記 `*`），手動值優先。Mod 以 `RimLLMProvider.GetContextWindow` 讀取。
3. **AES-256 設定加密**
   * API 金鑰以 AES-256 對稱加密儲存，金鑰由目前 OS 使用者的保護機制包裝。新資料使用 `v3:` 格式；`v1`／`v2` 的裝置衍生密文僅供遷移，下一次存檔時會改寫成受保護金鑰格式。
   * 設定介面預設也會**遮罩金鑰**（只留頭尾，仍可辨認自己設定了哪一把），每一列另有切換鈕可暫時顯示以便編輯。這防的是與加密不同的外洩途徑：截圖、回報問題與直播。
   * 所有供應商（含 Gemini）都以 HTTP Header 傳遞金鑰，絕不放在請求 URL，避免金鑰進入代理或伺服器的存取日誌。
   * RimWorld 的所有 Mod 都在同一個遊戲行程內執行。本框架不宣稱能阻止惡意 Mod 讀取記憶體、對公開 API 使用反射，或以其他行程內手段繞過邊界。
4. **設定介面**
   * 多欄供應商頁面，附狀態標示（已啟用／已停用／缺少金鑰）；快取的模型以標籤呈現，點一下就能加入備援鏈。
   * 模型選擇器支援搜尋與常見模型系列的快速過濾。
   * 對話測試頁：每則回覆會標示實際回答的模型、延遲與 token 用量（`~` 表示估計值），可以複製，也可以在串流途中停止。模型選擇器可把備援鏈上某一項釘選到鏈首測試（失敗仍會沿鏈降級），選自動則沿用備援鏈順序。回覆以 Markdig 解析 Markdown 後轉成 Unity rich text；Unity 表達不了的結構（例如表格）以近似方式呈現。
   * Embedding 設定沿用同樣的版面，提供本地伺服器自動偵測，以及會回報向量維度的連線測試。
5. **獨立除錯分頁與日誌開關**
   * 獨立的**除錯**設定分頁，含「詳細日誌」核取方塊（預設關閉——每次請求都會寫一行日誌，而 Verse 的共用日誌上限為 10000 筆），讓 Mod 開發者與玩家在排查問題時自由開關本 Mod 逐次請求的日誌輸出。一次性的警告（例如 API 金鑰無法解密、遙測寫檔失敗）則一律記錄。最近請求歷史會在供應商有回報用量時附上該次 token 數；拿不到用量的條目省略不顯示。
6. **一鍵連線測試**
   * 即時連線檢查，量測延遲並驗證 API 金鑰與模型。在基底類別實作一次，所有供應商共用。
7. **執行緒安全與主執行緒 Scribe 派送**
   * 所有設定字典皆以鎖保護，防止多執行緒並發讀寫。
   * `RecordLog` 的記憶體內日誌更新仍透過 `RimLLMDispatcher` 在 Unity 主執行緒執行，遙測寫檔（AES 加密＋JSON 序列化＋磁碟寫入）則由背景單寫者執行，並套用 15 秒寫入節流，避免背景存檔造成崩潰或 TPS 掉幀。
8. **推理模型**
   * 思維鏈以 MEAI 的 `TextReasoningContent` 放在 `ChatResponse.Messages`／`ChatResponseUpdate.Contents` 裡交付，刻意**不**併入 `ChatResponse.Text`，因此結構化輸出與 JSON 解析不會讀到它。要保留或丟棄，依內容型別過濾即可。
   * **思考強度**：自動（沿用供應商預設）、關閉，或低／中／高。各供應商會轉成自己的格式（`reasoning_effort`、OpenRouter 的 `reasoning`、`thinking`、`enable_thinking`），只接受部分等級或無法關閉推理的供應商也有對應處理。
   * **不支援的參數靠學習，不靠猜。** 只要模型不在一小份已知非推理模型的名單上，就會送出思考強度。模型若以 400 拒絕它（或 `temperature`），框架會拿掉該參數重試一次，並在本次遊戲中不再對該模型送出。
9. **上下文快取與 Prompt 快取**
   * 在 `RimLLMChatOptions` 設定 `CachedContext`，框架會把它併入系統訊息，具備服務端 prompt caching 的供應商（OpenAI，以及經 OpenAI 相容端點存取的 Gemini）會對重複前綴自動打折，大幅降低高頻重複請求的輸入 Token 成本與延遲。
   * **量化節省**：用量統計會解析 API 回傳的快取命中 Token（OpenAI `cached_tokens` 及其等價欄位），並依費率表中該模型的快取輸入估計費率計算。供應商定價可能變動，因此金額仍是估算值。
   * **成本估算來自內建費率表**（OpenAI、Gemini、DeepSeek、Groq、Qwen、Kimi、MiniMax、Z.ai、Player2 與 xAI 模型；`gpt-4o-2024-11-20` 這類帶日期的變體會對到基底模型）。Player2 記為已知的 $0（本地免費，雲端以 joules 而非美元計費，joules 不列入費用估算）。查無費率代表**費用未知**，不是已知免費：其 token 照計入每日預算，但費用不列入顯示總額。Debug 分頁會顯示本次執行中查無費率的請求數。每日預算在請求前檢查已累計的輸入＋輸出 token，並非精確的消費上限。
   * **預算超限應對策略只有兩種**：**直接阻斷並警告**（預設，請求被擋下並回報 `LLMError.QuotaExceeded`，每天彈一次遊戲內提示說明是 RimLLM 預算擋下的）與**只警告並照常送出**（每天彈一次遊戲內提示後繼續呼叫，超支要不要繼續由使用者自己決定）。設定檔若還留著已移除的策略（包含數值與「只警告」撞號的舊 SilentMocking），載入時會重設為「直接阻斷並警告」並提示一次，不會靜默變成放行。舊版以美元計的每日上限無法換算成 token，載入時會移除並提示一次，請玩家重新設定 token 預算。其他 Mod 自行實作、不記錄 token 的設定則退回舊版美元門檻。兩者都不改寫回應內容 —— 框架不會代你偽造一句看似正常的回答，那會讓呼叫端與玩家都誤以為模型真的回應了。
10. **Embedding SDK**
    * 框架公開由 Google Gemini、OpenAI、Ollama 或 OpenAI 相容端點支援的 embedding 功能。其他 Mod 可透過 `RimLLMProvider.CreateEmbeddingGenerator` 取得標準 `IEmbeddingGenerator`，用於語意檢索與分群。
    * 所有線上來源都走 OpenAI SDK：Google 經官方 OpenAI 相容端點存取 Gemini，OpenAI 走其原生端點；Ollama 與自架服務使用 OpenAI SDK 的 `EmbeddingClient`（Ollama 走其 OpenAI 相容的 `/v1` 端點）。因此「Embedding 端點」欄位填的是**服務根位址**（如 `http://localhost:11434/v1`）；填入完整 `/embeddings` 路徑會自動正規化。模型、端點與金鑰依 Embedding 供應商分別保存，切換啟用的供應商不會遺失其他供應商的設定；模型或端點留空代表使用該供應商預設值，金鑰留空則繼承對應對話供應商的金鑰。
    * 設定頁可直接抓取可用模型清單，不必憑記憶輸入名稱；只要伺服器說得出哪些是 embedding 模型，清單就**只留真正的 embedding 模型**：Google 走原生 `/models` 清單（`supportedGenerationMethods` 含 `embedContent`）、Ollama 看 `/api/show` 的 `capabilities`、LM Studio 看 `/api/v0/models` 的 `type`，OpenAI 則依官方型錄固定的 `text-embedding-*` 命名過濾。只有兩者皆無的通用 OpenAI 相容伺服器才退回沒有能力資訊的 `/v1/models`，此時清單只**排序**（把像 embedding 的名稱排前面）而不過濾 —— 伺服器的模型名可能由使用者自訂，過濾會把合法選項藏起來；狀態列會明講清單未過濾。沒有模型清單端點的伺服器仍可手動輸入。
    * Embedding 屬計費 API，因此與一般生成請求共用同一套防濫用檢查；其金鑰採用與供應商金鑰相同的 AES 加密。
11. **原生 Tool Calling（函式呼叫）**
    * 完整支援 Microsoft.Extensions.AI Tool Calling 標準（`AIFunction`、`ChatOptions.Tools`、`FunctionCallContent`、`FunctionResultContent`），所有供應商經共用的 OpenAI 協定路徑提供。
    * 提供 `RimWorldFunctionInvoker.AsMainThreadFunctionInvokingClient()`，自動將工具叫用委派排入 Unity 主執行緒執行，杜絕 RimWorld 跨執行緒崩潰風險。
12. **第三方整合（強制其他 Mod 改走 RimLLM）**
    * 設定頁新增「第三方整合」分頁，列出 RimLLM 可以接管 LLM 流量的 Mod——目前包含 **RimTalk**（`cj.rimtalk`）、**Auto Translation**（`seohyeon.autotranslation`）**Mod 兼容性檢查器**（`modcompatchecker.main`）與 **簡中自動化：One**（`ow1r.scauto`）。開關開啟後，目標 Mod 的所有請求一律導入 RimLLM 的備援鏈、預算、節流與用量統計。開啟期間目標 Mod 自己的 API 金鑰／模型／端點設定會被忽略；開關即時生效不需重啟，預設**關閉**。
    * 每一列會顯示四種狀態之一：**未安裝**（本機沒有該 Mod）、**未啟用**（已安裝但未在 Mod 清單中啟用，請到清單啟用）、**掛載失敗**，或**已安裝／接管中**。已安裝未啟用的排在未安裝之前。
    * **RimTalk**：提示工程完全不動，訊息（含「Output JSONL」指示）原樣轉送，串流文字逐塊餵進 RimTalk 自己的 `JsonStreamParser`，氣泡仍然像原生一樣一行一行冒出來。推理內容不會混進 JSONL 串流，且這類請求關閉思考以對齊 RimTalk 自身的預設。
    * **Auto Translation**：完整支援單條與 XML 批次翻譯。以原生具備批次能力與佔位符防護的 `Translator_OpenAICompatible` 作為哨兵轉接，佔位符（`__PH0__` 等）防護、XML 批次打包與解析均沿用 Auto Translation 自身邏輯。開關開啟時自動同步當前翻譯器為哨兵，關閉或 RimLLM 缺金鑰/離線時自動退回原生翻譯引擎並節流警告。
    * **Mod 兼容性檢查器**：攔截 `AIService.CallAPIWithTimeout`，將 Harmony/XML 衝突分析、依賴問題與報錯診斷等請求全面導流至 RimLLM。接管開啟時自動將 `IsAIConfigured` 覆寫為 `true`，玩家無需在檢查器內重複設定金鑰即可直接使用 AI 診斷，並自動短路餘額查詢以杜絕 401 報錯。
    * **簡中自動化：One**：它的 AI 批量翻譯、品質評審與健康探測都經過同一個私有傳輸方法，因此一個 Harmony 前綴就同時涵蓋 Chat Completions 與 Responses 兩種協議。請求 JSON 交給 RimLLM，再把同協議格式的合成回應餵回該 Mod 自己的解析器，它原有的重試、格式降級與結構化輸出處理照常運作。接管開啟期間它也把端點視為本機，玩家不必填假金鑰；設定頁的「測試連線」與思考參數探測則直接短路為成功。錯誤會對應到它自己的暫時性／永久性錯誤類型，讓它的重試邏輯正確運作；與其他目標不同，這裡不會退回原生路徑（沒有金鑰退回去也只會失敗）。它的 OpenAI Batch API 通道不在涵蓋範圍。它的類別皆為 `internal`，轉接器因此完全以名稱經反射綁定，不需要參考用 DLL。
    * 純 Harmony、全部 fail-soft，框架不實作任何第三方介面，也絕不在類別繼承、介面或欄位層級引用第三方型別，第三方 Mod 未安裝時框架組件仍可安全載入。其 API 若漂移導致掛載失敗，設定頁會直接顯示原因。請求當下 RimLLM 沒有可用供應商（備援鏈為空、缺金鑰）時，自動退回原生路徑並記一筆節流過的警告。
    * 新增其他 Mod 只需一筆登錄＋一個 `RimLLMCompatTarget` 子類；轉接器對著簽入版本庫的參考用 DLL（`Source/Libs/`）編譯，該 DLL 不隨包出貨。
13. **使用中的模組**
    * 設定頁「使用中的模組」分頁把兩個來源合併成每個 Mod 一列：已啟用且在 `About.xml` 的 `modDependencies` 宣告依賴 RimLLM Framework 的 Mod，以及本次遊戲曾透過 RimLLM 送出 chat 或 embedding 請求的 `modId`（`RimLLMUsageTracker.CallerModIds`）。呼叫端在請求進入守衛時才記錄，而不是建立 client 時——許多 Mod 一啟動就先取 client 卻從未送出請求；modId 可由呼叫端任意輪換，因此最多只收錄 64 個。因此在第三方整合分頁被接管的 Mod 雖未宣告依賴，送出第一個請求後也會出現。
    * 已呼叫過的列排在前面並標示「已呼叫」；宣告依賴但尚未呼叫的標示「尚未呼叫」。滑鼠懸停可看到是否宣告依賴，以及所有對應到該 Mod 的 `modId`。
    * `modId` 是呼叫端自取的字串，因此會比對已啟用 Mod 的 packageId（忽略 Steam 後綴）；對不上任何已啟用 Mod 時以原字串顯示。只排除框架自己的兩個設定頁工具（`RimLLM.DebugChat`、`RimLLM.SchemaSelfTest`）；名稱剛好以 `RimLLM.` 開頭的第三方 Mod 仍會列出。
    * 依賴掃描只做一次（Mod 清單在執行期不會變動）；執行期部分只在記錄到的 `modId` 數量改變時才重組。
