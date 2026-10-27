using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Providers;

/// <summary>Small text-only Gemini REST adapter; no extra SDK or local proxy is required.</summary>
internal sealed class GeminiNativeChatClient : IChatClient
{
    internal const string DefaultEndpoint = "https://generativelanguage.googleapis.com/v1beta";
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;
    private readonly string _key, _model, _root;
    private readonly TimeSpan _timeout;

    internal GeminiNativeChatClient(string apiKey, string model, string endpoint, float timeoutSeconds, HttpClient http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Gemini API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Gemini model is required.", nameof(model));
        _http = http ?? Http;
        _key = apiKey;
        _model = model.StartsWith("models/", StringComparison.Ordinal) ? model.Substring(7) : model;
        _root = NormalizeEndpoint(endpoint);
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 30);
    }

    internal static string NormalizeEndpoint(string endpoint)
    {
        string root = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : OpenAIProvider.NormalizeEndpoint(endpoint);
        if (root.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)) root = root.Substring(0, root.Length - 7);
        var uri = new Uri(root, UriKind.Absolute);
        if ((uri.Scheme != "https" && uri.Scheme != "http") || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Gemini endpoint must be an HTTP(S) base URL without a query or fragment.");
        return uri.AbsolutePath == "/" ? root.TrimEnd('/') + "/v1beta" : root.TrimEnd('/');
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions options = null, CancellationToken cancellationToken = default)
    {
        using (var request = BuildRequest(messages, options, false))
        using (var response = await SendAsync(request, false, cancellationToken).ConfigureAwait(false))
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
        {
            Frame frame = ParseFrame(document.RootElement);
            if (string.IsNullOrEmpty(frame.Text)) throw new RimLLMException(LLMError.InvalidResponse,
                "Gemini returned no text. Check the finish reason and increase the output token limit if needed.");
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, frame.Text))
            {
                ResponseId = frame.Id, ModelId = frame.Model ?? _model,
                FinishReason = frame.Finish, Usage = frame.Usage
            };
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(messages, options, true);
        using var response = await SendAsync(request, true, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var last = new Frame();
        bool finished = false, hasText = false;
        while (true)
        {
            string line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line != null && line.Length != 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.Substring(5).TrimStart());
                }
                continue;
            }
            if (data.Length > 0)
            {
                Frame frame;
                using (var doc = JsonDocument.Parse(data.ToString())) frame = ParseFrame(doc.RootElement);
                data.Clear();
                last.Usage = frame.Usage ?? last.Usage;
                last.Id = frame.Id ?? last.Id;
                last.Model = frame.Model ?? last.Model;
                if (frame.Finish.HasValue) { finished = true; last.Finish = frame.Finish; }
                hasText |= !string.IsNullOrEmpty(frame.Text);
                yield return new ChatResponseUpdate(ChatRole.Assistant, frame.Text ?? string.Empty)
                { ResponseId = last.Id, ModelId = last.Model ?? _model };
            }
            if (line == null) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!finished || !hasText) throw new RimLLMException(LLMError.InvalidResponse,
            "Gemini stream ended without a completed text response.");
        var final = new ChatResponseUpdate { ResponseId = last.Id, ModelId = last.Model ?? _model, FinishReason = last.Finish };
        if (last.Usage != null) final.Contents.Add(new UsageContent(last.Usage));
        yield return final;
    }

    private HttpRequestMessage BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions options, bool stream)
    {
        if (options?.Tools?.Count > 0) throw new NotSupportedException("The minimal Gemini native adapter supports text and JSON, not function calling.");
        var contents = new List<object>();
        var system = new List<object>();
        foreach (ChatMessage message in messages)
        {
            var parts = new List<object>();
            foreach (AIContent content in message.Contents)
            {
                if (content is TextContent text) parts.Add(new Dictionary<string, object> { ["text"] = text.Text ?? string.Empty });
                else if (!(content is TextReasoningContent)) throw new NotSupportedException("The Gemini native adapter currently supports text messages only.");
            }
            if (message.Role == ChatRole.System || message.Role.Value == "developer") system.AddRange(parts);
            else
            {
                if (message.Role != ChatRole.User && message.Role != ChatRole.Assistant)
                    throw new NotSupportedException("Unsupported Gemini message role: " + message.Role.Value);
                if (parts.Count > 0) contents.Add(new Dictionary<string, object>
                { ["role"] = message.Role == ChatRole.Assistant ? "model" : "user", ["parts"] = parts });
            }
        }
        if (contents.Count == 0) throw new ArgumentException("Gemini requires at least one text message.");
        var config = new Dictionary<string, object> { ["maxOutputTokens"] = options?.MaxOutputTokens ?? 1024 };
        if (options?.Temperature != null) config["temperature"] = options.Temperature.Value;
        if (options?.TopP != null) config["topP"] = options.TopP.Value;
        if (options?.TopK != null) config["topK"] = options.TopK.Value;
        if (options?.StopSequences?.Count > 0) config["stopSequences"] = options.StopSequences;
        string schema = RimLLMChatOptions.ReadAdditional<string>(options, "rimllm_response_schema", null);
        if (string.IsNullOrEmpty(schema) && options?.ResponseFormat is ChatResponseFormatJson format && format.Schema.HasValue)
            schema = format.Schema.Value.GetRawText();
        if (!string.IsNullOrEmpty(schema))
        {
            using (var doc = JsonDocument.Parse(schema)) config["responseJsonSchema"] = doc.RootElement.Clone();
        }
        if (!string.IsNullOrEmpty(schema) || options?.ResponseFormat is ChatResponseFormatJson) config["responseMimeType"] = "application/json";
        bool levelThinking = _model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase);
        bool budgetThinking = _model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase);
        if (RimLLMChatOptions.GetDisableReasoning(options) && (levelThinking || budgetThinking))
        {
            config["thinkingConfig"] = levelThinking
                // Gemini 3 cannot fully disable thinking; LOW also works on relays that reject MINIMAL.
                ? new Dictionary<string, object> { ["thinkingLevel"] = "low" }
                : new Dictionary<string, object> { ["thinkingBudget"] = _model.StartsWith("gemini-2.5-pro", StringComparison.OrdinalIgnoreCase) ? 128 : 0 };
        }
        else if (options?.Reasoning?.Effort != null && (levelThinking || budgetThinking))
        {
            var effort = options.Reasoning.Effort;
            string level = effort == ReasoningEffort.Low ? "low" : effort == ReasoningEffort.Medium ? "medium" : "high";
            config["thinkingConfig"] = levelThinking
                ? new Dictionary<string, object> { ["thinkingLevel"] = level }
                : new Dictionary<string, object> { ["thinkingBudget"] = level == "low" ? 1024 : level == "medium" ? 2048 : 4096 };
        }
        var body = new Dictionary<string, object> { ["contents"] = contents, ["generationConfig"] = config };
        if (system.Count > 0) body["systemInstruction"] = new Dictionary<string, object> { ["parts"] = system };
        var request = new HttpRequestMessage(HttpMethod.Post, _root + "/models/" + Uri.EscapeDataString(_model)
            + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent"));
        request.Headers.TryAddWithoutValidation("x-goog-api-key", _key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool stream, CancellationToken token)
    {
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cts.CancelAfter(_timeout);
            HttpResponseMessage response = await _http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                string message = "Gemini HTTP " + (int)response.StatusCode;
                try { using (var doc = JsonDocument.Parse(body))
                    if (doc.RootElement.TryGetProperty("error", out JsonElement error)) message += ": " + ReadString(error, "message"); }
                catch (JsonException) { }
                throw LLMErrorMapper.CreateException((int)response.StatusCode, message.Replace(_key, "[REDACTED]"),
                    LLMErrorMapper.ParseRetryAfter(response.Headers.RetryAfter?.ToString()));
            }
        }
    }

    private static Frame ParseFrame(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement error))
            throw new RimLLMException(LLMError.InvalidResponse, ReadString(error, "message") ?? "Gemini stream error.");
        if (root.TryGetProperty("promptFeedback", out JsonElement feedback) && feedback.TryGetProperty("blockReason", out _))
            throw new RimLLMException(LLMError.ContentFilter, "Gemini blocked the prompt: " + ReadString(feedback, "blockReason"));
        var frame = new Frame { Id = ReadString(root, "responseId"), Model = ReadString(root, "modelVersion") };
        if (root.TryGetProperty("candidates", out JsonElement candidates) && candidates.GetArrayLength() > 0)
        {
            JsonElement candidate = candidates[0];
            string reason = ReadString(candidate, "finishReason");
            if (reason == "SAFETY" || reason == "RECITATION" || reason == "BLOCKLIST" || reason == "PROHIBITED_CONTENT" || reason == "SPII")
                throw new RimLLMException(LLMError.ContentFilter, "Gemini blocked the response: " + reason);
            if (!string.IsNullOrEmpty(reason)) frame.Finish = reason == "STOP" ? ChatFinishReason.Stop
                : reason == "MAX_TOKENS" ? ChatFinishReason.Length : new ChatFinishReason(reason.ToLowerInvariant());
            if (candidate.TryGetProperty("content", out JsonElement content) && content.TryGetProperty("parts", out JsonElement parts))
            {
                var text = new StringBuilder();
                foreach (JsonElement part in parts.EnumerateArray())
                    if (!(part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True))
                        text.Append(ReadString(part, "text"));
                frame.Text = text.ToString();
            }
        }
        if (root.TryGetProperty("usageMetadata", out JsonElement usage))
        {
            long? output = ReadCount(usage, "candidatesTokenCount"), thoughts = ReadCount(usage, "thoughtsTokenCount");
            frame.Usage = new UsageDetails
            {
                InputTokenCount = ReadCount(usage, "promptTokenCount"),
                OutputTokenCount = output.HasValue || thoughts.HasValue ? (output ?? 0) + (thoughts ?? 0) : (long?)null,
                TotalTokenCount = ReadCount(usage, "totalTokenCount"),
                CachedInputTokenCount = ReadCount(usage, "cachedContentTokenCount")
            };
        }
        return frame;
    }

    private static string ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement part) && part.ValueKind == JsonValueKind.String ? part.GetString() : null;
    private static long? ReadCount(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement part) && part.TryGetInt64(out long count) ? count : (long?)null;
    public object GetService(Type serviceType, object serviceKey = null) =>
        serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    private sealed class Frame
    {
        public string Text, Id, Model;
        public ChatFinishReason? Finish;
        public UsageDetails Usage;
    }
}
