using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Compat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// 簡中自動化 One 相容層：請求 JSON 解析、回應 JSON 合成、錯誤分類，
    /// 以及以「結構同於該 Mod 真實簽章的假型別」驗證 Harmony 補丁綁定（該 Mod 的類別為 internal，無編譯期參考）。
    /// 實際掛載到真 Mod 由遊戲內測試 <c>CompatPatchTests</c> 負責。
    /// </summary>
    [TestFixture]
    public class CompatScAutoTests
    {
        private const string SampleImageDataUrl = "data:image/png;base64,iVBORw0KGgo=";

        // ---------- 請求解析 ----------

        [Test]
        public void ParsePayload_Chat_ReadsMessagesAndSamplingParameters()
        {
            string json = "{\"model\":\"m\",\"messages\":[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"user\",\"content\":\"usr\"}],"
                + "\"temperature\":0.3,\"top_p\":0.9,\"max_tokens\":4096}";

            ScAutoCompatClient.RequestSpec spec = ScAutoCompatClient.ParsePayload(json, chatProtocol: true);

            ClassicAssert.AreEqual(2, spec.Messages.Count);
            ClassicAssert.AreEqual(ChatRole.System, spec.Messages[0].Role);
            ClassicAssert.AreEqual("sys", spec.Messages[0].Text);
            ClassicAssert.AreEqual(ChatRole.User, spec.Messages[1].Role);
            ClassicAssert.AreEqual(4096, spec.MaxOutputTokens);
            ClassicAssert.AreEqual(0.3f, spec.Temperature.Value, 1e-6);
            ClassicAssert.AreEqual(0.9f, spec.TopP.Value, 1e-6);
        }

        [Test]
        public void ParsePayload_Chat_MaxCompletionTokensIsHonoured()
        {
            ScAutoCompatClient.RequestSpec spec = ScAutoCompatClient.ParsePayload(
                "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"max_completion_tokens\":777}", chatProtocol: true);
            ClassicAssert.AreEqual(777, spec.MaxOutputTokens);
        }

        [Test]
        public void ParsePayload_Chat_ImagePartBecomesDataContent()
        {
            string json = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"look\"},"
                + "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + SampleImageDataUrl + "\"}}]}]}";

            ScAutoCompatClient.RequestSpec spec = ScAutoCompatClient.ParsePayload(json, chatProtocol: true);

            IList<AIContent> contents = spec.Messages.Single().Contents;
            ClassicAssert.AreEqual(2, contents.Count);
            ClassicAssert.AreEqual("look", ((TextContent)contents[0]).Text);
            var image = (DataContent)contents[1];
            ClassicAssert.AreEqual("image/png", image.MediaType);
            ClassicAssert.IsNotEmpty(image.Data.ToArray());
        }

        [Test]
        public void ParsePayload_Responses_StringInputWithInstructions()
        {
            ScAutoCompatClient.RequestSpec spec = ScAutoCompatClient.ParsePayload(
                "{\"instructions\":\"rules\",\"input\":\"translate me\",\"max_output_tokens\":1234}", chatProtocol: false);

            ClassicAssert.AreEqual(2, spec.Messages.Count);
            ClassicAssert.AreEqual(ChatRole.System, spec.Messages[0].Role);
            ClassicAssert.AreEqual("rules", spec.Messages[0].Text);
            ClassicAssert.AreEqual(ChatRole.User, spec.Messages[1].Role);
            ClassicAssert.AreEqual("translate me", spec.Messages[1].Text);
            ClassicAssert.AreEqual(1234, spec.MaxOutputTokens);
        }

        [Test]
        public void ParsePayload_Responses_ItemArrayWithInputTextAndDeveloperRole()
        {
            string json = "{\"input\":[{\"role\":\"developer\",\"content\":\"dev\"},"
                + "{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"a\"},"
                + "{\"type\":\"input_image\",\"image_url\":\"" + SampleImageDataUrl + "\"}]}]}";

            ScAutoCompatClient.RequestSpec spec = ScAutoCompatClient.ParsePayload(json, chatProtocol: false);

            ClassicAssert.AreEqual(2, spec.Messages.Count);
            ClassicAssert.AreEqual(ChatRole.System, spec.Messages[0].Role);
            ClassicAssert.AreEqual(2, spec.Messages[1].Contents.Count);
            ClassicAssert.IsInstanceOf<DataContent>(spec.Messages[1].Contents[1]);
        }

        [Test]
        public void ParsePayload_NoMessages_Throws()
        {
            Assert.Throws<FormatException>(() => ScAutoCompatClient.ParsePayload("{\"model\":\"m\"}", chatProtocol: true));
            Assert.Throws<FormatException>(() => ScAutoCompatClient.ParsePayload("[]", chatProtocol: true));
            Assert.Catch<JsonException>(() => ScAutoCompatClient.ParsePayload("not json", chatProtocol: true));
        }

        // ---------- 回應合成 ----------

        [Test]
        public void BuildSuccessJson_Chat_HasChoicesMessageContent()
        {
            using (JsonDocument doc = JsonDocument.Parse(ScAutoCompatClient.BuildSuccessJson("譯文 \"q\"", chatProtocol: true)))
            {
                JsonElement choice = doc.RootElement.GetProperty("choices")[0];
                ClassicAssert.AreEqual("譯文 \"q\"", choice.GetProperty("message").GetProperty("content").GetString());
                ClassicAssert.AreEqual("stop", choice.GetProperty("finish_reason").GetString());
            }
        }

        [Test]
        public void BuildSuccessJson_Responses_HasCompletedStatusAndOutputText()
        {
            using (JsonDocument doc = JsonDocument.Parse(ScAutoCompatClient.BuildSuccessJson("譯文", chatProtocol: false)))
            {
                ClassicAssert.AreEqual("completed", doc.RootElement.GetProperty("status").GetString());
                JsonElement part = doc.RootElement.GetProperty("output")[0].GetProperty("content")[0];
                ClassicAssert.AreEqual("output_text", part.GetProperty("type").GetString());
                ClassicAssert.AreEqual("譯文", part.GetProperty("text").GetString());
            }
        }

        [Test]
        public void BuildErrorJson_Responses_AlsoMarksStatusFailed()
        {
            using (JsonDocument chat = JsonDocument.Parse(ScAutoCompatClient.BuildErrorJson("network", "boom", chatProtocol: true)))
            using (JsonDocument responses = JsonDocument.Parse(ScAutoCompatClient.BuildErrorJson("network", "boom", chatProtocol: false)))
            {
                ClassicAssert.AreEqual("network", chat.RootElement.GetProperty("error").GetProperty("type").GetString());
                ClassicAssert.IsFalse(chat.RootElement.TryGetProperty("status", out _));
                ClassicAssert.AreEqual("failed", responses.RootElement.GetProperty("status").GetString());
                ClassicAssert.AreEqual("boom", responses.RootElement.GetProperty("error").GetProperty("message").GetString());
            }
        }

        [TestCase(LLMError.Timeout, "timeout")]
        [TestCase(LLMError.RateLimit, "rate_limit")]
        [TestCase(LLMError.NetworkError, "network")]
        [TestCase(LLMError.ProviderOffline, "network")]
        [TestCase(LLMError.ContextWindowExceeded, "context_exceeded")]
        [TestCase(LLMError.InvalidResponse, "parse")]
        public void MapErrorType_TransientAndKnownKinds(LLMError error, string expected)
        {
            ClassicAssert.AreEqual(expected, ScAutoCompatClient.MapErrorType(error));
        }

        [TestCase(LLMError.InvalidKey)]
        [TestCase(LLMError.QuotaExceeded)]
        [TestCase(LLMError.ModelNotFound)]
        [TestCase(LLMError.ContentFilter)]
        [TestCase(LLMError.Cancelled)]
        [TestCase(LLMError.Unknown)]
        public void MapErrorType_PermanentKindsAvoidTransientKeywords(LLMError error)
        {
            // 該 Mod 以子字串判斷暫時性錯誤（network／timeout／TooManyRequests／rate_limit／server／service_unavailable），
            // 永久性錯誤的名稱不能含這些字樣，否則它會白白重試。
            string mapped = ScAutoCompatClient.MapErrorType(error);
            foreach (string keyword in new[] { "network", "timeout", "toomanyrequests", "rate_limit", "server", "service_unavailable" })
            {
                StringAssert.DoesNotContain(keyword, mapped.ToLowerInvariant());
            }
        }

        // ---------- CompleteAsync ----------

        [Test]
        public async Task CompleteAsync_Chat_ForwardsMessagesAndReturnsChatJson()
        {
            var fake = new CapturingChatClient { ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "譯")) };
            var client = new ScAutoCompatClient(fake);

            string json = await client.CompleteAsync(
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":0.5,\"max_tokens\":99}", true, CancellationToken.None);

            ClassicAssert.AreEqual("hi", fake.ReceivedMessages.Single().Single().Text);
            ClassicAssert.AreEqual(99, fake.ReceivedOptions.Single().MaxOutputTokens);
            ClassicAssert.AreEqual(0.5f, fake.ReceivedOptions.Single().Temperature.Value, 1e-6);
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                ClassicAssert.AreEqual("譯", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            }
        }

        [Test]
        public async Task CompleteAsync_NoMaxTokensInRequest_UsesDefaultOutputLimit()
        {
            var fake = new CapturingChatClient { ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")) };

            await new ScAutoCompatClient(fake).CompleteAsync("{\"input\":\"hi\"}", false, CancellationToken.None);

            ClassicAssert.AreEqual(ScAutoCompatClient.DefaultMaxOutputTokens, fake.ReceivedOptions.Single().MaxOutputTokens);
        }

        [Test]
        public async Task CompleteAsync_ProviderFailure_BecomesErrorJsonNotException()
        {
            var fake = new CapturingChatClient { ResponseException = new RimLLMException(LLMError.RateLimit, "slow down") };

            string json = await new ScAutoCompatClient(fake).CompleteAsync(
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", true, CancellationToken.None);

            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement error = doc.RootElement.GetProperty("error");
                ClassicAssert.AreEqual("rate_limit", error.GetProperty("type").GetString());
                StringAssert.Contains("slow down", error.GetProperty("message").GetString());
            }
        }

        [Test]
        public async Task CompleteAsync_MalformedPayload_BecomesParseError()
        {
            var fake = new CapturingChatClient();

            string json = await new ScAutoCompatClient(fake).CompleteAsync("garbage", true, CancellationToken.None);

            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                ClassicAssert.AreEqual("parse", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
            }
            ClassicAssert.IsEmpty(fake.ReceivedMessages);
        }

        [Test]
        public void CompleteAsync_CallerCancellation_Propagates()
        {
            var fake = new CapturingChatClient { ResponseException = new OperationCanceledException() };
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.ThrowsAsync<OperationCanceledException>(() => new ScAutoCompatClient(fake).CompleteAsync(
                    "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", true, cts.Token));
            }
        }

        // ---------- Harmony 補丁綁定（假型別，結構同該 Mod 的真簽章） ----------

        /// <summary>對應 RimTranslation.Api.ResponsesResult。</summary>
        internal sealed class FakeResult
        {
            public string Status;
            public string OutputText;
            public string ErrorType;
        }

        /// <summary>對應 ResponsesClient；PostOnceCoreAsync 是私有 async 方法，簽章逐字對應。</summary>
        internal sealed class FakeResponsesClient
        {
            internal Task<FakeResult> PostOnceCoreAsync(
                string relativeUrl,
                string payload,
                bool stream,
                Func<string, FakeResult> parseJson,
                Func<IReadOnlyList<string>, FakeResult> parseSse,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new FakeResult { Status = "completed", OutputText = "native" });
            }
        }

        internal static class FakeStore
        {
            internal static bool IsLocalBaseUrl(string baseUrl) => false;
        }

        internal sealed class FakeConnectionResult
        {
            public bool Ok { get; set; }
            public IReadOnlyList<string> Models { get; set; }
        }

        /// <summary>對應 ApiConnectionProbe；攔的是 <c>Task.Run</c> 薄殼 FetchModelsAsync，而非含例外過濾器的 ProbeCore。</summary>
        internal static class FakeConnectionProbe
        {
            public static Task<FakeConnectionResult> FetchModelsAsync(CancellationToken token)
                => Task.FromResult(new FakeConnectionResult { Ok = false });
        }

#pragma warning disable CS0649 // 欄位由補丁以反射寫入，與該 Mod 真實的 ThinkingProbeResult（公開欄位）一致
        internal sealed class FakeThinkingResult
        {
            public bool Success;
            public string Message;
            public List<string> Attempts;
        }
#pragma warning restore CS0649

        internal static class FakeThinkingProbe
        {
            public static Task<FakeThinkingResult> RunAsync(object config, string apiKey, object handler, CancellationToken token)
                => Task.FromResult(new FakeThinkingResult { Success = false });
        }

        /// <summary>模擬該 Mod 的兩個解析器：Chat 讀 choices[0].message.content，Responses 讀 output[0].content[0].text。</summary>
        private static FakeResult ParseFake(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                var result = new FakeResult { Status = root.TryGetProperty("error", out _) ? "failed" : "completed" };
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    result.ErrorType = error.GetProperty("type").GetString();
                }
                else if (root.TryGetProperty("choices", out JsonElement choices))
                {
                    result.OutputText = choices[0].GetProperty("message").GetProperty("content").GetString();
                }
                else
                {
                    result.OutputText = root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
                }
                return result;
            }
        }

        private static Type ResolveFake(string name)
        {
            switch (name)
            {
                case "RimTranslation.Api.ResponsesClient": return typeof(FakeResponsesClient);
                case "RimTranslation.Api.ApiConfigurationStore": return typeof(FakeStore);
                case "RimTranslation.Api.ApiConnectionProbe": return typeof(FakeConnectionProbe);
                case "RimTranslation.Api.ThinkingParameterProbeService": return typeof(FakeThinkingProbe);
                default: return null;
            }
        }

        private static Task<FakeResult> CallPostOnce(string relativeUrl, string payload)
        {
            return new FakeResponsesClient().PostOnceCoreAsync(relativeUrl, payload, false, ParseFake, lines => null, CancellationToken.None);
        }

        /// <summary>套上補丁、執行測試主體、一定還原；Harmony id 獨立，不干擾框架自己的補丁。</summary>
        private static async Task WithPatches(Func<Task> body, CapturingChatClient chat)
        {
            const string id = "test.rimllm.scauto";
            var harmony = new Harmony(id);
            ScAutoCompatClient previousClient = ScAutoCompatPatch.Client;
            try
            {
                ScAutoCompatPatch.Apply(harmony, ResolveFake);
                ScAutoCompatPatch.Client = new ScAutoCompatClient(chat);
                await body();
            }
            finally
            {
                harmony.UnpatchAll(id);
                ScAutoCompatPatch.Client = previousClient;
                ScAutoCompatPatch.ResetForTests();
            }
        }

        [Test]
        public async Task Patch_TakeoverOn_RoutesChatRequestThroughRimLLMAndParsesWithNativeParser()
        {
            var chat = new CapturingChatClient { ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "routed")) };
            await WithPatches(async () =>
            {
                ScAutoCompatPatch.SetTakeOverCacheForTests(true);

                FakeResult result = await CallPostOnce("chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"}]}");

                ClassicAssert.AreEqual("routed", result.OutputText);
                ClassicAssert.AreEqual("completed", result.Status);
                ClassicAssert.AreEqual("q", chat.ReceivedMessages.Single().Single().Text);
            }, chat);
        }

        [Test]
        public async Task Patch_TakeoverOn_RoutesResponsesRequest()
        {
            var chat = new CapturingChatClient { ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "r")) };
            await WithPatches(async () =>
            {
                ScAutoCompatPatch.SetTakeOverCacheForTests(true);

                FakeResult result = await CallPostOnce("responses", "{\"input\":\"q\"}");

                ClassicAssert.AreEqual("r", result.OutputText);
            }, chat);
        }

        [Test]
        public async Task Patch_TakeoverOn_ProviderFailureSurfacesAsFailedResultWithErrorType()
        {
            var chat = new CapturingChatClient { ResponseException = new RimLLMException(LLMError.NetworkError, "down") };
            await WithPatches(async () =>
            {
                ScAutoCompatPatch.SetTakeOverCacheForTests(true);

                FakeResult result = await CallPostOnce("chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"}]}");

                ClassicAssert.AreEqual("failed", result.Status);
                ClassicAssert.AreEqual("network", result.ErrorType);
            }, chat);
        }

        [Test]
        public async Task Patch_TakeoverOff_RunsNativePath()
        {
            var chat = new CapturingChatClient();
            await WithPatches(async () =>
            {
                ScAutoCompatPatch.SetTakeOverCacheForTests(false);

                FakeResult result = await CallPostOnce("chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"}]}");

                ClassicAssert.AreEqual("native", result.OutputText);
                ClassicAssert.IsEmpty(chat.ReceivedMessages);
            }, chat);
        }

        [Test]
        public async Task Patch_IsLocalBaseUrl_TreatedAsLocalOnlyWhileTakingOver()
        {
            await WithPatches(() =>
            {
                ScAutoCompatPatch.SetTakeOverCacheForTests(false);
                ClassicAssert.IsFalse(FakeStore.IsLocalBaseUrl("https://api.example.com"));

                ScAutoCompatPatch.SetTakeOverCacheForTests(true);
                ClassicAssert.IsTrue(FakeStore.IsLocalBaseUrl("https://api.example.com"));
                return Task.CompletedTask;
            }, new CapturingChatClient());
        }

        [Test]
        public async Task Patch_Probes_ShortCircuitToSuccessWhileTakingOver()
        {
            await WithPatches(() =>
            {
                ScAutoCompatPatch.SetTakeOverCacheForTests(false);
                ClassicAssert.IsFalse(FakeConnectionProbe.FetchModelsAsync(CancellationToken.None).Result.Ok);
                ClassicAssert.IsFalse(FakeThinkingProbe.RunAsync(null, null, null, CancellationToken.None).Result.Success);

                ScAutoCompatPatch.SetTakeOverCacheForTests(true);
                FakeConnectionResult connection = FakeConnectionProbe.FetchModelsAsync(CancellationToken.None).Result;
                ClassicAssert.IsTrue(connection.Ok);
                CollectionAssert.AreEqual(new[] { "RimLLM" }, connection.Models);
                FakeThinkingResult thinking = FakeThinkingProbe.RunAsync(null, null, null, CancellationToken.None).Result;
                ClassicAssert.IsTrue(thinking.Success);
                ClassicAssert.IsEmpty(thinking.Attempts);
                ClassicAssert.IsNotEmpty(thinking.Message);
                return Task.CompletedTask;
            }, new CapturingChatClient());
        }

        [Test]
        public void Apply_MissingTransportMethod_Throws()
        {
            Assert.Throws<MissingMethodException>(() => ScAutoCompatPatch.Apply(new Harmony("test.rimllm.scauto.missing"), _ => null));
        }
    }
}
