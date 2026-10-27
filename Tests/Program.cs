using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework;
using RimLLM_Framework.Providers;

internal static class Program
{
    private const string NativeReply = "{\"responseId\":\"native-1\",\"modelVersion\":\"gemini-test\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"thought\":true,\"text\":\"private reasoning\"},{\"text\":\"{\\\"ok\\\":true}\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":11,\"candidatesTokenCount\":4,\"thoughtsTokenCount\":3,\"totalTokenCount\":18,\"cachedContentTokenCount\":2}}";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--live"))
        {
            try { return await Live(); }
            catch (Exception ex)
            {
                Console.WriteLine("LIVE_FAILED=" + ex.GetType().FullName + ": " + ex.Message);
                if (ex is RimLLMException error) Console.WriteLine("LIVE_ERROR=" + error.Error + " HTTP=" + error.HttpStatusCode);
                return 1;
            }
        }
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("native request, schema, response and thought filtering", Generate),
            ("incremental SSE and JSONL preservation", Stream),
            ("429 error mapping", RateLimit),
            ("blocked response is a failure", Blocked),
            ("native model list, pagination and chat model filter", Models),
            ("cancelling a stalled SSE read", () => CancelStream()),
            ("truncated SSE is a failure", TruncatedStream),
            ("cached model used by connection test", ConnectionTest),
            ("other providers retain OpenAI protocol", OpenAiRegression),
            ("Gemini 2.5 Pro keeps its minimum thinking budget", ProThinking),
            ("older Gemini does not receive unsupported thinking config", OlderThinking),
            ("connection test fetches models on a fresh setup", ColdConnectionTest),
            ("Gemini 3 uses a supported low thinking level", FlashThinking),
            ("Gemini compatibility mode retains tools", GeminiCompatibility),
            ("Gemini protocol switches with the configured endpoint", GeminiProtocolSwitch),
            ("enumeration cancellation reaches native SSE", () => CancelStream(true)),
            ("cancellation interrupts a stalled streaming HTTP error", CancelledErrorBody)
        };
        int failures = 0;
        var selected = (args.Contains("--generate-only") ? tests.Take(1) : args.Contains("--cancel-only") ? tests.Where(t => t.Name.StartsWith("cancelling")) : tests).ToArray();
        foreach (var test in selected)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }
        Console.WriteLine($"TOTAL={selected.Length} PASSED={selected.Length - failures} FAILED={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static MockSettings Settings(string root, string key = "test-key")
    {
        var settings = new MockSettings();
        settings.ApiKeys[ProviderIds.Gemini] = key;
        settings.Endpoints[ProviderIds.Gemini] = root + "/v1beta";
        settings.ModelLists[ProviderIds.Gemini] = new List<string> { "gemini-test" };
        return settings;
    }

    private static async Task Generate()
    {
        string path = null, body = null, key = null;
        using var server = new Server(async context => {
            path = context.Request.RawUrl;
            key = context.Request.Headers["x-goog-api-key"];
            body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            await Reply(context, NativeReply);
        });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("models/gemini-test");
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}");
        var options = new ChatOptions { MaxOutputTokens = 1024, Temperature = 0.2f,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement) };
        ChatResponse response = null;
        Exception failure = null;
        try { response = await client.GetResponseAsync(new[] {
            new ChatMessage(ChatRole.System, "system rules"), new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "previous answer"), new ChatMessage(ChatRole.User, "return JSON") }, options); }
        catch (Exception ex) { failure = ex; }
        Equal(path, "/v1beta/models/gemini-test:generateContent", "native route");
        if (failure != null) throw failure;
        Equal(key, "test-key", "header authentication");
        using var request = JsonDocument.Parse(body);
        Equal(request.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString(), "system rules", "system instruction");
        Equal(request.RootElement.GetProperty("contents")[1].GetProperty("role").GetString(), "model", "assistant role");
        Equal(request.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString(), "application/json", "JSON mode");
        Check(request.RootElement.GetProperty("generationConfig").TryGetProperty("responseJsonSchema", out var sentSchema), "JSON Schema must use responseJsonSchema; responseSchema cannot represent all framework schemas");
        Check(!sentSchema.GetProperty("additionalProperties").GetBoolean(), "schema restriction lost");
        Equal(response.Text, "{\"ok\":true}", "thoughts must not leak into text");
        Equal(response.Usage.InputTokenCount, 11L, "prompt tokens");
        Equal(response.Usage.OutputTokenCount, 7L, "output includes charged thought tokens");
        Equal(response.Usage.TotalTokenCount, 18L, "total tokens");
        Equal(response.Usage.CachedInputTokenCount, 2L, "cached tokens");
    }

    private static async Task Stream()
    {
        var firstReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = null, requestBody = null;
        using var server = new Server(async context => {
            path = context.Request.RawUrl;
            requestBody = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            await WriteEvent(context, "{\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"hidden\"},{\"text\":\"{\\\"text\\\":\\\"first\\\"}\\n\"}]}}]}");
            await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await WriteEvent(context, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"text\\\":\\\"second\\\"}\\n\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":5,\"candidatesTokenCount\":7,\"totalTokenCount\":12}}");
        });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-test");
        var text = new StringBuilder(); UsageDetails usage = null;
        await foreach (var update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Output JSONL") }))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent part) { text.Append(part.Text); firstReceived.TrySetResult(true); }
                if (content is UsageContent count) usage = count.Details;
            }
        }
        Equal(path, "/v1beta/models/gemini-test:streamGenerateContent?alt=sse", "stream route");
        Equal(text.ToString(), "{\"text\":\"first\"}\n{\"text\":\"second\"}\n", "JSONL must be preserved");
        using var request = JsonDocument.Parse(requestBody);
        Check(!request.RootElement.GetProperty("generationConfig").TryGetProperty("responseMimeType", out _), "plain JSONL must not be forced into a single JSON object");
        Equal(usage.TotalTokenCount, 12L, "stream usage");
    }

    private static async Task RateLimit()
    {
        using var server = new Server(c => { c.Response.Headers["Retry-After"] = "7";
            return Reply(c, "{\"error\":{\"code\":429,\"message\":\"Too many requests\",\"status\":\"RESOURCE_EXHAUSTED\"}}", 429); });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-test");
        var error = await ExpectError(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }), LLMError.RateLimit);
        Equal(error.RetryAfter, TimeSpan.FromSeconds(7), "server backoff");
    }

    private static async Task Blocked()
    {
        using var server = new Server(c => Reply(c, "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}"));
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-test");
        await ExpectError(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }), LLMError.ContentFilter);
    }

    private static async Task Models()
    {
        int calls = 0;
        using var server = new Server(c => {
            Check(c.Request.Url.AbsolutePath == "/v1beta/models", "native model-list route");
            Check(c.Request.Headers["x-goog-api-key"] == "test-key", "model-list header authentication");
            calls++;
            return Reply(c, calls == 1
                ? "{\"models\":[{\"name\":\"models/gemini-test\",\"inputTokenLimit\":1000,\"supportedGenerationMethods\":[\"generateContent\"]},{\"name\":\"models/embedding-test\",\"supportedGenerationMethods\":[\"embedContent\"]}],\"nextPageToken\":\"next\"}"
                : "{\"models\":[{\"name\":\"models/gemini-second\",\"inputTokenLimit\":2000,\"supportedGenerationMethods\":[\"generateContent\"]}]}");
        });
        var models = await new GeminiProvider(Settings(server.Root)).FetchAvailableModelsAsync();
        Equal(string.Join(",", models), "gemini-test,gemini-second", "chat model filtering");
        Equal(calls, 2, "pagination");
    }

    private static async Task CancelStream(bool enumerationToken = false)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Server(async c => {
            await new StreamReader(c.Request.InputStream).ReadToEndAsync();
            c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true;
            await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(": heartbeat\n\n"));
            await c.Response.OutputStream.FlushAsync(); ready.TrySetResult(true);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-test");
        using var cts = new CancellationTokenSource();
        var read = Task.Run(async () => {
            var updates = client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }, cancellationToken: enumerationToken ? default : cts.Token);
            await foreach (var _ in updates.WithCancellation(enumerationToken ? cts.Token : default)) { }
        });
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)); cts.Cancel();
            try { await read.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("cancelled read succeeded"); }
            catch (OperationCanceledException) { }
        } finally { release.TrySetResult(true); }
    }

    private static async Task TruncatedStream()
    {
        using var server = new Server(async c => { c.Response.ContentType = "text/event-stream"; c.Response.SendChunked = true;
            await WriteEvent(c, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}"); });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-test");
        try { await foreach (var _ in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") })) { } }
        catch (RimLLMException) { return; }
        throw new Exception("unfinished stream was accepted as success");
    }

    private static async Task ConnectionTest()
    {
        string path = null;
        using var server = new Server(c => { path = c.Request.Url.AbsolutePath; return Reply(c, NativeReply); });
        var result = await new GeminiProvider(Settings(server.Root)).TestConnectionAsync();
        Check(result.Success, "connection test failed: " + result.ErrorMessage);
        Equal(path, "/v1beta/models/gemini-test:generateContent", "configured test model");
    }

    private static async Task<int> Live()
    {
        string key = Console.ReadLine();
        string endpoint = Environment.GetEnvironmentVariable("GEMINI_BASE_URL") ?? "https://generativelanguage.googleapis.com/v1beta";
        string model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.8-flash";
        var settings = Settings("http://127.0.0.1", key);
        settings.Endpoints[ProviderIds.Gemini] = endpoint;
        settings.ModelLists[ProviderIds.Gemini] = new List<string> { model };
        var provider = new GeminiProvider(settings);
        var models = await provider.FetchAvailableModelsAsync();
        Check(models.Contains(model), "live model missing");
        using var client = provider.CreateChatClient(model);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"},\"answer\":{\"type\":\"integer\"}},\"required\":[\"ok\",\"answer\"],\"additionalProperties\":false}");
        var options = new RimLLMChatOptions { MaxOutputTokens = 1024, DisableReasoning = true, ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement) };
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Return only JSON: {\"ok\":true,\"answer\":2}") }, options, timeout.Token);
        using var parsed = JsonDocument.Parse(response.Text);
        Check(parsed.RootElement.GetProperty("ok").GetBoolean() && parsed.RootElement.GetProperty("answer").GetInt32() == 2, "live JSON did not match");
        Console.WriteLine("LIVE_JSON=" + response.Text);
        var streamed = new StringBuilder(); int chunks = 0;
        await foreach (var update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Output exactly two JSONL lines, no markdown: {\"text\":\"first\"}\n{\"text\":\"second\"}") }, new RimLLMChatOptions { MaxOutputTokens = 1024, DisableReasoning = true }, timeout.Token))
        { foreach (var part in update.Contents.OfType<TextContent>()) { streamed.Append(part.Text); chunks++; } }
        var lines = streamed.ToString().Trim().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 2, "expected two JSONL lines");
        foreach (var line in lines) { using var value = JsonDocument.Parse(line); Check(value.RootElement.TryGetProperty("text", out _), "invalid JSONL text"); }
        Console.WriteLine("LIVE_JSONL=" + JsonSerializer.Serialize(streamed.ToString()));
        Console.WriteLine("LIVE_STREAM_CHUNKS=" + chunks);
        Console.WriteLine("LIVE_MODELS=" + string.Join(",", models));
        var connection = await provider.TestConnectionAsync();
        Check(connection.Success, "live connection test failed: " + connection.ErrorMessage);
        Console.WriteLine("LIVE_CONNECTION_MODEL=" + connection.Model);
        Console.WriteLine("LIVE_TESTS=PASS"); return 0;
    }

    private static async Task OpenAiRegression()
    {
        string path = null;
        using var server = new Server(c => { path = c.Request.Url.AbsolutePath;
            return Reply(c, "{\"id\":\"openai-1\",\"object\":\"chat.completion\",\"model\":\"gpt-test\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}"); });
        var settings = Settings(server.Root);
        settings.ApiKeys[ProviderIds.OpenAI] = "test-key";
        settings.Endpoints[ProviderIds.OpenAI] = server.Root + "/v1";
        using var client = new OpenAIProvider(settings).CreateChatClient("gpt-test");
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") });
        Equal(path, "/v1/chat/completions", "other providers must keep their route");
        Equal(response.Text, "ok", "OpenAI reply parsing");
    }

    private static async Task ProThinking()
    {
        string body = null;
        using var server = new Server(async c => { body = await new StreamReader(c.Request.InputStream).ReadToEndAsync(); await Reply(c, NativeReply); });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-2.5-pro");
        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }, new RimLLMChatOptions { DisableReasoning = true });
        using var request = JsonDocument.Parse(body);
        Equal(request.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32(), 128, "2.5 Pro cannot disable thinking");
    }

    private static async Task OlderThinking()
    {
        string body = null;
        using var server = new Server(async c => { body = await new StreamReader(c.Request.InputStream).ReadToEndAsync(); await Reply(c, NativeReply); });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-2.0-flash");
        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }, new RimLLMChatOptions { DisableReasoning = true });
        using var request = JsonDocument.Parse(body);
        Check(!request.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _), "older models do not support thinkingConfig");
    }

    private static async Task ColdConnectionTest()
    {
        string generatedPath = null;
        using var server = new Server(c => {
            if (c.Request.HttpMethod == "GET") return Reply(c, "{\"models\":[{\"name\":\"models/gemini-test\",\"supportedGenerationMethods\":[\"generateContent\"]}]}");
            generatedPath = c.Request.Url.AbsolutePath; return Reply(c, NativeReply);
        });
        var settings = Settings(server.Root); settings.ModelLists.Clear();
        var result = await new GeminiProvider(settings).TestConnectionAsync();
        Check(result.Success, "fresh setup connection failed: " + result.ErrorMessage);
        Equal(generatedPath, "/v1beta/models/gemini-test:generateContent", "native connection test must not call model default");
        Check(settings.GetModelList(ProviderIds.Gemini).Contains("gemini-test"), "model cache not populated");
    }

    private static async Task FlashThinking()
    {
        string body = null;
        using var server = new Server(async c => { body = await new StreamReader(c.Request.InputStream).ReadToEndAsync(); await Reply(c, NativeReply); });
        using var client = new GeminiProvider(Settings(server.Root)).CreateChatClient("gemini-3.8-flash");
        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }, new RimLLMChatOptions { DisableReasoning = true });
        using var request = JsonDocument.Parse(body);
        Equal(request.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString(), "low", "Gemini 3.8 relay rejects minimal thinking");
    }

    private const string CompatibleReply = "{\"id\":\"compat-1\",\"object\":\"chat.completion\",\"model\":\"gemini-test\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"compatible\"},\"finish_reason\":\"stop\"}]}";

    private static async Task GeminiCompatibility()
    {
        string path = null, body = null;
        using var server = new Server(async c => { path = c.Request.Url.AbsolutePath;
            body = await new StreamReader(c.Request.InputStream).ReadToEndAsync(); await Reply(c, CompatibleReply); });
        var settings = Settings(server.Root); settings.Endpoints[ProviderIds.Gemini] = server.Root + "/v1beta/openai";
        var provider = new GeminiProvider(settings);
        Check(provider.Capabilities.SupportsFunctionCalling, "compatible Gemini must retain tools");
        using var client = provider.CreateChatClient("gemini-test");
        var tool = AIFunctionFactory.Create(() => "pong", "ping");
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "ping") }, new ChatOptions { Tools = new List<AITool> { tool } });
        Equal(path, "/v1beta/openai/chat/completions", "compatible Gemini route");
        Equal(response.Text, "compatible", "compatible response");
        using var json = JsonDocument.Parse(body);
        Equal(json.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString(), "ping", "tool wire payload");
    }

    private static async Task GeminiProtocolSwitch()
    {
        using var server = new Server(c => Reply(c, c.Request.Url.AbsolutePath.EndsWith("/chat/completions") ? CompatibleReply : NativeReply));
        var settings = Settings(server.Root); settings.Endpoints[ProviderIds.Gemini] = server.Root + "/v1beta/openai";
        var provider = new GeminiProvider(settings);
        using (var compatible = provider.CreateChatClient("gemini-test"))
            Equal((await compatible.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") })).Text, "compatible", "compatible mode");
        settings.Endpoints[ProviderIds.Gemini] = server.Root + "/v1beta";
        Check(!provider.Capabilities.SupportsFunctionCalling, "native mode must advertise its text-only scope");
        using (var native = provider.CreateChatClient("gemini-test"))
            Equal((await native.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") })).Text, "{\"ok\":true}", "native mode after switching endpoint");
    }

    private static async Task CancelledErrorBody()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new ErrorBodyHandler(new StalledErrorContent(entered, release.Task)));
        using var client = new GeminiNativeChatClient("test-key", "gemini-test", "https://example.invalid/v1beta", 30, http);
        using var cts = new CancellationTokenSource();
        await using var iterator = client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "hello") }, cancellationToken: cts.Token).GetAsyncEnumerator();
        Task read = iterator.MoveNextAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cts.Cancel();
            try { await read.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("cancelled error body read succeeded"); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            release.TrySetResult(true);
            try { await read.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private sealed class ErrorBodyHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content });
    }

    private sealed class StalledErrorContent(TaskCompletionSource<bool> entered, Task release) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken token)
        {
            entered.TrySetResult(true);
            await release.WaitAsync(token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{}"), token);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static async Task<RimLLMException> ExpectError(Func<Task<ChatResponse>> action, LLMError expected)
    { try { await action(); } catch (RimLLMException ex) { Equal(ex.Error, expected, "error mapping"); return ex; } throw new Exception("expected an error"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T actual, T expected, string message) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception(message + $": expected {expected}; got {actual}"); }
    private static async Task Reply(HttpListenerContext c, string body, int status = 200)
    { c.Response.StatusCode = status; c.Response.ContentType = "application/json"; var bytes = Encoding.UTF8.GetBytes(body); c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes); }
    private static async Task WriteEvent(HttpListenerContext c, string json)
    { await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + json + "\n\n")); await c.Response.OutputStream.FlushAsync(); }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener listener;
        public string Root { get; }
        public Server(Func<HttpListenerContext, Task> handle)
        {
            // HTTP.sys reserves some loopback ports independently of TCP's ephemeral allocator.
            for (int attempt = 0; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                Root = "http://127.0.0.1:" + port; listener = new HttpListener(); listener.Prefixes.Add(Root + "/");
                try { listener.Start(); break; }
                catch (HttpListenerException) { listener.Close(); if (attempt == 9) throw; }
            }
            _ = Task.Run(async () => { while (listener.IsListening) { HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { break; }
                try { await handle(c); } catch (Exception ex) { Console.WriteLine("SERVER " + ex.Message); }
                finally { c.Response.Close(); }
            } });
        }
        public void Dispose() { listener.Close(); }
    }
}
