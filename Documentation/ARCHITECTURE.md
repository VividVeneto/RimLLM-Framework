# RimLLM Framework — Architecture, security and testing

[← Back to README](../README.md) · [繁體中文](ARCHITECTURE_zh.md)

## 🛠️ Architecture

### 1. Unified interface and dispatch core (`IChatClient` / `IEmbeddingGenerator` and `RimLLMProvider`)

* The framework exposes the standard Microsoft.Extensions.AI interfaces. Callers only work against `IChatClient` or `IEmbeddingGenerator` and never need to know which provider or model handled the request — `RimLLMManager` handles dispatch and fallback rotation.
* `CreateChatClient` returns a stack of MEAI `DelegatingChatClient` middleware — reasoning-effort normalization, anti-abuse throttle, budget guard, priority queue — terminating in a `FailoverChatClient` that routes across the fallback chain. Every layer is `internal`. The only framework-specific types a consumer touches are `RimLLMProvider`, `RimLLMDecisionClient`, `DecisionAvailability`, `RimLLMChatOptions`, `RimLLMException` and `LLMError`; everything else crossing the boundary is a MEAI type.
* `modId` is a plain label, not a credential. It keys per-mod anti-abuse throttling and telemetry attribution, and requires no registration call.
* Chat, embedding and decision facades share one token ledger. HardBlock atomically reserves an estimated amount while a logical request is in flight, including across fallback attempts; actual reported usage is settled per attempt, and cancellation or failure releases the remaining estimate. Concurrent requests cannot all claim the same remaining balance. The estimate still is not an exact cap for one request. Chat messages are materialized once at the guard boundary, so checking for a tool-loop continuation does not consume a one-shot enumerable before the provider sees it.
* Decision calls use `RimLLMDecisionService` (Kkdev92.Jev, TypeSafe's System One wire format) behind the `RimLLMDecisionClient` facade. The facade records the caller, applies the anti-abuse throttle and budget guard, and writes the provider-reported input/output tokens to the same ledger, attributed from a snapshot taken when the request starts (switching the enabled provider mid-request cannot misattribute tokens). All failures are converged to `RimLLMException` with `LLMError` codes through the shared `LLMErrorMapper`; the client appends `/v1/systemone` to the base address, so the endpoint is an origin; the settings page stores model, endpoint and key per provider and sends a real one-question decision for its connection test, refusing a disabled provider — and a missing key on an `https://` endpoint — before anything is sent.
* A mod may also decide against a provider other than the enabled one. `RimLLMDecisionService.ResolveConnection(providerId, modelOverride)` is the single place that resolves a decision connection: it takes the endpoint, key and model from that provider's own settings (through the `IRimLLMSettings` per-provider accessors), and refuses a disabled provider, an unknown provider id or a missing key before anything is sent. `EvaluateWithProviderDetailedAsync` and `FetchDecisionModelsAsync` both go through it, so the "which provider am I talking to" decision exists in exactly one place. `RimLLMDecisionService.GetAvailability()` composes the same values into a `DecisionAvailability` DTO for a read-only "has the player set one up?" check — it exposes only whether a key exists, never the key — and the manager forwards it to `RimLLMProvider.GetDecisionAvailability()`. The per-provider accessors on `IRimLLMSettings` carry default interface implementations so an external mod that implemented the interface against an older build keeps loading instead of hitting a `TypeLoadException`; those defaults answer only for the enabled provider and return an empty string for any other, because borrowing the enabled provider's key for a different provider would send it to the wrong host and charge the wrong account.
* The Jev SDK hard-requires an HTTPS base address (`ClientSettings.ValidateBaseAddress` rejects any other scheme), but a self-hosted decision server speaks plain HTTP. `DecisionEndpointRouting` splits the two concerns: the SDK is handed an HTTPS origin of the same host and port, and `PlaintextEndpointRewriteHandler` — a `DelegatingHandler` installed in the `HttpClient` pipeline the service already owns — rewrites the request URI back to the configured plaintext endpoint before it reaches the socket. The SDK's own request encoding and response decoding are therefore reused unchanged, and no proxy, tunnel or certificate is needed. The rewrite applies to the request URI only, so the SDK still builds `/v1/systemone` and `/v1/models` from its base address. Because the same `HttpClient` is used for every call, one handler instance covers both the evaluate and the model-list path. A plaintext endpoint also means the key is optional: the SDK rejects an empty key outright, so the framework substitutes a placeholder, while an `https://` endpoint still fails with `InvalidKey` when the key is missing. Both rules key off the endpoint the player configured, not off the provider's identity, so pointing a remote provider at a local server works without special-casing it.

### 2. Unity main-thread dispatcher (`RimLLMDispatcher`)

* Network requests run asynchronously on background thread-pool threads, but most Unity APIs and RimWorld logic are not thread safe — calling them from a background thread causes crashes or TPS spikes.
* `RimLLMDispatcher` is a MonoBehaviour singleton that collects callbacks from background threads in a `ConcurrentQueue` and dispatches them back to the main thread during Unity's per-frame `Update`.
* Request-log snapshots and telemetry dirty state are updated under the tracker lock before the background single-writer is scheduled. Persistence therefore does not depend on the bounded Unity callback queue draining before shutdown; telemetry serialization itself does not use Scribe.
* Queued tool calls observe cancellation before invocation and can stop waiting while still queued. Once a tool starts, its own cancellation support governs further work. Chat-test callbacks carry a request generation, checked when the callback executes; stopping or clearing a request invalidates its queued updates before history indexes can be reused.

### 3. Streaming bridge (`Channel<T>`)

* The executor's streaming API is callback-shaped (`Func<ChatResponseUpdate, Task> onUpdateReceived`), while MEAI expects `IAsyncEnumerable<ChatResponseUpdate>`. The bridge between them is a **bounded** `System.Threading.Channels.Channel<T>` (64 updates); the producer awaits `WriteAsync`, so a slow consumer applies backpressure to the network read instead of buffering the whole stream in memory. The consumer side is `ChannelReader.ReadAllAsync()`.
* Updates cross that bridge **verbatim** — the object the provider produced is the object you enumerate, with only `ModelId` rewritten. The framework no longer synthesizes a closing update of its own, so `UsageContent`, `FinishReason` and `ResponseId` are present exactly when the provider emits them.
* `IAsyncEnumerable` and `ValueTask` use the native .NET 10 types, without BCL compatibility packages or extern aliases.
* A thin wrapper unwraps `ChannelClosedException` so producer failures surface to callers as the original `RimLLMException`.
* The executor's fallback token estimate is accumulated chunk by chunk as a running count instead of buffering the streamed text, so an abnormally large stream cannot grow memory without bound.
* A stream with output or reported usage settles once on both normal and exceptional termination. Cancellation, idle timeout and a broken connection preserve their original error behavior without discarding already observed consumption. Streams that fail before producing output or reporting usage do not invent a successful usage record. The built-in OpenAI-compatible adapter also records usage for direct streams; executor-managed streams leave settlement to the executor to avoid double counting.

### 4. Unified HTTP error mapping (`LLMErrorMapper`)

* The rules that translate HTTP status codes into `LLMError` live in a single place, `LLMErrorMapper`, shared by the official SDK path (`ClientResultException`) and the embedding service.
* `Retry-After` parsing lives there too, built on `RetryConditionHeaderValue`, so both the delay-seconds and HTTP-date forms are handled identically everywhere.
* The built-in OpenAI-compatible adapter preserves the response `Retry-After` header when converting SDK exceptions, so the fallback policy can honor server-requested delays.
* As a result, "which status codes are retryable" and "which indicate a rejected schema that should be downgraded" behave identically everywhere. Third-party custom providers can reference the same mapper.

### 5. Fault-tolerant structured output (structured output & JSON repair)

* Developers frequently need the model to return a specific JSON shape.
* The built-in OpenAI-family providers prefer the official SDK's native structured output through `IChatClient`'s JSON Schema response format (Gemini uses its compatible endpoint or native responseJsonSchema). Required JSON member presence and nullability are checked against the generated schema shape derived from the serialization contract, including dictionary values, rather than inferred from CLR defaults after deserialization. Ignored and non-participating members are not treated as required output.
* The schema itself is generated in a single OpenAI-compatible dialect — all members land in `required`, and optionality is expressed as a `["integer","null"]` union. See [Architecture §6](#6-official-sdks-and-provider-responsibilities).
* The `RepairJson` fallback is only used when the provider has no native schema support, the service rejects the schema, or the model still returns malformed output. It handles Markdown fences (such as ` ```json `), unclosed brackets, trailing commas and JSON block extraction. Repairs distinguish JSON syntax from quoted string content: a literal comma, bracket or `<think>…</think>` inside a JSON string is preserved.

### 6. Official SDKs and provider responsibilities

* **One chat interface, two Gemini transports.** OpenAI-compatible providers use the `OpenAI` SDK `2.14.0`. Gemini keeps that SDK path for `/openai` endpoints and uses a .NET 10 HTTP text/JSON/SSE adapter for native base URLs. Both expose the same Microsoft.Extensions.AI `10.10.0` interface. The shared layer depends only on `IChatClient`, `LLMProviderCapabilities` and `ILLMProvider`.
* **RimWorld 1.7 / .NET 10 only.** All three projects target `net10.0`, with SDK and MEAI assets selected normally by NuGet.
* **MEAI.Abstractions uses its .NET 10 asset.** There is no forced netstandard asset or bundled legacy BCL; `ShippedAssembliesTargetNet10` verifies the release artifacts.
* **Structured-output schemas** come from `System.Text.Json`'s `JsonSchemaExporter`, then `RimLLMSchemaBuilder` normalizes them into a subset every provider accepts: `$ref` pointers are inlined (cycles are cut at the CLR type), nesting is capped at 5 levels (OpenAI strict mode's limit; its 100-property total is not enforced yet), every member is listed in `required` with optionality expressed as a `["type","null"]` union, and `[Description]` is carried over. Nullable value types keep that union when used as collection elements or dictionary values as well as object members.
* **Rules for structured-output types**: schema generation and deserialization share one System.Text.Json contract, so don't put a custom `JsonConverter` on them. Mods migrating from Newtonsoft must switch `[JsonProperty("x")]` to `[JsonPropertyName("x")]` and use System.Text.Json's `[JsonIgnore]`; the old attributes are silently ignored.

---

## 🔐 Security notes

To avoid misunderstanding, here is an honest description of what each security mechanism actually protects against:

* **API key encryption uses an OS-protected per-user key.** New entries use AES-256 with a random storage key wrapped by the current user's OS protection. Only `v3:` ciphertext is supported; there is no fixed-seed or device-derived legacy decryption path. This protects copied settings from decryption by another user or machine, but it **cannot** defend against code running in the same user context or the same RimWorld process (including other mods), where the framework must use the key to serve requests.
* **`modId` is an attribution label, not authentication.** Per-mod throttling preserves fair use for cooperative mods, while a shared safety ceiling (ten times the configured per-mod window) counts every actual provider call, including tool-loop continuations, and prevents rotating labels from creating unlimited provider calls. Same-process mods are still not isolated, so do not treat the SDK as a hostile-mod sandbox.
* **Keys never reach request URLs.** All providers pass keys via HTTP headers. Diagnostic previews use `SanitizeForLog` and length limits; the sanitizer covers known key prefixes and key/authorization assignments in headers, URLs and quoted JSON fields. It is a best-effort text sanitizer, not a guarantee that every arbitrary secret in third-party text can be recognized. The device identifier is masked in diagnostic exports.
* **Refreshing a model list also contacts models.dev.** Besides the provider itself, the refresh downloads the public `https://models.dev/api.json` to fill in context window sizes. It is a plain GET with no API key or other player data; if it fails, the refresh still succeeds and only a warning is logged.
* **Provider output is untrusted UI input.** ChatTest keeps only its own grey-thinking color wrapper as rich text; raw provider HTML tags are displayed literally before Markdown reaches Unity IMGUI.

---


Storage protection follows the host platform: Windows wraps the random 256-bit key with current-user DPAPI; macOS stores it as a generic-password item through Security.framework's SecItem APIs; Linux uses libsecret and the session Secret Service. On macOS/Linux, `RimLLM_EncryptionKey.dat` contains only a random `keyring:` item identifier, never the storage key. Moving the reference file within the same user's keyring retains access; copying settings to a different user or machine does not copy the secret.

Linux needs `libsecret-1.so.0`, a session D-Bus, and an unlocked Secret Service provider such as GNOME Keyring or a compatible KWallet setup. A sandboxed game must be allowed to access that service. libsecret operations are canceled after ten seconds. macOS needs an available login keychain and permission for RimWorld to access its item. An unavailable or locked provider produces an explicit error; no plaintext-key-file fallback is used.

An exclusive per-profile lock coordinates concurrent processes; new DPAPI blobs or keyring references are published through a flushed temporary file and atomic rename. An existing reference with a missing secret is rejected rather than silently replaced. Preserve the reference file and its OS-protected secret when backing up encrypted settings. `SecureKeyStoreTests` covers reference-only files, reload, corruption, unavailable stores, lost keys and concurrent creation; a separate Linux integration harness exercises a real libsecret/D-Bus/GNOME Keyring session and two processes. macOS native calls still require a real macOS validation run.

Only `v3:` ciphertext is supported. Legacy unversioned/v1/v2 formats, hardware salt injection, fixed seeds and migration helpers have been removed, along with the old CustomSalt and InitializeKeyAndIv APIs. Unknown prefixes return `null` before key-store access. For supported telemetry, failed history decryption triggers a `.bak` recovery attempt; when neither copy can be decrypted, the ciphertext and backup are preserved for a later recovery. These protections do not isolate code running as the same OS user or inside the RimWorld process.

## 🧪 Unit tests and verification

The project ships with a full unit test suite in `Source/RimLLM Framework.Tests` (a standalone project alongside the main one) covering AES encryption/decryption, model fallback, JSON Schema generation (normalization and repair), HTTP error mapping, `Retry-After` parsing, `ChatOptions` cloning, streaming retries, budget control, decision-provider catalog invariants (unique IDs, valid origins, HTTPS for every remote provider, plaintext HTTP only for loopback hosts, required providers and fallback behavior) and decision error mapping (every Jev SDK exception type rebuilt via reflection and asserted to converge to `RimLLMException` with the right `LLMError`, plus guard refusals for a disabled provider or a missing key).

Provider selection for decisions has its own fixture (`DecisionProviderSelectionTests`). It drives the service through an internal seam that swaps the `HttpMessageHandler` factory for a fake handler, so it can assert which provider's endpoint and key were used, which provider the usage was charged to, and that a missing key, an unknown provider id, a disabled provider and an exhausted daily budget are all refused before any request leaves. It also asserts that `GetDecisionAvailability` never leaks the key, that the player's settings are unchanged after a provider-scoped evaluation, and that a settings implementation compiled against the older interface (which does not override the new per-provider accessors) cannot borrow the enabled provider's key for another provider.

Local deployment has a second fixture (`DecisionLocalEndpointTests`). It uses the same seam, so the rewrite really runs: the tests assert that a plaintext endpoint reaches the wire as `http://host:port/v1/systemone` (not the SDK's `https://`), that a plaintext endpoint without a key sends the placeholder instead of failing, that an `https://` endpoint keeps using HTTPS and still refuses a missing key with `InvalidKey`, and that the routing handles default ports, IPv6 literals and LAN hosts without mangling the authority.

Review regression fixtures additionally exercise exhausted embedding budgets and caller cancellation, partial-stream accounting, long `Retry-After` delays, RoundRobin rotation without capability-query side effects, one-shot message sequences, queued-tool cancellation, stale chat callbacks, quoted log secrets, required JSON members, dictionary values and nullable collection schemas. HTTP behavior is tested with fake transports; these checks do not require paid provider requests.

### Build and runtime requirements

Use the .NET 10 SDK. `global.json` selects a compatible 10.0 SDK, and `Source/Directory.Build.props` gives all three projects the same `net10.0` target and C# 14 language version. RimWorld 1.7 API reference assemblies come from the `Krafs.Rimworld.Ref` NuGet package (version `1.7.4982-unstable`), so the mod builds without a local game install; Harmony 3.0 and the test-runtime game DLLs still come from the installed game/workshop. Game assemblies are not redistributed.

The default game path assumes this checkout is inside `RimWorld/Mods`. Elsewhere, set `-p:RimWorldDir="<game root>"` or `RIMWORLD_DIR`. Harmony defaults to Workshop item 2009463077's `1.7/Assemblies/0Harmony.dll`; override `HarmonyDll`, or `WorkshopDir` / `WORKSHOP_DIR`, when installed elsewhere. Missing references fail the build with a targeted error.

```powershell
dotnet build "Source/RimLLM Framework.slnx"
dotnet test "Source/RimLLM Framework.Tests/RimLLM Framework.Tests.csproj"
dotnet build "Source/RimLLM Framework/RimLLM Framework.csproj" -c Release
dotnet build "Source/RimLLM Framework.InGameTests/RimLLM Framework.InGameTests.csproj"
```

Only Release builds write the player's `Assemblies/`; Debug and NUnit builds use project-local `bin/` output. NUnit verifies the shipped .NET 10 asset targets and absence of legacy polyfills. The game supplies its BCL, including System.Text.Json, async streams, ValueTask and Channels. SDK packages use their normal compatible NuGet assets; packages without a `net10.0` asset may supply a compatible earlier .NET asset.

### Native keyring integration checks

Run pwsh -File Tests/NativeKeyring/run-linux.ps1 with Docker available. The launcher stages the actual security sources into ignored TestResults, then runs an isolated .NET 10/libsecret/D-Bus/GNOME Keyring container with no host secret mounts or external network at test time. It covers v3 roundtrip/reload, missing service, missing secret, reference-only persistence and concurrent processes. For a macOS machine with an unlocked login keychain, run dotnet run --project Tests/NativeKeyring/Probe.csproj; this uses the actual SecItem backend and removes its temporary item afterward. The helper stubs only game/logging boundaries, not cryptography or the native secret store.

### C# conventions

Source code uses stable C# 14 syntax: file-scoped namespaces, target-typed construction, exact-type collection expressions, using declarations and pattern matching where they preserve behavior. The repository .editorconfig records these choices for future work. Existing public SDK signatures and serialized data shapes remain compatible; cancellable dispatcher calls use an added overload while preserving the original signature. Streaming queue middleware uses an async iterator with EnumeratorCancellation and await foreach, so the compiler owns cancellation-token combination and enumerator disposal; the concurrency slot spans the entire enumeration, including pauses between updates. QueuedStreamingTests verifies lazy start, slot ownership across yields, disposal after early termination, and cancellation from either token when method and enumerator tokens differ. These changes improve maintainability; no performance gain is claimed without measurements.

### In-game integration tests (SmashPhil DevTools)

`Source/RimLLM Framework.InGameTests` is a development-only companion mod, outside the solution and excluded from player releases. Its non-static `MainMenu` fixtures cover startup settings/manager wiring, the dispatcher pump, type loading and debug-action reflection of shipped DLLs, installed compatibility patches, native tool/schema creation, and translation injection. NUnit remains the standalone unit-test runner.

DevTools is embedded in the companion: both `DevTools.dll` and `RimLLM Framework.InGameTests.dll` load from its `Mod/Assemblies/`. It requires only Core, Harmony and RimLLM Framework. There is no separate DevTools packageId or Laboratory dependency. The checked-in SDK reference and MIT license live in `Source/Libs/DevTools`; override `DevToolsDll` to use another compatible local build.

Save your progress and close RimWorld, then run with PowerShell 7:

```powershell
pwsh -File "Source/RimLLM Framework.InGameTests/launch-tests.ps1"
pwsh -File "Source/RimLLM Framework.InGameTests/launch-tests.ps1" -Where "class == StartupTests"
```

The launcher builds Release and the test DLL, creates a development junction under `Mods/`, and starts the game with a fresh `TestResults/devtools/<timestamp>/Config/ModsConfig.xml` using `-savedatafolder`. It preserves the player's configuration and saves. A run lock prevents concurrent launchers from sharing the companion's log configuration. `-AdditionalMods` accepts extra packageIds to exercise installed compatibility targets; these mods must support 1.7 and have their own dependencies included. `-SkipBuild` is for already rebuilt assemblies.

The underlying CLI uses `--pid GreenMushroom.RimLLMFramework.InGameTests --test --where "namespace == RimLLM_Framework.InGameTests" --exit`. Results are `Test.log`, `test-report.md`, `stdout.log` and `stderr.log` in the run directory; DevTools' log folder is explicitly set in the companion's generated `Configs/UnitTestConfig.xml`. Player.log contains additional startup diagnostics. The launcher requires case results and the current run's `Test runner finished. Result: Passed` marker; an exit code of zero alone is insufficient. Timeout, interruption and zero-case runs are failures to verify.

With no compatibility target active, the two compatibility cases are marked **Skipped**. Partial target/method omissions and unloaded languages are recorded as `[RimLLM.InGameTests] skipped:` prerequisites, rather than additional executed test cases. Run separate mod/language combinations for that coverage. Do not include the test companion, DevTools DLL or generated development configuration in a player archive.
