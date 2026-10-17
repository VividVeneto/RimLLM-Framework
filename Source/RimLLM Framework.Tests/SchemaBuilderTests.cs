using System;
#pragma warning disable S2699, S2701, S3415 // reason: 測試檔案斷言語意保留，Explicit 診斷測試無需斷言
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

/// <summary>
/// <see cref="RimLLMSchemaBuilder"/> 的形狀不變式與契約對齊測試。
///
/// 這裡守住的核心風險有兩個：
/// 一是 schema 由 System.Text.Json 的 exporter 產生、反序列化也由 STJ 執行，
/// 兩邊的成員契約一旦漂移，模型就會照 schema 填一個反序列化收不到的欄位；
/// 二是 MEAI 的完整 JSON Schema（聯集型別、<c>$ref</c>）不是所有 provider 都接受。
/// </summary>
[TestFixture]
public class SchemaBuilderTests
{
    // -----------------------------------------------------------------
    // 契約對齊：STJ 產 schema、STJ 反序列化
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // 契約對齊：STJ 產 schema、STJ 反序列化
    // -----------------------------------------------------------------

    [Test]
    public void SchemaPropertiesMatchStjContract()
    {
        foreach (Type type in SampleTypes())
        {
            // 以 STJ 自身的中繼資料解析反序列化契約，再與 schema 成員比對。
            // 用的是生產路徑同一份設定（RimLLMJsonHelper.Options），契約漂移會在這裡現形。
            JsonTypeInfo typeInfo = new DefaultJsonTypeInfoResolver().GetTypeInfo(type, RimLLMJsonHelper.Options);
            var expected = new List<string>();
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                expected.Add(property.Name);
            }

            JsonObject schema = ParseSchema(type);
            var actual = new List<string>();
            foreach (var property in schema["properties"].AsObject())
            {
                actual.Add(property.Key);
            }

            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(
                expected,
                actual,
                type.Name + " 的 schema 成員集合必須與 STJ 的反序列化契約一致。");
        }
    }

    [Test]
    public void SampleJsonSatisfiesRequiredMembers()
    {
        foreach (Type type in SampleTypes())
        {
            JsonObject schema = ParseSchema(type);
            var sample = JsonNode.Parse(RimLLMJsonHelper.GetSampleJson(type)).AsObject();

            foreach (var requiredName in schema["required"].AsArray())
            {
                string name = requiredName.GetValue<string>();
                ClassicAssert.IsTrue(
                    sample.ContainsKey(name),
                    type.Name + " 的提示式範例 JSON 缺少 required 成員 " + name + "，兩條路徑對成員的認知已經分歧。");
            }

            // ComplexTestDataStructure 刻意只有帶參建構子（見下方說明），跳過通用的反序列化檢查。
            if (type != typeof(ComplexTestDataStructure))
            {
                Assert.DoesNotThrow(
                    () => JsonSerializer.Deserialize(sample.ToJsonString(), type, RimLLMJsonHelper.Options),
                    type.Name + " 的範例 JSON 應能被 STJ 反序列化。");
            }

            // ComplexTestDataStructure 刻意只有帶參建構子（dummyParam 對應不到任何成員），
            // 用來覆蓋 CreateDummyInstance 的 RuntimeHelpers 逃生路徑。
            // STJ 要求建構子參數全數可綁定，這類舊 Newtonsoft 靜默容忍（傳 default）的形狀
            // 現在會明確失敗 —— 這是引擎遷移有意的契約收斂，不是回歸：schema 產生與 sample
            // 產生不受影響（上方的 required 檢查已通過），且失敗是 loud 而非靜默半初始化物件。
            if (type == typeof(ComplexTestDataStructure))
            {
                Assert.Throws<InvalidOperationException>(
                    () => JsonSerializer.Deserialize(
                        "{\"Name\":\"x\",\"Age\":1,\"IsActive\":true,\"Skills\":[],\"Mapping\":{},\"Nested\":null}",
                        type,
                        RimLLMJsonHelper.Options),
                    "無法綁定的建構子參數應明確失敗，而非靜默產出半初始化物件。");
            }
        }
    }

    /// <summary>
    /// schema 說某個成員可以是 null，執行期驗證就必須接受 null，兩邊不能互相矛盾。
    ///
    /// 原本 <c>ValidateRequiredMembers</c> 的判斷式是反的：只在「值為 null 且型別**允許** null」
    /// 時才拋，也就是只會對合法可為 null 的成員開火。模型照 schema 回傳 null 會被判定為解析失敗，
    /// 白白走一次靜態修復與第二次解析，最後仍以 RimLLMException 收場。
    /// 這條路徑先前沒有任何測試涵蓋。
    /// </summary>
    [Test]
    public void NullOptionalMemberPassesValidationButNullRequiredMemberDoesNot()
    {
        NullableTestDataStructure parsed = RimLLMJsonHelper.DeserializeAndValidate<NullableTestDataStructure>(
            "{\"Name\":\"Randy\",\"OptionalCount\":null}");

        ClassicAssert.AreEqual("Randy", parsed.Name);
        ClassicAssert.IsNull(parsed.OptionalCount, "Nullable<T> 成員為 null 是合法的，schema 也明確允許。");

        Assert.Throws<InvalidOperationException>(
            () => RimLLMJsonHelper.DeserializeAndValidate<NullableTestDataStructure>(
                "{\"Name\":null,\"OptionalCount\":3}"),
            "非選填成員為 null 仍應被擋下 —— 這才是這道驗證原本要防的情況。");
    }

    // -----------------------------------------------------------------
    // 形狀不變式
    // -----------------------------------------------------------------

    [Test]
    public void NormalizedSchemaHasNoCompositeOrReferenceKeywords()
    {
        string[] forbidden = ["$ref", "$defs", "$schema", "$id", "allOf", "anyOf", "oneOf"];

        foreach (Type type in SampleTypes())
        {
            JsonObject schema = ParseSchema(type);
            foreach (JsonObject node in EnumerateNodes(schema))
            {
                foreach (string keyword in forbidden)
                {
                    ClassicAssert.IsNull(
                        node[keyword],
                        type.Name + " 的 schema 不應含 " + keyword + "。");
                }
            }
        }
    }

    [Test]
    public void KeywordWhitelistIsEnforced()
    {
        string[] allowed = ["type", "enum", "properties", "required", "items", "additionalProperties", "description"];

        foreach (Type type in SampleTypes())
        {
            JsonObject schema = ParseSchema(type);
            foreach (JsonObject node in EnumerateNodes(schema))
            {
                foreach (var member in node)
                {
                    CollectionAssert.Contains(
                        allowed,
                        member.Key,
                        type.Name + " 的 schema 出現白名單外的關鍵字 " + member.Key + "。");
                }
            }
        }
    }

    [Test]
    public void OpenAiProfileUsesUnionsInsteadOfNullableKeyword()
    {
        foreach (Type type in SampleTypes())
        {
            foreach (JsonObject node in EnumerateNodes(ParseSchema(type)))
            {
                ClassicAssert.IsNull(node["nullable"], type.Name + " 的 schema 不應使用 OpenAPI 的 nullable 關鍵字。");
            }
        }
    }

    /// <summary>
    /// OpenAI 的 strict structured output 要求 <c>required</c> 涵蓋所有 property，
    /// 選填只能靠聯集型別表達 —— 這正是舊實作把 <c>Nullable&lt;T&gt;</c> 排除在 required 之外時
    /// 會在 OpenAI 端被 400 的原因。
    /// </summary>
    [Test]
    public void EveryPropertyIsRequired()
    {
        foreach (Type type in SampleTypes())
        {
            foreach (JsonObject node in EnumerateNodes(ParseSchema(type)))
            {
                if (node["properties"] is not JsonObject properties) continue;

                var required = new List<string>();
                foreach (var name in node["required"].AsArray())
                {
                    required.Add(name.GetValue<string>());
                }

                var declared = new List<string>();
                foreach (var property in properties)
                {
                    declared.Add(property.Key);
                }

                required.Sort(StringComparer.Ordinal);
                declared.Sort(StringComparer.Ordinal);
                CollectionAssert.AreEqual(
                    declared,
                    required,
                    type.Name + " 的 required 必須與 properties 完全一致。");
            }
        }
    }

    [Test]
    public void NullableMemberUsesUnionType()
    {
        JsonObject schema = ParseSchema(typeof(NullableTestDataStructure));
        JsonNode memberType = schema["properties"].AsObject()["OptionalCount"].AsObject()["type"];
        ClassicAssert.AreEqual(JsonValueKind.Array, memberType.GetValueKind(), "int? 應寫成聯集型別。");
        CollectionAssert.AreEquivalent(new[] { "integer", "null" }, memberType.Deserialize<string[]>());

        // 專案未啟用 NRT，exporter 會把所有參考型別也寫成可為 null 的聯集。
        // 只有 Nullable<T> 才算選填 —— 與舊實作的 IsOptionalMember 判定一致。
        ClassicAssert.AreEqual(
            JsonValueKind.String,
            schema["properties"].AsObject()["Name"].AsObject()["type"].GetValueKind(),
            "參考型別成員不應被誤判為選填。");
    }

    // -----------------------------------------------------------------
    // 遞迴、深度與 $ref 去重
    // -----------------------------------------------------------------

    /// <summary>
    /// MEAI 的 <c>$ref</c> 不只用於循環，也用於去重：<c>List&lt;string&gt;</c> 第二次出現時會變成
    /// <c>{"$ref":"#/properties/Skills"}</c>。一律截斷 <c>$ref</c> 會誤刪這種正常成員，
    /// 所以正規化必須真的解析 JSON pointer，只在指向祖先時才視為循環。
    /// </summary>
    [Test]
    public void RecursiveMemberIsTruncatedButDeduplicatedMemberSurvives()
    {
        JsonObject schema = ParseSchema(typeof(ComplexTestDataStructure));

        // 去重的 $ref 必須完整展開成原本的 schema，不能只剩空殼 —— 這是「一律截斷 $ref」會踩到的坑。
        var skills = schema["properties"].AsObject()["Skills"];
        ClassicAssert.IsNotNull(skills, "Skills 是 $ref 去重而非循環，不得被截斷。");
        ClassicAssert.AreEqual("array", skills["type"].GetValue<string>());
        ClassicAssert.AreEqual("string", skills["items"].AsObject()["type"].GetValue<string>());

        // 循環在 CLR 型別層截斷，而非等到 JSON pointer 重現。exporter 會先把遞迴成員完整
        // 展開一輪、其中才出現指回祖先的 $ref，只靠 pointer 偵測會多送一整層
        // （實測 789 → 3119 字元，而那是每次請求都要付的 prompt token）。
        var nested = schema["properties"].AsObject()["Nested"].AsObject();
        ClassicAssert.IsNotNull(nested, "非循環的巢狀成員應正常展開。");
        ClassicAssert.AreEqual("number", nested["properties"].AsObject()["Weight"].AsObject()["type"].GetValue<string>());

        ClassicAssert.IsNull(nested["properties"].AsObject()["SelfRef"], "指回祖先型別的成員應被截斷。");
        CollectionAssert.DoesNotContain(RequiredNames(nested), "SelfRef", "被截斷的成員不得留在 required。");
    }

    /// <summary>
    /// 遞迴型別的 schema 體積是每次結構化請求都要付的 prompt token，
    /// 因此循環截斷點退步（例如改回只靠 JSON pointer 偵測）必須被擋下來。
    /// 上限取目前體積的約 1.5 倍，只擋量級上的退步。
    /// </summary>
    private const int MaxRecursiveSchemaLength = 700;

    [Test]
    public void RecursiveSchemaStaysCompact()
    {
        int size = RimLLMSchemaBuilder.BuildJson(typeof(ComplexTestDataStructure)).Length;

        ClassicAssert.Less(size, MaxRecursiveSchemaLength, "遞迴型別的 schema 體積退步。size=" + size);
    }

    /// <summary>
    /// OpenAI 的 strict structured output 明訂最多 5 層巢狀，超過會被服務端拒絕並靜默降級成
    /// 提示式 JSON，因此產生器在此先截斷。
    /// </summary>
    [Test]
    public void DeepNestingIsTruncatedAtStrictLimit()
    {
        int depth = MeasureNextChainDepth(ParseSchema(typeof(DeepChainLevel0)));

        ClassicAssert.LessOrEqual(
            depth,
            RimLLMSchemaBuilder.OpenAIMaxSchemaDepth,
            "巢狀層數不得超過服務端上限。實際：" + depth);
    }

    private static int MeasureNextChainDepth(JsonObject schema)
    {
        int depth = 0;
        JsonObject current = schema;
        while (true)
        {
            if (current["properties"].AsObject()["Next"] is not JsonObject next) break;

            depth++;
            current = next;
            ClassicAssert.Less(depth, 20, "深度截斷失效，schema 無限展開。");
        }

        CollectionAssert.DoesNotContain(RequiredNames(current), "Next", "被截斷的成員不得留在 required。");
        return depth;
    }

    // -----------------------------------------------------------------
    // 型別對照
    // -----------------------------------------------------------------

    [Test]
    [Explicit("診斷用：印出 Stage A 的原始輸出")]
    public void DumpRawExporterOutput()
    {
        foreach (Type type in SampleTypes())
        {
            TestContext.WriteLine(type.Name + " => " + RimLLMSchemaBuilder.ExportRaw(type).ToJsonString());
        }
    }

    [Test]
    public void StringEnumValueFromLlmDeserializes()
    {
        // schema 把列舉宣告為字串名稱，LLM 會照 schema 回傳 "Kind":"Beta"。
        // 反序列化必須接受名稱（與舊 Newtonsoft 預設一致），否則含列舉的結構化輸出
        // 會拋 JsonException —— 而該例外被歸類為不可重試，連備援都不會觸發。
        EnumTestDataStructure parsed = RimLLMJsonHelper.DeserializeAndValidate<EnumTestDataStructure>(
            "{\"Kind\":\"Beta\",\"Label\":\"x\"}");

        ClassicAssert.AreEqual(TestKind.Beta, parsed.Kind);
        ClassicAssert.AreEqual("x", parsed.Label);
    }

    [Test]
    public void EnumMemberBecomesStringEnum()
    {
        JsonObject schema = ParseSchema(typeof(EnumTestDataStructure));
        var kind = schema["properties"].AsObject()["Kind"];

        ClassicAssert.AreEqual("string", kind["type"].GetValue<string>(), "列舉應以字串名稱表達，STJ 反序列化接受名稱。");
        CollectionAssert.AreEquivalent(
            new[] { "Alpha", "Beta" },
            kind["enum"].Deserialize<string[]>());
    }

    [Test]
    public void DescriptionAttributeFlowsIntoSchema()
    {
        JsonObject schema = ParseSchema(typeof(DescribedTestDataStructure));

        ClassicAssert.AreEqual(
            "殖民者的名字",
            schema["properties"].AsObject()["Name"].AsObject()["description"].GetValue<string>(),
            "成員層級的 [Description] 應傳進 schema —— 這是舊反射實作沒有的能力。");

        ClassicAssert.AreEqual(
            "一筆殖民者紀錄",
            schema["description"].GetValue<string>(),
            "類別層級的 [Description] 也應傳進 schema。");
    }

    [Test]
    public void DictionaryBecomesOpenMapAndDisablesStrict()
    {
        RimLLMSchemaResult result = RimLLMSchemaBuilder.Build(typeof(ComplexTestDataStructure));
        var schema = JsonNode.Parse(result.Json).AsObject();
        var mapping = schema["properties"].AsObject()["Mapping"];

        ClassicAssert.AreEqual("object", mapping["type"].GetValue<string>());
        ClassicAssert.AreEqual("integer", mapping["additionalProperties"].AsObject()["type"].GetValue<string>());

        ClassicAssert.IsTrue(result.ContainsOpenEndedMap, "含 Dictionary 的型別應被判定為開放式 map。");
        ClassicAssert.IsFalse(result.StrictCompatible, "開放式 map 不相容於 OpenAI 的 strict structured output。");

        RimLLMSchemaResult plain = RimLLMSchemaBuilder.Build(typeof(NullableTestDataStructure));
        ClassicAssert.IsFalse(plain.ContainsOpenEndedMap);
        ClassicAssert.IsTrue(plain.StrictCompatible);
    }


    // -----------------------------------------------------------------
    // 快取
    // -----------------------------------------------------------------

    [Test]
    public void ResultCacheReturnsSameImmutableInstance()
    {
        RimLLMSchemaResult first = RimLLMSchemaBuilder.Build(typeof(TestDataStructure));
        RimLLMSchemaResult second = RimLLMSchemaBuilder.Build(typeof(TestDataStructure));
        ClassicAssert.AreSame(first, second, "結果不可變，快取應直接共用同一個實例。");
    }

    // -----------------------------------------------------------------
    // 輔助
    // -----------------------------------------------------------------

    private static IEnumerable<Type> SampleTypes()
    {
        yield return typeof(TestDataStructure);
        yield return typeof(NullableTestDataStructure);
        yield return typeof(ComplexTestDataStructure);
        yield return typeof(EnumTestDataStructure);
    }

    private static JsonObject ParseSchema(Type type)
    {
        return JsonNode.Parse(RimLLMSchemaBuilder.BuildJson(type)).AsObject();
    }

    private static List<string> PropertyNames(JsonObject node)
    {
        var names = new List<string>();
        if (node["properties"] is JsonObject properties)
        {
            foreach (var property in properties)
            {
                names.Add(property.Key);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static List<string> RequiredNames(JsonObject node)
    {
        var names = new List<string>();
        if (node["required"] is JsonArray required)
        {
            foreach (JsonNode name in required)
            {
                names.Add(name.GetValue<string>());
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>深度優先走訪 schema 中的每個節點（含根節點）。</summary>
    private static IEnumerable<JsonObject> EnumerateNodes(JsonObject node)
    {
        if (node == null) yield break;

        yield return node;

        foreach (JsonObject child in EnumerateNodes(node["items"] as JsonObject))
        {
            yield return child;
        }

        foreach (JsonObject child in EnumerateNodes(node["additionalProperties"] as JsonObject))
        {
            yield return child;
        }

        if (node["properties"] is not JsonObject properties) yield break;

        foreach (var property in properties)
        {
            foreach (JsonObject child in EnumerateNodes(property.Value as JsonObject))
            {
                yield return child;
            }
        }
    }

    public enum TestKind
    {
        Alpha,
        Beta
    }

    public class EnumTestDataStructure
    {
        public TestKind Kind { get; set; }
        public string Label { get; set; }
    }

    [System.ComponentModel.Description("一筆殖民者紀錄")]
    public class DescribedTestDataStructure
    {
        [System.ComponentModel.Description("殖民者的名字")]
        public string Name { get; set; }
    }

    public class DeepChainLevel0 { public DeepChainLevel1 Next { get; set; } }
    public class DeepChainLevel1 { public DeepChainLevel2 Next { get; set; } }
    public class DeepChainLevel2 { public DeepChainLevel3 Next { get; set; } }
    public class DeepChainLevel3 { public DeepChainLevel4 Next { get; set; } }
    public class DeepChainLevel4 { public DeepChainLevel5 Next { get; set; } }
    public class DeepChainLevel5 { public DeepChainLevel6 Next { get; set; } }
    public class DeepChainLevel6 { public DeepChainLevel7 Next { get; set; } }
    public class DeepChainLevel7 { public DeepChainLevel8 Next { get; set; } }
    public class DeepChainLevel8 { public DeepChainLevel9 Next { get; set; } }
    public class DeepChainLevel9 { public DeepChainLevel10 Next { get; set; } }
    public class DeepChainLevel10 { public int Value { get; set; } }
}
