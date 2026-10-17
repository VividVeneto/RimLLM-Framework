using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimLLM_Framework.Core;
#pragma warning disable S108, S1133, S1643, S2486, S6610 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀

namespace RimLLM_Framework.Manager;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// JSON 的靜態處理與格式修復輔助工具。
/// 包含結構化資料的 JSON 補齊、Regex 修復以及 Dummy 物件生成（用於產生 schema 快取）。
/// </summary>
public static class RimLLMJsonHelper
{
    private static readonly Regex TrailingCommaRegex = new(@",\s*([\]}])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex JsonBlockRegex = new(@"(\{.*\}|\[.*\])", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static readonly Regex ThinkTagRegex = new(@"<think>.*?</think>", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static readonly ConcurrentDictionary<Type, string> SampleJsonCache = new();

    // 全框架共用的 JSON 引擎與寬容設定（原 RimLLMJson，已併入此檔）。
    // DTO 大量使用 public field（STJ 預設只看 property），反序列化大小寫不敏感，
    // 並對齊舊 Newtonsoft 寬容度：數字可由字串讀入、尾隨逗號與註解略過、列舉接受名稱。
    private static readonly JsonSerializerOptions SharedOptions = CreateSharedOptions();

    private static JsonSerializerOptions CreateSharedOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>共用設定。JsonSerializerOptions 建構後即凍結語意，可多執行緒共用。</summary>
    public static JsonSerializerOptions Options => SharedOptions;

    /// <summary>以執行期型別序列化（DTO 可能是 object 宣告，泛型推斷會丟失成員）。</summary>
    public static string Serialize(object value)
    {
        if (value == null) return "null";
        return JsonSerializer.Serialize(value, value.GetType(), SharedOptions);
    }

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, SharedOptions);
    }

    public static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, SharedOptions);
    }

    /// <summary>
    /// 獲取指定型別的 Sample JSON 字串。
    /// </summary>
    public static string GetSampleJson<T>()
    {
        return GetSampleJson(typeof(T));
    }

    /// <summary>
    /// 獲取指定型別的 Sample JSON 字串。
    /// </summary>
    public static string GetSampleJson(Type type)
    {
        if (SampleJsonCache.TryGetValue(type, out string json))
        {
            return json;
        }

        try
        {
            object instance = CreateDummyInstance(type);
            string generatedJson = Serialize(instance);
            SampleJsonCache[type] = generatedJson;
            return generatedJson;
        }
        catch
        {
            return "{}";
        }
    }



    /// <summary>
    /// 結構化輸出的核心流程：直接解析 → 靜態 JSON repair → 抽出 JSON 區塊後第二次解析。
    /// 純本地修復，不會再向模型發第二次請求。
    /// </summary>
    public static T DeserializeStructured<T>(string rawResponse, IRimLLMSettings settings)
    {
        try
        {
            return DeserializeAndValidate<T>(rawResponse);
        }
        catch (Exception ex)
        {
            if (settings?.EnableJsonRepair != true)
            {
                throw new RimLLMException(
                    LLMError.InvalidResponse,
                    $"Unable to parse LLM response to target object {typeof(T).Name} (JSON Repair is disabled). Raw Response: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                    ex);
            }

            string repairedJson = RepairJson(rawResponse);
            // 每次需要修復的結構化回應都會觸發，受詳細日誌開關控制。
            if (RimLLMLog.Enabled)
            {
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting static repair. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}\nRepaired preview: {RimLLMLog.SanitizeForLog(repairedJson, 300)}\nError: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            }
            try
            {
                string fallbackExtracted = ExtractJsonBlock(repairedJson);
                return DeserializeAndValidate<T>(fallbackExtracted);
            }
            catch (Exception repairEx)
            {
                throw new RimLLMException(
                    LLMError.InvalidResponse,
                    $"Unable to parse LLM response to target object {typeof(T).Name}. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}. Static repair error: {RimLLMLog.SanitizeForLog(repairEx.Message, 200)}",
                    repairEx);
            }
        }
    }

    /// <summary>
    /// 修復不完整的 JSON 字串，例如移除 `<think>` 標籤、Markdown 語法、多餘的尾隨逗號以及補齊括號。
#pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
    /// </summary>
    public static string RepairJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        json = json.Trim();

        // 0. 剝離 <think>...</think> 標籤及其內容，以避免結構化 JSON 解析失敗
        json = ThinkTagRegex.Replace(json, "").Trim();

        // 1. 移除 Markdown 標記
        if (json.StartsWith("```"))
        {
            int startIndex = json.IndexOf('\n');
            if (startIndex != -1)
            {
                json = json.Substring(startIndex + 1);
            }
            else
            {
                json = json.Substring(3);
            }
        }
        if (json.EndsWith("```"))
        {
            json = json.Substring(0, json.Length - 3);
        }
        json = json.Trim();

        // 2. 移除尾隨逗號 (使用編譯後的靜態 Regex 提效)
        json = TrailingCommaRegex.Replace(json, "$1");

        // 3. 補齊缺失括號 (跳過雙引號字串內部的字元)。
        //    以堆疊記錄尚待閉合的符號，才能對交錯巢狀（如 {"a":[1) 產生正確的閉合順序；
        //    僅用計數會固定先補 } 再補 ]，對巢狀結構會產生無法解析的結果。
        //    以 List<char> 保存遇到的右括號，從尾端依巢狀順序補回。
        var expectedClosers = new List<char>();
        bool inString = false;
        bool escapeNext = false;
        foreach (char c in json)
        {
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }
            if (c == '\\')
            {
                escapeNext = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (c == '{') expectedClosers.Add('}');
            else if (c == '[') expectedClosers.Add(']');
            else if (c == '}' || c == ']')
            {
                // 閉合符號與堆疊頂端不符，代表結構本身已損毀而非單純截斷。
                // 此時任何補齊都只會讓結果更糟，直接交給 ExtractJsonBlock 的第二次解析處理。
                if (expectedClosers.Count == 0 || expectedClosers[expectedClosers.Count - 1] != c)
                {
                    return json;
                }
                expectedClosers.RemoveAt(expectedClosers.Count - 1);
            }
        }
#pragma warning restore S3776

        // 3a. 字串在結尾處未閉合時先補上引號，否則後續補的括號會落在字串內部。
        if (inString)
        {
            json += "\"";
        }

        // 3b. 清掉截斷造成的懸空 token，避免補完括號後仍無法解析。
        //     只清除 JSON 允許的空白字元。
        json = json.TrimEnd(' ', '\t', '\r', '\n');
        if (json.EndsWith(":"))
        {
            json += "null";
        }
        else if (json.EndsWith(","))
        {
            json = json.Substring(0, json.Length - 1);
        }

        // 3c. 依 LIFO 順序補齊
        for (int i = expectedClosers.Count - 1; i >= 0; i--)
        {
            json += expectedClosers[i];
        }

        return json;
    }

    /// <summary>
    /// 提取字串中的 JSON 區塊（第一個匹配的 { ... } 或 [ ... ]）。
    /// </summary>
    public static string ExtractJsonBlock(string input)
    {
        var match = JsonBlockRegex.Match(input);
        return match.Success ? match.Value : input;
    }

    private static object CreateDummyInstance(Type type)
    {
        return CreateDummyInstance(type, []);
    }
#pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本

    private static object CreateDummyInstance(Type type, HashSet<Type> visitedTypes)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return 0;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return 0.0;
        if (type == typeof(bool)) return false;
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.Length > 0 ? values.GetValue(0) : 0;
        }

        // 避免循環引用導致 StackOverflow
        if (visitedTypes.Contains(type))
        {
            return null;
        }
        visitedTypes.Add(type);

        try
        {
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var array = Array.CreateInstance(elementType, 1);
                array.SetValue(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)), 0);
                return array;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                var listType = typeof(List<>).MakeGenericType(elementType);
                var list = Activator.CreateInstance(listType) as System.Collections.IList;
                if (list != null)
                {
                    list.Add(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)));
                }
                return list;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var keyType = type.GetGenericArguments()[0];
                var valueType = type.GetGenericArguments()[1];
                var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                var dict = Activator.CreateInstance(dictType) as System.Collections.IDictionary;
                if (dict != null)
                {
                    var dummyKey = CreateDummyInstance(keyType, new HashSet<Type>(visitedTypes));
                    var dummyVal = CreateDummyInstance(valueType, new HashSet<Type>(visitedTypes));
                    if (dummyKey != null)
                    {
                        dict.Add(dummyKey, dummyVal);
                    }
                }
                return dict;
            }

            object instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch
            {
                // 若無無參數建構子，建立不執行建構子的預設實例。
                instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            }

            if (instance != null)
            {
                // 遞迴填充公開欄位與屬性
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        field.SetValue(instance, CreateDummyInstance(field.FieldType, new HashSet<Type>(visitedTypes)));
                    }
                    catch { }
                }
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.CanWrite)
                    {
                        try
                        {
                            prop.SetValue(instance, CreateDummyInstance(prop.PropertyType, new HashSet<Type>(visitedTypes)), null);
                        }
                        catch { }
                    }
                }
            }
            return instance;
        }
        catch
        {
            return null;
        }
    }
#pragma warning restore S3776

    /// <summary>
    /// 反序列化 JSON 字串為指定型別並驗證所有必填欄位。
    /// </summary>
    public static T DeserializeAndValidate<T>(string json)
    {
        T result = Deserialize<T>(json);
        ValidateStructuredObject(result);
        return result;
    }

    /// <summary>
    /// 驗證反序列化後的物件及其子屬性/欄位皆不可為 null。
    /// </summary>
    public static void ValidateStructuredObject<T>(T result)
    {
        if (ReferenceEquals(result, null))
        {
            throw new InvalidOperationException("Structured response deserialized to null.");
        }

        ValidateRequiredMembers(result, typeof(T), []);
    }

#pragma warning disable S3776 // reason: 遞迴驗證結構化型別屬性與欄位，拆分反而增加呼叫成本
    /// <summary>
    /// 遞迴驗證結構化物件。以「目前遞迴路徑」追蹤型別，而非「整次驗證已看過」：
    /// <paramref name="visitedTypes"/> 只包含目前正在下潛的祖先型別，離開某型別時移除。
    /// 這樣同一型別的多個實例（例如 <c>List&lt;Item&gt;</c> 的第二筆資料）都會完整驗證，
    /// 同時仍能防止循環引用造成 StackOverflow。
    /// </summary>
    public static void ValidateRequiredMembers(object value, Type type, HashSet<Type> visitedTypes)
    {
        if (value == null || type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
        {
            return;
        }
        if (!visitedTypes.Add(type))
        {
            // 目前遞迴路徑上已有此型別，代表循環引用；停止下潛避免 StackOverflow。
            return;
        }

        try
        {
            if (value is System.Collections.IEnumerable enumerable && type != typeof(string))
            {
                Type elementType = GetCollectionElementType(type);
                foreach (object item in enumerable)
                {
                    if (item == null)
                    {
                        // 可空元素型別（如 int?）允許 null；其餘一律視為必填，null 代表 LLM 回傳了殘缺陣列。
                        if (elementType != null && !IsRequiredMember(elementType))
                        {
                            continue;
                        }
                        throw new InvalidOperationException(
                            $"Required structured response collection element is null (collection type '{type.Name}').");
                    }
                    ValidateRequiredMembers(item, item.GetType(), visitedTypes);
                }
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object memberValue = property.GetValue(value, null);
                if (memberValue == null && IsRequiredMember(property.PropertyType))
                {
                    throw new InvalidOperationException($"Required structured response member '{property.Name}' is null.");
                }
                if (memberValue != null)
                {
                    ValidateRequiredMembers(memberValue, property.PropertyType, visitedTypes);
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsLiteral || field.IsInitOnly)
                {
                    continue;
                }

                object memberValue = field.GetValue(value);
                if (memberValue == null && IsRequiredMember(field.FieldType))
                {
                    throw new InvalidOperationException($"Required structured response member '{field.Name}' is null.");
                }
                if (memberValue != null)
                {
                    ValidateRequiredMembers(memberValue, field.FieldType, visitedTypes);
                }
            }
        }
        finally
        {
            // 離開此型別後從路徑移除，讓同型別的其他實例也能完整驗證。
            visitedTypes.Remove(type);
        }
    }
#pragma warning restore S3776

    private static bool IsRequiredMember(Type type)
    {
        return Nullable.GetUnderlyingType(type) == null;
    }

    /// <summary>
    /// 取得集合的元素型別：陣列取 <c>GetElementType</c>，
    /// 泛型集合取其 <c>IEnumerable&lt;T&gt;</c> 的 <c>T</c>
    ///（字典會取到 <c>KeyValuePair&lt;K,V&gt;</c>，為實值型別，不影響 null 判斷）。
    /// 非泛型集合無從得知，回傳 null，呼叫端將 null 元素視為非法。
    /// </summary>
    private static Type GetCollectionElementType(Type type)
    {
        if (type == null) return null;
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType)
        {
            foreach (Type iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>))
                {
                    return iface.GetGenericArguments()[0];
                }
            }
        }
        return null;
    }
}
#pragma warning restore S101, S2342
#pragma warning restore S108, S1133, S1643, S2486, S6610
