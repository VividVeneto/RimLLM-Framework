using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>RimLLM SDK 對 IChatClient 的擴充方法。</summary>
public static class RimLLMClientExtensions
{
    /// <summary>
    /// 回應上的標記鍵：本次請求的 Tools 因實際回答的供應商不支援工具呼叫而被框架移除。
    /// </summary>
    internal const string ToolsStrippedKey = "rimllm_tools_stripped";

    /// <summary>
    /// 本次回應是否由一個不支援工具呼叫的供應商產生，且請求上的 Tools 已被框架移除。
    /// 為 true 時模型從未看過工具定義，Agent 端不應把「沒有 FunctionCallContent」解讀成
    /// 模型決定不呼叫工具。
    /// </summary>
    public static bool WereToolsStripped(this ChatResponse response)
    {
        return response?.AdditionalProperties != null &&
               response.AdditionalProperties.TryGetValue(ToolsStrippedKey, out object value) &&
               value is bool flag && flag;
    }

    /// <summary>串流版本：每個 update 都帶同一個標記，檢查任一個即可。</summary>
    public static bool WereToolsStripped(this ChatResponseUpdate update)
    {
        return update?.AdditionalProperties != null &&
               update.AdditionalProperties.TryGetValue(ToolsStrippedKey, out object value) &&
               value is bool flag && flag;
    }

    /// <summary>
    /// 結構化輸出：以目標型別 T 產生 JSON Schema 送出，並回傳反序列化結果。
    /// client 為 RimLLMProvider.CreateChatClient 回傳的 facade 時走完整路徑
    /// （含 JSON repair）；其他 IChatClient 走簡化路徑（schema + repair）。
    /// </summary>
    public static Task<T> GetResponseObjectAsync<T>(
        this IChatClient client,
        IEnumerable<ChatMessage> messages,
        RimLLMChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));

        // 以 GetService 而非型別轉換辨識框架堆疊：MEAI 的 DelegatingChatClient 會把
        // GetService 往內層轉發，因此無論外面包了幾層中介層都認得出來。
        if (client.GetService(typeof(RimLLMFailoverChatClient)) is RimLLMFailoverChatClient)
        {
            return FrameworkPathAsync<T>(client, messages, options, cancellationToken);
        }

        return SimplifiedPathAsync<T>(client, messages, options, cancellationToken);
    }

    /// <summary>
    /// 框架路徑：只在 options 上標註目標型別，schema 方言交給知道供應商身分的路由層決定。
    /// 請求本身走完整的 IChatClient 鏈，因此防濫用、預算、佇列與快取都不會被繞過
    /// ——先前這條路徑直接呼叫 facade，任何搬到 facade 外層的中介層都會被跳過。
    /// </summary>
    private static async Task<T> FrameworkPathAsync<T>(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        RimLLMChatOptions options,
        CancellationToken cancellationToken)
    {
        ChatOptions effective = (options ?? new RimLLMChatOptions()).Clone();
        if (effective.AdditionalProperties == null)
        {
            effective.AdditionalProperties = [];
        }
        effective.AdditionalProperties[RimLLMChatOptions.ResponseTypeKey] = typeof(T);

        ChatResponse response = await client
            .GetResponseAsync(messages, effective, cancellationToken)
            .ConfigureAwait(false);

        string raw = response?.Text ?? string.Empty;
        return RimLLMProvider.TryGetManager(out RimLLMManager manager)
            ? RimLLMJsonHelper.DeserializeStructured<T>(raw, manager.Settings)
            : RimLLMJsonHelper.DeserializeAndValidate<T>(raw);
    }

    private static async Task<T> SimplifiedPathAsync<T>(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        RimLLMChatOptions options,
        CancellationToken cancellationToken)
    {
        // 與框架路徑一樣先複製：ResponseFormat 是寫在副本上的，不能改到呼叫端手上那份。
        ChatOptions effective = (options ?? new RimLLMChatOptions()).Clone();
        // schema 的快取由 RimLLMSchemaBuilder 統一負責，此處不再另建一份。
        string schemaJson = RimLLMSchemaBuilder.BuildJson(typeof(T));
        using (JsonDocument document = JsonDocument.Parse(schemaJson))
        {
            effective.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                document.RootElement.Clone(),
                "custom_type",
                "RimLLM structured response");
        }

        ChatResponse response = await client
            .GetResponseAsync(messages, effective, cancellationToken)
            .ConfigureAwait(false);

        string raw = response?.Text ?? string.Empty;
        try
        {
            return RimLLMJsonHelper.DeserializeAndValidate<T>(raw);
        }
        catch (Exception)
        {
            string repaired = RimLLMJsonHelper.RepairJson(raw);
            return RimLLMJsonHelper.DeserializeAndValidate<T>(RimLLMJsonHelper.ExtractJsonBlock(repaired));
        }
    }
}
#pragma warning restore S101, S2342
