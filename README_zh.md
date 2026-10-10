# RimLLM Framework

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](http://rimworldgame.com/)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20繁中%20%7C%20简中-orange.svg)

[English](README.md)

`RimLLM Framework` 是一個為 RimWorld Mod 提供大型語言模型（LLM）呼叫介面與核心基礎建設的底層框架，讓其他 RimWorld AI Mod 有一個穩健、方便、高效且開箱即用的 SDK，不必重造輪子。

框架回傳的一切都是**標準的 Microsoft.Extensions.AI 型別** —— `IChatClient`、`ChatMessage`、`ChatResponse`、`IEmbeddingGenerator`，沒有另一套自訂 client 介面要學。

```csharp
// 呼叫端 API 的全貌。供應商、模型、API 金鑰與 Fallback 鏈
// 都已經由玩家在本 Mod 的設定介面裡配置好。
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

> [!IMPORTANT]
> 本框架是相依項，不是獨立功能。執行期玩家必須同時啟用 **RimLLM Framework** 這個 Mod，
> 並且已經設定好至少一個帶 API 金鑰的供應商；否則 `RimLLMProvider.CreateChatClient` 會擲出
> `InvalidOperationException`，呼叫也會以 `RimLLMException` 失敗。
> 支援的遊戲版本：**RimWorld 1.6**（見 `About/About.xml`）。

## 目錄

* [安裝](#-安裝) —— 只參考組件，不重複出貨
* [SDK 使用方式](#-sdk-使用方式) —— 對話、串流、結構化輸出、Embedding、錯誤處理
* [功能特色](#-功能特色) —— 框架替你做掉了什麼（[詳細說明](Documentation/FEATURES_zh.md)）
* [架構、安全性與測試](Documentation/ARCHITECTURE_zh.md) —— 怎麼做到的、為什麼這樣做、每個安全機制防得住什麼
* [授權條款](#-授權條款)

---

## 📦 安裝

你的 Mod 需要在**編譯期**取得 Microsoft.Extensions.AI（MEAI）型別，但**執行期不可以自己帶一份**。RimLLM Framework 已經把所有 MEAI DLL 部署在自己的 `Assemblies/` 資料夾裡，而 RimWorld 會把所有 Mod 載入同一個 AppDomain —— 多一份複本就會產生兩個彼此不相容的 `IChatClient` 型別，任何轉型都會失敗。

下面兩種做法的原則相同：**只參考，不複製。**

### 方案 A —— NuGet（建議）

```xml
<ItemGroup>
  <!-- IChatClient / ChatMessage / ChatResponse / IEmbeddingGenerator。
       ExcludeAssets="runtime" 保留參考但不把 DLL 複製到你的 Assemblies 資料夾。 -->
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.10.0" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0) —— 使用端 Mod 只需要這一個。它會帶進 `Microsoft.Extensions.AI.Abstractions`，`IChatClient` 就在裡面。
* [`Microsoft.Extensions.AI.OpenAI` 10.10.1](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.10.1) —— 框架另外會一併發佈這一顆。只有在你要自己建構 OpenAI SDK 用戶端時才需要參考；單純呼叫 `RimLLMProvider.CreateChatClient` 的 Mod 不需要。

> [!IMPORTANT]
> **版本必須釘死在 `10.10.0`。** 組件識別必須與框架載入的那一份完全一致。使用端 Mod 也不要把 `CopyLocalLockFileAssemblies` 設成 `true` —— 那正是造成上述 DLL 重複問題的原因。
>
> **從舊版框架升上來要注意：**MEAI 的組件版本是跟著 `major.minor` 走的，`10.8.3` 產生的是 `10.8.0.0`，`10.10.0` 產生的是 `10.10.0.0`。因此對著舊版 MEAI 編譯的使用端 Mod 必須把這行的版本改掉並重新編譯 —— 這不是原始碼層的破壞性變更，但舊的二進位已經對不上框架載入的那一份。

框架本身的組件不在 NuGet 上，那部分請看方案 B。

### 方案 B —— 直接參考 DLL

直接從已安裝的框架 Mod 參考 DLL。`<Private>false</Private>` 是阻止 MSBuild 把它們複製到你輸出目錄的關鍵。

```xml
<PropertyGroup>
  <RimLLMDir>$(MSBuildProgramFiles32)\Steam\steamapps\common\RimWorld\Mods\RimLLM Framework\Assemblies</RimLLMDir>
</PropertyGroup>

<ItemGroup>
  <Reference Include="RimLLM Framework">
    <HintPath>$(RimLLMDir)\RimLLM Framework.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="Microsoft.Extensions.AI.Abstractions">
    <HintPath>$(RimLLMDir)\Microsoft.Extensions.AI.Abstractions.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

若 RimWorld 不在 Steam 預設路徑，請自行調整 `RimLLMDir`。

### 載入順序

在你的 Mod 的 `About/About.xml` 宣告相依，確保框架先初始化：

```xml
<loadAfter>
  <li>GreenMushroom.RimLLMFramework</li>
</loadAfter>
```

框架本身的第三方整合層需要 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)（`brrainz.harmony`），
已在 `About.xml` 宣告為必要相依。`0Harmony.dll` **不會**隨 `Assemblies/` 出貨，執行期由 Harmony Mod 提供。

---

## 💻 SDK 使用方式

**如果你已經會用 [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0)，你就已經會用這套 API。**

RimLLM Framework 的全部工作，就是交給你一個標準的 MEAI `IChatClient`。從那一行之後全是純 Microsoft.Extensions.AI，因此本文件只寫本框架專屬的部分；MEAI 本身的語法請看 [Microsoft 官方文件](https://learn.microsoft.com/zh-tw/dotnet/ai/microsoft-extensions-ai)。

### 只有一行不一樣

```csharp
// 由玩家在模組設定中提供供應商、模型、金鑰與 Fallback 鏈
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");
```

其他 provider 套件要你用模型名稱與 API 金鑰自行建構 client，這裡改成呼叫這一行。`"myai.mod"` 只是一個標籤，用於各 Mod 的節流與用量歸屬。你這邊不需要註冊呼叫，也不需要處理任何金鑰。

### 對話

```csharp
using Microsoft.Extensions.AI;
using RimLLM_Framework;

IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

訊息清單與 `ChatOptions` 的用法與 MEAI 文件完全相同。唯一與本框架有關的規則是：不設定 `ModelId` —— 或填 `"fallback"`（`RimLLMChatOptions.FallbackModelId`，大小寫不限）—— 時，實際由哪個供應商與模型執行，交給玩家設定的 Fallback 鏈決定。對外而言備援鏈就是一個叫 `fallback` 的模型；要指定就填 `"供應商:模型"` 形式的項目。帶後綴的 `"fallback:…"` 視為一般項目，走相同的 `"供應商:模型"` 規則。指定的模型不論路由策略為何、是否已在鏈上，一律最先嘗試，失敗後才輪到鏈上其餘候選。`RimLLMProvider.GetFallbackChain()` 會依設定順序回傳玩家備援鏈的副本，可用來做模型選單。

`RimLLMProvider.GetContextWindow("供應商:模型")` 回傳該模型的上下文上限（token 數），可用來依上限的百分比壓縮對話歷史。玩家在模型設置分頁手動填寫的值優先，否則是玩家上次重新整理該供應商模型清單時記下的值 —— 優先採用供應商自己的 API（OpenRouter、Groq、Gemini），其餘由 [models.dev](https://models.dev) 資料庫補齊。上限不明、或傳入的不是明確的 `"供應商:模型"` 時回傳 `null` —— 它不會替你猜備援鏈最後會選哪個模型，拿到 `null` 時請用你自己的預設值。

有兩個欄位沒設定時框架會補預設值：`MaxOutputTokens` 為 **1024**、`Temperature` 為 **0.7**。批次翻譯、多角色對話、欄位很多的結構化輸出這類長回應請自行設定 `MaxOutputTokens`，否則回覆會在 1024 個 token 處被截斷。

回傳的 `ChatResponse` 就是供應商產生的那一個，除了 `ModelId` 被改寫成 `"供應商:模型"`（讓你在 failover 之後仍分辨得出實際是誰回的）之外，一律原樣交還。`ResponseId`、`CreatedAt`、`ConversationId`、`Usage`、`FinishReason`、`RawRepresentation` 與 `AdditionalProperties` 都是供應商設什麼就是什麼，包括 `null`。`Usage` 為 `null` 代表供應商沒有回報 token 數，不是這次呼叫免費。

### 串流

串流是標準 MEAI 的 `GetStreamingResponseAsync` / `await foreach`，語法見 MEAI 文件。框架額外保證一件事：整條 Fallback 鏈失敗時，原始的 `RimLLMException` 會從 `await foreach` 重新擲出，串流不會無聲結束。

框架**不會**把 update 派送到 Unity 主執行緒。迴圈在哪個執行緒續行，取決於你自己的 `await`：在主執行緒上開始 `await foreach`，Unity 的 `SynchronizationContext` 會讓每一輪都回到主執行緒；若從 `Task.Run` 或 `ConfigureAwait(false)` 之後開始，迴圈本體就跑在執行緒池上，此時只能透過 `RimLLMDispatcher.EnqueueOnMainThread` 碰遊戲或 UI 狀態。

### 結構化輸出

`GetResponseObjectAsync<T>` 是掛在 `IChatClient` 上的擴充方法。它會產生 JSON Schema、修復格式錯誤的輸出，並反序列化給你：

```csharp
public class PawnIncidentDecision
{
    public string EventType;       // "Good" 或 "Bad"
    public string IncidentDefName; // 例如 "RaidEnemy"
    public float Probability;
}

PawnIncidentDecision decision = await client.GetResponseObjectAsync<PawnIncidentDecision>(
    new List<ChatMessage>
    {
        new ChatMessage(ChatRole.User, "決定下一個事件類型與 DefName。")
    });
```

> [!NOTE]
> 請用這個而不是 MEAI 自己的 `GetResponseAsync<T>`。MEAI 的原始 schema 形狀（union 型別、`$ref`、無上限巢狀）會被多家供應商的 strict structured output 拒絕，而且 MEAI 沒有 JSON 修復路徑。RimLLM 驅動的是同一個底層 `JsonSchemaExporter`，但在其上加了正規化層與單一 OpenAI 相容方言。詳見[架構設計 §6](Documentation/ARCHITECTURE_zh.md#6-官方-sdk-與供應商職責)。

### 原生 Tool Calling（函式呼叫）

RimLLM Framework 原生支援 Microsoft.Extensions.AI 的 Tool Calling（`AIFunction`、`ChatOptions.Tools`、`FunctionCallContent`）。所有內建供應商都走 OpenAI 協定，工具定義與呼叫只有一種 wire 形狀。`AIFunctionFactory.Create` 在遊戲內可正常使用：框架出貨的是 `Microsoft.Extensions.AI.Abstractions` 的 `netstandard2.0` 版本，讓 MEAI 的 schema 產生不再碰 RimWorld Mono 沒有的 `System.ComponentModel.DataAnnotations` 組件（詳見[架構設計 §6](Documentation/ARCHITECTURE_zh.md#6-官方-sdk-與供應商職責)）。

#### 1. 自動迴圈執行模式（推薦，內建 Unity 主執行緒安全調度）

在 RimWorld 中，工具委派通常需要存取遊戲地圖、Pawn 或遊戲世界物件。為了避免 Unity 跨執行緒 API 違規引發遊戲崩潰，請使用 `AsMainThreadFunctionInvokingClient()` 擴充方法包裝客戶端。這能確保工具的執行委派一律透過 `RimLLMDispatcher` 安全排入 Unity 主執行緒：

```csharp
// 包裝 client 以獲得自動多輪迴圈呼叫能力與主執行緒安全
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod")
    .AsMainThreadFunctionInvokingClient(maxIterations: 10);

var weatherTool = AIFunctionFactory.Create(
    (string colonyName) => Find.CurrentMap.weatherManager.curWeather.label,
    "GetColonyWeather",
    "取得殖民地當前天氣");

var options = new ChatOptions
{
    Tools = new List<AITool> { weatherTool }
};

// 模型呼叫工具，RimLLM 在主執行緒執行該工具並將結果回傳給模型，直到產出最終回覆
ChatResponse response = await client.GetResponseAsync(
    new List<ChatMessage> { new ChatMessage(ChatRole.User, "目前我們殖民地的天氣如何？") },
    options);

Log.Message(response.Text);
```

#### 2. 手動單輪模式 (Raw Mode)

若你的 Mod 希望手動掌控每一輪，就不要套用上面那層包裝，直接把 `ChatOptions.Tools` 傳給 `client.GetResponseAsync()`。回應會帶著 `FunctionCallContent` 與 `FinishReason = ChatFinishReason.ToolCalls`，之後的迴圈完全依 MEAI 文件的標準寫法自行驅動。注意這條路徑不會有任何主執行緒派送 —— 那正是上面那層包裝存在的理由。

#### 3. Agent 作者須知

當一個請求變成多輪工具迴圈時，下列幾點會影響你的設計：

* **防濫用節流以外層請求計數，不以迴圈輪次計數。** 每個 Mod 的節流（預設 10 秒內 10 次，超過冷卻 60 秒）看得到 `FunctionInvokingChatClient` 發出的每一次呼叫，但最後一則訊息是 `ChatRole.Tool` 工具結果的呼叫會被視為「啟動這個迴圈的那個請求」的延續，不計入時間視窗。已在冷卻中的 Mod 仍然會被擋，續輪也不例外。判定靠訊息形狀，因此直接用 MEAI 自己的 `FunctionInvokingChatClient` 也適用。
* **送出前先確認工具到得了模型。** 不支援原生函式呼叫的供應商會在呼叫前把請求的 `Tools` 移除。`RimLLMProvider.GetEffectiveCapabilities()` 回傳目前備援鏈可能路由到的所有候選能力的**交集** —— 那裡的 `SupportsFunctionCalling` 為 `true`，就沒有任何候選會丟掉你的工具。傳入與 `ChatOptions.ModelId` 相同格式的 `"Provider:Model"` 字串可以縮小範圍。
* **工具仍被丟掉時，回應會告訴你。** `response.WereToolsStripped()`（串流的每個 `ChatResponseUpdate` 也有同名方法）為 `true` 代表實際回答的供應商根本沒看過工具定義。這和「模型決定不呼叫工具」是兩回事，要分開處理。
* **fallback 可能在迴圈中途換供應商。** 每一輪都是獨立請求，第 3 輪可能由第 1 輪之外的供應商回答，歷史裡帶著前一家產生的 `tool_call_id`。所有內建供應商都走 OpenAI 協議，因此可以互通；若你的邏輯依賴實際回答的模型，檢查 `response.ModelId`（`"Provider:Model"`）。
* **框架不保存任何對話狀態。** 歷史、上下文修剪與 token 預算都由呼叫端負責；框架只負責把溢位對應成 `LLMError.ContextWindowExceeded`。

### Embedding 向量

形狀一樣：一行框架呼叫，之後全是標準 MEAI：

```csharp
IEmbeddingGenerator<string, Embedding<float>> generator =
    RimLLMProvider.CreateEmbeddingGenerator("myai.mod");
```

`GenerateAsync`、`GeneratedEmbeddings<T>` 與 `Embedding<float>` 的行為與 MEAI 文件相同。每個 `Embedding<float>` 都帶著實際算出它的 `ModelId`，`GeneratedEmbeddings.Usage` 則在供應商有回報時帶回輸入 token 數—— OpenAI 相容端點會回報，Gemini 公開 API 不會（它的 `tokenCount` 限 Enterprise 平台），因此在 Gemini 下 `Usage` 維持 `null`。與對話一樣，`null` 代表供應商沒有回報，不是這次呼叫免費。Embedding 供應商預設為**停用**；玩家選擇之前，`GenerateAsync` 會擲出 `RimLLMException`。

同一次 `GenerateAsync` 的多筆輸入會合成一次批次請求送出（為了上限較小的端點，每 100 筆分一批），不再每筆一次 HTTP 來回；供應商回報的輸入 token 也和對話請求一樣計入用量看板與每日 token 預算。

### 錯誤處理

所有失敗都以 `RimLLMException` 呈現，並帶有與供應商無關的 `LLMError` 代碼：

```csharp
try
{
    ChatResponse response = await client.GetResponseAsync(messages);
}
catch (RimLLMException ex) when (ex.Error == LLMError.QuotaExceeded)
{
    Messages.Message("API 額度已用盡。", MessageTypeDefOf.RejectInput, false);
}
catch (RimLLMException ex)
{
    Log.Error($"[MyAIMod] {ex.Error}：{ex.Message}");
}
```

`LLMError` 的值：`Timeout`、`RateLimit`、`InvalidKey`、`ProviderOffline`、`InvalidResponse`、`NetworkError`、`ModelNotFound`、`ContentFilter`、`QuotaExceeded`、`Cancelled`、`Unknown`、`ContextWindowExceeded`。

`ContentFilter` 與 `ContextWindowExceeded` 從籠統的 4xx 拒絕中拆出來，是因為三者的處置方式完全不同：縮短提示詞、換一家供應商，還是修請求。OpenAI 協定家族對這兩種情況一律回 `400`，因此由錯誤對照表依訊息內容判定；`413` 則依定義直接對應 `ContextWindowExceeded`。Schema、reasoning 與 temperature 遭拒仍維持回報 `InvalidResponse`，不影響現有的「去掉該參數重打一次」流程。新的列舉成員一律追加在 `LLMError` 末尾，已編譯的下游 Mod 數值不變。

### 唯一多出來的型別：`RimLLMChatOptions`

一般旋鈕用 `ChatOptions` 就夠了。只有在你需要 MEAI 沒有對應概念的功能時，才改用 `RimLLMChatOptions` —— 其餘設定的行為完全不變：

```csharp
var options = new RimLLMChatOptions
{
    Temperature = 0.7f,          // 這是原本的 ChatOptions
    Priority = 5,                // 數值越高，在全域請求佇列中越先執行
    CachedContext = worldRules,  // 大型可重用前綴，啟用供應商端的上下文快取
    MinFallbackLevel = "Medium", // 降級不得低於此模型等級
    DisableReasoning = true      // 對支援推理的模型關閉思考
};

ChatResponse response = await client.GetResponseAsync(messages, options);
```

`CachedContext` 不為空時，`EnableContextCaching` 會自動開啟。可重用的前綴會併入系統訊息，具備服務端 prompt caching 的供應商（OpenAI，以及經 OpenAI 相容端點存取的 Gemini）會自動對重複前綴打折。

### 你不必自己寫的部分

這才是這個框架存在的意義。以下全部已經在那一個 `IChatClient` 後面完成：

| 你可以省略 | 因為框架已經做了 |
| --- | --- |
| API 金鑰的儲存與 UI | 使用 OS 每使用者保護金鑰的 AES-256 加密設定，所有 Mod 共用同一份 |
| 挑選供應商或模型 | 玩家設定的 Fallback 鏈，項目為 `Provider:Model` 形式 |
| 重試與 `Retry-After` | 逾時／429／連線錯誤自動重試，兩種標頭格式都支援 |
| 供應商之間的容錯切換 | 回應開始輸出之前，自動沿 Fallback 鏈降級 |
| 處理掛掉的供應商 | 熔斷器，連續失敗後以指數退避冷卻 |
| 跨 Mod 的流量控制 | 全域優先佇列與並行上限，避免多個 Mod 同時打 API 造成掉幀 |
| 費用控管 | 每日 token 預算，可選只警告／直接阻斷並警告 |
| 用量與費用回報 | Debug 分頁的各供應商 Token 與成本看板 |
| 推理模型的差異 | `reasoning_content` 統一正規化為 MEAI 的 `TextReasoningContent` |
| 格式錯誤的 JSON | 修復 Markdown 圍籬、未閉合括號與尾隨逗號，再抽出 JSON 區塊做第二次解析 |
| 主執行緒切換 | 記憶體內的請求日誌更新派送回 Unity 主執行緒；公開 `RimLLMDispatcher` 供你派送自己的回呼 |
| 原生 Tool Calling 與主執行緒排程 | 所有供應商經 OpenAI 協定送出工具定義；工具委派自動排入 Unity 主執行緒 |

### API 表面速查

`using RimLLM_Framework;` 會引入 14 個公開型別。多數 Mod 只會碰到第一列：

| 分層 | 型別 | 什麼時候需要 |
| --- | --- | --- |
| **呼叫模型** | `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException`、`LLMError`、`RimLLMClientExtensions`（`GetResponseObjectAsync<T>`、`WereToolsStripped`）、`RimWorldFunctionInvoker` | 一定會用到 —— 這就是全部的使用端 API |
| **提供供應商** | `ILLMProvider`、`LLMProviderCapabilities`、`IRimLLMSettings` | 只有要用 `RimLLMProvider.RegisterProvider` 註冊自己的 LLM 後端時（`ILLMProvider` 直接產出標準 `Microsoft.Extensions.AI.IChatClient`） |
| **診斷** | `TestResult`、`ProviderIds`、`LLMErrorMapper` | 連線測試、內建供應商 ID 常數、HTTP 狀態碼對照 |

其餘的 `IChatClient`、`ChatMessage`、`ChatResponse`、`ChatResponseUpdate`、`IEmbeddingGenerator` 全都是 `Microsoft.Extensions.AI`。具體的 client 類別刻意設為 `internal`，所以沒有任何 RimLLM 的 client 型別需要你對接。

---

## 📖 功能特色

1. **多供應商支援** —— Gemini、OpenAI、DeepSeek、Groq、Grok、Z.ai、OpenRouter、Kimi、MiniMax、Qwen、NVIDIA、Player2，以及任何 OpenAI 相容端點（LM Studio、Ollama、vLLM 等）。
2. **容錯與模型 Fallback** —— 玩家設定的備援鏈，搭配指數退避、以模型為單位的冷卻、四種路由策略與上下文上限查詢。
3. **API 金鑰加密** —— AES-256，金鑰由目前 OS 使用者保護；設定介面預設遮蔽，也不會出現在請求 URL。
4. **設定介面** —— 多欄供應商頁面、可過濾的模型選擇器、支援 Markdown 的對話測試頁。
5. **除錯分頁** —— 逐請求日誌開關（預設關閉）。
6. **一鍵連線測試** —— 檢查延遲、API 金鑰與模型。
7. **執行緒安全** —— 設定加鎖、主執行緒派送、背景節流寫入遙測。
8. **推理模型** —— 思維鏈以 MEAI 的 `TextReasoningContent` 交付；每個供應商都能控制思考強度。
9. **Prompt 快取** —— 以 `CachedContext` 使用服務端快取，用量統計會算出快取省下的費用。
10. **Embedding SDK** —— 標準 `IEmbeddingGenerator`，後端可選 Gemini、OpenAI、Ollama 或 OpenAI 相容端點。
11. **原生 Tool Calling** —— 所有供應商都支援 MEAI 的 `AIFunction`，工具在主執行緒執行。
12. **第三方整合** —— 讓 RimTalk、Auto Translation、Mod Compatibility Checker、簡中自動化：One 改走 RimLLM。
13. **使用中的模組** —— 設定分頁列出宣告依賴或本次遊戲呼叫過 RimLLM 的模組。

各項的詳細行為與設計理由：**[Documentation/FEATURES_zh.md](Documentation/FEATURES_zh.md)**。

---

## 🛠️ 架構、安全性與測試

框架怎麼做到上述功能、每項安全機制實際防得住什麼，以及如何執行測試：**[Documentation/ARCHITECTURE_zh.md](Documentation/ARCHITECTURE_zh.md)**。

---

## 📜 授權條款

本模組原始碼以 **MIT License** 釋出 —— Copyright (c) 2026 **mushroomTW**。詳見 [LICENSE](LICENSE)。

隨附於 `Assemblies/` 的相依組件維持各自的授權：Microsoft.Extensions.AI 與 OpenAI .NET SDK 為 MIT。RimWorld 本身的組件屬於 Ludeon Studios，本模組不予散布。
