using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 決策模組測試共用的測試替身與小幫手。
/// 兩支決策 fixture（供應商選用、本機端點）都要攔截 SDK 的請求，各自抄一份
/// 假 handler 與設定骨架只會讓兩邊的行為悄悄分歧，因此集中在這裡。
/// </summary>
internal static class DecisionTestDoubles
{
    /// <summary>
    /// 攔截 Jev SDK 送出的請求並回應 System One wire format 的假端點。
    /// 回應內容依路徑切換：/v1/systemone 回一題 Noul，/v1/models 回模型清單。
    /// </summary>
    internal sealed class FakeJevHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        public List<string> AuthorizationHeaders { get; } = [];

        public string EvaluateResponseBody { get; set; } =
            "{\"model\":\"jev-latest\",\"answers\":{\"hostile\":{\"type\":\"noul\",\"noul\":0.75}}," +
            "\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}";

        public string ModelsResponseBody { get; set; } =
            "{\"models\":[{\"name\":\"jev-latest\",\"description\":\"latest\",\"release_date\":\"2026-01-01\"}," +
            "{\"name\":\"jev-1.13\",\"description\":\"stable\",\"release_date\":\"2025-12-01\"}]}";

        public int ResponseStatusCode { get; set; } = 200;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            lock (RequestUris)
            {
                RequestUris.Add(request.RequestUri);
                AuthorizationHeaders.Add(
                    request.Headers.TryGetValues("Authorization", out IEnumerable<string> values)
                        ? string.Join(",", values)
                        : "");
            }

            bool isModelList = path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase);
            string body = isModelList ? ModelsResponseBody : EvaluateResponseBody;

            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)ResponseStatusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// 測試用的設定：關掉防濫用節流，免得請求數影響後續斷言。
    /// </summary>
    internal static MockSettings NewSettings()
    {
        return new MockSettings { EnableAntiAbuse = false };
    }

    /// <summary>只有一題 Noul 的決策計畫；回傳 handle 以便讀回機率。</summary>
    internal static (JevDecisionPlan Plan, JevNoulHandle Handle) OneQuestion()
    {
        var builder = new JevDecisionPlanBuilder();
        JevNoulHandle handle = builder.AddNoul("hostile", "Is the speaker hostile?", default);
        return (builder.Build(), handle);
    }

    internal static JevDecisionPlan OneQuestionPlan()
    {
        return OneQuestion().Plan;
    }

    /// <summary>
    /// 暫時把框架的 manager 換成指定設定，執行動作後還原。
    /// 靜態的 <see cref="RimLLMProvider"/> 是單一執行個體，測試之間必須互相還原。
    /// </summary>
    internal static DecisionAvailability WithManager(
        MockSettings settings, Func<DecisionAvailability> action)
    {
        RimLLMProvider.TryGetManager(out RimLLMManager previousManager);
        RimLLMProvider.Initialize(new RimLLMManager(settings));
        try
        {
            return action();
        }
        finally
        {
            RimLLMProvider.Initialize(previousManager);
        }
    }
}
