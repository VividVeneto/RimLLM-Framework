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
    /// <summary>
    /// 目錄不變量：識別碼唯一、端點是絕對 origin、模型清單完整。
    /// 遠端供應商一律 HTTPS；本機架設的項目可以用明文 HTTP，但主機必須是迴圈位址——
    /// 資料與金鑰不該在明文中離開本機。
    /// </summary>
    [Test]
    public void Catalog_HasUniqueIds_AndValidOrigins()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            Assert.That(seen.Add(provider.Id), Is.True, $"重複的 provider id: {provider.Id}");
            Assert.That(
                Uri.TryCreate(provider.DefaultEndpoint, UriKind.Absolute, out Uri uri),
                Is.True,
                $"{provider.Id} 的預設端點不是絕對 URI");
            Assert.That(uri.AbsolutePath, Is.EqualTo("/"), $"{provider.Id} 的 BaseAddress 必須是 origin");
            Assert.That(
                uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host)),
                Is.True,
                $"{provider.Id} 必須使用 HTTPS（本機端點才可以用明文 HTTP）");
            Assert.That(provider.DefaultModel, Is.Not.Null.And.Not.Empty, $"{provider.Id} 缺少預設模型");
            Assert.That(
                provider.PresetModels,
                Does.Contain(provider.DefaultModel),
                $"{provider.Id} 的預設模型不在預設清單中");
        }
    }

    /// <summary>主機是否為本機迴圈位址（localhost、127.0.0.0/8、::1）。</summary>
    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(host, out System.Net.IPAddress address)
               && System.Net.IPAddress.IsLoopback(address);
    }

    [Test]
    public void Catalog_ContainsEveryLocalDecisionProvider()
    {
        // 本機架設的三個項目：Ollama、Unsloth 與通用本機項。
        foreach (string id in new[]
                 {
                     RimLLMDecisionProviderCatalog.Ollama,
                     RimLLMDecisionProviderCatalog.Unsloth,
                     RimLLMDecisionProviderCatalog.Local
                 })
        {
            Assert.That(RimLLMDecisionProviderCatalog.TryGet(id, out _), Is.True, $"缺少供應商: {id}");
        }

        // 這三家的預設端點都在本機迴圈位址上走明文 HTTP。
        foreach (string id in new[]
                 {
                     RimLLMDecisionProviderCatalog.Ollama,
                     RimLLMDecisionProviderCatalog.Unsloth,
                     RimLLMDecisionProviderCatalog.Local
                 })
        {
            Uri uri = new(RimLLMDecisionProviderCatalog.GetDefaultEndpoint(id), UriKind.Absolute);
            Assert.That(uri.Scheme, Is.EqualTo(Uri.UriSchemeHttp), id);
            Assert.That(IsLoopbackHost(uri.Host), Is.True, id);
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
