namespace RimLLM_Framework.Manager;

/// <summary>
/// 單次請求結算的 token（輸入、輸出）。用量回呼與請求日誌以此傳遞，
/// 避免裸露的 int 配對在多層之間傳遞時錯置。
/// </summary>
internal readonly struct UsageTokens
{
    public readonly int Prompt;
    public readonly int Completion;

    public UsageTokens(int prompt, int completion)
    {
        Prompt = prompt;
        Completion = completion;
    }
}
