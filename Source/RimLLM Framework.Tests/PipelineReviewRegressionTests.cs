using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

[TestFixture]
public sealed class PipelineReviewRegressionTests
{
    [Test]
    public async Task StreamAsync_RecordsReportedUsageWhenCancelledAfterOutput()
    {
        var client = new PartialStreamClient();
        using var cancellation = new CancellationTokenSource();
        var updateReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new List<UsageTokens>();

        Task stream = RimLLMChatClientExecutor.StreamAsync(
            client,
            [new ChatMessage(ChatRole.User, "billable prompt")],
            null,
            "mock-model",
            false,
            "Mock",
            _ =>
            {
                updateReceived.TrySetResult();
                return Task.CompletedTask;
            },
            10f,
            cancellation.Token,
            onUsage: value => usage.Add(value));

        await updateReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await stream);
        ClassicAssert.AreEqual(1, usage.Count);
        ClassicAssert.AreEqual(101, usage[0].Prompt);
        ClassicAssert.AreEqual(3, usage[0].Completion);
    }

    [Test]
    public async Task StreamAsync_RecordsReportedUsageWhenNetworkFailsAfterOutput()
    {
        var usage = new List<UsageTokens>();
        Task stream = RimLLMChatClientExecutor.StreamAsync(
            new FailingAfterPartialUpdateClient(),
            [new ChatMessage(ChatRole.User, "billable prompt")],
            null,
            "mock-model",
            false,
            "Mock",
            null,
            10f,
            CancellationToken.None,
            onUsage: value => usage.Add(value));

        RimLLMException exception = Assert.ThrowsAsync<RimLLMException>(async () =>
            await stream.WaitAsync(TimeSpan.FromSeconds(2)));

        ClassicAssert.AreEqual(LLMError.NetworkError, exception.Error);
        AssertReportedUsage(usage);
    }

    [Test]
    public async Task StreamAsync_RecordsReportedUsageWhenIdleTimeoutFollowsOutput()
    {
        var usage = new List<UsageTokens>();
        Task stream = RimLLMChatClientExecutor.StreamAsync(
            new PartialStreamClient(),
            [new ChatMessage(ChatRole.User, "billable prompt")],
            null,
            "mock-model",
            false,
            "Mock",
            null,
            0.1f,
            CancellationToken.None,
            onUsage: value => usage.Add(value));

        RimLLMException exception = Assert.ThrowsAsync<RimLLMException>(async () =>
            await stream.WaitAsync(TimeSpan.FromSeconds(2)));

        ClassicAssert.AreEqual(LLMError.Timeout, exception.Error);
        AssertReportedUsage(usage);
    }

    [Test]
    public async Task StreamAsync_CancellationBeforeAnyOutputDoesNotRecordUsage()
    {
        var client = new EmptyUntilCancelledStreamClient();
        using var cancellation = new CancellationTokenSource();
        var usage = new List<UsageTokens>();

        Task stream = RimLLMChatClientExecutor.StreamAsync(
            client,
            [new ChatMessage(ChatRole.User, "not yet billable")],
            null,
            "mock-model",
            false,
            "Mock",
            null,
            10f,
            cancellation.Token,
            onUsage: value => usage.Add(value));

        await client.StreamStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await stream.WaitAsync(TimeSpan.FromSeconds(2)));
        ClassicAssert.IsEmpty(usage);
    }

    [Test]
    public void RetryDelay_UsesServerRetryAfterBeyondLocalBackoffCap()
    {
        RimLLMException retryAfter = LLMErrorMapper.CreateException(
            429,
            "limited",
            TimeSpan.FromSeconds(120));

        float delay = RimLLMFallbackPipeline.ResolveRetryDelay(0f, 0, retryAfter);

        ClassicAssert.AreEqual(120f, delay);
        ClassicAssert.AreEqual(60f, RimLLMFallbackPipeline.ResolveRetryDelay(100f, 0, null));
    }

    [Test]
    public async Task LongRetryAfter_CanBeCancelledWithoutExceedingPlatformDelayLimit()
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 1, RetryDelay = 0f };
        var provider = new RoutingProvider("P1")
        {
            Failure = LLMErrorMapper.CreateException(429, "limited", TimeSpan.FromDays(60))
        };
        var manager = BuildManager(settings, provider);
        using var cancellation = new CancellationTokenSource();

        Task<ChatResponse> request = manager.CreateChatClient("test.long-retry-after")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "retry later")], cancellationToken: cancellation.Token);
        await provider.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        ClassicAssert.IsFalse(request.IsCompleted, "The provider-requested wait should remain active until cancelled.");
        cancellation.Cancel();

        Task completed = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(5)));
        ClassicAssert.AreSame(request, completed, "Cancelling a long Retry-After should release the wait promptly.");
        Assert.CatchAsync<OperationCanceledException>(async () => await request);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Guard_MaterializesSingleUseMessagesOnce(bool streaming)
    {
        var settings = new MockSettings { EnableAntiAbuse = true };
        var inner = new CapturingChatClient();
        var guard = new RimLLMGuardChatClient(
            inner,
            settings,
            new RimLLMThrottleStore(settings),
            "test.single-use",
            new RimLLMUsageTracker(settings));
        var messages = new SingleUseMessages(new ChatMessage(ChatRole.User, "keep this prompt"));

        if (streaming)
        {
            await foreach (ChatResponseUpdate _ in guard.GetStreamingResponseAsync(messages))
            {
            }
        }
        else
        {
            await guard.GetResponseAsync(messages);
        }

        ClassicAssert.AreEqual(1, messages.EnumerationCount);
        ClassicAssert.AreEqual("keep this prompt", inner.ReceivedMessages.Single().Text);
    }

    [Test]
    public void RoundRobin_RotatesFallbackCandidatesAndKeepsPreferredFirst()
    {
        var settings = new MockSettings { RoutingStrategy = 2 };
        var pipeline = CreatePipeline(settings, out _);

        string[][] actual = Enumerable.Range(0, 4)
            .Select(_ => pipeline.ResolveCandidates(null, null).Select(c => c.ProviderId).ToArray())
            .ToArray();

        CollectionAssert.AreEqual(new[] { "P1", "P2", "P3" }, actual[0]);
        CollectionAssert.AreEqual(new[] { "P2", "P3", "P1" }, actual[1]);
        CollectionAssert.AreEqual(new[] { "P3", "P1", "P2" }, actual[2]);
        CollectionAssert.AreEqual(actual[0], actual[3]);

        string[][] preferred = Enumerable.Range(0, 2)
            .Select(_ => pipeline.ResolveCandidates("P3:m3", null).Select(c => c.ProviderId).ToArray())
            .ToArray();

        ClassicAssert.AreEqual("P3", preferred[0][0]);
        ClassicAssert.AreEqual("P3", preferred[1][0]);
        CollectionAssert.AreEqual(new[] { "P3", "P1", "P2" }, preferred[0]);
        CollectionAssert.AreEqual(new[] { "P3", "P2", "P1" }, preferred[1]);
    }

    [Test]
    public async Task EffectiveCapabilityQueriesDoNotAdvanceRoundRobin()
    {
        var settings = new MockSettings { RoutingStrategy = 2 };
        var p1 = new RoutingProvider("P1");
        var p2 = new RoutingProvider("P2");
        var p3 = new RoutingProvider("P3");
        var manager = BuildManager(settings, p1, p2, p3);

        for (int i = 0; i < 4; i++)
        {
            ClassicAssert.IsTrue(manager.TryGetEffectiveCapabilities(null, out _, out _));
        }

        await manager.CreateChatClient("test.round-robin.capabilities")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "route this")]);

        ClassicAssert.AreEqual(1, p1.RequestCount);
        ClassicAssert.AreEqual(0, p2.RequestCount);
        ClassicAssert.AreEqual(0, p3.RequestCount);
    }

    [Test]
    public async Task CancellingQueuedToolInvocation_CompletesWaitAndSkipsSideEffectOnDrain()
    {
        FieldInfo pumpField = typeof(RimLLMDispatcher).GetField("_hasPump", BindingFlags.NonPublic | BindingFlags.Static);
        bool previousPump = (bool)pumpField.GetValue(null);
        RimLLMDispatcher.ResetQueueForTests();
        pumpField.SetValue(null, true);

        try
        {
            int sideEffects = 0;
            IChatClient inner = new ToolCallingClient();
            IChatClient client = inner.AsMainThreadFunctionInvokingClient();
            var tool = AIFunctionFactory.Create(() => Interlocked.Increment(ref sideEffects), "Increment");
            using var cancellation = new CancellationTokenSource();

            Task<ChatResponse> request = client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "call the tool")],
                new ChatOptions { Tools = [tool] },
                cancellation.Token);

            await WaitForQueuedAction();
            cancellation.Cancel();

            Task completed = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(2)));
            ClassicAssert.AreSame(request, completed, "Cancellation should release the queued waiter without waiting for a Unity frame.");
            Assert.CatchAsync<OperationCanceledException>(async () => await request);

            RimLLMDispatcher.DrainWithBudget(int.MaxValue, long.MaxValue);
            ClassicAssert.AreEqual(0, sideEffects, "A tool cancelled before dispatch must not run when the queue drains.");
        }
        finally
        {
            RimLLMDispatcher.ResetQueueForTests();
            pumpField.SetValue(null, previousPump);
        }
    }

    private static RimLLMFallbackPipeline CreatePipeline(MockSettings settings, out Dictionary<string, RoutingProvider> providers)
    {
        var providerMap = new Dictionary<string, RoutingProvider>(StringComparer.OrdinalIgnoreCase);
        providers = providerMap;
        foreach (string id in new[] { "P1", "P2", "P3" })
        {
            providerMap.Add(id, new RoutingProvider(id));
        }

        settings.FallbackChain = ["P1:m1", "P2:m2", "P3:m3"];
        SetProvidersEnabled(settings, providerMap.Values);
        return new RimLLMFallbackPipeline(
            settings,
            new RimLLMHealthLedger(),
            new RimLLMUsageTracker(settings),
            id => providerMap.TryGetValue(id, out RoutingProvider provider) ? provider : null,
            id => settings.IsProviderEnabled(id));
    }

    private static RimLLMManager BuildManager(MockSettings settings, params RoutingProvider[] providers)
    {
        settings.FallbackChain = providers.Select(p => p.ProviderId + ":model").ToList();
        SetProvidersEnabled(settings, providers);
        var manager = new RimLLMManager(settings);
        foreach (RoutingProvider provider in providers)
        {
            manager.RegisterProvider(provider);
        }

        return manager;
    }

    private static void SetProvidersEnabled(MockSettings settings, IEnumerable<RoutingProvider> providers)
    {
        foreach (RoutingProvider provider in providers)
        {
            settings.EnabledProviders[provider.ProviderId] = true;
            settings.ApiKeys[provider.ProviderId] = "test-key";
        }
    }

    private static async Task WaitForQueuedAction()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (RimLLMDispatcher.QueuedCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }

        ClassicAssert.Greater(RimLLMDispatcher.QueuedCount, 0, "The tool invocation should be waiting in the main-thread queue.");
    }

    private static void AssertReportedUsage(IReadOnlyList<UsageTokens> usage)
    {
        ClassicAssert.AreEqual(1, usage.Count);
        ClassicAssert.AreEqual(101, usage[0].Prompt);
        ClassicAssert.AreEqual(3, usage[0].Completion);
    }

    private sealed class PartialStreamClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => Stream(cancellationToken);

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }

        private static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new TextContent("partial answer"),
                new UsageContent(new UsageDetails { InputTokenCount = 101, OutputTokenCount = 3 })
            ]);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingAfterPartialUpdateClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => Stream();

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }

        private static async IAsyncEnumerable<ChatResponseUpdate> Stream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
            [
                new TextContent("partial answer"),
                new UsageContent(new UsageDetails { InputTokenCount = 101, OutputTokenCount = 3 })
            ]);
            await Task.Yield();
            throw new HttpRequestException("stream disconnected");
        }
    }

    private sealed class EmptyUntilCancelledStreamClient : IChatClient
    {
        private readonly TaskCompletionSource _streamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StreamStarted => _streamStarted.Task;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => Stream(cancellationToken);

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _streamStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class SingleUseMessages : IEnumerable<ChatMessage>
    {
        private readonly ChatMessage _message;

        public int EnumerationCount { get; private set; }

        public SingleUseMessages(ChatMessage message) => _message = message;

        public IEnumerator<ChatMessage> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException("This message sequence can only be enumerated once.");
            }

            yield return _message;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage> ReceivedMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages = messages.ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessages = messages.ToArray();
            return OneUpdate(cancellationToken);
        }

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }

        private static async IAsyncEnumerable<ChatResponseUpdate> OneUpdate(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]);
            await Task.CompletedTask;
        }
    }

    private sealed class RoutingProvider : ILLMProvider
    {
        public string ProviderId { get; }
        public bool RequiresApiKey => true;
        public LLMProviderCapabilities Capabilities { get; } = new()
        {
            SupportsStreaming = true,
            SupportsFunctionCalling = true,
            SupportsUsageMetadata = true
        };
        public Exception Failure { get; set; }
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RequestStarted => _requestStarted.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);
        private int _requestCount;

        public RoutingProvider(string providerId) => ProviderId = providerId;

        public IChatClient CreateChatClient(string model) => new CapturingRoutingClient(this);
        public Task<TestResult> TestConnectionAsync() => Task.FromResult(new TestResult { Success = true });
        public Task<List<string>> FetchAvailableModelsAsync() => Task.FromResult(new List<string> { "model" });

        private sealed class CapturingRoutingClient : IChatClient
        {
            private readonly RoutingProvider _provider;
            public CapturingRoutingClient(RoutingProvider provider) => _provider = provider;

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _provider._requestCount);
                _provider._requestStarted.TrySetResult();
                if (_provider.Failure != null)
                {
                    return Task.FromException<ChatResponse>(_provider.Failure);
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _provider.ProviderId)));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }
    }

    private sealed class ToolCallingClient : IChatClient
    {
        private int _requestCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                var call = new FunctionCallContent("call-1", "Increment", new Dictionary<string, object>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }
    }
}
