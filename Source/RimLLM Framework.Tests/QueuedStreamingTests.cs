using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

[TestFixture]
public sealed class QueuedStreamingTests
{
    [Test]
    public async Task StreamIsLazyAndKeepsItsSlotUntilEarlyDisposal()
    {
        var queue = new RimLLMRequestQueue(new MockSettings { MaxConcurrentRequests = 1 });
        var inner = new ProbeStreamClient();
        using var client = new RimLLMRequestQueueChatClient(inner, queue);
        await using var enumerator = client.GetStreamingResponseAsync([]).GetAsyncEnumerator();

        // 建立串流與列舉器都不應送出請求，也不應佔住並行名額。
        using (await queue.AcquireSlotAsync(0, default).WaitAsync(TimeSpan.FromSeconds(3)))
        {
            ClassicAssert.AreEqual(0, inner.Starts);
        }

        ClassicAssert.IsTrue(await enumerator.MoveNextAsync());
        ClassicAssert.AreEqual(1, inner.Starts);
        var waiting = queue.AcquireSlotAsync(0, default);
        ClassicAssert.IsFalse(waiting.IsCompleted, "串流停在 yield 時仍必須持有名額。");

        await enumerator.DisposeAsync();
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        ClassicAssert.IsTrue(inner.EnumeratorDisposed, "提早停止也必須釋放供應商列舉器。");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EitherCancellationTokenCanCancelAQueuedStream(bool cancelMethodToken)
    {
        var queue = new RimLLMRequestQueue(new MockSettings { MaxConcurrentRequests = 1 });
        using var occupied = await queue.AcquireSlotAsync(0, default);
        using var methodCancellation = new CancellationTokenSource();
        using var consumerCancellation = new CancellationTokenSource();
        var inner = new ProbeStreamClient();
        using var client = new RimLLMRequestQueueChatClient(inner, queue);
        await using var enumerator = client.GetStreamingResponseAsync([], cancellationToken: methodCancellation.Token)
            .GetAsyncEnumerator(consumerCancellation.Token);

        var pending = enumerator.MoveNextAsync().AsTask();
        ClassicAssert.IsFalse(pending.IsCompleted);
        (cancelMethodToken ? methodCancellation : consumerCancellation).Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        ClassicAssert.AreEqual(0, inner.Starts, "排隊取消不能啟動供應商。");

        occupied.Dispose();
        using var next = await queue.AcquireSlotAsync(0, default).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ConsumerCancellationDisposesTheActiveProviderStream()
    {
        var queue = new RimLLMRequestQueue(new MockSettings { MaxConcurrentRequests = 1 });
        using var cancellation = new CancellationTokenSource();
        var inner = new ProbeStreamClient();
        using var client = new RimLLMRequestQueueChatClient(inner, queue);
        await using var enumerator = client.GetStreamingResponseAsync([])
            .GetAsyncEnumerator(cancellation.Token);

        ClassicAssert.IsTrue(await enumerator.MoveNextAsync());
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        await enumerator.DisposeAsync();

        ClassicAssert.IsTrue(inner.EnumeratorDisposed);
        using var next = await queue.AcquireSlotAsync(0, default).WaitAsync(TimeSpan.FromSeconds(3));
    }

    private sealed class ProbeStreamClient : IChatClient
    {
        public int Starts { get; private set; }
        public bool EnumeratorDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions options = null, CancellationToken cancellationToken = default)
            => Stream(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Starts++;
            try
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                EnumeratorDisposed = true;
            }
        }

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }
    }
}
