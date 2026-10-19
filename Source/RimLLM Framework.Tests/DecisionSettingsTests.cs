using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager.Services;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 決策模型設定的純資料測試：預設值、各供應商獨立性與作用中供應商屬性委派。
/// 不連網，也不觸發 Scribe 或加密金鑰存放區。
/// </summary>
[TestFixture]
public class DecisionSettingsTests
{
    [Test]
    public void DecisionProvider_DefaultsToDisabled()
    {
        var settings = new RimLLMFrameworkSettings();

        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.Disabled, settings.DecisionProvider);
    }

    [Test]
    public void EveryCatalogProvider_HasDefaultModelAndEndpoint()
    {
        var settings = new RimLLMFrameworkSettings();

        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            ClassicAssert.AreEqual(provider.DefaultModel, settings.GetDecisionModel(provider.Id), provider.Id);
            ClassicAssert.AreEqual(provider.DefaultEndpoint, settings.GetDecisionEndpoint(provider.Id), provider.Id);
            ClassicAssert.AreEqual("", settings.GetDecisionApiKey(provider.Id), provider.Id);
        }
    }

    [Test]
    public void PerProviderDecisionSettings_AreIndependent()
    {
        var settings = new RimLLMFrameworkSettings();

        settings.SetDecisionModel("TypeSafe", "jev-custom");
        settings.SetDecisionModel("Liquid", "liquid-custom");
        settings.SetDecisionEndpoint("Liquid", "https://gateway.example");
        settings.SetDecisionApiKey("Liquid", "liquid-key");

        ClassicAssert.AreEqual("jev-custom", settings.GetDecisionModel("TypeSafe"));
        ClassicAssert.AreEqual("liquid-custom", settings.GetDecisionModel("Liquid"));
        ClassicAssert.AreEqual("cloudflare/clef", settings.GetDecisionModel("Clef"));

        ClassicAssert.AreEqual("https://api.typesafe.ai", settings.GetDecisionEndpoint("TypeSafe"));
        ClassicAssert.AreEqual("https://gateway.example", settings.GetDecisionEndpoint("Liquid"));

        ClassicAssert.AreEqual("liquid-key", settings.GetDecisionApiKey("Liquid"));
        ClassicAssert.AreEqual("", settings.GetDecisionApiKey("TypeSafe"));
    }

    [Test]
    public void DecisionProperties_DelegateToActiveProvider()
    {
        var settings = new RimLLMFrameworkSettings();
        settings.SetDecisionModel("TypeSafe", "jev-typesafe-custom");
        settings.SetDecisionModel("OpenAI", "openai-custom");

        settings.DecisionProvider = "TypeSafe";
        ClassicAssert.AreEqual("jev-typesafe-custom", settings.DecisionModel);

        settings.DecisionProvider = "OpenAI";
        ClassicAssert.AreEqual("openai-custom", settings.DecisionModel);

        settings.DecisionModel = "openai-v2";
        ClassicAssert.AreEqual("openai-v2", settings.GetDecisionModel("OpenAI"));
        ClassicAssert.AreEqual("jev-typesafe-custom", settings.GetDecisionModel("TypeSafe"));
    }

    [Test]
    public void RawDecisionSettings_PreserveBlankInput_WhileEffectiveValuesUseDefaults()
    {
        var settings = new RimLLMFrameworkSettings();

        settings.SetDecisionModel("TypeSafe", "");
        settings.SetDecisionEndpoint("TypeSafe", "");

        ClassicAssert.AreEqual("", settings.GetDecisionModelRaw("TypeSafe"));
        ClassicAssert.AreEqual("", settings.GetDecisionEndpointRaw("TypeSafe"));
        ClassicAssert.AreEqual("jev-latest", settings.GetDecisionModel("TypeSafe"));
        ClassicAssert.AreEqual("https://api.typesafe.ai", settings.GetDecisionEndpoint("TypeSafe"));
    }

    [Test]
    public void SanitizeDecisionProvider_UnknownOrEmptyIdFallsBackToDisabled()
    {
        // 空值與未知 id 一律退回 Disabled：DecisionProvider 欄位承載啟用狀態，
        // 不得擅自把來路不明的供應商換成別家已啟用（避免「TypeSafe 已啟用、無金鑰」的幽靈設定）。
        ClassicAssert.AreEqual(
            RimLLMDecisionProviderCatalog.Disabled,
            RimLLMFrameworkSettings.SanitizeDecisionProvider(null));
        ClassicAssert.AreEqual(
            RimLLMDecisionProviderCatalog.Disabled,
            RimLLMFrameworkSettings.SanitizeDecisionProvider(""));
        ClassicAssert.AreEqual(
            RimLLMDecisionProviderCatalog.Disabled,
            RimLLMFrameworkSettings.SanitizeDecisionProvider("NotAProvider"));
        ClassicAssert.AreEqual(
            RimLLMDecisionProviderCatalog.Disabled,
            RimLLMFrameworkSettings.SanitizeDecisionProvider(RimLLMDecisionProviderCatalog.Disabled));

        // 現存供應商原樣保留。
        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            ClassicAssert.AreEqual(provider.Id, RimLLMFrameworkSettings.SanitizeDecisionProvider(provider.Id), provider.Id);
        }
    }
}
