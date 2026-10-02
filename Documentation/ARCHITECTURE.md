# RimLLM Framework — Architecture, security and testing

[← Back to README](../README.md) · [繁體中文](ARCHITECTURE_zh.md)

## 🛠️ Architecture

### 1. Unified interface and dispatch core (`IChatClient` / `IEmbeddingGenerator` and `RimLLMProvider`)

* The framework exposes the standard Microsoft.Extensions.AI interfaces. Callers only work against `IChatClient` or `IEmbeddingGenerator` and never need to know which provider or model handled the request — `RimLLMManager` handles dispatch and fallback rotation.
* `CreateChatClient` returns a stack of MEAI `DelegatingChatClient` middleware — reasoning-effort normalization, anti-abuse throttle, budget guard, priority queue — terminating in a `FailoverChatClient` that routes across the fallback chain. Every layer is `internal`. The only framework-specific types a consumer touches are `RimLLMProvider`, `RimLLMChatOptions`, `RimLLMException` and `LLMError`; everything else crossing the boundary is a MEAI type.
* `modId` is a plain label, not a credential. It keys per-mod anti-abuse throttling and telemetry attribution, and requires no registration call.

### 2. Unity main-thread dispatcher (`RimLLMDispatcher`)

* Network requests run asynchronously on background thread-pool threads, but most Unity APIs and RimWorld logic are not thread safe — calling them from a background thread causes crashes or TPS spikes.
* `RimLLMDispatcher` is a MonoBehaviour singleton that collects callbacks from background threads in a `ConcurrentQueue` and dispatches them back to the main thread during Unity's per-frame `Update`.

### 3. Streaming bridge (`Channel<T>`)

* The executor's streaming API is callback-shaped (`Func<ChatResponseUpdate, Task> onUpdateReceived`), while MEAI expects `IAsyncEnumerable<ChatResponseUpdate>`. The bridge between them is a **bounded** `System.Threading.Channels.Channel<T>` (64 updates); the producer awaits `WriteAsync`, so a slow consumer applies backpressure to the network read instead of buffering the whole stream in memory. The consumer side is `ChannelReader.ReadAllAsync()`.
* Updates cross that bridge **verbatim** — the object the provider produced is the object you enumerate, with only `ModelId` rewritten. The framework no longer synthesizes a closing update of its own, so `UsageContent`, `FinishReason` and `ResponseId` are present exactly when the provider emits them.
* Because `IAsyncEnumerable` reaches this project through the `bclasync` extern alias, C# 8 cannot compile an async iterator over it. `ReadAllAsync()` sidesteps that entirely: it returns the same assembly's `IAsyncEnumerable`, so no iterator has to be hand-written.
* A thin wrapper unwraps `ChannelClosedException` so producer failures surface to callers as the original `RimLLMException`.
* The executor's fallback token estimate is accumulated chunk by chunk as a running count instead of buffering the streamed text, so an abnormally large stream cannot grow memory without bound.

### 4. Unified HTTP error mapping (`LLMErrorMapper`)

* The rules that translate HTTP status codes into `LLMError` live in a single place, `LLMErrorMapper`, shared by the official SDK path (`ClientResultException`) and the embedding service.
* `Retry-After` parsing lives there too, built on `RetryConditionHeaderValue`, so both the delay-seconds and HTTP-date forms are handled identically everywhere.
* As a result, "which status codes are retryable" and "which indicate a rejected schema that should be downgraded" behave identically everywhere. Third-party custom providers can reference the same mapper.

### 5. Fault-tolerant structured output (structured output & JSON repair)

* Developers frequently need the model to return a specific JSON shape.
* The built-in OpenAI-family providers prefer the official SDK's native structured output through `IChatClient`'s JSON Schema response format (Gemini included, via its OpenAI-compatible endpoint). The framework validates required members and null state before deserializing into the target C# object.
* The schema itself is generated in a single OpenAI-compatible dialect — all members land in `required`, and optionality is expressed as a `["integer","null"]` union. See [Architecture §6](#6-official-sdks-and-provider-responsibilities).
* The `RepairJson` fallback is only used when the provider has no native schema support, the service rejects the schema, or the model still returns malformed output. It handles Markdown fences (such as ` ```json `), unclosed brackets, trailing commas and JSON block extraction.

### 6. Official SDKs and provider responsibilities

* **One SDK for every provider.** All built-in providers go through the `OpenAI` SDK `2.14.0` and the `IChatClient` of Microsoft.Extensions.AI `10.10.0`; there is no raw HTTP chat path and no second SDK. Gemini uses Google's official OpenAI-compatible endpoint. The shared layer depends only on `IChatClient`, `LLMProviderCapabilities` and `ILLMProvider`.
* **Stays on `net472`.** The SDKs' dependency DLLs ship with the mod, and `ProviderSdkIntegrationTests` loads each one, so a missing transitive assembly fails the build rather than the game.
* **`Microsoft.Extensions.AI.Abstractions` ships its `netstandard2.0` build.** The `net462` build NuGet would otherwise pick references `System.ComponentModel.DataAnnotations`, which RimWorld's Mono lacks, so `AIFunctionFactory.Create` threw `TypeLoadException` in-game. `ShippedAbstractionsHasNoDataAnnotationsDependency` guards this choice.
* **Structured-output schemas** come from `System.Text.Json`'s `JsonSchemaExporter`, then `RimLLMSchemaBuilder` normalizes them into a subset every provider accepts: `$ref` pointers are inlined (cycles are cut at the CLR type), nesting is capped at 5 levels (OpenAI strict mode's limit; its 100-property total is not enforced yet), every member is listed in `required` with optionality expressed as a `["type","null"]` union, and `[Description]` is carried over.
* **Rules for structured-output types**: schema generation and deserialization share one System.Text.Json contract, so don't put a custom `JsonConverter` on them. Mods migrating from Newtonsoft must switch `[JsonProperty("x")]` to `[JsonPropertyName("x")]` and use System.Text.Json's `[JsonIgnore]`; the old attributes are silently ignored.

---

## 🔐 Security notes

To avoid misunderstanding, here is an honest description of what each security mechanism actually protects against:

* **API key encryption uses an OS-protected per-user key.** New entries use AES-256 with a random storage key wrapped by the current user's OS protection. Legacy `v1`/`v2` entries still use the old device-derived material only so they can be migrated; new data never derives its storage key from a source-embedded seed or `deviceUniqueIdentifier`. This protects copied settings from decryption by another user or machine, but it **cannot** defend against code running in the same user context or the same RimWorld process (including other mods), where the framework must use the key to serve requests.
* **`modId` is an attribution label, not authentication.** Per-mod throttling preserves fair use for cooperative mods, while a shared safety ceiling (ten times the configured per-mod window) counts every actual provider call, including tool-loop continuations, and prevents rotating labels from creating unlimited provider calls. Same-process mods are still not isolated, so do not treat the SDK as a hostile-mod sandbox.
* **Keys never reach URLs or logs.** All providers pass keys via HTTP headers; log output always goes through `SanitizeForLog` and is length-truncated, and the device identifier is masked in diagnostic exports.
* **Refreshing a model list also contacts models.dev.** Besides the provider itself, the refresh downloads the public `https://models.dev/api.json` to fill in context window sizes. It is a plain GET with no API key or other player data; if it fails, the refresh still succeeds and only a warning is logged.
* **Provider output is untrusted UI input.** ChatTest keeps only its own grey-thinking color wrapper as rich text; raw provider HTML tags are displayed literally before Markdown reaches Unity IMGUI.

---

## 🧪 Unit tests and verification

The project ships with a full unit test suite in `Source/RimLLM Framework.Tests` (a standalone project alongside the main one) covering AES encryption/decryption, model fallback, JSON Schema generation (normalization and repair), HTTP error mapping, `Retry-After` parsing, `ChatOptions` cloning, streaming retries and budget control.

> **Prerequisite**: the tests need RimWorld's `Assembly-CSharp` and Unity DLLs at run time. Those files are not redistributable, so a local RimWorld installation is required.
> The default path is `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed`,
> and can be overridden with the MSBuild property `RimWorldManagedDir` or the environment variable `RIMWORLD_MANAGED_DIR`.

Build and test from the project root with the `dotnet` CLI:

```bash
# Restore and rebuild the solution
dotnet build "Source/RimLLM Framework.slnx"

# Run all NUnit unit tests
dotnet test "Source/RimLLM Framework.Tests/RimLLM Framework.Tests.csproj"
```

> **Note**: the `Krafs.Rimworld.Ref` reference assemblies do not restrict the BCL surface, so it is possible to
> write code that compiles but fails inside RimWorld's Mono runtime. Known examples: `Stack<T>` throws
> `TypeLoadException`, and the parameterless `String.TrimEnd()` overload does not exist.
> Always verify with an actual `dotnet test` run rather than relying on a successful build.

### In-game integration tests (RimTest Redux)

`Source/RimLLM Framework.InGameTests` is a development-only companion mod (outside the `.slnx`, never shipped) that runs
[RimTest Redux](https://github.com/ilyvion/rimtest-redux) suites inside the live game. It covers what the headless tests cannot see with Unity stubbed out:

- **Startup wiring**: settings, manager and a live `RimLLMDispatcher` main-thread pump (headless tests always take the synchronous no-pump fallback).
- **Assembly loading**: every shipped DLL resolves all of its types in RimWorld's shared AppDomain, which has no binding redirects.
- **Compat hooks**: every hook listed in the `Expected` table of `CompatPatchTests` (update it whenever a `*CompatPatch.Apply` gains or drops a hook) carries a `GreenMushroom.RimLLMFramework` patch on the installed RimTalk / Auto Translation / Mod Compatibility Checker; a target that is not active is logged as `skipped`.
- **Translation injection**: in every language the game has loaded, each key in `Languages/*/Keyed` is injected from this mod's own file, with no load error attributable to this mod; languages that are not loaded are logged as `skipped` (switch the game language to cover them).

Dependencies: Harmony, ilyvion's Laboratory and RimTest Redux (Workshop 3762405308). The test project references the Release DLL in `Assemblies/`
(internals are exposed through `InternalsVisibleTo`) and never rebuilds it, so build the framework in Release first:

```bash
dotnet build "Source/RimLLM Framework/RimLLM Framework.csproj" -c Release
dotnet build "Source/RimLLM Framework.InGameTests/RimLLM Framework.InGameTests.csproj"
```

Then link `Source/RimLLM Framework.InGameTests/Mod` into `Mods/` (e.g. a directory junction) and launch the game with it enabled together with its dependencies and RimLLM Framework
(add `cj.rimtalk`, `seohyeon.autotranslation`, `modcompatchecker.main` and `ow1r.scauto` to cover the compat hooks). RimTest Redux's **Run at startup**
(on by default) runs the suites once loading finishes; results go to `Player.log` between `TESTING START` and `TESTING END`,
and `[RimLLM.InGameTests] skipped:` lines count the preconditions that were absent.
