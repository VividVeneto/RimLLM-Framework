using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

[TestFixture]
[NonParallelizable]
public class UiAndLogReviewRegressionTests
{
    [TestCase("{\"api_key\":\"opaque-unit-test-secret\"}")]
    [TestCase("{'x-api-key':'opaque-unit-test-secret'}")]
    [TestCase("{\"x-goog-api-key\" : \"opaque-unit-test-secret\"}")]
    [TestCase("{\"authorization\":\"Bearer opaque-unit-test-secret\"}")]
    [TestCase("Authorization: Bearer opaque-unit-test-secret")]
    [TestCase("https://example.invalid/?key=opaque-unit-test-secret&model=test")]
    public void LogSanitizerRedactsQuotedAndUnquotedAssignments(string value)
    {
        string result = RimLLMLog.SanitizeForLog(value);
        Assert.That(result, Does.Not.Contain("opaque-unit-test-secret"));
        Assert.That(result, Does.Contain("redacted"));
    }

    [Test]
    public void LogSanitizerKeepsOrdinaryJson()
    {
        const string value = "{\"model\":\"test-model\",\"message\":\"ready\"}";
        Assert.That(RimLLMLog.SanitizeForLog(value), Is.EqualTo(value));
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    [TestCase(true, true, true)]
    public void CanceledRequestCallbacksCannotChangeClearedOrNewHistory(bool error, bool clearOnly, bool enqueueAfterCancel)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Type drawer = typeof(ChatTestDrawer);
        var history = (List<string>)drawer.GetField("chatHistory", flags).GetValue(null);
        var oldHistory = new List<string>(history);
        FieldInfo pump = typeof(RimLLMDispatcher).GetField("_hasPump", flags);
        bool oldPump = (bool)pump.GetValue(null);
        FieldInfo loading = drawer.GetField("chatLoading", flags);
        bool oldLoading = (bool)loading.GetValue(null);
        FieldInfo ctsField = drawer.GetField("chatCts", flags);
        object oldCts = ctsField.GetValue(null);
        FieldInfo generationField = drawer.GetField("chatRequestGeneration", flags);
        long generation = (long)generationField.GetValue(null);
        using var requestCts = new CancellationTokenSource();
        RimLLMDispatcher.ResetQueueForTests();
        try
        {
            pump.SetValue(null, true);
            history.Clear();
            history.Add("old placeholder");
            ctsField.SetValue(null, requestCts);
            loading.SetValue(null, true);
            void EnqueueOldCallback()
            {
                if (error)
                {
                    drawer.GetMethod("UpdateChatError", flags).Invoke(null,
                        new object[] { generation, 0, "old delayed error" });
                }
                else
                {
                    drawer.GetMethod("UpdateAiHistoryEntry", flags).Invoke(null,
                        new object[] { generation, 0, "old delayed reply", null, true, false });
                }
            }
            if (!enqueueAfterCancel) EnqueueOldCallback();

            drawer.GetMethod("CancelActiveChatRequest", flags).Invoke(null, null);
            history.Clear();
            if (!clearOnly) history.Add("new request placeholder");
            loading.SetValue(null, !clearOnly);
            if (enqueueAfterCancel) EnqueueOldCallback();
            RimLLMDispatcher.DrainWithBudget(int.MaxValue, long.MaxValue);

            Assert.That(history, Is.EqualTo(clearOnly ? Array.Empty<string>() : new[] { "new request placeholder" }),
                "已取消請求的延遲回覆不能寫進重建後的歷史。");
            Assert.That(loading.GetValue(null), Is.EqualTo(!clearOnly));

            if (!clearOnly)
            {
                long currentGeneration = (long)generationField.GetValue(null);
                drawer.GetMethod("UpdateAiHistoryEntry", flags).Invoke(null,
                    new object[] { currentGeneration, 0, "current reply", null, false, false });
                RimLLMDispatcher.DrainWithBudget(int.MaxValue, long.MaxValue);
                Assert.That(history[0], Does.Contain("current reply"), "新請求的更新仍須正常顯示。");
                Assert.That(loading.GetValue(null), Is.True);
            }
        }
        finally
        {
            history.Clear();
            history.AddRange(oldHistory);
            loading.SetValue(null, oldLoading);
            ctsField.SetValue(null, oldCts);
            generationField.SetValue(null, generation);
            pump.SetValue(null, oldPump);
            RimLLMDispatcher.ResetQueueForTests();
        }
    }
}
