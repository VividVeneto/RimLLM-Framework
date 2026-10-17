using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class CostEstimationTests
{
    [Test]
    public void UnknownRateIsDistinctFromKnownFreeRate()
    {
        var settings = new MockSettings();
        var tracker = new RimLLMUsageTracker(settings);

        ClassicAssert.IsFalse(tracker.TryEstimateCost("unknown", "model", 1000, 1000, 0, out float unknownCost));
        ClassicAssert.AreEqual(0f, unknownCost);
        ClassicAssert.IsTrue(tracker.TryEstimateCost("z.ai", "glm-4.5-flash", 1000, 1000, 0, out float freeCost));
        ClassicAssert.AreEqual(0f, freeCost);

        tracker.RecordUsage("unknown", "model", 1000, 1000);
        tracker.RecordUsage("z.ai", "glm-4.5-flash", 1000, 1000);

        ClassicAssert.AreEqual(1L, tracker.UnpricedUsageCount);
        ClassicAssert.AreEqual(2000, settings.TotalPromptTokens);
        ClassicAssert.AreEqual(2000, settings.TotalCompletionTokens);
        ClassicAssert.AreEqual(0f, settings.TotalEstimatedCost);

        tracker.ResetUsage();
        ClassicAssert.AreEqual(0L, tracker.UnpricedUsageCount);
    }

    [Test]
    public void CachedInputUsesMatchedModelsRate()
    {
        var tracker = new RimLLMUsageTracker(new MockSettings());

        ClassicAssert.IsTrue(tracker.TryEstimateCost("deepseek", "deepseek-chat", 1000000, 0, 1000000, out float deepseekCost));
        ClassicAssert.AreEqual(0.14f * 0.02f, deepseekCost, 0.00001f);

        ClassicAssert.IsTrue(tracker.TryEstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 1000000, out float geminiCost));
        ClassicAssert.AreEqual(0.075f * 0.25f, geminiCost, 0.00001f);
    }
}
