using System;
using System.ClientModel.Primitives;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class EmbeddingReviewRegressionTests
{
    private const string EmbeddingResponse =
        "{\"object\":\"list\",\"model\":\"text-embedding-3-small\",\"data\":[" +
        "{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}]," +
        "\"usage\":{\"prompt_tokens\":10,\"total_tokens\":10}}";

    [Test]
    public async Task EmbeddingFacadeHardBlockRejectsBeforeSendingRequest()
    {
        var settings = CreateEmbeddingSettings();
        settings.DailyTokenBudgetLimit = 1;
        settings.DailyAccumulatedTokens = 1;
        settings.DailyBudgetResetDate = Today;
        var manager = new RimLLMManager(settings);
        var handler = SuccessfulEmbeddingHandler();

        await WithEmbeddingTransportAsync(handler, async () =>
        {
            var generator = manager.CreateEmbeddingGenerator("test.embed.budget.hardblock");
            RimLLMException exception = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
            });

            ClassicAssert.AreEqual(LLMError.QuotaExceeded, exception.Error);
            ClassicAssert.AreEqual(0, handler.RequestCount, "超出 HardBlock 預算時不應送出 HTTP 請求。");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task EmbeddingFacadeWarnOnlyAllowsRequestAndDailyResetUnblocks()
    {
        var warnSettings = CreateEmbeddingSettings();
        warnSettings.DailyTokenBudgetLimit = 1;
        warnSettings.DailyAccumulatedTokens = 1;
        warnSettings.DailyBudgetResetDate = Today;
        warnSettings.BudgetPolicy = 1;
        var warnManager = new RimLLMManager(warnSettings);
        var warnHandler = SuccessfulEmbeddingHandler();

        await WithEmbeddingTransportAsync(warnHandler, async () =>
        {
            var generator = warnManager.CreateEmbeddingGenerator("test.embed.budget.warnonly");
            GeneratedEmbeddings<Embedding<float>> generated =
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);

            ClassicAssert.AreEqual(1, generated.Count);
            ClassicAssert.AreEqual(1, warnHandler.RequestCount);
        }).ConfigureAwait(false);

        var resetSettings = CreateEmbeddingSettings();
        resetSettings.DailyTokenBudgetLimit = 1;
        resetSettings.DailyAccumulatedTokens = 100;
        resetSettings.DailyBudgetResetDate = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var resetManager = new RimLLMManager(resetSettings);
        var resetHandler = SuccessfulEmbeddingHandler();

        await WithEmbeddingTransportAsync(resetHandler, async () =>
        {
            var generator = resetManager.CreateEmbeddingGenerator("test.embed.budget.reset");
            GeneratedEmbeddings<Embedding<float>> generated =
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);

            ClassicAssert.AreEqual(1, generated.Count);
            ClassicAssert.AreEqual(1, resetHandler.RequestCount);
            ClassicAssert.AreEqual(Today, resetSettings.DailyBudgetResetDate);
            ClassicAssert.AreEqual(10, resetSettings.DailyAccumulatedTokens);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task EmbeddingComputePreservesCallerCancellation()
    {
        var settings = CreateEmbeddingSettings();
        var service = new RimLLMEmbeddingService(settings);
        var handler = SuccessfulEmbeddingHandler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await WithEmbeddingTransportAsync(handler, async () =>
        {
            OperationCanceledException exception = Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await service.ComputeEmbeddingsAsync(new[] { "hello" }, cancellation.Token).ConfigureAwait(false);
            });

            ClassicAssert.IsNotNull(exception);
            ClassicAssert.AreEqual(0, handler.RequestCount);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task EmbeddingModelListAndTestPreserveCallerCancellation()
    {
        var settings = CreateEmbeddingSettings();
        var service = new RimLLMEmbeddingService(settings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException modelListException = Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await service.FetchAvailableModelsAsync(
                "OpenAI", "http://127.0.0.1:1/v1", "test-key", cancellation.Token).ConfigureAwait(false);
        });
        ClassicAssert.IsNotNull(modelListException);

        var handler = SuccessfulEmbeddingHandler();
        await WithEmbeddingTransportAsync(handler, async () =>
        {
            OperationCanceledException testException = Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await service.TestEmbeddingAsync(
                    "OpenAI", "text-embedding-3-small", "http://127.0.0.1:1/v1", "test-key",
                    cancellationToken: cancellation.Token).ConfigureAwait(false);
            });

            ClassicAssert.IsNotNull(testException);
            ClassicAssert.AreEqual(0, handler.RequestCount);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task EmbeddingEndpointsPreserveInFlightCallerCancellation()
    {
        var settings = CreateEmbeddingSettings();
        settings.ApiTimeout = 10f;
        var service = new RimLLMEmbeddingService(settings);
        var computeHandler = DelayedEmbeddingHandler();
        using var computeCancellation = new CancellationTokenSource();
        computeCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await WithEmbeddingTransportAsync(computeHandler, async () =>
        {
            OperationCanceledException computeException = Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await service.ComputeEmbeddingsAsync(new[] { "hello" }, computeCancellation.Token).ConfigureAwait(false);
            });
            ClassicAssert.IsNotNull(computeException);
            ClassicAssert.AreEqual(1, computeHandler.RequestCount);
        }).ConfigureAwait(false);

        using var modelListServer = new SlowLocalHttpServer(
            TimeSpan.FromMilliseconds(350),
            "{\"object\":\"list\",\"data\":[{\"id\":\"text-embedding-3-small\"}]}");
        using var modelListCancellation = new CancellationTokenSource();
        modelListCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        OperationCanceledException modelListException = Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await service.FetchAvailableModelsAsync(
                "OpenAI", modelListServer.Endpoint.ToString(), "test-key", modelListCancellation.Token).ConfigureAwait(false);
        });
        await modelListServer.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        ClassicAssert.IsNotNull(modelListException);
        ClassicAssert.AreEqual(1, modelListServer.AcceptedConnections);

        var testHandler = DelayedEmbeddingHandler();
        using var testCancellation = new CancellationTokenSource();
        testCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        await WithEmbeddingTransportAsync(testHandler, async () =>
        {
            OperationCanceledException testException = Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await service.TestEmbeddingAsync(
                    "OpenAI", "text-embedding-3-small", "http://127.0.0.1:1/v1", "test-key",
                    cancellationToken: testCancellation.Token).ConfigureAwait(false);
            });
            ClassicAssert.IsNotNull(testException);
            ClassicAssert.AreEqual(1, testHandler.RequestCount);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task EmbeddingFrameworkTimeoutsMapToTimeout()
    {
        var settings = CreateEmbeddingSettings();
        settings.ApiTimeout = 0.1f;
        var service = new RimLLMEmbeddingService(settings);
        var handler = DelayedEmbeddingHandler();

        await WithEmbeddingTransportAsync(handler, async () =>
        {
            RimLLMException computeException = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await service.ComputeEmbeddingsAsync(new[] { "hello" }).ConfigureAwait(false);
            });
            ClassicAssert.AreEqual(LLMError.Timeout, computeException.Error);

            RimLLMException testException = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await service.TestEmbeddingAsync(
                    "OpenAI", "text-embedding-3-small", "http://127.0.0.1:1/v1", "test-key").ConfigureAwait(false);
            });
            ClassicAssert.AreEqual(LLMError.Timeout, testException.Error);
            ClassicAssert.AreEqual(2, handler.RequestCount);
        }).ConfigureAwait(false);

        var modelListSettings = CreateEmbeddingSettings();
        modelListSettings.ApiTimeout = 0.1f;
        using var server = new SlowLocalHttpServer(
            TimeSpan.FromMilliseconds(350),
            "{\"object\":\"list\",\"data\":[{\"id\":\"text-embedding-3-small\"}]}");
        var modelListService = new RimLLMEmbeddingService(modelListSettings);

        RimLLMException modelListException = Assert.ThrowsAsync<RimLLMException>(async () =>
        {
            await modelListService.FetchAvailableModelsAsync(
                "OpenAI", server.Endpoint.ToString(), "test-key").ConfigureAwait(false);
        });
        await server.Completion.ConfigureAwait(false);
        ClassicAssert.AreEqual(LLMError.Timeout, modelListException.Error);
        ClassicAssert.AreEqual(1, server.AcceptedConnections);
    }

    [Test]
    public async Task OpenAIConnectionTestUsesConfiguredApiTimeout()
    {
        var settings = new MockSettings { ApiTimeout = 0.1f };
        settings.ApiKeys[ProviderIds.OpenAI] = "test-key";
        using var server = new SlowLocalHttpServer(
            TimeSpan.FromMilliseconds(350),
            "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":0," +
            "\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"message\":{" +
            "\"role\":\"assistant\",\"content\":\"pong\"},\"finish_reason\":\"stop\"}]}");
        settings.Endpoints[ProviderIds.OpenAI] = server.Endpoint.ToString();
        var provider = new OpenAIProvider(settings);

        TestResult result = await provider.TestConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        ClassicAssert.IsFalse(result.Success);
        ClassicAssert.AreEqual(LLMError.Timeout, result.ErrorCode);
        ClassicAssert.AreEqual(1, server.AcceptedConnections, "逾時後不應因 SDK 重試再次送出連線測試請求。");
    }

    [Test]
    public async Task OpenAIInitialModelCatalogUsesConfiguredApiTimeoutWithoutRetries()
    {
        var settings = new MockSettings { ApiTimeout = 0.1f };
        settings.ApiKeys[ProviderIds.OpenAI] = "test-key";
        using var server = new SlowLocalHttpServer(
            TimeSpan.FromMilliseconds(350),
            "{\"object\":\"list\",\"data\":[{\"id\":\"model-test\",\"object\":\"model\",\"created\":0,\"owned_by\":\"test\"}]}");
        settings.Endpoints[ProviderIds.OpenAI] = server.Endpoint.ToString();
        var provider = new OpenAIProvider(settings);

        RimLLMException exception = Assert.ThrowsAsync<RimLLMException>(async () =>
        {
            await provider.FetchAvailableModelsAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        });
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        ClassicAssert.AreEqual(LLMError.Timeout, exception.Error);
        ClassicAssert.AreEqual(1, server.AcceptedConnections, "逾時後不應因 SDK 重試再次抓取模型目錄。");
    }

    private static string Today => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static MockSettings CreateEmbeddingSettings()
    {
        return new MockSettings
        {
            ApiTimeout = 30f,
            EnableAntiAbuse = false,
            EmbeddingProvider = "OpenAI",
            EmbeddingModel = "text-embedding-3-small",
            EmbeddingApiKey = "test-key"
        };
    }

    private static TestHttpMessageHandler SuccessfulEmbeddingHandler()
    {
        return new TestHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EmbeddingResponse, Encoding.UTF8, "application/json")
        }));
    }

    private static TestHttpMessageHandler DelayedEmbeddingHandler()
    {
        return new TestHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmbeddingResponse, Encoding.UTF8, "application/json")
            };
        });
    }

    private static async Task WithEmbeddingTransportAsync(TestHttpMessageHandler handler, Func<Task> action)
    {
        System.ClientModel.Primitives.PipelineTransport previous = RimLLMEmbeddingService.TransportOverride;
        using var httpClient = new HttpClient(handler);
        RimLLMEmbeddingService.TransportOverride = new HttpClientPipelineTransport(httpClient);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            RimLLMEmbeddingService.TransportOverride = previous;
        }
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return _send(request, cancellationToken);
        }
    }

    private sealed class SlowLocalHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TimeSpan _responseDelay;
        private readonly string _responseBody;
        private int _acceptedConnections;

        public Uri Endpoint { get; }
        public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
        public Task Completion { get; }

        public SlowLocalHttpServer(TimeSpan responseDelay, string responseBody)
        {
            _responseDelay = responseDelay;
            _responseBody = responseBody;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/v1");
            Completion = RespondOnceAsync();
        }

        private async Task RespondOnceAsync()
        {
            try
            {
                using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using TcpClient client = await _listener.AcceptTcpClientAsync(acceptTimeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _acceptedConnections);
                using NetworkStream stream = client.GetStream();
                using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (!await ReadCompleteRequestAsync(stream, requestTimeout.Token).ConfigureAwait(false))
                {
                    return;
                }

                await Task.Delay(_responseDelay).ConfigureAwait(false);
                byte[] body = Encoding.UTF8.GetBytes(_responseBody);
                byte[] headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
                await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException || exception is SocketException || exception is ObjectDisposedException || exception is OperationCanceledException)
            {
                // 呼叫端逾時後關閉連線是此測試預期涵蓋的情況。
            }
        }

        private static async Task<bool> ReadCompleteRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var header = new StringBuilder();
            var singleByte = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(singleByte, 0, singleByte.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) return false;
                header.Append((char)singleByte[0]);
            }

            int contentLength = 0;
            bool chunked = false;
            bool expectContinue = false;
            foreach (string line in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;

                string name = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    chunked = value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                {
                    expectContinue = value.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            if (expectContinue)
            {
                byte[] continueResponse = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(continueResponse, 0, continueResponse.Length, cancellationToken).ConfigureAwait(false);
            }

            if (contentLength > 0)
            {
                return await DrainBytesAsync(stream, contentLength, cancellationToken).ConfigureAwait(false);
            }

            return !chunked || await DrainChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> DrainChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                string sizeLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                if (sizeLine == null) return false;

                int extension = sizeLine.IndexOf(';');
                string sizeText = extension >= 0 ? sizeLine.Substring(0, extension) : sizeLine;
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size)) return false;
                if (size == 0)
                {
                    string trailer;
                    do
                    {
                        trailer = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                        if (trailer == null) return false;
                    }
                    while (trailer.Length > 0);
                    return true;
                }

                if (!await DrainBytesAsync(stream, size, cancellationToken).ConfigureAwait(false)) return false;
                if (!await DrainBytesAsync(stream, 2, cancellationToken).ConfigureAwait(false)) return false;
            }
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            var singleByte = new byte[1];
            while (true)
            {
                int read = await stream.ReadAsync(singleByte, 0, singleByte.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) return null;
                if (singleByte[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)singleByte[0]);
            }
        }

        private static async Task<bool> DrainBytesAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            int remaining = count;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0) return false;
                remaining -= read;
            }
            return true;
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }
}
