using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 決策服務的錯誤契約測試：所有從 Jev SDK / HTTP 層冒出的例外都必須收斂成
/// RimLLMException（並帶 LLMError 代碼），呼叫端取消才維持 OperationCanceledException。
/// Jev 例外型別的建構子是 internal，因此以反射建立實例來覆蓋每一個映射分支。
/// </summary>
[TestFixture]
public class DecisionServiceErrorTests
{
    private static readonly Assembly JevAssembly = ResolveJevAssembly();

    private static Assembly ResolveJevAssembly()
    {
        try
        {
            return Assembly.Load("Kkdev92.Jev");
        }
        catch (System.IO.FileNotFoundException)
        {
            // 測試 bin 沒帶到套件 DLL 時，直接從 mod 的出貨目錄載入。
            string binRoot = AppContext.BaseDirectory;
            string candidate = System.IO.Path.Combine(binRoot, "..", "..", "..", "..", "Assemblies", "Kkdev92.Jev.dll");
            return Assembly.LoadFrom(System.IO.Path.GetFullPath(candidate));
        }
    }

    private static object EnumOf(string typeName, string value)
    {
        Type type = JevAssembly.GetType($"Kkdev92.Jev.{typeName}", throwOnError: true)!;
        return Enum.Parse(type, value);
    }

    private static Exception BuildJev(string typeName, params object[] args)
    {
        Type type = JevAssembly.GetType($"Kkdev92.Jev.{typeName}", throwOnError: true)!;
        foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            if (parameters.Length != args.Length) continue;

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (args[i] == null)
                {
                    if (parameters[i].ParameterType.IsValueType &&
                        Nullable.GetUnderlyingType(parameters[i].ParameterType) == null)
                    {
                        matches = false;
                        break;
                    }
                    continue;
                }
                if (!parameters[i].ParameterType.IsInstanceOfType(args[i]))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                return (Exception)ctor.Invoke(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, args, null);
            }
        }
        throw new MissingMethodException($"Kkdev92.Jev.{typeName} 沒有符合參數數量的建構子");
    }

    private static Exception Translate(Exception ex)
    {
        return RimLLMDecisionService.TranslateDecisionError(ex);
    }

    private static object EmptyValidationErrorList()
    {
        Type errorType = JevAssembly.GetType("Kkdev92.Jev.JevValidationError", throwOnError: true)!;
        return Activator.CreateInstance(typeof(List<>).MakeGenericType(errorType))!;
    }

    /// <summary>反覆建立含一題 Noul 的決策計畫；空計畫會被 SDK 拒收，不是要測的路徑。</summary>
    private static Kkdev92.Jev.JevDecisionPlan OneQuestionPlan()
    {
        var builder = new Kkdev92.Jev.JevDecisionPlanBuilder();
        builder.AddNoul("q", "question?", default);
        return builder.Build();
    }

    [Test]
    public void JevHttpException_IsMappedByStatusCode_ThroughLlmErrorMapper()
    {
        // 401 → InvalidKey
        var unauthorized = (RimLLMException)Translate(BuildJev("JevHttpException",
            EnumOf("JevOperation", "Evaluate"), 401, "req-1", null, null, EmptyValidationErrorList(), null));
        ClassicAssert.AreEqual(LLMError.InvalidKey, unauthorized.Error);
        ClassicAssert.AreEqual(401, unauthorized.HttpStatusCode);

        // 429 → RateLimit；Retry-After 必須保留，讓呼叫端的重試邏輯能運作
        var rateLimit = (RimLLMException)Translate(BuildJev("JevHttpException",
            EnumOf("JevOperation", "Evaluate"), 429, "req-2", null, TimeSpan.FromSeconds(7), EmptyValidationErrorList(), null));
        ClassicAssert.AreEqual(LLMError.RateLimit, rateLimit.Error);
        ClassicAssert.AreEqual(429, rateLimit.HttpStatusCode);
        ClassicAssert.AreEqual(TimeSpan.FromSeconds(7), rateLimit.RetryAfter);
        ClassicAssert.IsNotNull(rateLimit.InnerException);

        // 404 → ModelNotFound
        var notFound = (RimLLMException)Translate(BuildJev("JevHttpException",
            EnumOf("JevOperation", "Evaluate"), 404, null, null, null, EmptyValidationErrorList(), null));
        ClassicAssert.AreEqual(LLMError.ModelNotFound, notFound.Error);

        // 500 → ProviderOffline
        var offline = (RimLLMException)Translate(BuildJev("JevHttpException",
            EnumOf("JevOperation", "Evaluate"), 500, null, null, null, EmptyValidationErrorList(), null));
        ClassicAssert.AreEqual(LLMError.ProviderOffline, offline.Error);
    }

    [Test]
    public void JevTimeoutException_MapsToTimeout()
    {
        var timeout = (RimLLMException)Translate(BuildJev("JevTimeoutException",
            EnumOf("JevOperation", "Evaluate"), TimeSpan.FromSeconds(30),
            Enum.Parse(JevAssembly.GetType("Kkdev92.Jev.JevRequestTransmission", true)!, "Started"),
            null));
        ClassicAssert.AreEqual(LLMError.Timeout, timeout.Error);
        ClassicAssert.IsNotNull(timeout.InnerException);
    }

    [Test]
    public void JevTransportException_MapsToNetworkError()
    {
        var transport = (RimLLMException)Translate(BuildJev("JevTransportException",
            EnumOf("JevOperation", "Evaluate"), EnumOf("JevTransportFailure", "Connect"),
            Enum.Parse(JevAssembly.GetType("Kkdev92.Jev.JevRequestTransmission", true)!, "NotStarted"),
            new System.Net.Http.HttpRequestException("connection refused")));
        ClassicAssert.AreEqual(LLMError.NetworkError, transport.Error);
        ClassicAssert.IsNotNull(transport.InnerException);
    }

    [Test]
    public void JevProtocolException_MapsToInvalidResponse()
    {
        var protocol = (RimLLMException)Translate(BuildJev("JevProtocolException",
            EnumOf("JevOperation", "Evaluate"), EnumOf("JevProtocolError", "MalformedJson"), "req-3"));
        ClassicAssert.AreEqual(LLMError.InvalidResponse, protocol.Error);
    }

    [Test]
    public void JevLimitException_MapsToUnknown()
    {
        var limit = (RimLLMException)Translate(BuildJev("JevLimitException",
            EnumOf("JevOperation", "Evaluate"), EnumOf("JevLimit", "Queue"), (long?)3,
            Enum.Parse(JevAssembly.GetType("Kkdev92.Jev.JevRequestTransmission", true)!, "NotStarted")));
        ClassicAssert.AreEqual(LLMError.Unknown, limit.Error);
    }

    [Test]
    public void PlainTimeoutException_MapsToTimeout()
    {
        var timeout = (RimLLMException)Translate(new TimeoutException("deadline"));
        ClassicAssert.AreEqual(LLMError.Timeout, timeout.Error);
    }

    [Test]
    public void InvalidOperationExceptionFromSdkCredential_MapsToInvalidKey_WithoutLeakingTheKey()
    {
        // JevClient 在金鑰不可用時丟出這則訊息的 InvalidOperationException（屬 SDK 的內部檢查）。
        var invalidKey = (RimLLMException)Translate(new InvalidOperationException(
            "The credential returned an API key that is empty or contains whitespace, control or non-ASCII characters."));
        ClassicAssert.AreEqual(LLMError.InvalidKey, invalidKey.Error);
        StringAssert.DoesNotContain("Bearer", invalidKey.Message, "錯誤訊息不得包含推測的金鑰內容");

        // 其餘 InvalidOperationException 與契約無關，退回 Unknown 即可。
        var unknown = (RimLLMException)Translate(new InvalidOperationException("unrelated"));
        ClassicAssert.AreEqual(LLMError.Unknown, unknown.Error);
    }

    [Test]
    public void GenericException_MapsToUnknown()
    {
        var unknown = (RimLLMException)Translate(new Exception("boom <script>"));
        ClassicAssert.AreEqual(LLMError.Unknown, unknown.Error);
    }

    [Test]
    public void DisabledProvider_ThrowsRimLLMExceptionNotInvalidOperationException()
    {
        var settings = new MockSettings();

        var service = new RimLLMDecisionService(settings);
        var ex = ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.EvaluateAsync("state", OneQuestionPlan()));
        ClassicAssert.AreEqual(LLMError.Unknown, ex!.Error);
        ClassicAssert.AreEqual("Disabled", settings.DecisionProvider);
    }

    [Test]
    public void EnabledProviderWithoutKey_ThrowsInvalidKey()
    {
        var settings = new MockSettings();
        settings.DecisionProvider = "TypeSafe";

        var service = new RimLLMDecisionService(settings);
        var ex = ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.EvaluateAsync("state", OneQuestionPlan()));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }

    [Test]
    public void TestConnectionWithoutKey_FailsBeforeAnyNetworkCall()
    {
        var service = new RimLLMDecisionService(new MockSettings());
        var ex = ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.TestConnectionAsync("https://api.typesafe.ai", "jev-latest", ""));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }

    [Test]
    public void FetchModelsWithoutKey_FailsBeforeAnyNetworkCall()
    {
        var service = new RimLLMDecisionService(new MockSettings());
        var ex = ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.FetchModelsAsync("https://api.typesafe.ai", "jev-latest", ""));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }
}
