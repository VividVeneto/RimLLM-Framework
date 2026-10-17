using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Providers;

#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
/// <summary>
/// LLM 供應商對接介面（標準 MEAI IChatClient 產出、能力宣告與診斷）。
/// </summary>
public interface ILLMProvider
{
    /// <summary>
    /// 供應商唯一識別碼。
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// 此供應商是否必須提供 API Key 才能使用。
    /// 本地相容介面（如 OpenAICompatible / LM Studio / Ollama）可回傳 false。
    /// </summary>
    bool RequiresApiKey { get; }

    /// <summary>
    /// 供應商支援的能力宣告（如原生結構化輸出、Schema 方言偏好等）。
    /// </summary>
    LLMProviderCapabilities Capabilities { get; }

    /// <summary>
    /// 建立已預先配置好協定補丁、選項客製化與錯誤對映的 MEAI <see cref="IChatClient"/>。
    /// </summary>
    /// <param name="model">要使用的模型名稱</param>
    /// <returns>已配置的 IChatClient 執行個體</returns>
    IChatClient CreateChatClient(string model);

    /// <summary>
    /// 測試此供應商的 API 金鑰與連線狀態。
    /// </summary>
    /// <returns>連線測試結果</returns>
    Task<TestResult> TestConnectionAsync();

    /// <summary>
    /// 從 API 伺服器獲取可用模型列表。
    /// </summary>
    /// <returns>模型名稱清單</returns>
    Task<List<string>> FetchAvailableModelsAsync();
}
#pragma warning restore S101, S2342
