using System;
using System.Collections.Generic;
using NUnit.Framework;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 決策模型供應商清單與模型快取鍵的純資料測試；不連網，也不依賴 RimWorld 執行階段。
/// </summary>
[TestFixture]
public class DecisionProviderTests
{
    [Test]
    public void Catalog_HasUniqueIds_AndValidHttpsOrigins()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            Assert.That(seen.Add(provider.Id), Is.True, $"重複的 provider id: {provider.Id}");
            Assert.That(
                Uri.TryCreate(provider.DefaultEndpoint, UriKind.Absolute, out Uri uri),
                Is.True,
                $"{provider.Id} 的預設端點不是絕對 URI");
            Assert.That(uri.Scheme, Is.EqualTo(Uri.UriSchemeHttps), $"{provider.Id} 必須使用 HTTPS");
            Assert.That(uri.AbsolutePath, Is.EqualTo("/"), $"{provider.Id} 的 BaseAddress 必須是 origin");
            Assert.That(provider.DefaultModel, Is.Not.Null.And.Not.Empty, $"{provider.Id} 缺少預設模型");
            Assert.That(
                provider.PresetModels,
                Does.Contain(provider.DefaultModel),
                $"{provider.Id} 的預設模型不在預設清單中");
        }
    }

    [Test]
    public void Catalog_ContainsEveryRequestedProvider()
    {
        string[] expected =
        [
            "TypeSafe", "Clef", "Liquid", "Solar", "Kev",
            "Tev", "Laya", "Span", "Microsoft", "OpenAI"
        ];

        foreach (string id in expected)
        {
            Assert.That(
                RimLLMDecisionProviderCatalog.TryGet(id, out _),
                Is.True,
                $"缺少供應商: {id}");
        }
    }

    [Test]
    public void UnknownProvider_FallsBackToTypeSafeDefaults()
    {
        Assert.That(RimLLMDecisionProviderCatalog.Get("NotAProvider").Id,
            Is.EqualTo(RimLLMDecisionProviderCatalog.TypeSafe));
        Assert.That(RimLLMDecisionProviderCatalog.GetDefaultModel("NotAProvider"),
            Is.EqualTo("jev-latest"));
        Assert.That(RimLLMDecisionProviderCatalog.GetDefaultEndpoint("NotAProvider"),
            Is.EqualTo("https://api.typesafe.ai"));
    }

    [Test]
    public void ModelListKey_IsScopedToDecisionModule()
    {
        Assert.That(RimLLMDecisionService.GetModelListKey("TypeSafe"), Is.EqualTo("decision:TypeSafe"));
        Assert.That(RimLLMDecisionService.GetModelListKey(null), Is.EqualTo("decision:"));
    }
}
