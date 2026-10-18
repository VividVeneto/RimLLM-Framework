using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

[TestFixture]
public sealed class StructuredReviewRegressionTests
{
    [Test]
    public void SchemaAndValidationFollowStjDeserializationContract()
    {
        JsonObject schema = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(ContractDto))).AsObject();
        JsonObject properties = schema["properties"].AsObject();

        var propertyNames = new List<string>();
        foreach (KeyValuePair<string, JsonNode> property in properties)
        {
            propertyNames.Add(property.Key);
        }
        CollectionAssert.AreEquivalent(new[] { "Name" }, propertyNames);

        ContractDto parsed = RimLLMJsonHelper.DeserializeAndValidate<ContractDto>(
            "{\"Name\":\"ok\",\"Unknown\":\"extension value\"}");

        ClassicAssert.AreEqual("ok", parsed.Name);
        Assert.DoesNotThrow(() => RimLLMJsonHelper.ValidateStructuredObject(
            new ContractDto { Name = "ok", Extra = null }));
    }

    [Test]
    public void DeserializeAndValidateRejectsMissingMembersEvenWhenTheyHaveDefaults()
    {
        Assert.Throws<InvalidOperationException>(
            () => RimLLMJsonHelper.DeserializeAndValidate<RequiredDto>("{\"Label\":\"set\"}"));
        Assert.Throws<InvalidOperationException>(
            () => RimLLMJsonHelper.DeserializeAndValidate<RequiredDto>("{\"Count\":4}"));
    }

    [Test]
    public void DeserializeAndValidateKeepsPermissiveNumberAndCaseHandling()
    {
        RequiredDto parsed = RimLLMJsonHelper.DeserializeAndValidate<RequiredDto>(
            "{\"count\":\"4\",\"label\":\"set\"}");

        ClassicAssert.AreEqual(4, parsed.Count);
        ClassicAssert.AreEqual("set", parsed.Label);
    }

    [Test]
    public void CaseInsensitiveDuplicatePropertiesUseLastJsonValue()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RimLLMJsonHelper.DeserializeAndValidate<ContractDto>("{\"Name\":\"ok\",\"name\":null}"));

        ContractDto parsed = RimLLMJsonHelper.DeserializeAndValidate<ContractDto>(
            "{\"Name\":null,\"name\":\"ok\"}");
        ClassicAssert.AreEqual("ok", parsed.Name);
    }

    [Test]
    public void DeserializeAndValidateChecksDictionaryValuesRecursively()
    {
        Assert.Throws<InvalidOperationException>(
            () => RimLLMJsonHelper.DeserializeAndValidate<DictionaryRoot>("{\"Items\":{\"a\":null}}"));
        Assert.Throws<InvalidOperationException>(
            () => RimLLMJsonHelper.DeserializeAndValidate<DictionaryRoot>("{\"Items\":{\"a\":{}}}"));

        var root = new DictionaryRoot
        {
            Items = new Dictionary<string, DictionaryItem> { ["a"] = null }
        };
        Assert.Throws<InvalidOperationException>(() => RimLLMJsonHelper.ValidateStructuredObject(root));
    }

    [Test]
    public void NullableCollectionAndMapValuesRemainNullableInSchema()
    {
        JsonObject properties = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(NullableContainersDto)))
            .AsObject()["properties"].AsObject();
        JsonObject itemType = properties["Values"].AsObject()["items"].AsObject();
        JsonObject mapValueType = properties["Lookup"].AsObject()["additionalProperties"].AsObject();

        CollectionAssert.AreEquivalent(new[] { "integer", "null" }, itemType["type"].Deserialize<string[]>());
        CollectionAssert.AreEquivalent(new[] { "integer", "null" }, mapValueType["type"].Deserialize<string[]>());

        NullableContainersDto parsed = RimLLMJsonHelper.DeserializeAndValidate<NullableContainersDto>(
            "{\"Values\":[null,1],\"Lookup\":{\"empty\":null}}");
        ClassicAssert.IsNull(parsed.Values[0]);
        ClassicAssert.IsNull(parsed.Lookup["empty"]);
    }

    [Test]
    public void NullableEnumCollectionAndMapSchemasAllowNullEnumValues()
    {
        JsonObject properties = JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(typeof(NullableEnumContainersDto)))
            .AsObject()["properties"].AsObject();
        JsonObject itemSchema = properties["Values"].AsObject()["items"].AsObject();
        JsonObject mapValueSchema = properties["Lookup"].AsObject()["additionalProperties"].AsObject();

        AssertNullableEnumSchema(itemSchema);
        AssertNullableEnumSchema(mapValueSchema);

        NullableEnumContainersDto parsed = RimLLMJsonHelper.DeserializeAndValidate<NullableEnumContainersDto>(
            "{\"Values\":[null,\"First\"],\"Lookup\":{\"empty\":null}}");
        ClassicAssert.IsNull(parsed.Values[0]);
        ClassicAssert.IsNull(parsed.Lookup["empty"]);
    }

    private static void AssertNullableEnumSchema(JsonObject schema)
    {
        CollectionAssert.AreEquivalent(new[] { "string", "null" }, schema["type"].Deserialize<string[]>());

        bool enumAllowsNull = false;
        foreach (JsonNode enumValue in schema["enum"].AsArray())
        {
            if (enumValue == null || enumValue.GetValueKind() == JsonValueKind.Null)
            {
                enumAllowsNull = true;
                break;
            }
        }
        ClassicAssert.IsTrue(enumAllowsNull, "Nullable enum 的 enum 約束也必須包含 null。");
    }

    [Test]
    public void JsonRepairPreservesThinkTagsAndCommasInsideStrings()
    {
        const string response = "<think>reasoning</think>\n```json\n{\"Name\":\"literal ,} and <think>keep</think>\",}\n```";

        RepairDto parsed = RimLLMJsonHelper.DeserializeStructured<RepairDto>(
            response,
            new MockSettings { EnableJsonRepair = true });

        ClassicAssert.AreEqual("literal ,} and <think>keep</think>", parsed.Name);
    }

    public sealed class ContractDto
    {
        public string Name { get; set; }

        [JsonIgnore]
        public string LocalOnly { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; } = new();

        public string ReadOnly => null;
    }

    public sealed class RequiredDto
    {
        public int Count { get; set; }

        public string Label { get; set; } = "default";
    }

    public sealed class DictionaryRoot
    {
        public Dictionary<string, DictionaryItem> Items { get; set; } = new();
    }

    public sealed class DictionaryItem
    {
        public string Name { get; set; }
    }

    public sealed class NullableContainersDto
    {
        public List<int?> Values { get; set; } = new();

        public Dictionary<string, int?> Lookup { get; set; } = new();
    }

    public enum ReviewEnum
    {
        First,
        Second
    }

    public sealed class NullableEnumContainersDto
    {
        public List<ReviewEnum?> Values { get; set; } = new();

        public Dictionary<string, ReviewEnum?> Lookup { get; set; } = new();
    }

    public sealed class RepairDto
    {
        public string Name { get; set; }
    }
}
