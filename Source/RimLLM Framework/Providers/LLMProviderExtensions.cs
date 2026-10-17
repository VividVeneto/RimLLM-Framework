using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Providers;

/// <summary>
/// 為 ILLMProvider 提供便利的非同步擴充方法。
/// 所有文字生成與串流皆以底層的 CreateChatClient 為核心。
/// </summary>
#pragma warning disable S101 // reason: LLM 為品牌縮寫，維持現狀
public static class LLMProviderExtensions
{
    /// <summary>
    /// 使用指定供應商與模型發送非同步生成請求並取得純文字結果。
    /// </summary>
    public static async Task<string> GenerateAsync(
        this ILLMProvider provider,
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        string model)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        using IChatClient client = provider.CreateChatClient(model);
        ChatResponse response = await client.GetResponseAsync(messages, options).ConfigureAwait(false);
        if (response == null) return string.Empty;

        string text = response.Text ?? string.Empty;
        string reasoning = ExtractReasoning(response);
        if (!string.IsNullOrEmpty(reasoning))
        {
            return string.IsNullOrEmpty(text)
                ? $"<think>\n{reasoning}\n</think>"
                : $"<think>\n{reasoning}\n</think>\n\n{text}";
        }
        return text;
    }

    private static string ExtractReasoning(ChatResponse response)
    {
        if (response?.Messages == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (ChatMessage msg in System.Linq.Enumerable.Where(response.Messages, m => m?.Contents != null))
        {
            foreach (TextReasoningContent trc in System.Linq.Enumerable.Where(System.Linq.Enumerable.OfType<TextReasoningContent>(msg.Contents), c => !string.IsNullOrEmpty(c.Text)))
            {
                sb.Append(trc.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 使用指定供應商與模型發送非同步串流請求。
    /// </summary>
    public static async Task StreamAsync(
        this ILLMProvider provider,
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        string model,
        Action<string> onChunkReceived)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        using IChatClient client = provider.CreateChatClient(model);
#pragma warning disable S3267 // reason: IAsyncEnumerable 串流更新過濾，維持現狀
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
        {
            if (!string.IsNullOrEmpty(update?.Text))
            {
                onChunkReceived?.Invoke(update.Text);
            }
        }
#pragma warning restore S3267
    }
}
