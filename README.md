# RimLLM Framework

[![RimWorld 1.7](https://img.shields.io/badge/RimWorld-1.7-brightgreen.svg)](http://rimworldgame.com/)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20繁中%20%7C%20简中-orange.svg)

[繁體中文說明](README_zh.md)

`RimLLM Framework` is a foundational framework providing a Large Language Model (LLM) calling interface and core infrastructure for RimWorld mods. It gives other RimWorld AI mods a robust, convenient, efficient and ready-to-use SDK, so mod developers don't have to reinvent the wheel.

Everything you get back from the framework is a **standard Microsoft.Extensions.AI type** — `IChatClient`, `ChatMessage`, `ChatResponse`, `IEmbeddingGenerator`. There is no bespoke client interface to learn.

```csharp
// The whole consumer API. The player has already configured the provider,
// model, API key and fallback chain in this mod's settings.
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

> [!IMPORTANT]
> This framework is a dependency, not a standalone feature. At run time the player must have the
> **RimLLM Framework** mod active *and* have configured at least one provider with an API key —
> otherwise `RimLLMProvider.CreateChatClient` throws `InvalidOperationException` and calls fail with
> `RimLLMException`. Supported game version: **RimWorld 1.7** (see `About/About.xml`).

Consumer mods must rebuild for **.NET 10 (`net10.0`)**. RimWorld 1.6 and .NET Framework are no longer supported. Key storage uses Windows DPAPI, macOS Keychain, or Linux Secret Service (libsecret, a session D-Bus, and an unlocked keyring). Only `v3:` ciphertext is accepted; older formats are not supported.

## Contents

* [Installation](#-installation) — reference the assemblies without shipping duplicates
* [SDK Usage](#-sdk-usage) — chat, streaming, structured output, embeddings, error handling
* [Features](#-features) — what the framework does for you ([full details](Documentation/FEATURES.md))
* [Architecture, security and testing](Documentation/ARCHITECTURE.md) — how it does it, why, and what each security mechanism protects against
* [License](#-license)

---

## 📦 Installation

Your mod needs the Microsoft.Extensions.AI (MEAI) types at **compile time**, but must **not ship them at run time**. RimLLM Framework already deploys every MEAI DLL into its own `Assemblies/` folder, and RimWorld loads all mods into a single AppDomain — a second copy would create two distinct `IChatClient` types and every cast between them would fail.

The rule is the same for both options below: **reference, don't copy.**

### Option A — NuGet (recommended)

```xml
<ItemGroup>
  <!-- IChatClient / ChatMessage / ChatResponse / IEmbeddingGenerator.
       ExcludeAssets="runtime" keeps the reference but stops the DLLs
       being copied into your mod's Assemblies folder. -->
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.10.0" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0) — this is all a consuming mod needs. It brings in `Microsoft.Extensions.AI.Abstractions`, where `IChatClient` lives.
* [`Microsoft.Extensions.AI.OpenAI` 10.10.1](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.10.1) — additionally shipped by the framework. You only need to reference it if you construct OpenAI SDK clients yourself; a mod that just calls `RimLLMProvider.CreateChatClient` does not.

> [!IMPORTANT]
> **Pin the version to exactly `10.10.0`.** Assembly identity must match what the framework loaded. Do not set `CopyLocalLockFileAssemblies` to `true` in a consuming mod — that is what causes the duplicate-DLL problem above.
>
> **Upgrading from an earlier framework release:** MEAI's assembly version tracks `major.minor`, so `10.8.3` produced `10.8.0.0` while `10.10.0` produces `10.10.0.0`. A consuming mod compiled against an older MEAI release therefore has to bump this reference and rebuild — it is not a source-level break, but the old binary no longer matches what the framework loads.

You still need a reference to the framework assembly itself, which is not on NuGet — see Option B for that part.

### Option B — Direct DLL reference

Reference the DLLs straight out of the installed framework mod. `<Private>false</Private>` is what stops MSBuild copying them to your output.

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
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

Adjust `RimLLMDir` if RimWorld is not installed under the default Steam path.

### Load order

Declare the dependency in your mod's `About/About.xml` so the framework initialises first:

```xml
<loadAfter>
  <li>GreenMushroom.RimLLMFramework</li>
</loadAfter>
```

The framework itself requires [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
(`brrainz.harmony`) for its third-party integration layer; it is declared in `About.xml` as a hard dependency.
`0Harmony.dll` is **not** shipped in `Assemblies/` — the Harmony mod provides it at run time.

---

## 💻 SDK Usage

**If you already know [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0), you already know this API.**

RimLLM Framework's entire job is to hand you a standard MEAI `IChatClient`. Everything after that one line is plain Microsoft.Extensions.AI, so this page documents only what is specific to this framework — for the MEAI calls themselves, read [Microsoft's own docs](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai).

### Only one line differs

```csharp
// The player supplies the provider, model, key and fallback chain in the mod settings
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");
```

Where another provider package would have you construct a client from a model name and an API key, you call this instead. `"myai.mod"` is just a label used for per-mod throttling and usage attribution. There is no registration call and no key handling on your side.

### Chat

```csharp
using Microsoft.Extensions.AI;
using RimLLM_Framework;

IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

Message lists and `ChatOptions` behave exactly as MEAI documents them. The one framework-specific rule: leave `ModelId` unset — or set it to `"fallback"` (`RimLLMChatOptions.FallbackModelId`, case-insensitive) — and the player's configured fallback chain decides which provider and model actually runs. Externally the chain is just a model called `fallback`. Set it to a `"Provider:Model"` entry to pin one. A `"fallback:..."` entry with a suffix is an ordinary entry, resolved by the usual `"Provider:Model"` rules. A pinned model is always tried first, whatever the routing strategy and whether or not it is already on the chain; the rest of the chain only runs if it fails. `RimLLMProvider.GetFallbackChain()` returns a copy of the player's chain in its configured order, e.g. to offer a model picker.

`RimLLMProvider.GetContextWindow("Provider:Model")` returns the model's context window in tokens, e.g. to trim history at a percentage of it. A value the player typed on the Models tab wins; otherwise it is what was recorded the last time the player refreshed that provider's model list — the provider's own API first (OpenRouter, Groq, Gemini), then the [models.dev](https://models.dev) database for the rest. It returns `null` when the size is unknown or the argument is not an explicit `"Provider:Model"` — it does not guess which model the fallback chain will pick, so decide your own default for `null`.

Two knobs get a framework default when you leave them unset: `MaxOutputTokens` becomes **1024** and `Temperature` **0.7**. Set `MaxOutputTokens` yourself for anything long — batch translations, multi-character dialogue, structured output with many fields — or the reply is cut off at 1024 tokens.

The `ChatResponse` you get back is the provider's own, handed over unchanged apart from `ModelId`, which is rewritten to `"Provider:Model"` so you can tell who actually answered after a failover. `ResponseId`, `CreatedAt`, `ConversationId`, `Usage`, `FinishReason`, `RawRepresentation` and `AdditionalProperties` are whatever the provider set — including `null`. A `null` `Usage` means the provider reported no token counts, not that the call was free.

### Chat streaming

Streaming is MEAI's standard `GetStreamingResponseAsync` / `await foreach`. The framework adds one guarantee on top: if the whole fallback chain fails, the original `RimLLMException` is rethrown from `await foreach`, so a failing stream never ends silently.

Updates are **not** marshalled onto the Unity main thread by the framework. The loop resumes wherever your own `await` resumes: start the `await foreach` on the main thread and Unity's `SynchronizationContext` brings every iteration back there; start it from `Task.Run` or after a `ConfigureAwait(false)` and the loop body runs on a thread-pool thread, so touch game or UI state only through `RimLLMDispatcher.EnqueueOnMainThread`.

### Structured output

`GetResponseObjectAsync<T>` is an extension on `IChatClient`. It generates the JSON Schema, repairs malformed output, and deserializes for you:

```csharp
public class PawnIncidentDecision
{
    public string EventType;       // "Good" or "Bad"
    public string IncidentDefName; // e.g. "RaidEnemy"
    public float Probability;
}

PawnIncidentDecision decision = await client.GetResponseObjectAsync<PawnIncidentDecision>(
    new List<ChatMessage>
    {
        new ChatMessage(ChatRole.User, "Decide the next incident type and DefName.")
    });
```

> [!NOTE]
> Use this rather than MEAI's own `GetResponseAsync<T>`. MEAI's raw schema shape (union types, `$ref`, unbounded nesting) is rejected by strict structured output on several providers, and it has no JSON-repair path. RimLLM drives the same underlying `JsonSchemaExporter` but adds a normalization layer and a single OpenAI-compatible dialect on top. See [Architecture §6](Documentation/ARCHITECTURE.md#6-official-sdks-and-provider-responsibilities).

### Native Tool Calling (Function Calling)

RimLLM Framework provides native support for Microsoft.Extensions.AI Tool Calling (`AIFunction`, `ChatOptions.Tools`, `FunctionCallContent`). Every built-in provider speaks the OpenAI protocol, so tool definitions and calls use one wire shape everywhere. `AIFunctionFactory.Create` uses the .NET 10 MEAI assets and the game-provided System.Text.Json; see [Architecture](Documentation/ARCHITECTURE.md).

#### 1. Auto-Invoking Mode with Unity Main-Thread Safety (Recommended)

When invoking tools in RimWorld, mod functions typically access game maps, pawns, or World state. To prevent Unity threading crashes, wrap your client with `AsMainThreadFunctionInvokingClient()`. This ensures that tool delegates are automatically dispatched onto the Unity main thread via `RimLLMDispatcher`:

```csharp
// Wrap the client for automatic multi-turn loop with main-thread safety
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod")
    .AsMainThreadFunctionInvokingClient(maxIterations: 10);

var weatherTool = AIFunctionFactory.Create(
    (string colonyName) => Find.CurrentMap.weatherManager.curWeather.label,
    "GetColonyWeather",
    "Returns current colony weather");

var options = new ChatOptions
{
    Tools = new List<AITool> { weatherTool }
};

// The model calls the tool, RimLLM executes it on the main thread, and returns the final answer
ChatResponse response = await client.GetResponseAsync(
    new List<ChatMessage> { new ChatMessage(ChatRole.User, "What's our colony weather right now?") },
    options);

Log.Message(response.Text);
```

#### 2. Raw / Manual Mode

If your mod wants full manual control over each turn, skip the wrapper and pass `ChatOptions.Tools` straight to `client.GetResponseAsync()`. The response then carries `FunctionCallContent` with `FinishReason = ChatFinishReason.ToolCalls`, and you drive the loop yourself exactly as MEAI documents it. Note that nothing is dispatched onto the Unity main thread on this path — that is what the wrapper above exists for.

#### 3. Notes for agent authors

Things that matter once a request turns into a multi-turn tool loop:

* **Anti-abuse throttling counts outer requests, not loop iterations.** The per-mod throttle (default 10 requests per 10 s, then a 60 s cooldown) sees every call the `FunctionInvokingChatClient` makes, but a call whose last message is a `ChatRole.Tool` result is treated as a continuation of the request that started the loop and is not counted toward the window. A mod that is already in cooldown is still blocked, continuations included. The rule keys on message shape, so it applies equally to MEAI's own `FunctionInvokingChatClient`.
* **Check up front whether tools will reach the model.** A provider without native function calling has the request's `Tools` removed before the call. `RimLLMProvider.GetEffectiveCapabilities()` returns the intersection of the capabilities of every candidate the current fallback chain could route to — if `SupportsFunctionCalling` is `true` there, no candidate can drop your tools. Pass the same `"Provider:Model"` string you would put in `ChatOptions.ModelId` to narrow it.
* **When tools were dropped anyway, the response says so.** `response.WereToolsStripped()` (and the same method on every `ChatResponseUpdate` of a stream) is `true` when the answering provider never saw the tool definitions. Treat that differently from a model that chose not to call a tool.
* **Fallback can change providers between iterations.** Each iteration is its own request, so iteration 3 may be answered by a different provider than iteration 1, carrying the earlier provider's `tool_call_id`s in the history. Every built-in provider speaks the OpenAI protocol, so this round-trips; check `response.ModelId` (`"Provider:Model"`) if your logic depends on which model is answering.
* **The framework keeps no conversation state.** History, context-window trimming and token budgeting are the caller's job; the framework only maps an overflow to `LLMError.ContextWindowExceeded`.

### Embeddings

Same shape — one framework call, then standard MEAI:

```csharp
IEmbeddingGenerator<string, Embedding<float>> generator =
    RimLLMProvider.CreateEmbeddingGenerator("myai.mod");
```

`GenerateAsync`, `GeneratedEmbeddings<T>` and `Embedding<float>` behave as MEAI documents them. Each `Embedding<float>` carries the `ModelId` that actually produced it, and `GeneratedEmbeddings.Usage` carries the input token count when the provider reports one — OpenAI-compatible endpoints do, Gemini's public API does not (its `tokenCount` is Enterprise-only), so there `Usage` stays `null`. As with chat, `null` means the provider reported nothing, not that the call was free. The embedding provider defaults to **Disabled**; until the player picks one, `GenerateAsync` throws `RimLLMException`.

The inputs of one `GenerateAsync` call go out as a single batch request (split into chunks of 100 for endpoints with smaller limits) rather than one HTTP round trip per string, and the reported input tokens are counted in the usage dashboard and the daily token budget exactly like chat calls.

### Error handling

Every failure surfaces as `RimLLMException`, with a provider-independent `LLMError` code:

```csharp
try
{
    ChatResponse response = await client.GetResponseAsync(messages);
}
catch (RimLLMException ex) when (ex.Error == LLMError.QuotaExceeded)
{
    Messages.Message("Out of API credit.", MessageTypeDefOf.RejectInput, false);
}
catch (RimLLMException ex)
{
    Log.Error($"[MyAIMod] {ex.Error}: {ex.Message}");
}
```

`LLMError` values: `Timeout`, `RateLimit`, `InvalidKey`, `ProviderOffline`, `InvalidResponse`, `NetworkError`, `ModelNotFound`, `ContentFilter`, `QuotaExceeded`, `Cancelled`, `Unknown`, `ContextWindowExceeded`.

`ContentFilter` and `ContextWindowExceeded` are split out of the generic 4xx rejection because the three cases call for different responses: shorten the prompt, try a different provider, or fix the request. Providers on the OpenAI protocol report both as plain `400`s, so the mapper identifies them from the message text; `413` maps to `ContextWindowExceeded` by definition. Schema, reasoning and temperature rejections keep reporting `InvalidResponse`, leaving the existing "retry without that parameter" path untouched. New enum members are appended at the end of `LLMError`, so already-compiled mods keep their values.

### The one optional type: `RimLLMChatOptions`

`ChatOptions` covers the standard knobs. `RimLLMChatOptions` is pure convenience: every one of its properties reads and writes `ChatOptions.AdditionalProperties` under a `rimllm_*` key, and the framework only ever reads that dictionary. Setting those keys on a plain `ChatOptions` works identically, so you never have to reference this type — and because it holds no fields of its own, a middleware that clones your options into a plain `ChatOptions` cannot silently drop your settings.

```csharp
var options = new RimLLMChatOptions
{
    Temperature = 0.7f,          // plain ChatOptions
    Priority = 5,                // higher runs earlier in the global request queue
    CachedContext = worldRules,  // large reusable prefix; enables provider-side context caching
    MinFallbackLevel = "Medium", // don't degrade below this model tier
    DisableReasoning = true      // skip thinking on reasoning-capable models
};

ChatResponse response = await client.GetResponseAsync(messages, options);
```

`EnableContextCaching` turns on automatically when `CachedContext` is non-empty. The reusable prefix is merged into the system message, so providers with server-side prompt caching (OpenAI, and Gemini through its OpenAI-compatible endpoint) discount repeated prefixes automatically.

### What you don't have to write

This is the point of the framework. All of the following already happens behind that one `IChatClient`:

| You skip | Because the framework does it |
| --- | --- |
| API key storage and UI | AES-256 encrypted settings with an OS-protected per-user key, shared across every mod |
| Picking a provider or model | Player-configured fallback chain, `Provider:Model` entries |
| Retry and `Retry-After` | Retries on timeout / 429 / connection error, honouring both header formats |
| Failover between providers | Automatic descent through the fallback chain before the reply starts |
| Dead-provider handling | Circuit breaker with exponential cooldown after repeated failures |
| Rate limiting across mods | Global priority queue and concurrency cap, so mods don't stutter the game |
| Cost control | Daily token budget with warn-only or hard-block policies |
| Usage and cost reporting | Per-provider token and cost dashboard in the Debug tab |
| Reasoning-model quirks | `reasoning_content` normalized into MEAI `TextReasoningContent` |
| Malformed JSON | Markdown fences, unclosed brackets and trailing commas repaired, then the JSON block re-extracted before a second parse |
| Main-thread marshalling | In-memory request-log updates dispatched back to Unity's main thread; `RimLLMDispatcher` exposed for your own callbacks |
| Native Tool Calling & Dispatching | Tools sent in the OpenAI protocol shape on every provider; automatic dispatch onto Unity main thread |

### API surface at a glance

`using RimLLM_Framework;` brings in 14 public types. Most mods only ever touch the first row:

| Tier | Types | Needed when |
| --- | --- | --- |
| **Calling a model** | `RimLLMProvider`, `RimLLMChatOptions`, `RimLLMException`, `LLMError`, `RimLLMClientExtensions` (`GetResponseObjectAsync<T>`, `WereToolsStripped`), `RimWorldFunctionInvoker` | Always — this is the whole consumer API |
| **Supplying a provider** | `ILLMProvider`, `LLMProviderCapabilities`, `IRimLLMSettings` | Only if you register your own LLM backend via `RimLLMProvider.RegisterProvider` (`ILLMProvider` produces standard `Microsoft.Extensions.AI.IChatClient`) |
| **Diagnostics** | `TestResult`, `ProviderIds`, `LLMErrorMapper` | Connection tests, built-in provider id constants, HTTP-status mapping |

Everything else — `IChatClient`, `ChatMessage`, `ChatResponse`, `ChatResponseUpdate`, `IEmbeddingGenerator` — is `Microsoft.Extensions.AI`. The concrete client classes are `internal`, so there is deliberately no RimLLM client type to program against.

---

## 📖 Features

1. **Multi-provider support** — Gemini, OpenAI, DeepSeek, Groq, Grok, Z.ai, OpenRouter, Kimi, MiniMax, Qwen, NVIDIA and Player2, plus any OpenAI-compatible endpoint (LM Studio, Ollama, vLLM, …).
2. **Failover and model fallback** — a player-configured fallback chain with exponential backoff, per-model cooldowns, four routing strategies and context window lookup.
3. **Encrypted API keys** — AES-256 with a key protected by the current OS user; masked in the settings UI and never sent in request URLs.
4. **Settings UI** — multi-column provider pages, a filterable model picker and a chat test page with Markdown rendering.
5. **Debug tab** — a per-request logging toggle (off by default).
6. **One-click connection test** — checks latency, the API key and the model.
7. **Thread safety** — locked settings, main-thread dispatch and throttled background telemetry writes.
8. **Reasoning models** — chain of thought delivered as MEAI `TextReasoningContent`; reasoning effort control on every provider.
9. **Prompt caching** — `CachedContext` for server-side caching, with cache-hit savings shown in the usage stats.
10. **Embedding SDK** — a standard `IEmbeddingGenerator` backed by Gemini, OpenAI, Ollama or an OpenAI-compatible endpoint, sharing the daily budget guard with chat.
11. **Native tool calling** — MEAI `AIFunction` on every provider, with main-thread execution and cancellation before dispatch.
12. **Third-party integration** — route RimTalk, Auto Translation, Mod Compatibility Checker and Simplified Chinese Automation: One through RimLLM.
13. **Mods using RimLLM** — a settings tab listing the mods that depend on or have called RimLLM this session.

Each item's detailed behavior, and the reasons behind it: **[Documentation/FEATURES.md](Documentation/FEATURES.md)**.

---

## 🛠️ Architecture, security and testing

How the framework does all this, what each security mechanism actually protects against, and how to run the test suite: **[Documentation/ARCHITECTURE.md](Documentation/ARCHITECTURE.md)**.

---

## 📜 License

This mod's source code is released under the **MIT License** — Copyright (c) 2026 **mushroomTW**. See [LICENSE](LICENSE).

Redistributed dependency assemblies in `Assemblies/` keep their own licenses: Microsoft.Extensions.AI and the OpenAI .NET SDK are MIT. RimWorld's own assemblies belong to Ludeon Studios and are not redistributed here.
